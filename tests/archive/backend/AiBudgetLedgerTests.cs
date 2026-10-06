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
		await using var harness = new LedgerHarness();
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
		await using var harness = new LedgerHarness(capEur: 0.01m);
		await harness.SeedAsync("2026-10", costMicroEur: 9500);

		var refused = await Assert.ThrowsAsync<AiBudgetExceededException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, estimatedInput: 100, maxOutput: 200), CancellationToken.None));

		Assert.Equal("2026-10", refused.YearMonth);
		Assert.Single(await harness.EntriesAsync());
		var status = await harness.Budget.GetStatusAsync(CancellationToken.None);
		Assert.Equal(new AiBudgetStatus("2026-10", 9500, 10000), status);
		Assert.False(status.IsExhausted);
	}

	[Fact]
	public async Task ACallInFlightHoldsItsWorstCaseUntilItIsSettled()
	{
		// Cap 1 000 micro-EUR; each call may cost up to 100 + 4 × 200 = 900.
		await using var harness = new LedgerHarness(capEur: 0.001m);
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
		await using var harness = new LedgerHarness();

		await Assert.ThrowsAsync<AiBudgetUnavailableException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null) with { Model = "unbekannt" }, CancellationToken.None));

		Assert.Empty(await harness.EntriesAsync());
	}

	[Fact]
	public async Task ABrokenLedgerStoreRefusesTheCall()
	{
		var gate = new[] { true };
		await using var harness = new LedgerHarness(interceptor: new GatedFailSaveInterceptor(gate));

		await Assert.ThrowsAsync<AiBudgetUnavailableException>(() =>
			harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null), CancellationToken.None));

		gate[0] = false;
		Assert.Empty(await harness.EntriesAsync());
	}

	[Fact]
	public async Task TheBudgetMonthIsTheCalendarMonthInViennaAndStartsEmpty()
	{
		// 31 October 2026 23:30 UTC is already 1 November 00:30 in Vienna (CET).
		await using var harness = new LedgerHarness(capEur: 0.01m,
			now: new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero));
		await harness.SeedAsync("2026-10", costMicroEur: 10000);

		var reservation = await harness.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None);

		Assert.Equal("2026-11", reservation.YearMonth);
		var status = await harness.Budget.GetStatusAsync(CancellationToken.None);
		Assert.Equal("2026-11", status.YearMonth);
		Assert.Equal(900, status.SpentMicroEur);
	}

	/// <summary>
	/// The race between concurrent callers, against real PostgreSQL. EF
	/// InMemory cannot show it: it has no transactions, so a save that loses
	/// the version check still leaves its other rows behind. Skips unless
	/// <c>ARCHIVE_TEST_POSTGRES</c> holds a connection string to an empty
	/// throwaway database; all migrations are applied to it first.
	/// </summary>
	[Fact]
	[Trait("Category", "PostgresLedger")]
	public async Task ParallelCallersNeverReserveMoreThanTheCapOnPostgres()
	{
		var connection = Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES");
		if (string.IsNullOrWhiteSpace(connection))
		{
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return;
		}
		await using var harness = new LedgerHarness(capEur: 0.005m, postgres: connection);
		await harness.MigrateAsync();
		await AssertParallelReservationsStayUnderCapAsync(harness, callers: 24, capMicroEur: 5000, perCallMicroEur: 900);

		// A second "replica" (its own service provider and connection pool)
		// sees the same exhausted month.
		await using var replica = new LedgerHarness(capEur: 0.005m, postgres: connection);
		await Assert.ThrowsAsync<AiBudgetExceededException>(() =>
			replica.Budget.ReserveAsync(Call(Guid.NewGuid(), null, 100, 200), CancellationToken.None));
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

	internal sealed class LedgerHarness : IAsyncDisposable
	{
		private readonly ServiceProvider services;

		public LedgerHarness(decimal capEur = 15m, DateTimeOffset? now = null,
			ISaveChangesInterceptor? interceptor = null, string? postgres = null)
		{
			var collection = new ServiceCollection();
			collection.AddLogging();
			// The context reads the Identity schema version from its application services.
			collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			var databaseName = $"ledger-{Guid.NewGuid():N}";
			var root = new InMemoryDatabaseRoot();
			collection.AddDbContext<ArchiveDbContext>(options =>
			{
				if (postgres is not null)
					options.UseNpgsql(postgres);
				else
					options.UseInMemoryDatabase(databaseName, root);
				if (interceptor is not null)
					options.AddInterceptors(interceptor);
			});
			collection.AddSingleton<TimeProvider>(new FixedTime(now ?? October));
			collection.Configure<AiOptions>(o =>
			{
				o.MonthlyCapEur = capEur;
				o.Models[Model] = new AiModelPrice { InputPricePerMillionEur = 1m, OutputPricePerMillionEur = 4m };
			});
			collection.AddSingleton<IAiBudget, AiBudgetLedger>();
			services = collection.BuildServiceProvider();
		}

		public IAiBudget Budget => services.GetRequiredService<IAiBudget>();

		public async Task MigrateAsync()
		{
			await using var scope = services.CreateAsyncScope();
			await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
		}

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

		public ValueTask DisposeAsync() => services.DisposeAsync();
	}

	private sealed class FixedTime(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}
}
