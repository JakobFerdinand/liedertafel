using Archive.Backend.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-022-3: the chat-client middleware in front of the provider, over a
/// recording budget. Prices do not matter here; the ledger tests cover them.
/// </summary>
public sealed class BudgetedChatClientTests
{
	[Fact]
	public async Task RefusedReservationNeverReachesTheProvider()
	{
		var provider = new StubProvider();
		var budget = new RecordingBudget { Refuse = new AiBudgetExceededException("2026-10") };
		var client = Client(provider, budget);

		await Assert.ThrowsAsync<AiBudgetExceededException>(() => DrainAsync(client, Options()));

		Assert.Equal(0, provider.Calls);
		Assert.Empty(budget.Settled);
	}

	[Fact]
	public async Task CallWithoutAnOperationIsRefusedBeforeBudgetAndProvider()
	{
		var provider = new StubProvider();
		var budget = new RecordingBudget();
		var client = Client(provider, budget);

		await Assert.ThrowsAsync<AiBudgetUnavailableException>(() => DrainAsync(client, new AiChatOptions()));
		await Assert.ThrowsAsync<AiBudgetUnavailableException>(() => DrainAsync(client, null));

		Assert.Equal(0, provider.Calls);
		Assert.Empty(budget.Reserved);
	}

	[Fact]
	public async Task CompletedCallSettlesTheReportedUsageAndBoundsTheOutput()
	{
		var provider = new StubProvider
		{
			Updates =
			[
				new ChatResponseUpdate(ChatRole.Assistant, "Antwort"),
				Usage(40, 5), Usage(120, 30),
			],
		};
		var budget = new RecordingBudget();
		var operation = AiOperation.Start("chat", Guid.NewGuid());
		var options = new AiChatOptions { MaxOutputTokens = 50_000 };
		operation.AttachTo(options);

		await DrainAsync(Client(provider, budget, maxOutput: 700), options);

		var call = Assert.Single(budget.Reserved);
		Assert.Equal(("chat", operation.OperationId, operation.AccountId, "stub-model", 700),
			(call.Feature, call.OperationId, call.AccountId, call.Model, call.MaxOutputTokens));
		Assert.True(call.EstimatedInputTokens > 0);
		// The largest report of the call, not the sum of repeated reports.
		Assert.Equal(new AiUsage(120, 30), Assert.Single(budget.Settled));
		var sent = Assert.Single(provider.OptionsSeen);
		Assert.Equal(700, sent!.MaxOutputTokens);
		// The marker stays inside the application.
		Assert.Null(AiOperation.From(sent));
		Assert.Null(sent.AdditionalProperties);
		// The caller's options are not modified.
		Assert.Equal(50_000, options.MaxOutputTokens);
	}

	[Fact]
	public async Task CallThatFailsMidwayIsStillCharged()
	{
		// 30 characters arrived before the failure and no usage report: the
		// call is charged its input estimate and 10 output tokens (3 chars each).
		var provider = new StubProvider
		{
			Updates = [new ChatResponseUpdate(ChatRole.Assistant, new string('x', 30))],
			FailAfterUpdates = new HttpRequestException("Verbindung verloren"),
		};
		var budget = new RecordingBudget();

		await Assert.ThrowsAsync<HttpRequestException>(() => DrainAsync(Client(provider, budget), Options()));

		var settled = Assert.Single(budget.Settled);
		Assert.Equal(Assert.Single(budget.Reserved).EstimatedInputTokens, settled.InputTokens);
		Assert.Equal(10, settled.OutputTokens);
	}

	[Fact]
	public async Task AbandonedStreamIsStillCharged()
	{
		// A disconnected client stops reading after the first update.
		var provider = new StubProvider
		{
			Updates =
			[
				new ChatResponseUpdate(ChatRole.Assistant, "abc"),
				new ChatResponseUpdate(ChatRole.Assistant, "never read"),
			],
		};
		var budget = new RecordingBudget();
		var client = Client(provider, budget);

		await foreach (var _ in client.GetStreamingResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], Options()))
			break;

		Assert.Equal(1, Assert.Single(budget.Settled).OutputTokens);
	}

	[Fact]
	public async Task CancelledCallIsStillCharged()
	{
		var provider = new StubProvider { WaitForCancellation = true };
		var budget = new RecordingBudget();
		using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrainAsync(Client(provider, budget), Options(), cancel.Token));

		Assert.Single(budget.Settled);
	}

	[Fact]
	public async Task NonStreamingCallIsReservedAndSettledToo()
	{
		var provider = new StubProvider { Updates = [new ChatResponseUpdate(ChatRole.Assistant, "Antwort"), Usage(9, 4)] };
		var budget = new RecordingBudget();

		await Client(provider, budget).GetResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], Options());

		Assert.Single(budget.Reserved);
		Assert.Equal(new AiUsage(9, 4), Assert.Single(budget.Settled));
	}

	/// <summary>
	/// The provider seam (<see cref="IChatClient"/>) may only be consumed by
	/// the gateway; any other constructor taking it would bypass the cap.
	/// </summary>
	[Fact]
	public void OnlyTheGatewayTakesTheRawProviderClient()
	{
		var offenders = typeof(AiGateway).Assembly.GetTypes()
			.Where(t => t != typeof(AiGateway) && t != typeof(BudgetedChatClient) && !typeof(IChatClient).IsAssignableFrom(t))
			.Where(t => t.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
				.Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IChatClient))))
			.Select(t => t.FullName)
			.ToList();
		Assert.Empty(offenders);
	}

	private static ChatResponseUpdate Usage(int input, int output) => new()
	{
		Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })],
	};

	private static AiChatOptions Options()
	{
		var options = new AiChatOptions();
		AiOperation.Start("chat", null).AttachTo(options);
		return options;
	}

	private static BudgetedChatClient Client(IChatClient provider, IAiBudget budget, int maxOutput = 2000)
		=> new(provider, budget, new StaticOptions(new AiOptions { MaxOutputTokensPerCall = maxOutput }), "stub-model");

	private static async Task DrainAsync(IChatClient client, AiChatOptions? options, CancellationToken token = default)
	{
		await foreach (var _ in client.GetStreamingResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], options, token))
		{
		}
	}

	private sealed class StaticOptions(AiOptions value) : IOptionsMonitor<AiOptions>
	{
		public AiOptions CurrentValue => value;
		public AiOptions Get(string? name) => value;
		public IDisposable? OnChange(Action<AiOptions, string?> listener) => null;
	}

	private sealed class RecordingBudget : IAiBudget
	{
		public Exception? Refuse { get; init; }
		public List<AiCall> Reserved { get; } = [];
		public List<AiUsage> Settled { get; } = [];

		public Task<AiReservation> ReserveAsync(AiCall call, CancellationToken cancellationToken)
		{
			if (Refuse is not null)
				throw Refuse;
			Reserved.Add(call);
			return Task.FromResult(new AiReservation(Guid.NewGuid(), "2026-10", call.Feature, call.Model, 1));
		}

		public Task SettleAsync(AiReservation reservation, AiUsage usage)
		{
			Settled.Add(usage);
			return Task.CompletedTask;
		}

		public Task<AiBudgetStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	private sealed class StubProvider : IChatClient
	{
		public int Calls { get; private set; }
		public List<ChatResponseUpdate> Updates { get; init; } = [];
		public Exception? FailAfterUpdates { get; init; }
		public bool WaitForCancellation { get; init; }
		public List<AiChatOptions?> OptionsSeen { get; } = [];

		public async Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			var updates = new List<ChatResponseUpdate>();
			await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
				updates.Add(update);
			return updates.ToChatResponse();
		}

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			Calls++;
			OptionsSeen.Add(options);
			if (WaitForCancellation)
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			foreach (var update in Updates)
			{
				await Task.Yield();
				yield return update;
			}
			if (FailAfterUpdates is not null)
				throw FailAfterUpdates;
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}
}
