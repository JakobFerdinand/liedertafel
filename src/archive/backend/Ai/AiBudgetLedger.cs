using System.Diagnostics.Metrics;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Ai;

/// <summary>
/// The database-backed AI budget (ARC-022-3). The ledger rows are the only
/// source of truth for the month's spend; nothing is kept in memory, so the
/// cap holds across restarts and replicas.
/// <para>
/// Every write reads the month's <see cref="AiBudgetMonth"/> row, does its
/// work and increments the row's version in the same transaction. The version
/// is an optimistic concurrency token: of two writers that read the same
/// version only one commits, the other reads again. A reservation therefore
/// always checks the cap against a sum no concurrent writer has changed.
/// </para>
/// <para>
/// Each operation uses its own scope and context, never the request's: a
/// reservation must be durable before the provider is called, independent of
/// whatever the request has pending.
/// </para>
/// </summary>
public sealed class AiBudgetLedger(
	IServiceScopeFactory scopes, TimeProvider time, IOptionsMonitor<AiOptions> options, ILogger<AiBudgetLedger> logger)
	: IAiBudget
{
	/// <summary>Optimistic retries before a writer gives up (and a reservation is refused).</summary>
	private const int MaxAttempts = 25;

	private static readonly Meter Meter = new(Microsoft.Extensions.Hosting.Extensions.AiTelemetryName);

	private static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
		"archive.ai.tokens", "{token}", "Tokens charged to the AI budget.");

	private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
		"archive.ai.cost", "EUR", "Settled AI cost.");

	private static readonly Counter<long> Refusals = Meter.CreateCounter<long>(
		"archive.ai.budget.refusals", "{call}", "AI calls refused before reaching the provider.");

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
		var reserve = CostMicroEur(price, Math.Max(0, call.EstimatedInputTokens), Math.Max(0, call.MaxOutputTokens));
		var cap = CapMicroEur(current);
		var month = AiMonth.Key(time.GetUtcNow());
		try
		{
			return await WriteAsync(month, async db =>
			{
				var committed = await SpentAsync(db, month, cancellationToken);
				if (committed + reserve > cap)
				{
					Refuse(call, "cap");
					Record(committed, cap);
					logger.LogWarning(
						"KI-Budget: Monatsbudget erreicht ({Month}: {SpentEur} von {CapEur} EUR); Aufruf für {Feature} abgelehnt.",
						month, Eur(committed), Eur(cap), call.Feature);
					throw new AiBudgetExceededException(month);
				}
				var now = time.GetUtcNow();
				var entry = await db.AiUsageEntries.FirstOrDefaultAsync(e =>
					e.OperationId == call.OperationId && e.Model == call.Model && e.YearMonth == month, cancellationToken);
				if (entry is null)
				{
					entry = new AiUsageEntry
					{
						YearMonth = month,
						Feature = call.Feature,
						Model = call.Model,
						OperationId = call.OperationId,
						AccountId = call.AccountId,
						CreatedAt = now,
					};
					db.AiUsageEntries.Add(entry);
				}
				entry.Calls++;
				entry.ReservedMicroEur += reserve;
				entry.UpdatedAt = now;
				Record(committed + reserve, cap);
				return new AiReservation(entry.Id, month, call.Feature, call.Model, reserve);
			}, cancellationToken);
		}
		catch (Exception ex) when (ex is not AiBudgetExceededException and not OperationCanceledException)
		{
			// Fail closed: without a durable reservation no provider call happens.
			Refuse(call, "ledger_unavailable");
			logger.LogError(
				"KI-Budget: Das Verbrauchsbuch ist nicht erreichbar ({ExceptionType}); Aufruf für {Feature} abgelehnt.",
				ex.GetType().Name, call.Feature);
			throw new AiBudgetUnavailableException("The AI usage ledger is unavailable.", ex);
		}
	}

	public async Task SettleAsync(AiReservation reservation, AiUsage usage)
	{
		var input = Math.Max(0, usage.InputTokens);
		var output = Math.Max(0, usage.OutputTokens);
		// A price that vanished since the reservation keeps the reserved amount as the cost.
		var cost = options.CurrentValue.FindPrice(reservation.Model) is { } price
			? CostMicroEur(price, input, output)
			: reservation.ReservedMicroEur;
		try
		{
			await WriteAsync(reservation.YearMonth, async db =>
			{
				var entry = await db.AiUsageEntries.FirstAsync(e => e.Id == reservation.EntryId, CancellationToken.None);
				entry.ReservedMicroEur = Math.Max(0, entry.ReservedMicroEur - reservation.ReservedMicroEur);
				entry.InputTokens += input;
				entry.OutputTokens += output;
				entry.CostMicroEur += cost;
				entry.UpdatedAt = time.GetUtcNow();
				return true;
			}, CancellationToken.None);
		}
		catch (Exception ex)
		{
			// The reservation stays in the ledger at its worst case, so the
			// month is over- rather than under-counted.
			logger.LogError(
				"KI-Budget: Verbrauch für {Feature} konnte nicht verbucht werden ({ExceptionType}); die Reservierung von {ReservedEur} EUR bleibt bestehen.",
				reservation.Feature, ex.GetType().Name, Eur(reservation.ReservedMicroEur));
			return;
		}
		var tags = new KeyValuePair<string, object?>[]
		{
			new("archive.ai.feature", reservation.Feature), new("gen_ai.request.model", reservation.Model),
		};
		Tokens.Add(input, [.. tags, new("gen_ai.token.type", "input")]);
		Tokens.Add(output, [.. tags, new("gen_ai.token.type", "output")]);
		Cost.Add((double)Eur(cost), tags);
		logger.LogInformation(
			"KI-Budget: {Feature} mit {Model} verbucht: {InputTokens} Eingabe-, {OutputTokens} Ausgabe-Token, {CostEur} EUR ({Month}).",
			reservation.Feature, reservation.Model, input, output, Eur(cost), reservation.YearMonth);
	}

	public async Task<AiBudgetStatus> GetStatusAsync(CancellationToken cancellationToken)
	{
		var month = AiMonth.Key(time.GetUtcNow());
		await using var scope = scopes.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		return new AiBudgetStatus(month, await SpentAsync(db, month, cancellationToken), CapMicroEur(options.CurrentValue));
	}

	/// <summary>
	/// Runs one ledger write for a month under the month's version token and
	/// repeats it when a concurrent writer committed first.
	/// </summary>
	private async Task<T> WriteAsync<T>(string month, Func<ArchiveDbContext, Task<T>> write, CancellationToken cancellationToken)
	{
		for (var attempt = 1; ; attempt++)
		{
			await using var scope = scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			try
			{
				var row = await db.AiBudgetMonths.FirstOrDefaultAsync(m => m.YearMonth == month, cancellationToken);
				if (row is null)
				{
					// Two first writers of a month collide on the primary key; the loser retries.
					row = new AiBudgetMonth { YearMonth = month };
					db.AiBudgetMonths.Add(row);
				}
				var result = await write(db);
				row.Version++;
				await db.SaveChangesAsync(cancellationToken);
				return result;
			}
			catch (Exception ex) when (attempt < MaxAttempts && IsConflict(ex))
			{
				await Task.Delay(Random.Shared.Next(2, 10 * attempt), cancellationToken);
			}
		}
	}

	/// <summary>
	/// A lost race: the version moved, or a concurrent writer inserted the
	/// same month or operation row first (PostgreSQL unique violation 23505;
	/// the in-memory test provider reports a duplicate key as ArgumentException).
	/// </summary>
	private static bool IsConflict(Exception ex) => ex switch
	{
		DbUpdateConcurrencyException => true,
		DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } } => true,
		ArgumentException => true,
		_ => false,
	};

	private static Task<long> SpentAsync(ArchiveDbContext db, string month, CancellationToken cancellationToken)
		=> db.AiUsageEntries.Where(e => e.YearMonth == month)
			.SumAsync(e => e.CostMicroEur + e.ReservedMicroEur, cancellationToken);

	/// <summary>EUR per million tokens times tokens is micro-EUR; rounded up so usage is never free.</summary>
	private static long CostMicroEur(AiModelPrice price, long inputTokens, long outputTokens)
		=> (long)Math.Ceiling(inputTokens * price.InputPricePerMillionEur + outputTokens * price.OutputPricePerMillionEur);

	private static long CapMicroEur(AiOptions current) => (long)Math.Floor(current.MonthlyCapEur * 1_000_000m);

	private static decimal Eur(long microEur) => microEur / 1_000_000m;

	private static void Refuse(AiCall call, string reason)
		=> Refusals.Add(1, new("archive.ai.feature", call.Feature), new("gen_ai.request.model", call.Model),
			new KeyValuePair<string, object?>("archive.ai.refusal.reason", reason));

	private static void Record(long spentMicroEur, long capMicroEur)
	{
		MonthSpend.Record((double)Eur(spentMicroEur));
		MonthCap.Record((double)Eur(capMicroEur));
	}
}
