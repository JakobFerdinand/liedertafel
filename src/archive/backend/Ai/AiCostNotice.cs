using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Ai;

/// <summary>The month's AI spend as a maintainer sees it (ARC-043 #150).</summary>
public sealed record AiCostState(
	string Month, decimal CapEur, IReadOnlyList<decimal> ThresholdsEur, decimal SpentEur,
	long NotifiedMicroEur, IReadOnlyList<AiMonthEntry> Entries);

/// <summary>
/// The maintainer-facing side of the AI budget (ARC-043 #150). The ledger
/// (ARC-022-3) already holds the spend per feature and model; nothing is added
/// to it here. The daily review (the <c>--dispatch-reading</c> sweep,
/// outside maintenance) compares the month against the explicitly configured
/// thresholds and, before the hard cap, the cap itself, and logs a warning
/// exactly once per level per month: the Azure alert rules forward those lines
/// to the maintainers through the shared action group. The read-only state
/// feeds the editor's <c>GET /api/maintenance/ai-budget</c>. Deterministic
/// (architecture §14): no model call, and no destination invented in code —
/// an empty threshold list means nothing is notified.
/// </summary>
public sealed class AiCostNotice(
	ArchiveDbContext db, IAiLedgerStore store, IOptionsMonitor<AiOptions> options,
	TimeProvider time, ILogger<AiCostNotice> logger)
{
	/// <summary>Every configured threshold plus the hard cap, sorted ascending.</summary>
	public IReadOnlyList<decimal> Levels()
	{
		var current = options.CurrentValue;
		var levels = new List<decimal>(current.NoticeThresholdsEur);
		if (!levels.Contains(current.MonthlyCapEur))
			levels.Add(current.MonthlyCapEur);
		return levels.Order().Where(l => l > 0).ToList();
	}

	/// <summary>The state a maintainer view shows: no logging, no persistence.</summary>
	public async Task<AiCostState> StateAsync(CancellationToken cancellationToken)
	{
		var month = AiMonth.Key(time.GetUtcNow());
		var entries = await store.MonthEntriesAsync(month, cancellationToken);
		return new AiCostState(month, options.CurrentValue.MonthlyCapEur, Levels(),
			entries.Sum(e => e.CostMicroEur + e.ReservedMicroEur) / 1_000_000m,
			await NotifiedMicroEurAsync(month, cancellationToken), entries);
	}

	/// <summary>
	/// The monthly threshold review. The highest not-yet-notified level under
	/// the month's spend is announced with the feature breakdown; the month's
	/// row then remembers the level, so every level notifies exactly once —
	/// also the cap (which the refused calls' existing warning already names;
	/// this review re-states it so it is seen even when no further call is
	/// attempted afterwards).
	/// </summary>
	public async Task<AiCostState> ReviewAsync(CancellationToken cancellationToken)
	{
		var state = await StateAsync(cancellationToken);
		var notified = state.NotifiedMicroEur;
		var spentMicroEur = (long)Math.Floor(state.SpentEur * 1_000_000m);
		decimal? target = null;
		foreach (var level in Levels())
		{
			var levelMicroEur = MicroEur(level);
			if (spentMicroEur >= levelMicroEur && notified < levelMicroEur)
				target = level;
		}
		if (target is not { } noticeLevel)
			return state;
		var noticeMicroEur = MicroEur(noticeLevel);
		if (noticeLevel == options.CurrentValue.MonthlyCapEur)
		{
			// Same marker as the refused calls' warning, so one alert rule
			// covers both; the review states it without naming any feature.
			logger.LogWarning(
				"KI-Budget: Monatsbudget erreicht ({Month}: {SpentEur} von {CapEur} EUR)",
				state.Month, FormatEur(state.SpentEur), FormatEur(state.CapEur));
		}
		else
		{
			logger.LogWarning(
				"KI-Budget: Schwellwert {NoticeEur} EUR im Monat {Month} erreicht ({SpentEur} von {CapEur} EUR): {Breakdown}",
				FormatEur(noticeLevel), state.Month, FormatEur(state.SpentEur), FormatEur(state.CapEur), Breakdown(state.Entries));
		}
		await NotifyAsync(state.Month, noticeMicroEur, cancellationToken);
		return state with { NotifiedMicroEur = Math.Max(notified, noticeMicroEur) };
	}

	private async Task<long> NotifiedMicroEurAsync(string month, CancellationToken cancellationToken)
		=> (await db.AiBudgetMonths.AsNoTracking()
			.FirstOrDefaultAsync(m => m.YearMonth == month, cancellationToken))?.NotifiedMicroEur ?? 0;

	private async Task NotifyAsync(string month, long noticeMicroEur, CancellationToken cancellationToken)
	{
		// The month's row may not exist yet (no reservation this month) or be
		// created concurrently by a reservation: GREATEST on conflict keeps
		// the level monotonic; the EF path backs the in-memory tests.
		if (db.Database.IsRelational())
		{
			await db.Database.ExecuteSqlAsync(
				$"""
				INSERT INTO ai_budget_months ("YearMonth", "NotifiedMicroEur")
				VALUES ({month}, {noticeMicroEur})
				ON CONFLICT ("YearMonth")
				DO UPDATE SET "NotifiedMicroEur" = GREATEST(ai_budget_months."NotifiedMicroEur", EXCLUDED."NotifiedMicroEur")
				WHERE ai_budget_months."NotifiedMicroEur" < EXCLUDED."NotifiedMicroEur"
				""", cancellationToken);
			return;
		}
		var row = await db.AiBudgetMonths.FirstOrDefaultAsync(m => m.YearMonth == month, cancellationToken);
		if (row is null)
		{
			db.AiBudgetMonths.Add(new AiBudgetMonth { YearMonth = month, NotifiedMicroEur = noticeMicroEur });
		}
		else if (row.NotifiedMicroEur < noticeMicroEur)
		{
			row.NotifiedMicroEur = noticeMicroEur;
		}
		await db.SaveChangesAsync(cancellationToken);
	}

	/// <summary>"chat 0.42 EUR; noten-lesung 0.05 EUR" — the alert mail's per-feature picture, invariantly formatted.</summary>
	internal static string Breakdown(IReadOnlyList<AiMonthEntry> entries)
		=> string.Join("; ", entries.Select(e =>
			$"{e.Feature} {FormatEur((e.CostMicroEur + e.ReservedMicroEur) / 1_000_000m)} EUR"));

	private static long MicroEur(decimal eur) => (long)Math.Floor(eur * 1_000_000m);

	/// <summary>Two decimals, invariant: telemetry readability over locale.</summary>
	private static string FormatEur(decimal eur) => eur.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

public static class AiNoticeServiceCollectionExtensions
{
	/// <summary>
	/// Registers the maintainer-facing AI cost review (ARC-043 #150) with hosts
	/// that already registered the archive database and the AI gateway.
	/// </summary>
	public static IServiceCollection AddAiCostNotice(this IServiceCollection services)
		=> services.AddScoped<AiCostNotice>();
}
