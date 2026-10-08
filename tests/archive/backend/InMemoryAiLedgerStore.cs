using Archive.Backend.Ai;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// Stand-in for <see cref="PostgresAiLedgerStore"/> on the EF in-memory
/// provider, which runs neither SQL nor transactions. It writes the same
/// rows through the context so HTTP tests can read the ledger, and makes
/// each decision atomic with a process-wide gate. The production SQL is
/// tested by the PostgreSQL ledger tests (<c>ARCHIVE_TEST_POSTGRES</c>).
/// </summary>
internal sealed class InMemoryAiLedgerStore(IServiceScopeFactory scopes) : IAiLedgerStore
{
	private readonly SemaphoreSlim gate = new(1, 1);

	/// <summary>Simulates an unreachable database.</summary>
	public bool Broken { get; set; }

	public async Task<AiLedgerDecision> ReserveAsync(AiLedgerReservation r, CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			await using var scope = scopes.CreateAsyncScope();
			var db = Context(scope);
			var spent = await SpentAsync(db, r.YearMonth, cancellationToken);
			if (spent + r.ReserveMicroEur > r.CapMicroEur)
				return new AiLedgerDecision(null, spent);
			if (!await db.AiBudgetMonths.AnyAsync(m => m.YearMonth == r.YearMonth, cancellationToken))
				db.AiBudgetMonths.Add(new AiBudgetMonth { YearMonth = r.YearMonth });
			var entry = await db.AiUsageEntries.FirstOrDefaultAsync(
				e => e.OperationId == r.OperationId && e.Model == r.Model && e.YearMonth == r.YearMonth, cancellationToken);
			if (entry is null)
			{
				entry = new AiUsageEntry
				{
					YearMonth = r.YearMonth, Feature = r.Feature, Model = r.Model, OperationId = r.OperationId,
					AccountId = r.AccountId, CreatedAt = r.Now,
				};
				db.AiUsageEntries.Add(entry);
			}
			entry.Calls++;
			entry.ReservedMicroEur += r.ReserveMicroEur;
			entry.UpdatedAt = r.Now;
			await db.SaveChangesAsync(cancellationToken);
			return new AiLedgerDecision(entry.Id, spent + r.ReserveMicroEur);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<bool> SettleAsync(AiLedgerSettlement s, CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			await using var scope = scopes.CreateAsyncScope();
			var db = Context(scope);
			var entry = await db.AiUsageEntries.FirstOrDefaultAsync(e => e.Id == s.EntryId, cancellationToken);
			if (entry is null)
				return false;
			entry.ReservedMicroEur = Math.Max(0, entry.ReservedMicroEur - s.ReleaseMicroEur);
			entry.InputTokens += s.InputTokens;
			entry.OutputTokens += s.OutputTokens;
			entry.CostMicroEur += s.CostMicroEur;
			entry.UpdatedAt = s.Now;
			await db.SaveChangesAsync(cancellationToken);
			return true;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<long> SpentAsync(string yearMonth, CancellationToken cancellationToken)
	{
		await using var scope = scopes.CreateAsyncScope();
		return await SpentAsync(Context(scope), yearMonth, cancellationToken);
	}

	private ArchiveDbContext Context(AsyncServiceScope scope)
		=> Broken
			? throw new InvalidOperationException("Test: Verbrauchsbuch nicht erreichbar.")
			: scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();

	private static Task<long> SpentAsync(ArchiveDbContext db, string yearMonth, CancellationToken cancellationToken)
		=> db.AiUsageEntries.Where(e => e.YearMonth == yearMonth)
			.SumAsync(e => e.CostMicroEur + e.ReservedMicroEur, cancellationToken);
}
