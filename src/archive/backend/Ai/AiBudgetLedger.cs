using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Ai;

/// <summary>
/// The AI budget over the usage ledger (ARC-022-3). It knows prices, the cap
/// and the budget month; <see cref="IAiLedgerStore"/> makes each decision
/// atomic. The ledger rows are the only source of truth for the month's
/// spend: nothing is kept in memory, so the cap holds across restarts and
/// replicas. The guarantee: settled cost plus open reservations plus the
/// worst case of a newly admitted call never exceeds the cap.
/// </summary>
public sealed class AiBudgetLedger(
	IAiLedgerStore store, TimeProvider time, IOptionsMonitor<AiOptions> options, ILogger<AiBudgetLedger> logger)
	: IAiBudget
{
	private static readonly TimeSpan[] SettleRetryDelays =
		[TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1600)];

	private static readonly Meter Meter = new(Microsoft.Extensions.Hosting.Extensions.AiTelemetryName);

	private static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
		"archive.ai.tokens", "{token}", "Tokens charged to the AI budget.");

	private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
		"archive.ai.cost", "EUR", "Settled AI cost.");

	private static readonly Counter<long> Refusals = Meter.CreateCounter<long>(
		"archive.ai.budget.refusals", "{call}", "AI calls refused before reaching the provider.");

	private static readonly Counter<long> SettlementFailures = Meter.CreateCounter<long>(
		"archive.ai.budget.settlement_failures", "{call}", "Settlements that could not be written; their reservation stays counted.");

	private static readonly Gauge<double> MonthSpend = Meter.CreateGauge<double>(
		"archive.ai.budget.month_spend", "EUR", "Spend of the current budget month including open reservations.");

	private static readonly Gauge<double> MonthCap = Meter.CreateGauge<double>(
		"archive.ai.budget.month_cap", "EUR", "Configured monthly AI cap.");

	public async Task<AiReservation> ReserveAsync(AiCall call, CancellationToken cancellationToken)
	{
		var current = options.CurrentValue;
		if (current.FindPrice(call.Model) is not { } price)
		{
			Refuse(call, "unpriced_model");
			logger.LogError(
				"KI-Budget: Für das Modell {Model} ist kein Preis konfiguriert; der Aufruf ({Feature}) wurde abgelehnt.",
				call.Model, call.Feature);
			throw new AiBudgetUnavailableException($"No price is configured for model '{call.Model}'.");
		}
		var reserve = WorstCaseMicroEur(price, call);
		var cap = CapMicroEur(current);
		var month = AiMonth.Key(time.GetUtcNow());
		AiLedgerDecision decision;
		try
		{
			decision = await store.ReserveAsync(new AiLedgerReservation(
				month, call.Feature, call.Model, call.OperationId, call.AccountId, reserve, cap, time.GetUtcNow()),
				cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Fail closed: without a durable reservation no provider call happens.
			Refuse(call, "ledger_unavailable");
			logger.LogError(
				"KI-Budget: Das Verbrauchsbuch ist nicht erreichbar ({ExceptionType}); Aufruf für {Feature} abgelehnt.",
				ex.GetType().Name, call.Feature);
			throw new AiBudgetUnavailableException("The AI usage ledger is unavailable.", ex);
		}
		MonthSpend.Record((double)Eur(decision.SpentMicroEur));
		MonthCap.Record((double)Eur(cap));
		if (decision.EntryId is not { } entryId)
		{
			Refuse(call, "cap");
			logger.LogWarning(
				"KI-Budget: Monatsbudget erreicht ({Month}: {SpentEur} von {CapEur} EUR); Aufruf für {Feature} abgelehnt.",
				month, Eur(decision.SpentMicroEur), Eur(cap), call.Feature);
			throw new AiBudgetExceededException(month);
		}
		return new AiReservation(entryId, month, call.Feature, call.Model, reserve,
			Math.Max(0, call.EstimatedInputTokens), Math.Max(0, call.MaxOutputTokens));
	}

	public async Task SettleAsync(AiReservation reservation, AiUsage usage)
	{
		// Without a usage report nobody knows what the provider charged: the
		// call costs its full reservation. A price that vanished since the
		// reservation does the same.
		long input = reservation.EstimatedInputTokens, output = reservation.MaxOutputTokens, cost = reservation.ReservedMicroEur;
		if (usage.IsReported && options.CurrentValue.FindPrice(reservation.Model) is { } price)
		{
			input = Math.Max(0, usage.InputTokens);
			output = Math.Max(0, usage.OutputTokens);
			cost = CostMicroEur(price, input, output);
		}
		var settlement = new AiLedgerSettlement(reservation.EntryId, reservation.ReservedMicroEur, input, output, cost, time.GetUtcNow());
		string? failure = null;
		for (var attempt = 0; ; attempt++)
		{
			try
			{
				failure = await store.SettleAsync(settlement, CancellationToken.None) ? null : "RowMissing";
				break;
			}
			catch (Exception ex)
			{
				failure = ex.GetType().Name;
				if (attempt == SettleRetryDelays.Length)
					break;
				await Task.Delay(SettleRetryDelays[attempt]);
			}
		}
		var tags = new KeyValuePair<string, object?>[]
		{
			new("archive.ai.feature", reservation.Feature), new("gen_ai.request.model", reservation.Model),
		};
		if (failure is not null)
		{
			// The reservation stays in the ledger at its worst case, so the
			// month is over- rather than under-counted. Never silent.
			SettlementFailures.Add(1, tags);
			logger.LogError(
				"KI-Budget: Verbrauch für {Feature} konnte nicht verbucht werden ({Failure}); die Reservierung von {ReservedEur} EUR bleibt bis Monatsende bestehen.",
				reservation.Feature, failure, Eur(reservation.ReservedMicroEur));
			return;
		}
		Tokens.Add(input, [.. tags, new("gen_ai.token.type", "input")]);
		Tokens.Add(output, [.. tags, new("gen_ai.token.type", "output")]);
		Cost.Add((double)Eur(cost), tags);
		logger.LogInformation(
			"KI-Budget: {Feature} mit {Model} verbucht: {InputTokens} Eingabe-, {OutputTokens} Ausgabe-Token, {CostEur} EUR ({Month}, Verbrauch gemeldet: {Reported}).",
			reservation.Feature, reservation.Model, input, output, Eur(cost), reservation.YearMonth, usage.IsReported);
	}

	public async Task<bool> WouldAdmitAsync(AiCall call, CancellationToken cancellationToken)
	{
		var current = options.CurrentValue;
		if (current.FindPrice(call.Model) is not { } price)
			return false;
		try
		{
			var spent = await store.SpentAsync(AiMonth.Key(time.GetUtcNow()), cancellationToken);
			return spent + WorstCaseMicroEur(price, call) <= CapMicroEur(current);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return false;
		}
	}

	public async Task<AiBudgetStatus> GetStatusAsync(CancellationToken cancellationToken)
	{
		var month = AiMonth.Key(time.GetUtcNow());
		return new AiBudgetStatus(month, await store.SpentAsync(month, cancellationToken), CapMicroEur(options.CurrentValue));
	}

	private static long WorstCaseMicroEur(AiModelPrice price, AiCall call)
		=> CostMicroEur(price, Math.Max(0, call.EstimatedInputTokens), Math.Max(0, call.MaxOutputTokens));

	/// <summary>EUR per million tokens times tokens is micro-EUR; rounded up so usage is never free.</summary>
	private static long CostMicroEur(AiModelPrice price, long inputTokens, long outputTokens)
		=> (long)Math.Ceiling(inputTokens * price.InputPricePerMillionEur + outputTokens * price.OutputPricePerMillionEur);

	private static long CapMicroEur(AiOptions current) => (long)Math.Floor(current.MonthlyCapEur * 1_000_000m);

	private static decimal Eur(long microEur) => microEur / 1_000_000m;

	private static void Refuse(AiCall call, string reason)
		=> Refusals.Add(1, new("archive.ai.feature", call.Feature), new("gen_ai.request.model", call.Model),
			new KeyValuePair<string, object?>("archive.ai.refusal.reason", reason));
}
