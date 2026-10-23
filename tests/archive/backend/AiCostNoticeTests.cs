using Archive.Backend.Ai;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-043 (#150): the maintainer-side AI cost review. The thresholds are
/// explicit configuration — an empty list notifies nothing short of the cap
/// — and every level notifies exactly once per month; a raised cap
/// re-enables the notices. Deterministic end to end (architecture §14): no
/// model call, the InMemory ledger store is enough, and the time is a fixed
/// provider. The collecting warning logger stands in for the Azure alert
/// rules that forward the lines to the maintainers.
/// </summary>
public sealed class AiCostNoticeTests
{
	[Fact]
	public async Task MonthEntriesSumPerFeatureAndModel()
	{
		using var harness = await NoticeHarness.CreateAsync(o => { });
		await harness.AddEntriesAsync(
			new AiUsageEntry { YearMonth = "2026-10", Feature = "chat", Model = "test", Calls = 2, CostMicroEur = 400, CreatedAt = Winter, UpdatedAt = Winter },
			new AiUsageEntry { YearMonth = "2026-10", Feature = "chat", Model = "test", Calls = 1, CostMicroEur = 100, ReservedMicroEur = 50, CreatedAt = Winter, UpdatedAt = Winter },
			new AiUsageEntry { YearMonth = "2026-10", Feature = "lied-lesung", Model = "test", Calls = 1, CostMicroEur = 30, CreatedAt = Winter, UpdatedAt = Winter },
			// Another month must not leak in.
			new AiUsageEntry { YearMonth = "2026-09", Feature = "chat", Model = "test", Calls = 1, CostMicroEur = 99_000, CreatedAt = Winter, UpdatedAt = Winter });

		var entries = await harness.Store.MonthEntriesAsync("2026-10", CancellationToken.None);
		Assert.Equal(2, entries.Count);
		var chat = Assert.Single(entries, e => e.Feature == "chat");
		Assert.Equal(3, chat.Calls);
		Assert.Equal(500L, chat.CostMicroEur);
		Assert.Equal(50L, chat.ReservedMicroEur);
		Assert.Equal(30L, Assert.Single(entries, e => e.Feature == "lied-lesung").CostMicroEur);
	}

	[Fact]
	public async Task BelowEveryThresholdNothingIsLoggedAndNothingIsNotified()
	{
		using var harness = await NoticeHarness.CreateAsync(o => { }, seedMicroEur: 900_000);
		var state = await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Equal(0.90m, state.SpentEur);
		Assert.Equal(0, state.NotifiedMicroEur);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Empty(harness.Warnings);
	}

	[Fact]
	public async Task TheHighestReachedThresholdNotifiesOnceWithTheBreakdown()
	{
		using var harness = await NoticeHarness.CreateAsync(
			o => o.NoticeThresholdsEur = "5,12", seedMicroEur: 7_500_000);
		var state = await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Equal([5m, 12m, 15m], state.ThresholdsEur);
		var line = Assert.Single(harness.Warnings);
		Assert.StartsWith("KI-Budget: Schwellwert 5.00 EUR im Monat 2026-10 erreicht (7.50 von 15.00 EUR): ", line);
		Assert.EndsWith("chat 7.50 EUR", line);
		// Once: a second review on the same day stays silent.
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Single(harness.Warnings);
		// The month's row remembers the level, also across processes.
		Assert.Equal(5_000_000L, await harness.NotifiedMicroEurAsync());
	}

	[Fact]
	public async Task AHigherThresholdNotifiesWhenTheSpendPassesIt()
	{
		using var harness = await NoticeHarness.CreateAsync(
			o => o.NoticeThresholdsEur = "5,12", seedMicroEur: 7_500_000);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Single(harness.Warnings, w => w.Contains("Schwellwert 5.00 EUR"));
		// The spend grows; the next review announces the next level.
		await harness.SeedAsync(6_000_000);
		var second = await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Equal(13.50m, second.SpentEur);
		Assert.Single(harness.Warnings, w => w.Contains("Schwellwert 12.00 EUR"));
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Equal(2, harness.Warnings.Count);
	}

	[Fact]
	public async Task TheCapNotifiesOnceEvenWithoutConfiguredThresholds()
	{
		using var harness = await NoticeHarness.CreateAsync(o => { }, seedMicroEur: 15_000_000);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		var line = Assert.Single(harness.Warnings);
		Assert.StartsWith("KI-Budget: Monatsbudget erreicht (2026-10: 15.00 von 15.00 EUR)", line);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Single(harness.Warnings);
	}

	[Fact]
	public async Task ARaisedCapReEnablesTheCapNotice()
	{
		using var harness = await NoticeHarness.CreateAsync(
			o => o.NoticeThresholdsEur = "10", seedMicroEur: 12_000_000);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Single(harness.Warnings, w => w.Contains("Schwellwert 10.00 EUR"));
		// The maintainers raise the hard cap mid-month; the month spends past
		// it and the notice above the remembered level fires again.
		harness.Options.Options.MonthlyCapEur = 25m;
		await harness.SeedAsync(14_000_000);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		Assert.Single(harness.Warnings, w => w.Contains("Monatsbudget erreicht (2026-10: 26.00 von 25.00 EUR)"));
	}

	[Fact]
	public async Task AnUnusableExplicitEntryIsReportedAsAnErrorAndDropped()
	{
		var harness = await NoticeHarness.CreateAsync(
			o => o.NoticeThresholdsEur = "5,bloss,7", seedMicroEur: 12_000_000);
		await harness.Notice.ReviewAsync(CancellationToken.None);
		// The usable levels stay live: 5 and 7 are below the 12 EUR spend; "bloss"
		// is not silently forgotten.
		Assert.Contains(harness.Lines, l => l.Contains("nicht lesbar") && l.Contains("bloss"));
		Assert.Single(harness.Warnings, w => w.Contains("Schwellwert 7.00 EUR"));
	}

	[Fact]
	public void TheBreakdownReadsPerFeatureInvariantly()
	{
		var breakdown = AiCostNotice.Breakdown(
		[
			new AiMonthEntry("chat", "test", 3, 1000, 2000, 4_200_000, 0),
			new AiMonthEntry("lied-lesung", "test", 1, 500, 0, 50_000, 100_000),
		]);
		Assert.Equal("chat 4.20 EUR; lied-lesung 0.15 EUR", breakdown);
	}

	private static readonly DateTimeOffset Winter = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

	/// <summary>One notice test's services: InMemory database, InMemory ledger and a mutable options monitor.</summary>
	private sealed class NoticeHarness : IDisposable
	{
		private readonly ServiceProvider provider;
		private readonly RecordingLogger warnings;

		private NoticeHarness(
			ServiceProvider provider, InMemoryAiLedgerStore store, AiCostNotice notice,
			MutableCapOptions monitor, RecordingLogger warnings)
		{
			this.provider = provider;
			this.warnings = warnings;
			Store = store;
			Notice = notice;
			Options = monitor;
		}

		public InMemoryAiLedgerStore Store { get; }

		public AiCostNotice Notice { get; }

		public MutableCapOptions Options { get; }

		public IReadOnlyList<string> Warnings => warnings.Warnings;

		public IReadOnlyList<string> Lines => warnings.Lines;

		public static async Task<NoticeHarness> CreateAsync(
			Action<AiOptions> configureOptions, long? seedMicroEur = null)
		{
			var collection = new ServiceCollection();
			collection.AddLogging();
			collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			var dbName = $"notice-{Guid.NewGuid():N}";
			var root = new InMemoryDatabaseRoot();
			collection.AddDbContext<ArchiveDbContext>(options =>
				options.UseInMemoryDatabase(dbName, root));
			collection.AddSingleton<InMemoryAiLedgerStore>(sp =>
				new InMemoryAiLedgerStore(sp.GetRequiredService<IServiceScopeFactory>()));
			collection.AddSingleton<IAiLedgerStore>(sp => sp.GetRequiredService<InMemoryAiLedgerStore>());
			collection.AddSingleton<TimeProvider>(new FixedTime(Winter));
			var monitor = new MutableCapOptions();
			monitor.Options.MonthlyCapEur = 15m;
			collection.AddSingleton<IOptionsMonitor<AiOptions>>(monitor);
			collection.AddSingleton(monitor);
			configureOptions(monitor.Options);
			var warnings = new RecordingLogger();
			collection.AddSingleton(warnings);
			collection.AddSingleton<ILogger<AiCostNotice>>(warnings);
			collection.AddScoped<AiCostNotice>();
			var provider = collection.BuildServiceProvider();
			var harness = new NoticeHarness(provider,
				provider.GetRequiredService<InMemoryAiLedgerStore>(),
				provider.GetRequiredService<AiCostNotice>(),
				provider.GetRequiredService<MutableCapOptions>(),
				provider.GetRequiredService<RecordingLogger>());
			if (seedMicroEur is { } spend)
				await harness.SeedAsync(spend);
			return harness;
		}

		/// <summary>One settled chat run's spend; test data, like every seeded row.</summary>
		public async Task SeedAsync(long microEur)
		{
			await using var scope = provider.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.AiUsageEntries.Add(new AiUsageEntry
			{
				YearMonth = "2026-10", Feature = "chat", Model = "test", Calls = 1,
				CostMicroEur = microEur, CreatedAt = Winter, UpdatedAt = Winter,
			});
			await db.SaveChangesAsync();
		}

		public async Task AddEntriesAsync(params AiUsageEntry[] entries)
		{
			await using var scope = provider.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.AiUsageEntries.AddRange(entries);
			await db.SaveChangesAsync();
		}

		public async Task<long> NotifiedMicroEurAsync()
		{
			await using var scope = provider.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			return (await db.AiBudgetMonths.AsNoTracking()
				.FirstOrDefaultAsync(m => m.YearMonth == "2026-10"))?.NotifiedMicroEur ?? 0;
		}

		public void Dispose() => provider.Dispose();
	}

	/// <summary>The month of the harness: fixed, unchanging.</summary>
	private sealed class FixedTime(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}

	/// <summary>The single AiOptions instance every reader sees; tests mutate it directly.</summary>
	public sealed class MutableCapOptions : IOptionsMonitor<AiOptions>
	{
		public AiOptions Options { get; } = new();

		public AiOptions CurrentValue => Options;

		public AiOptions Get(string? name) => Options;

		public IDisposable? OnChange(Action<AiOptions, string?> listener) => null;
	}

	/// <summary>Collects the lines the Azure alert rules would forward.</summary>
	private sealed class RecordingLogger : ILogger<AiCostNotice>
	{
		private readonly List<string> all = [];
		private readonly List<string> warnings = [];

		public IReadOnlyList<string> Lines => all;

		public IReadOnlyList<string> Warnings => warnings;

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			var line = formatter(state, exception);
			if (logLevel == LogLevel.Warning)
				warnings.Add(line);
			all.Add(line);
		}
	}
}
