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
/// reservation throws and the provider is never called. One instance serves
/// one model key (one provider deployment) and prices every call as that model.
/// </summary>
public sealed class BudgetedChatClient(
	IChatClient provider, IAiBudget budget, IOptionsMonitor<AiOptions> options, string model)
	: DelegatingChatClient(provider)
{
	public override async Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		var call = await AdmitAsync(messages, options, cancellationToken);
		var usage = new CallUsage();
		try
		{
			var response = await base.GetResponseAsync(call.Messages, call.Options, cancellationToken);
			usage.Observe(response.Usage);
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
		var usage = new CallUsage();
		try
		{
			await foreach (var update in base.GetStreamingResponseAsync(call.Messages, call.Options, cancellationToken))
			{
				foreach (var content in update.Contents)
				{
					if (content is UsageContent reported)
						usage.Observe(reported.Details);
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
		var operation = AiOperation.From(requested?.AdditionalProperties)
			?? throw new AiBudgetUnavailableException("The model call carries no AiOperation and cannot be charged.");
		// This client is one deployment. A caller that names another model
		// would be priced as that model while this one runs, so it is refused;
		// the model is never left to the caller.
		if (!string.IsNullOrWhiteSpace(requested!.ModelId)
			&& !string.Equals(requested.ModelId.Trim(), model, StringComparison.OrdinalIgnoreCase))
			throw new AiBudgetUnavailableException($"Model '{requested.ModelId}' is not served by this client.");
		var callOptions = requested.Clone();
		callOptions.ModelId = null;
		// Application markers (the operation, per-run agent state) stay inside.
		callOptions.AdditionalProperties = null;
		var ceiling = Math.Max(1, options.CurrentValue.MaxOutputTokensPerCall);
		callOptions.MaxOutputTokens = Math.Clamp(operation.MaxOutputTokens ?? callOptions.MaxOutputTokens ?? ceiling, 1, ceiling);
		var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
		if (operation.NonTextInputTokens is null && AiTokenEstimate.HasNonTextInput(list))
			throw new AiBudgetUnavailableException("The call has non-text input but its AiOperation gives no estimate for it.");
		var estimate = AiTokenEstimate.TextInput(list, callOptions) + Math.Max(0, operation.NonTextInputTokens ?? 0);
		var reservation = await budget.ReserveAsync(
			new AiCall(operation.Feature, operation.OperationId, operation.AccountId, model, estimate, callOptions.MaxOutputTokens.Value),
			cancellationToken);
		return new AdmittedCall(list, callOptions, reservation);
	}

	private sealed record AdmittedCall(IReadOnlyList<ChatMessage> Messages, ChatOptions Options, AiReservation Reservation);

	/// <summary>
	/// Usage of one model call. Providers may repeat a cumulative usage report
	/// within one call, so the largest report counts. A call that ends without
	/// any report (failure, cancellation, an abandoned stream, a provider that
	/// sends none) is settled at its full reservation: what the provider
	/// billed is unknown, and guessing low would let the month overshoot.
	/// </summary>
	private sealed class CallUsage
	{
		private long input;
		private long output;
		private bool reported;

		public void Observe(UsageDetails? details)
		{
			if (details is null)
				return;
			reported = true;
			input = Math.Max(input, details.InputTokenCount ?? 0);
			output = Math.Max(output, details.OutputTokenCount ?? 0);
		}

		public AiUsage ToUsage() => reported ? new AiUsage(input, output) : AiUsage.Unreported;
	}
}

/// <summary>
/// Conservative token estimates for text input: three characters per token
/// (German prose is nearer four) and a small overhead per message. Non-text
/// input is never guessed here; the feature states it in its
/// <see cref="AiOperation"/>.
/// </summary>
public static class AiTokenEstimate
{
	private const int CharsPerToken = 3;
	private const int TokensPerMessage = 8;

	public static long FromChars(long chars) => (chars + CharsPerToken - 1) / CharsPerToken;

	public static bool HasNonTextInput(IReadOnlyList<ChatMessage> messages)
		=> messages.Any(m => m.Contents.Any(c => c is DataContent or UriContent or HostedFileContent));

	public static long TextInput(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
	{
		long chars = options?.Instructions?.Length ?? 0;
		long tokens = 0;
		foreach (var message in messages)
		{
			tokens += TokensPerMessage;
			foreach (var content in message.Contents)
			{
				chars += content switch
				{
					TextContent text => text.Text.Length,
					FunctionCallContent call => call.Name.Length + Length(call.Arguments),
					FunctionResultContent result => Length(result.Result),
					_ => 0,
				};
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

/// <summary>
/// The same chokepoint for embeddings (ARC-052 registers the provider): a
/// reservation for the estimated input before the call, the reported usage
/// afterwards. Embedding calls have no output tokens.
/// </summary>
public sealed class BudgetedEmbeddingGenerator(
	IEmbeddingGenerator<string, Embedding<float>> provider, IAiBudget budget, string model)
	: DelegatingEmbeddingGenerator<string, Embedding<float>>(provider)
{
	public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
		IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
	{
		var operation = AiOperation.From(options?.AdditionalProperties)
			?? throw new AiBudgetUnavailableException("The embedding call carries no AiOperation and cannot be charged.");
		if (!string.IsNullOrWhiteSpace(options!.ModelId)
			&& !string.Equals(options.ModelId.Trim(), model, StringComparison.OrdinalIgnoreCase))
			throw new AiBudgetUnavailableException($"Model '{options.ModelId}' is not served by this generator.");
		var callOptions = options.Clone();
		callOptions.ModelId = null;
		callOptions.AdditionalProperties = null;
		var list = values as IReadOnlyList<string> ?? [.. values];
		var estimate = AiTokenEstimate.FromChars(list.Sum(v => (long)v.Length));
		var reservation = await budget.ReserveAsync(
			new AiCall(operation.Feature, operation.OperationId, operation.AccountId, model, estimate, 0), cancellationToken);
		var usage = AiUsage.Unreported;
		try
		{
			var embeddings = await base.GenerateAsync(list, callOptions, cancellationToken);
			if (embeddings.Usage is { } reported)
				usage = new AiUsage(reported.InputTokenCount ?? reported.TotalTokenCount ?? 0, 0);
			return embeddings;
		}
		finally
		{
			await budget.SettleAsync(reservation, usage);
		}
	}
}
