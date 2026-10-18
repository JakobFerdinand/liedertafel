using Archive.Backend.Ai;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Reading;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034-1 on real PostgreSQL (the worker tests run on EF InMemory): the
/// migration adds the text-source column to extraction rows that already exist
/// and creates <c>reading_jobs</c> with its constraints, and the reading job
/// runs against real query translation, the real ledger SQL and the real
/// optimistic-concurrency tokens, including the sweep that wakes work parked
/// for budget. Every test creates, migrates and drops its own database.
/// Skipped without <c>ARCHIVE_TEST_POSTGRES</c>.
/// </summary>
public sealed class ReadingPostgresLiveTests(ITestOutputHelper output)
{
	private const string PreviousMigration = "20261016170055_AddLiederAndDateien";

	[Fact]
	[Trait("Category", "PostgresReading")]
	public async Task MigrationRekeysTheJobsToDateienAndConstrainsTheirRows()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync(migrate: false);
		await using (var scope = harness.Scopes.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(PreviousMigration);
		}
		await harness.SeedRevisionBeforeTheMigrationAsync();

		await using (var scope = harness.Scopes.CreateAsyncScope())
			await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();

		await using var verify = harness.Scopes.CreateAsyncScope();
		var after = verify.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		// Rows keyed by file revision cannot map to Dateien: they are dropped (test data, ADR-0001).
		Assert.Empty(await after.ExtractionJobs.AsNoTracking().ToListAsync());
		Assert.Empty(await after.ReadingJobs.AsNoTracking().ToListAsync());

		// A Noten Datei with its extraction and reading rows, then the constraints by name.
		var account = Guid.NewGuid();
		var lied = new Archive.Backend.Lieder.Lied
		{
			Title = "Neues Lied", CreatedByAccountId = account, UpdatedByAccountId = account,
			CreatedAt = harness.Now, UpdatedAt = harness.Now, RowVersion = 1,
		};
		var datei = new Archive.Backend.Lieder.Datei
		{
			LiedId = lied.Id, Kind = Archive.Backend.Lieder.DateiKinds.Noten, IsPartitur = true, BlobName = "dateien/x", ContentType = "application/pdf",
			SizeBytes = 10, CreatedByAccountId = account, CreatedAt = harness.Now, RowVersion = 1,
		};
		after.AddRange(lied, datei);
		await after.SaveChangesAsync();
		after.ExtractionJobs.Add(new ExtractionJob
		{
			DateiId = datei.Id, Status = ExtractionStatus.Completed, Text = "Text", TriggeredByAccountId = account,
			CreatedAt = harness.Now, UpdatedAt = harness.Now,
		});
		after.ReadingJobs.Add(new ReadingJob
		{
			DateiId = datei.Id, Status = ReadingStatus.WaitingForBudget, PageLimit = 20,
			TriggeredByAccountId = account, CreatedAt = harness.Now, UpdatedAt = harness.Now,
		});
		await after.SaveChangesAsync();
		await using var raw = new NpgsqlConnection(after.Database.GetConnectionString());
		await raw.OpenAsync();
		await using (var badStatus = new NpgsqlCommand("UPDATE reading_jobs SET \"Status\" = 9", raw))
			Assert.Equal("CK_reading_jobs_status", (await Assert.ThrowsAsync<PostgresException>(() => badStatus.ExecuteNonQueryAsync())).ConstraintName);
		await using (var negativePages = new NpgsqlCommand("UPDATE reading_jobs SET \"PagesRead\" = -1", raw))
			Assert.Equal("CK_reading_jobs_pages", (await Assert.ThrowsAsync<PostgresException>(() => negativePages.ExecuteNonQueryAsync())).ConstraintName);
		// The Datei is retained: its job rows block a hard delete (Restrict).
		await using (var deleteDatei = new NpgsqlCommand("DELETE FROM dateien", raw))
			Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => deleteDatei.ExecuteNonQueryAsync())).SqlState);
		// A job row for a Datei that does not exist is refused.
		await using (var orphan = new NpgsqlCommand(
			"INSERT INTO extraction_jobs (\"DateiId\", \"Status\", \"AttemptCount\", \"TriggeredByAccountId\", \"CreatedAt\", \"UpdatedAt\", \"RowVersion\", \"TextSource\")"
			+ " VALUES (@id, 0, 0, @account, now(), now(), 0, 0)", raw))
		{
			orphan.Parameters.AddWithValue("id", Guid.NewGuid());
			orphan.Parameters.AddWithValue("account", account);
			Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync())).SqlState);
		}
	}

	[Fact]
	[Trait("Category", "PostgresReading")]
	public async Task AScanIsReadAndParkedAndWokenAgainOnRealSql()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync();
		var score = await harness.SeedScoreAsync(ScanFixtures.ScanPdf(["Seite 1"], ["Seite 2"]));
		harness.Model.Transcriptions.Add(
			"Waldfahrt\nMusik: Franz Schubert\nText: Anna Beispiel\nTenor I\nG-Dur\nIm Wald da singen die Vögel\nUnd alle stimmen mit ein");
		harness.Model.FieldsJson = """{"felder":[{"feld":"lyrics","wert":"Im Wald da singen die Vögel","sicherheit":"sicher","zitat":"Im Wald da singen die Vögel"}]}""";

		// The cap holds exactly nothing: parked, message acknowledged.
		harness.Options.MonthlyCapEur = 0.0001m;
		var parked = await harness.RunAsync(score.DateiId);
		Assert.False(parked.Abandon);
		await using (var scope = harness.Scopes.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(ReadingStatus.WaitingForBudget, (await db.ReadingJobs.AsNoTracking().SingleAsync()).Status);
		}

		// Raised: the sweep (real SQL) wakes the row, the worker finishes it.
		harness.Options.MonthlyCapEur = 15m;
		await using (var scope = harness.Scopes.CreateAsyncScope())
		{
			var sent = await ReadingDispatcher.DispatchPendingAsync(
				scope.ServiceProvider.GetRequiredService<ArchiveDbContext>(), harness.Queue, harness.Budget, harness.ScanModel,
				Options.Create(harness.ReadingOptions), TimeProvider.System, NullLogger.Instance, CancellationToken.None);
			Assert.Equal(1, sent);
		}
		Assert.Equal([score.DateiId], harness.Queue.Attempts);
		Assert.False((await harness.RunAsync(score.DateiId)).Abandon);

		await using var verify = harness.Scopes.CreateAsyncScope();
		var after = verify.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var reading = await after.ReadingJobs.AsNoTracking().SingleAsync();
		Assert.Equal((ReadingStatus.Completed, 2, 4), (reading.Status, reading.PagesRead, reading.FieldsApplied));
		var song = await after.Lieder.AsNoTracking().SingleAsync();
		Assert.Equal(("Franz Schubert", "Anna Beispiel", "Im Wald da singen die Vögel"), (song.Composer, song.Lyricist, song.Lyrics));
		Assert.Equal(ExtractionTextSource.Reading, (await after.ExtractionJobs.AsNoTracking().SingleAsync()).TextSource);
		// The real ledger SQL holds both model calls, settled, with nothing left reserved.
		var ledger = await after.AiUsageEntries.AsNoTracking().ToListAsync();
		Assert.Equal([ScanPrompts.FieldsFeature, ScanPrompts.TranscriptionFeature], ledger.Select(e => e.Feature).Order().ToList());
		Assert.All(ledger, entry => Assert.Equal(0L, entry.ReservedMicroEur));
		Assert.All(ledger, entry => Assert.True(entry.CostMicroEur > 0));
	}

	private sealed class Harness : IAsyncDisposable
	{
		private readonly string owned;
		private readonly ServiceProvider services;

		public static readonly DateTimeOffset October = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

		public IServiceScopeFactory Scopes { get; }
		public ScanModelStub Model { get; }
		public FakeReadingQueue Queue { get; } = new();
		public AiOptions Options { get; }
		public ReadingOptions ReadingOptions { get; }
		public AiBudgetLedger Budget { get; }
		public ScanReadingModel ScanModel { get; }
		public DateTimeOffset Now => October;
		private string Server { get; }
		private readonly FakeAssetStorage storage = new();

		private static string? Configuration => Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES") is { Length: > 0 } value ? value : null;

		public static bool Configured(ITestOutputHelper output)
		{
			if (Configuration is not null)
				return true;
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return false;
		}

		private Harness(string server, string owned, ServiceProvider services, HarnessState state)
		{
			Model = state.Model;
			Options = state.Ai;
			ReadingOptions = state.Reading;
			Server = server;
			this.owned = owned;
			this.services = services;
			Scopes = services.GetRequiredService<IServiceScopeFactory>();
			Budget = services.GetRequiredService<AiBudgetLedger>();
			ScanModel = services.GetRequiredService<ScanReadingModel>();
		}

		public static async Task<Harness> CreateAsync(bool migrate = true)
		{
			var server = Configuration ?? throw new InvalidOperationException("ARCHIVE_TEST_POSTGRES is not set.");
			var owned = $"reading_{Guid.NewGuid():N}";
			await using (var admin = new NpgsqlConnection(server))
			{
				await admin.OpenAsync();
				await using var create = new NpgsqlCommand($"CREATE DATABASE {owned}", admin);
				await create.ExecuteNonQueryAsync();
			}
			var connection = new NpgsqlConnectionStringBuilder(server) { Database = owned, MaxPoolSize = 20 }.ConnectionString;
			var collection = new ServiceCollection();
			collection.AddLogging();
			collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			collection.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(connection));
			collection.AddSingleton(TimeProvider.System);
			var harnessHolder = new HarnessState();
			collection.AddSingleton(harnessHolder);
			collection.AddSingleton<IAiLedgerStore, PostgresAiLedgerStore>();
			collection.AddSingleton(sp => new AiBudgetLedger(sp.GetRequiredService<IAiLedgerStore>(), TimeProvider.System,
				new StaticMonitor(harnessHolder.Ai), NullLogger<AiBudgetLedger>.Instance));
			collection.AddSingleton(sp => new AiGateway(harnessHolder.Model, sp, sp.GetRequiredService<AiBudgetLedger>(),
				new StaticMonitor(harnessHolder.Ai), new AiModels { Chat = ReadingTestHost.ModelKey }));
			collection.AddSingleton(sp => new ScanReadingModel(sp.GetRequiredService<AiGateway>(),
				Microsoft.Extensions.Options.Options.Create(harnessHolder.Reading), NullLoggerFactory.Instance));
			collection.AddScoped<CatalogueWriteService>();
			collection.AddScoped<AutomatedFieldWriter>();
			collection.AddScoped<ScoreFieldPrefill>();
			var provider = collection.BuildServiceProvider();
			if (migrate)
			{
				await using var scope = provider.CreateAsyncScope();
				await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
			}
			return new Harness(server, owned, provider, harnessHolder);
		}

		/// <summary>
		/// Seeds the old catalogue chain with raw SQL (its entities are gone) and a revision-keyed
		/// extraction and reading row, as the schema before the Datei migration holds them.
		/// </summary>
		public async Task SeedRevisionBeforeTheMigrationAsync()
		{
			await using var scope = Scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			await using var raw = new NpgsqlConnection(db.Database.GetConnectionString());
			await raw.OpenAsync();
			await using var seed = new NpgsqlCommand(
				"""
				INSERT INTO songs ("Id", "Title", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
					VALUES ('10000000-0000-0000-0000-000000000001', 'Altes Lied', now(), @account, now(), @account, 0);
				INSERT INTO arrangements ("Id", "SongId", "Label", "CreatedAt", "CreatedByAccountId")
					VALUES ('10000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'Chorsatz', now(), @account);
				INSERT INTO musical_versions ("Id", "ArrangementId", "Label", "CreatedAt", "CreatedByAccountId")
					VALUES ('10000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000002', 'Original', now(), @account);
				INSERT INTO assets ("Id", "MusicalVersionId", "CreatedByAccountId", "CreatedAt", "RowVersion")
					VALUES ('10000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000003', @account, now(), 0);
				INSERT INTO file_revisions ("Id", "AssetId", "RevisionNumber", "BlobName", "ContentType", "SizeBytes", "CreatedByAccountId", "CreatedAt")
					VALUES ('10000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000004', 1, 'revisions/alt.pdf', 'application/pdf', 10, @account, now());
				INSERT INTO extraction_jobs ("RevisionId", "AssetId", "Status", "Text", "AttemptCount", "TriggeredByAccountId", "CreatedAt", "UpdatedAt", "RowVersion")
					VALUES ('10000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000004', 2, 'Text von früher', 1, @account, now(), now(), 0);
				INSERT INTO reading_jobs ("RevisionId", "AssetId", "Status", "PagesRead", "PageLimit", "FieldsDone", "FieldsApplied", "AttemptCount", "TriggeredByAccountId", "CreatedAt", "UpdatedAt", "RowVersion")
					VALUES ('10000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000004', 3, 0, 20, false, 0, 1, @account, now(), now(), 0);
				""", raw);
			seed.Parameters.AddWithValue("account", Guid.NewGuid());
			await seed.ExecuteNonQueryAsync();
		}

		public async Task<(Guid DateiId, Guid LiedId)> SeedScoreAsync(byte[] pdf)
		{
			await using var scope = Scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var account = Guid.NewGuid();
			var lied = new Archive.Backend.Lieder.Lied
			{
				Title = "Waldfahrt", CreatedByAccountId = account, UpdatedByAccountId = account,
				CreatedAt = October, UpdatedAt = October, RowVersion = 1,
			};
			var datei = new Archive.Backend.Lieder.Datei
			{
				LiedId = lied.Id, Kind = Archive.Backend.Lieder.DateiKinds.Noten, IsPartitur = true, BlobName = $"dateien/{Guid.NewGuid():N}.pdf",
				ContentType = "application/pdf", SizeBytes = pdf.Length, CreatedByAccountId = account, CreatedAt = October, RowVersion = 1,
			};
			db.AddRange(lied, datei);
			await db.SaveChangesAsync();
			db.ExtractionJobs.Add(new ExtractionJob
			{
				DateiId = datei.Id, Status = ExtractionStatus.NoText, TriggeredByAccountId = account,
				CreatedAt = October, UpdatedAt = October, CompletedAt = October,
			});
			db.ReadingJobs.Add(ReadingService.CreateForDatei(datei, account, ReadingOptions.MaxPagesPerRequest, October));
			await db.SaveChangesAsync();
			storage.Store(datei.BlobName, pdf);
			return (datei.Id, lied.Id);
		}

		/// <summary>The message a worker run handles, in its own scope like the pump does.</summary>
		public async Task<MessageDisposition> RunAsync(Guid revisionId)
		{
			await using var scope = Scopes.CreateAsyncScope();
			var sp = scope.ServiceProvider;
			var worker = new ReadingWorker(sp.GetRequiredService<ArchiveDbContext>(), storage, new PdfiumPageRenderer(), ScanModel,
				sp.GetRequiredService<ScoreFieldPrefill>(), Microsoft.Extensions.Options.Options.Create(ReadingOptions),
				Microsoft.Extensions.Options.Options.Create(new ExtractionOptions()),
				new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System,
				NullLogger<ReadingWorker>.Instance);
			return await worker.HandleAsync(ReadingTestHost.Message(revisionId), DateTimeOffset.UtcNow + ReadingOptions.TimeBudget, CancellationToken.None);
		}

		public async ValueTask DisposeAsync()
		{
			await services.DisposeAsync();
			NpgsqlConnection.ClearAllPools();
			await using var admin = new NpgsqlConnection(Server);
			await admin.OpenAsync();
			await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {owned} WITH (FORCE)", admin);
			await drop.ExecuteNonQueryAsync();
		}

		private sealed class HarnessState
		{
			public AiOptions Ai { get; } = new()
			{
				MonthlyCapEur = 15m,
				Models = { [ReadingTestHost.ModelKey] = new AiModelPrice { InputPricePerMillionEur = 0.1056m, OutputPricePerMillionEur = 0.528m } },
			};
			public ScanModelStub Model { get; } = new();
			public ReadingOptions Reading { get; } = new();
		}

		private sealed class StaticMonitor(AiOptions value) : IOptionsMonitor<AiOptions>
		{
			public AiOptions CurrentValue => value;
			public AiOptions Get(string? name) => value;
			public IDisposable? OnChange(Action<AiOptions, string?> listener) => null;
		}
	}
}
