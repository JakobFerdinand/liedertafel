using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Ai;

/// <summary>What a reservation writes: one admitted call of an operation.</summary>
public sealed record AiLedgerReservation(
	string YearMonth, string Feature, string Model, Guid OperationId, Guid? AccountId,
	long ReserveMicroEur, long CapMicroEur, DateTimeOffset Now);

/// <param name="EntryId">The ledger row of the operation, or null when the call was refused at the cap.</param>
/// <param name="SpentMicroEur">The month's spend including open reservations, after this decision.</param>
public sealed record AiLedgerDecision(Guid? EntryId, long SpentMicroEur);

/// <summary>What a settlement changes on the operation's row; all amounts are relative.</summary>
public sealed record AiLedgerSettlement(
	Guid EntryId, long ReleaseMicroEur, long InputTokens, long OutputTokens, long CostMicroEur, DateTimeOffset Now);

/// <summary>
/// Storage of the AI usage ledger. <see cref="AiBudgetLedger"/> owns prices,
/// the cap and the month; the store owns atomicity. Production uses
/// <see cref="PostgresAiLedgerStore"/>; tests that run on the EF in-memory
/// provider swap in a stand-in, because the production store is SQL.
/// </summary>
public interface IAiLedgerStore
{
	/// <summary>
	/// Atomically: sum the month, and if the sum plus the reservation stays
	/// within the cap, add the reservation to the operation's row.
	/// </summary>
	Task<AiLedgerDecision> ReserveAsync(AiLedgerReservation reservation, CancellationToken cancellationToken);

	/// <summary>Applies a settlement; false when the row no longer exists.</summary>
	Task<bool> SettleAsync(AiLedgerSettlement settlement, CancellationToken cancellationToken);

	Task<long> SpentAsync(string yearMonth, CancellationToken cancellationToken);
}

/// <summary>
/// The ledger on PostgreSQL. A reservation runs in one transaction that locks
/// the month's row in <c>ai_budget_months</c> (<c>SELECT … FOR UPDATE</c>),
/// so reservations of a month queue behind each other on every replica and
/// each one sums a month no other reservation is changing. A settlement is a
/// single relative <c>UPDATE</c> of its own row: it takes no month lock and
/// cannot lose against another writer. Settlements only ever lower a month's
/// sum below what was reserved, so they need not queue with reservations.
/// Each call uses its own scope and connection, never the request's.
/// </summary>
public sealed class PostgresAiLedgerStore(IServiceScopeFactory scopes) : IAiLedgerStore
{
	public async Task<AiLedgerDecision> ReserveAsync(AiLedgerReservation r, CancellationToken cancellationToken)
	{
		await using var scope = scopes.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
		// A stuck lock holder must not hang requests: waiting ends as "ledger unavailable".
		await db.Database.ExecuteSqlAsync($"SET LOCAL lock_timeout = '5s'", cancellationToken);
		// Two first callers of a month meet on the primary key; the second
		// waits for the first to commit and then inserts nothing.
		await db.Database.ExecuteSqlAsync(
			$"""INSERT INTO ai_budget_months ("YearMonth") VALUES ({r.YearMonth}) ON CONFLICT DO NOTHING""", cancellationToken);
		await db.Database.ExecuteSqlAsync(
			$"""SELECT 1 FROM ai_budget_months WHERE "YearMonth" = {r.YearMonth} FOR UPDATE""", cancellationToken);
		var spent = await SpentAsync(db, r.YearMonth, cancellationToken);
		if (spent + r.ReserveMicroEur > r.CapMicroEur)
		{
			await transaction.RollbackAsync(CancellationToken.None);
			return new AiLedgerDecision(null, spent);
		}
		var id = Guid.CreateVersion7();
		var rows = await db.Database.SqlQuery<Guid>(
			$"""
			INSERT INTO ai_usage_entries
				("Id", "YearMonth", "Feature", "Model", "OperationId", "AccountId", "Calls",
				 "InputTokens", "OutputTokens", "CostMicroEur", "ReservedMicroEur", "CreatedAt", "UpdatedAt")
			VALUES ({id}, {r.YearMonth}, {r.Feature}, {r.Model}, {r.OperationId}, {r.AccountId}, 1,
				 0, 0, 0, {r.ReserveMicroEur}, {r.Now}, {r.Now})
			ON CONFLICT ("OperationId", "Model", "YearMonth") DO UPDATE SET
				"Calls" = ai_usage_entries."Calls" + 1,
				"ReservedMicroEur" = ai_usage_entries."ReservedMicroEur" + EXCLUDED."ReservedMicroEur",
				"UpdatedAt" = EXCLUDED."UpdatedAt"
			RETURNING "Id" AS "Value"
			""").ToListAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return new AiLedgerDecision(rows.Single(), spent + r.ReserveMicroEur);
	}

	public async Task<bool> SettleAsync(AiLedgerSettlement s, CancellationToken cancellationToken)
	{
		await using var scope = scopes.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var changed = await db.Database.ExecuteSqlAsync(
			$"""
			UPDATE ai_usage_entries SET
				"ReservedMicroEur" = GREATEST(0, "ReservedMicroEur" - {s.ReleaseMicroEur}),
				"InputTokens" = "InputTokens" + {s.InputTokens},
				"OutputTokens" = "OutputTokens" + {s.OutputTokens},
				"CostMicroEur" = "CostMicroEur" + {s.CostMicroEur},
				"UpdatedAt" = {s.Now}
			WHERE "Id" = {s.EntryId}
			""", cancellationToken);
		return changed == 1;
	}

	public async Task<long> SpentAsync(string yearMonth, CancellationToken cancellationToken)
	{
		await using var scope = scopes.CreateAsyncScope();
		return await SpentAsync(scope.ServiceProvider.GetRequiredService<ArchiveDbContext>(), yearMonth, cancellationToken);
	}

	private static async Task<long> SpentAsync(ArchiveDbContext db, string yearMonth, CancellationToken cancellationToken)
		=> (await db.Database.SqlQuery<long>(
			$"""
			SELECT COALESCE(SUM("CostMicroEur" + "ReservedMicroEur"), 0)::bigint AS "Value"
			FROM ai_usage_entries WHERE "YearMonth" = {yearMonth}
			""").ToListAsync(cancellationToken)).Single();
}
