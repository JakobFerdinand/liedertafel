using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Ai;

/// <summary>
/// The chokepoint in front of the model provider (ARC-022-3, architecture
/// §14): every chat-client call of every agent and job passes here. Before a
/// call it reserves the worst case in the budget and bounds the output; after
/// it — also when the stream fails, is cancelled or is abandoned by a
/// disconnected client — it settles what was really used. A refused
/// reservation throws and the provider is never called.
/// </summary>
public sealed class BudgetedChatClient(
	IChatClient provider, IAiBudget budget, IOptionsMonitor<AiOptions> options, string defaultModel)
	: DelegatingChatClient(provider)
{
	public override async Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		var call = await AdmitAsync(messages, options, cancellationToken);
		var usage = new CallUsage(call.EstimatedInputTokens);
		try
		{
			var response = await base.GetResponseAsync(call.Messages, call.Options, cancellationToken);
			usage.Observe(response.Usage);
			usage.ObserveText(response.Text);
			return response;
		}
		finally
		{
			await budget.SettleAsync(call.Reservation, usage.ToUsage());
		}
	}

	public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages, ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var call = await AdmitAsync(messages, options, cancellationToken);
		var usage = new CallUsage(call.EstimatedInputTokens);
		try
		{
			await foreach (var update in base.GetStreamingResponseAsync(call.Messages, call.Options, cancellationToken))
			{
				foreach (var content in update.Contents)
				{
					if (content is UsageContent reported)
						usage.Observe(reported.Details);
					else if (content is TextContent text)
						usage.ObserveText(text.Text);
				}
				yield return update;
			}
		}
		finally
		{
			// Runs on completion, failure, cancellation and when the consumer
			// stops reading: the call is always accounted for.
			await budget.SettleAsync(call.Reservation, usage.ToUsage());
		}
	}

	private async Task<AdmittedCall> AdmitAsync(
		IEnumerable<ChatMessage> messages, ChatOptions? requested, CancellationToken cancellationToken)
	{
		var operation = AiOperation.From(requested)
			?? throw new AiBudgetUnavailableException("The model call carries no AiOperation and cannot be charged.");
		var callOptions = requested!.Clone();
		AiOperation.RemoveFrom(callOptions);
		var maxOutput = Math.Max(1, options.CurrentValue.MaxOutputTokensPerCall);
		callOptions.MaxOutputTokens = Math.Min(callOptions.MaxOutputTokens ?? maxOutput, maxOutput);
		var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
		var estimate = AiTokenEstimate.Input(list, callOptions);
		var model = string.IsNullOrWhiteSpace(callOptions.ModelId) ? defaultModel : callOptions.ModelId.Trim();
		var reservation = await budget.ReserveAsync(
			new AiCall(operation.Feature, operation.OperationId, operation.AccountId, model, estimate, callOptions.MaxOutputTokens.Value),
			cancellationToken);
		return new AdmittedCall(list, callOptions, reservation, estimate);
	}

	private sealed record AdmittedCall(
		IReadOnlyList<ChatMessage> Messages, ChatOptions Options, AiReservation Reservation, long EstimatedInputTokens);

	/// <summary>
	/// Usage of one model call. Providers may repeat a cumulative usage report
	/// within one call, so the largest report counts. A call that ends without
	/// any report (failure, cancellation, a provider that sends none) is
	/// charged the input estimate plus the output it visibly produced.
	/// </summary>
	private sealed class CallUsage(long estimatedInputTokens)
	{
		private long input;
		private long output;
		private bool reported;
		private long outputChars;

		public void Observe(UsageDetails? details)
		{
			if (details is null)
				return;
			reported = true;
			input = Math.Max(input, details.InputTokenCount ?? 0);
			output = Math.Max(output, details.OutputTokenCount ?? 0);
		}

		public void ObserveText(string? text) => outputChars += text?.Length ?? 0;

		public AiUsage ToUsage() => reported
			? new AiUsage(input, output)
			: new AiUsage(estimatedInputTokens, AiTokenEstimate.FromChars(outputChars));
	}
}

/// <summary>
/// Conservative token estimates for reservations and for calls without a
/// usage report: three characters per token (German prose is nearer four),
/// a small overhead per message and a flat amount per binary part.
/// </summary>
public static class AiTokenEstimate
{
	private const int CharsPerToken = 3;
	private const int TokensPerMessage = 8;
	private const int TokensPerBinaryPart = 2000;

	public static long FromChars(long chars) => (chars + CharsPerToken - 1) / CharsPerToken;

	public static long Input(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
	{
		long chars = options?.Instructions?.Length ?? 0;
		long tokens = 0;
		foreach (var message in messages)
		{
			tokens += TokensPerMessage;
			foreach (var content in message.Contents)
			{
				switch (content)
				{
					case TextContent text:
						chars += text.Text.Length;
						break;
					case FunctionCallContent call:
						chars += call.Name.Length + Length(call.Arguments);
						break;
					case FunctionResultContent result:
						chars += Length(result.Result);
						break;
					case DataContent or UriContent:
						tokens += TokensPerBinaryPart;
						break;
				}
			}
		}
		foreach (var tool in options?.Tools ?? [])
		{
			chars += tool.Name.Length + tool.Description.Length;
			if (tool is AIFunctionDeclaration function)
				chars += function.JsonSchema.GetRawText().Length;
		}
		return tokens + FromChars(chars);
	}

	private static int Length(object? value) => value switch
	{
		null => 0,
		string text => text.Length,
		System.Text.Json.JsonElement element => element.GetRawText().Length,
		_ => System.Text.Json.JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions).Length,
	};
}
