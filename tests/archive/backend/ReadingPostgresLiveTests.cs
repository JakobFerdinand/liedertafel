using Archive.Backend.Ai;
using Archive.Backend.Assets;
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
	private const string PreviousMigration = "20261011160524_ChatMessageA2ui";

	[Fact]
	[Trait("Category", "PostgresReading")]
	public async Task MigrationKeepsExistingExtractionRowsAsEmbeddedTextAndConstrainsReadingRows()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync(migrate: false);
		await using (var scope = harness.Scopes.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(PreviousMigration);
		}
		var (revisionId, assetId) = await harness.SeedRevisionBeforeTheMigrationAsync();

		await using (var scope = harness.Scopes.CreateAsyncScope())
			await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();

		await using var verify = harness.Scopes.CreateAsyncScope();
		var after = verify.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var extraction = await after.ExtractionJobs.AsNoTracking().SingleAsync(j => j.RevisionId == revisionId);
		Assert.Equal((ExtractionStatus.Completed, ExtractionTextSource.Embedded, "Text von früher"),
			(extraction.Status, extraction.TextSource, extraction.Text));

		// A reading row for that revision, then the constraints by name.
		after.ReadingJobs.Add(new ReadingJob
		{
			RevisionId = revisionId, AssetId = assetId, Status = ReadingStatus.WaitingForBudget, PageLimit = 20,
			TriggeredByAccountId = Guid.NewGuid(), CreatedAt = harness.Now, UpdatedAt = harness.Now,
		});
		await after.SaveChangesAsync();
		await using var raw = new NpgsqlConnection(after.Database.GetConnectionString());
		await raw.OpenAsync();
		await using (var badStatus = new NpgsqlCommand("UPDATE reading_jobs SET \"Status\" = 9", raw))
			Assert.Equal("CK_reading_jobs_status", (await Assert.ThrowsAsync<PostgresException>(() => badStatus.ExecuteNonQueryAsync())).ConstraintName);
		await using (var negativePages = new NpgsqlCommand("UPDATE reading_jobs SET \"PagesRead\" = -1", raw))
			Assert.Equal("CK_reading_jobs_pages", (await Assert.ThrowsAsync<PostgresException>(() => negativePages.ExecuteNonQueryAsync())).ConstraintName);
		// The revision is retained: a reading row blocks its deletion (Restrict).
		await using (var deleteRevision = new NpgsqlCommand("DELETE FROM file_revisions", raw))
			Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => deleteRevision.ExecuteNonQueryAsync())).SqlState);
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
		var parked = await harness.RunAsync(score.RevisionId);
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
		Assert.Equal([score.RevisionId], harness.Queue.Attempts);
		Assert.False((await harness.RunAsync(score.RevisionId)).Abandon);

		await using var verify = harness.Scopes.CreateAsyncScope();
		var after = verify.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var reading = await after.ReadingJobs.AsNoTracking().SingleAsync();
		Assert.Equal((ReadingStatus.Completed, 2, 5), (reading.Status, reading.PagesRead, reading.FieldsApplied));
		var song = await after.Songs.AsNoTracking().SingleAsync();
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

		public async Task<(Guid RevisionId, Guid AssetId)> SeedRevisionBeforeTheMigrationAsync()
		{
			await using var scope = Scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var account = Guid.NewGuid();
			var song = new Song { Title = "Altes Lied", CreatedByAccountId = account, UpdatedByAccountId = account, CreatedAt = October, UpdatedAt = October };
			var arrangement = new Arrangement { Song = song, Label = "Chorsatz", CreatedByAccountId = account, CreatedAt = October };
			var version = new MusicalVersion { Arrangement = arrangement, Label = "Original", CreatedByAccountId = account, CreatedAt = October };
			var asset = new ArchiveAsset { AssetType = AssetEndpoints.ScoreAssetType, MusicalVersion = version, CreatedByAccountId = account, CreatedAt = October };
			var revision = new FileRevision
			{
				Asset = asset, RevisionNumber = 1, BlobName = $"revisions/{Guid.NewGuid():N}.pdf", ContentType = AssetEndpoints.PdfContentType,
				SizeBytes = 10, CreatedByAccountId = account, CreatedAt = October,
			};
			db.AddRange(song, arrangement, version, asset, revision);
			await db.SaveChangesAsync();
			// The extraction table of the previous schema has no text-source column yet: raw SQL.
			await using var raw = new NpgsqlConnection(db.Database.GetConnectionString());
			await raw.OpenAsync();
			await using var insert = new NpgsqlCommand(
				"INSERT INTO extraction_jobs (\"RevisionId\", \"AssetId\", \"Status\", \"Text\", \"AttemptCount\", \"TriggeredByAccountId\", \"CreatedAt\", \"UpdatedAt\", \"RowVersion\")"
				+ " VALUES (@revision, @asset, 2, 'Text von früher', 1, @account, now(), now(), 0)", raw);
			insert.Parameters.AddWithValue("revision", revision.Id);
			insert.Parameters.AddWithValue("asset", asset.Id);
			insert.Parameters.AddWithValue("account", account);
			await insert.ExecuteNonQueryAsync();
			return (revision.Id, asset.Id);
		}

		public async Task<(Guid RevisionId, Guid SongId)> SeedScoreAsync(byte[] pdf)
		{
			await using var scope = Scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var account = Guid.NewGuid();
			var song = new Song { Title = "Waldfahrt", CreatedByAccountId = account, UpdatedByAccountId = account, CreatedAt = October, UpdatedAt = October };
			var arrangement = new Arrangement { Song = song, Label = "Chorsatz", CreatedByAccountId = account, CreatedAt = October };
			var version = new MusicalVersion { Arrangement = arrangement, Label = "Original", CreatedByAccountId = account, CreatedAt = October };
			var asset = new ArchiveAsset { AssetType = AssetEndpoints.ScoreAssetType, MusicalVersion = version, CreatedByAccountId = account, CreatedAt = October };
			var revision = new FileRevision
			{
				Asset = asset, RevisionNumber = 1, BlobName = $"revisions/{Guid.NewGuid():N}.pdf", ContentType = AssetEndpoints.PdfContentType,
				SizeBytes = pdf.Length, CreatedByAccountId = account, CreatedAt = October,
			};
			db.AddRange(song, arrangement, version, asset, revision);
			await db.SaveChangesAsync();
			asset.CurrentRevisionId = revision.Id;
			db.ExtractionJobs.Add(new ExtractionJob
			{
				RevisionId = revision.Id, AssetId = asset.Id, Status = ExtractionStatus.NoText, TriggeredByAccountId = account,
				CreatedAt = October, UpdatedAt = October, CompletedAt = October,
			});
			db.ReadingJobs.Add(ReadingService.CreateForRevision(revision, account, ReadingOptions.MaxPagesPerRequest, October));
			await db.SaveChangesAsync();
			storage.Store(revision.BlobName, pdf);
			return (revision.Id, song.Id);
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
