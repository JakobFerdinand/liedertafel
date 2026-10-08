using Archive.Backend.Ai;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-022-3: the shared AI budget. Every expectation is stated in millionths
/// of a EUR from the configured prices (1 EUR input, 4 EUR output per million
/// tokens), so one token costs 1 or 4 micro-EUR.
/// </summary>
public sealed class AiBudgetLedgerTests(ITestOutputHelper output)
{
	private const string Model = "test-model";
	private static readonly DateTimeOffset October = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task CallsOfOneOperationAreSummedIntoOneRow()
	{
		await using var harness = await LedgerHarness.CreateAsync();
		var operation = Guid.NewGuid();
		var account = Guid.NewGuid();

		var first = await harness.Budget.ReserveAsync(Call(operation, account), CancellationToken.None);
		await harness.Budget.SettleAsync(first, new AiUsage(1000, 500));
		var second = await harness.Budget.ReserveAsync(Call(operation, account), CancellationToken.None);
		await harness.Budget.SettleAsync(second, new AiUsage(2000, 250));

		var entry = Assert.Single(await harness.EntriesAsync());
		Assert.Equal("2026-10", entry.YearMonth);
		Assert.Equal("chat", entry.Feature);
		Assert.Equal(Model, entry.Model);
		Assert.Equal(account, entry.AccountId);
		Assert.Equal(2, entry.Calls);
		Assert.Equal(3000, entry.InputTokens);
		Assert.Equal(750, entry.OutputTokens);
		Assert.Equal(6000, entry.CostMicroEur);
		Assert.Equal(0, entry.ReservedMicroEur);
	}

	[Fact]
	public async Task ACallThatWouldPassTheCapIsRefusedAndLeavesNoTrace()
	{
		// Cap 0.01 EUR = 10 000 micro-EUR; 9 500 are spent. The worst case of
		// the next call is 100 input + 200 output tokens = 900 micro-EUR.
		await using var harness = await LedgerHarness.CreateAsync(capEur: 0.01m);
		await harness.SeedAsync("2026-10", costMicroEur: 9500);

		var refused = await Assert.ThrowsAsync<AiBudgetExceededException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, estimatedInput: 100, maxOutput: 200), CancellationToken.None));

		Assert.Equal("2026-10", refused.YearMonth);
		Assert.Single(await harness.EntriesAsync());
		var status = await harness.Budget.GetStatusAsync(CancellationToken.None);
		Assert.Equal(new AiBudgetStatus("2026-10", 9500, 10000), status);
		Assert.Equal(500, status.RemainingMicroEur);
		// The month is not "full", yet only a call whose worst case fits the
		// remaining 500 micro-EUR would be admitted: 100 + 4 × 50 = 300 does.
		Assert.False(await harness.Budget.WouldAdmitAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None));
		Assert.True(await harness.Budget.WouldAdmitAsync(Call(Guid.NewGuid(), null, 100, 50), CancellationToken.None));
		Assert.Single(await harness.EntriesAsync());
	}

	[Fact]
	public async Task ACallInFlightHoldsItsWorstCaseUntilItIsSettled()
	{
		// Cap 1 000 micro-EUR; each call may cost up to 100 + 4 × 200 = 900.
		await using var harness = await LedgerHarness.CreateAsync(capEur: 0.001m);
		var first = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);
		Assert.Equal(900, first.ReservedMicroEur);

		await Assert.ThrowsAsync<AiBudgetExceededException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None));

		// The first call really used 50 input and 10 output tokens = 90.
		await harness.Budget.SettleAsync(first, new AiUsage(50, 10));
		var second = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);
		await harness.Budget.SettleAsync(second, new AiUsage(0, 0));

		var status = await harness.Budget.GetStatusAsync(CancellationToken.None);
		Assert.Equal(90, status.SpentMicroEur);
	}

	[Fact]
	public async Task AModelWithoutAPriceIsRefusedBeforeAnythingIsWritten()
	{
		await using var harness = await LedgerHarness.CreateAsync();

		await Assert.ThrowsAsync<AiBudgetUnavailableException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null) with { Model = "unbekannt" }, CancellationToken.None));

		Assert.Empty(await harness.EntriesAsync());
	}

	[Fact]
	public async Task ABrokenLedgerStoreRefusesTheCall()
	{
		// On PostgreSQL the store points at a port nobody listens on.
		await using var harness = await LedgerHarness.CreateAsync(brokenStore: true);

		await Assert.ThrowsAsync<AiBudgetUnavailableException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null), CancellationToken.None));
		Assert.False(await harness.Budget.WouldAdmitAsync(Call(Guid.NewGuid(), null), CancellationToken.None));
	}

	[Fact]
	public async Task TheBudgetMonthIsTheCalendarMonthInViennaAndStartsEmpty()
	{
		// 31 October 2026 23:30 UTC is already 1 November 00:30 in Vienna (CET).
		await using var harness = await LedgerHarness.CreateAsync(capEur: 0.01m,
			now: new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero));
		await harness.SeedAsync("2026-10", costMicroEur: 10000);

		var reservation = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);

		Assert.Equal("2026-11", reservation.YearMonth);
		var status = await harness.Budget.GetStatusAsync(CancellationToken.None);
		Assert.Equal("2026-11", status.YearMonth);
		Assert.Equal(900, status.SpentMicroEur);
	}

	[Fact]
	public async Task AZeroPriceCountsAsUnpriced()
	{
		foreach (var (input, output) in new[] { (0m, 4m), (1m, 0m) })
		{
			await using var harness = await LedgerHarness.CreateAsync(inputPrice: input, outputPrice: output);
			await Assert.ThrowsAsync<AiBudgetUnavailableException>(() =>
				harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null), CancellationToken.None));
			Assert.Empty(await harness.EntriesAsync());
		}
	}

	[Fact]
	public async Task ACallWithoutAUsageReportIsSettledAtItsFullReservation()
	{
		await using var harness = await LedgerHarness.CreateAsync();
		var reservation = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);

		await harness.Budget.SettleAsync(reservation, AiUsage.Unreported);

		var entry = Assert.Single(await harness.EntriesAsync());
		Assert.Equal(900, entry.CostMicroEur);
		Assert.Equal(0, entry.ReservedMicroEur);
		Assert.Equal(100, entry.InputTokens);
		Assert.Equal(200, entry.OutputTokens);
	}

	[Fact]
	public async Task SettlingAfterTheMonthRolledOverChargesTheMonthOfTheReservation()
	{
		// 22:59 UTC on 31 October is 23:59 in Vienna; two minutes later it is November.
		await using var harness = await LedgerHarness.CreateAsync(now: new DateTimeOffset(2026, 10, 31, 22, 59, 0, TimeSpan.Zero));
		var reservation = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);
		harness.Time.Now = new DateTimeOffset(2026, 10, 31, 23, 1, 0, TimeSpan.Zero);

		await harness.Budget.SettleAsync(reservation, new AiUsage(50, 10));

		var entry = Assert.Single(await harness.EntriesAsync());
		Assert.Equal(("2026-10", 90L, 0L), (entry.YearMonth, entry.CostMicroEur, entry.ReservedMicroEur));
		var november = await harness.Budget.GetStatusAsync(CancellationToken.None);
		Assert.Equal(("2026-11", 0L), (november.YearMonth, november.SpentMicroEur));
	}

	/// <summary>
	/// The race between concurrent callers at the cap. Needs real PostgreSQL
	/// (<c>ARCHIVE_TEST_POSTGRES</c>): the ledger's locking is SQL.
	/// </summary>
	[Fact]
	[Trait("Category", "PostgresLedger")]
	public async Task ParallelCallersNeverReserveMoreThanTheCapOnPostgres()
	{
		if (!LedgerHarness.PostgresConfigured(output))
			return;
		await using var harness = await LedgerHarness.CreateAsync(capEur: 0.005m);
		await AssertParallelReservationsStayUnderCapAsync(harness, callers: 24, capMicroEur: 5000, perCallMicroEur: 900);

		// A second "replica" (its own service provider and connection pool)
		// sees the same exhausted month.
		await using var replica = await LedgerHarness.CreateAsync(capEur: 0.005m, sameDatabaseAs: harness);
		await Assert.ThrowsAsync<AiBudgetExceededException>(() =>
			replica.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None));
	}

	/// <summary>
	/// Far from the cap nothing may be refused or lost however many callers
	/// queue: 32 workers each run ten reserve → 0–30 ms → settle rounds.
	/// </summary>
	[Fact]
	[Trait("Category", "PostgresLedger")]
	public async Task ContendedCallersFarFromTheCapLoseNothingOnPostgres()
	{
		if (!LedgerHarness.PostgresConfigured(output))
			return;
		const int workers = 32, rounds = 10;
		await using var harness = await LedgerHarness.CreateAsync();
		var refused = 0;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
		{
			for (var round = 0; round < rounds; round++)
			{
				try
				{
					var reservation = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);
					await Task.Delay(Random.Shared.Next(0, 31));
					await harness.Budget.SettleAsync(reservation, new AiUsage(50, 10));
				}
				catch (Exception ex) when (ex is AiBudgetExceededException or AiBudgetUnavailableException)
				{
					Interlocked.Increment(ref refused);
				}
			}
		})));
		var entries = await harness.EntriesAsync();
		var settled = entries.Count(e => e.ReservedMicroEur == 0 && e.CostMicroEur == 90);
		output.WriteLine($"contention: {workers} workers × {rounds} rounds in {clock.ElapsedMilliseconds} ms: {refused} refused, "
			+ $"{entries.Count} rows, {entries.Count - settled} settlements lost, reserved {entries.Sum(e => e.ReservedMicroEur)}, cost {entries.Sum(e => e.CostMicroEur)}");
		Assert.Equal(0, refused);
		Assert.Equal(workers * rounds, entries.Count);
		Assert.Equal(0, entries.Sum(e => e.ReservedMicroEur));
		Assert.Equal(workers * rounds * 90L, entries.Sum(e => e.CostMicroEur));
		Assert.Equal(workers * rounds * 50L, entries.Sum(e => e.InputTokens));
	}

	[Fact]
	[Trait("Category", "PostgresLedger")]
	public async Task TwoReplicasOpeningANewMonthBothGetThroughOnPostgres()
	{
		if (!LedgerHarness.PostgresConfigured(output))
			return;
		await using var first = await LedgerHarness.CreateAsync();
		await using var second = await LedgerHarness.CreateAsync(sameDatabaseAs: first);
		using var start = new SemaphoreSlim(0);
		var calls = new[] { first, second, first, second, first, second, first, second }.Select(async replica =>
		{
			await start.WaitAsync();
			return await replica.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);
		}).ToList();
		start.Release(calls.Count);

		var reservations = await Task.WhenAll(calls);

		Assert.All(reservations, r => Assert.Equal("2026-10", r.YearMonth));
		Assert.Equal(8 * 900L, (await first.Budget.GetStatusAsync(CancellationToken.None)).SpentMicroEur);
		Assert.Equal(1, await first.MonthRowsAsync());
	}

	private async Task AssertParallelReservationsStayUnderCapAsync(
		LedgerHarness harness, int callers, long capMicroEur, long perCallMicroEur)
	{
		using var start = new SemaphoreSlim(0);
		var attempts = Enumerable.Range(0, callers).Select(async _ =>
		{
			await start.WaitAsync();
			try
			{
				return (Reservation: await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None),
					Outcome: "admitted");
			}
			catch (AiBudgetExceededException)
			{
				return (Reservation: (AiReservation?)null, Outcome: "cap");
			}
			catch (AiBudgetUnavailableException)
			{
				// Lost every optimistic retry: refused, which is the safe side.
				return (Reservation: (AiReservation?)null, Outcome: "unavailable");
			}
		}).ToList();
		start.Release(callers);
		var results = await Task.WhenAll(attempts);

		var admitted = results.Count(r => r.Outcome == "admitted");
		output.WriteLine($"parallel reservations: {admitted} admitted, {results.Count(r => r.Outcome == "cap")} refused at the cap, "
			+ $"{results.Count(r => r.Outcome == "unavailable")} refused after contention");
		var entries = await harness.EntriesAsync();
		var reserved = entries.Sum(e => e.CostMicroEur + e.ReservedMicroEur);
		Assert.Equal(admitted * perCallMicroEur, reserved);
		Assert.True(reserved <= capMicroEur, $"reserved {reserved} micro-EUR exceeds the cap {capMicroEur}");
		// The cap is used, not just respected: every slot that fits was given out.
		Assert.Equal(capMicroEur / perCallMicroEur, admitted);
	}

	private static AiCall Call(Guid operation, Guid? account, long estimatedInput = 4000, int maxOutput = 2000)
		=> new("chat", operation, account, Model, estimatedInput, maxOutput);

	/// <summary>
	/// A ledger over its own database. With <c>ARCHIVE_TEST_POSTGRES</c> (a
	/// connection string to a throwaway server; the user may create databases)
	/// every harness gets a fresh migrated PostgreSQL database and the tests
	/// run the production SQL; without it they run on the in-memory stand-in.
	/// </summary>
	internal sealed class LedgerHarness : IAsyncDisposable
	{
		private readonly ServiceProvider services;
		private readonly string? ownedDatabase;
		private readonly string? connection;

		public MutableTime Time { get; }

		private static string? Server => Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES") is { Length: > 0 } value ? value : null;

		public static bool PostgresConfigured(ITestOutputHelper output)
		{
			if (Server is not null)
				return true;
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return false;
		}

		public static async Task<LedgerHarness> CreateAsync(decimal capEur = 15m, DateTimeOffset? now = null,
			ISaveChangesInterceptor? interceptor = null, LedgerHarness? sameDatabaseAs = null,
			decimal inputPrice = 1m, decimal outputPrice = 4m, bool brokenStore = false)
		{
			string? connection = sameDatabaseAs?.connection, owned = null;
			if (Server is not null && brokenStore)
				connection = Server;
			else if (Server is not null && connection is null)
			{
				owned = $"ledger_{Guid.NewGuid():N}";
				await using (var admin = new Npgsql.NpgsqlConnection(Server))
				{
					await admin.OpenAsync();
					await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {owned}", admin);
					await create.ExecuteNonQueryAsync();
				}
				connection = new Npgsql.NpgsqlConnectionStringBuilder(Server) { Database = owned, MaxPoolSize = 20 }.ConnectionString;
			}
			var harness = new LedgerHarness(capEur, now, interceptor, connection, owned, inputPrice, outputPrice, brokenStore);
			if (owned is not null)
			{
				await using var scope = harness.services.CreateAsyncScope();
				await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
			}
			return harness;
		}

		private LedgerHarness(decimal capEur, DateTimeOffset? now, ISaveChangesInterceptor? interceptor,
			string? connection, string? ownedDatabase, decimal inputPrice, decimal outputPrice, bool brokenStore)
		{
			this.connection = connection;
			this.ownedDatabase = ownedDatabase;
			Time = new MutableTime { Now = now ?? October };
			var collection = new ServiceCollection();
			collection.AddLogging();
			// The context reads the Identity schema version from its application services.
			collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			var databaseName = $"ledger-{Guid.NewGuid():N}";
			var root = new InMemoryDatabaseRoot();
			collection.AddDbContext<ArchiveDbContext>(options =>
			{
				if (connection is not null)
					options.UseNpgsql(brokenStore ? BrokenConnection(connection) : connection);
				else
					options.UseInMemoryDatabase(databaseName, root);
				if (interceptor is not null)
					options.AddInterceptors(interceptor);
			});
			collection.AddSingleton<TimeProvider>(Time);
			collection.Configure<AiOptions>(o =>
			{
				o.MonthlyCapEur = capEur;
				o.Models[Model] = new AiModelPrice { InputPricePerMillionEur = inputPrice, OutputPricePerMillionEur = outputPrice };
			});
			ConfigureLedger(collection, connection is not null, brokenStore);
			services = collection.BuildServiceProvider();
		}

		private static string BrokenConnection(string connection)
			=> new Npgsql.NpgsqlConnectionStringBuilder(connection) { Port = 1, Timeout = 2 }.ConnectionString;

		private static void ConfigureLedger(IServiceCollection collection, bool postgres, bool brokenStore)
		{
			if (postgres)
				collection.AddSingleton<IAiLedgerStore, PostgresAiLedgerStore>();
			else
				collection.AddSingleton<IAiLedgerStore>(sp =>
					new InMemoryAiLedgerStore(sp.GetRequiredService<IServiceScopeFactory>()) { Broken = brokenStore });
			collection.AddSingleton<IAiBudget, AiBudgetLedger>();
		}

		public IAiBudget Budget => services.GetRequiredService<IAiBudget>();

		public async Task SeedAsync(string yearMonth, long costMicroEur)
		{
			await using var scope = services.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.AiUsageEntries.Add(new AiUsageEntry
			{
				YearMonth = yearMonth,
				Feature = "chat",
				Model = Model,
				OperationId = Guid.NewGuid(),
				CostMicroEur = costMicroEur,
				CreatedAt = October,
				UpdatedAt = October,
			});
			await db.SaveChangesAsync();
		}

		public async Task<List<AiUsageEntry>> EntriesAsync()
		{
			await using var scope = services.CreateAsyncScope();
			return await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().AiUsageEntries.AsNoTracking().ToListAsync();
		}

		public async Task<int> MonthRowsAsync()
		{
			await using var scope = services.CreateAsyncScope();
			return await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().AiBudgetMonths.CountAsync();
		}

		public async ValueTask DisposeAsync()
		{
			await services.DisposeAsync();
			if (ownedDatabase is null)
				return;
			Npgsql.NpgsqlConnection.ClearAllPools();
			await using var admin = new Npgsql.NpgsqlConnection(Server);
			await admin.OpenAsync();
			await using var drop = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS {ownedDatabase} WITH (FORCE)", admin);
			await drop.ExecuteNonQueryAsync();
		}
	}

	internal sealed class MutableTime : TimeProvider
	{
		public DateTimeOffset Now { get; set; }

		public override DateTimeOffset GetUtcNow() => Now;
	}
}
