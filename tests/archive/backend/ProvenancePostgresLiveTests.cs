using Archive.Backend.Ai;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Provenance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-013-1 on real PostgreSQL (the backend API tests run on EF InMemory):
/// the migration really creates <c>field_provenance</c> and <c>proposals</c>
/// with their unique index and kind check, constraint violations map by name,
/// the automated write path applies and proposes against real query
/// translation, and a lost row-version race answers stale 409. Every test
/// creates, migrates and drops its own database. Skipped without
/// <c>ARCHIVE_TEST_POSTGRES</c>.
/// </summary>
public sealed class ProvenancePostgresLiveTests(ITestOutputHelper output)
{
	internal const string Editor = "redaktion@liedertafel.test";

	/// <summary>One fixed moment for every write in the live tests.</summary>
	public static readonly DateTimeOffset October = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

	[Fact]
	[Trait("Category", "PostgresProvenance")]
	public async Task MigrationCreatesTablesWithProvenanceConstraintsAndRowVersions()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync();
		using var scope = harness.Scopes.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();

		Assert.Empty(await db.Database.GetPendingMigrationsAsync());

		// The row-version columns landed on the existing catalogue tables.
		var song = await harness.SeedSongAsync("Existierendes Lied");
		var arrangement = song.Arrangements.Single();
		Assert.Equal(0u, arrangement.RowVersion);
		Assert.Equal(0u, arrangement.MusicalVersions.Single().RowVersion);

		// The unique per-field provenance index is a real database constraint.
		db.FieldProvenance.Add(new FieldProvenance
		{
			EntityType = FieldCatalog.EntityTypeSong,
			EntityId = song.Id,
			Field = "composer",
			Source = ProvenanceSource.Regex,
			Confidence = ProvenanceConfidence.Sicher,
			ChangedAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero),
		});
		await db.SaveChangesAsync();
		db.FieldProvenance.Add(new FieldProvenance
		{
			EntityType = FieldCatalog.EntityTypeSong,
			EntityId = song.Id,
			Field = "composer",
			Source = ProvenanceSource.Ai,
			Confidence = ProvenanceConfidence.Unsicher,
			ChangedAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero),
		});
		var constrained = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		var violated = Assert.IsAssignableFrom<PostgresException>(constrained.InnerException);
		Assert.Equal("IX_field_provenance_target", violated.ConstraintName);

		// The closed proposal kinds are a real check constraint.
		await using var raw = new NpgsqlConnection(db.Database.GetConnectionString());
		await raw.OpenAsync();
		await using var command = new NpgsqlCommand(
			"INSERT INTO proposals (\"Id\", \"Kind\", \"Payload\", \"Reason\", \"Source\", \"Confidence\", \"Status\", \"CreatedAt\")"
			+ " VALUES (@id, 'UnbekannteArt', '{}', 'pruefung', 'Regex', 'Sicher', 'Open', now())", raw);
		command.Parameters.AddWithValue("id", Guid.NewGuid());
		var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
		Assert.Equal("CK_proposals_kind", refused.ConstraintName);

		Assert.Equal(1, await db.FieldProvenance.CountAsync());
		Assert.Equal(0, await db.Proposals.CountAsync());
	}

	[Fact]
	[Trait("Category", "PostgresProvenance")]
	public async Task AutomatedWriteAppliesProposesAndHonoursTheHumanLockOnRealSql()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync();
		using var scope = harness.Scopes.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var songId = (await harness.SeedSongAsync("Leselauf")).Id;

		// Each automated run rides its own scope, like the endpoints would.
		async Task<AutomatedFieldWriter.AutomatedWriteOutcome> RunWriterAsync(
			string field, string? value, ProvenanceConfidence confidence, string description)
		{
			using var writerScope = harness.Scopes.CreateScope();
			var writer = writerScope.ServiceProvider.GetRequiredService<AutomatedFieldWriter>();
			return await writer.WriteFieldAsync(
				new AutomatedFieldWriter.FieldWriteRequest(
					EntityType: FieldCatalog.EntityTypeSong, EntityId: songId, Field: field,
					Value: value, Source: ProvenanceSource.Regex, Confidence: confidence,
					Model: null, PromptVersion: null, Reason: "Notentext gelesen.", SourceDescription: description),
				null, CancellationToken.None);
		}

		var above = await RunWriterAsync("composer", "Franz Xaver Gruber", ProvenanceConfidence.Sicher, "Stand 1");
		Assert.Equal(AutomatedFieldWriter.AutomatedWriteResult.Applied, above.Result);

		// The human edit locks the field against later runs.
		using (var editScope = harness.Scopes.CreateScope())
		{
			var writes = editScope.ServiceProvider.GetRequiredService<CatalogueWriteService>();
			var entity = await writes.LoadEntityAsync(FieldCatalog.EntityTypeSong, songId, CancellationToken.None);
			var saved = await writes.WriteFieldAsync(entity!, "composer", "Josef Gruber",
				new WriteActor(Guid.NewGuid(), Harness.October), null, CancellationToken.None);
			Assert.True(saved.IsSaved);
		}

		var second = await RunWriterAsync("composer", "Anders", ProvenanceConfidence.Sicher, "Stand 2");
		Assert.Equal(AutomatedFieldWriter.AutomatedWriteResult.Proposed, second.Result);

		var proposal = Assert.Single(await db.Proposals.Where(p => p.Status == ProposalStatus.Open).ToListAsync());
		Assert.Equal(ProposalKind.FieldSuggestion, proposal.Kind);

		// The uncertain run below the threshold proposes as well.
		var below = await RunWriterAsync("language", "Deutsch", ProvenanceConfidence.Unsicher, "Stand 1");
		Assert.Equal(AutomatedFieldWriter.AutomatedWriteResult.Proposed, below.Result);
		Assert.Equal(2, await db.Proposals.CountAsync(p => p.Status == ProposalStatus.Open));
	}

	[Fact]
	[Trait("Category", "PostgresProvenance")]
	public async Task ProposalAcceptRunsTheStaleGateAndAppliesOnRealPostgres()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync();
		using var scope = harness.Scopes.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var songId = (await harness.SeedSongAsync("Vorschlagsziel")).Id;

		var writer = scope.ServiceProvider.GetRequiredService<AutomatedFieldWriter>();
		var below = await writer.WriteFieldAsync(
			new AutomatedFieldWriter.FieldWriteRequest(
				EntityType: FieldCatalog.EntityTypeSong, EntityId: songId, Field: "language",
				Value: "Deutsch", Source: ProvenanceSource.Ai, Confidence: ProvenanceConfidence.Unsicher,
				Model: "gpt-5-4-mini", PromptVersion: "arc-1", Reason: "Leselauf.", SourceDescription: "Stand 1"),
			null, CancellationToken.None);
		Assert.Equal(AutomatedFieldWriter.AutomatedWriteResult.Proposed, below.Result);
		var proposalId = below.ProposalId!.Value;

		var editor = Guid.NewGuid();
		var writes = new CatalogueWriteService(db);
		var handlers = new ProposalHandlers(new IProposalHandler[]
		{
			new FieldSuggestionHandler(writes),
			new SongPublicationHandler(db, writes),
		});
		var decisions = new ProposalDecisions(db, handlers, harness.Time);
		using (var other = new NpgsqlConnection(harness.Own))
		{
			// Another editor moves the target past the proposal's row version.
			await other.OpenAsync();
			await using var bump = new Npgsql.NpgsqlCommand(
				"UPDATE songs SET \"RowVersion\" = \"RowVersion\" + 1 WHERE \"Id\" = @id", other);
			bump.Parameters.AddWithValue("id", songId);
			await bump.ExecuteNonQueryAsync();
		}
		// The decision loads its target freshly, like the endpoint would.
		await using (var freshScope = harness.Scopes.CreateAsyncScope())
		{
			var freshDb = freshScope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var freshWrites = new CatalogueWriteService(freshDb);
			var freshHandlers = new ProposalHandlers(new IProposalHandler[]
			{
				new FieldSuggestionHandler(freshWrites),
				new SongPublicationHandler(freshDb, freshWrites),
			});
			var freshDecisions = new ProposalDecisions(freshDb, freshHandlers, harness.Time);

			var stale = await freshDecisions.AcceptAsync(proposalId, editor, CancellationToken.None);
			Assert.False(stale.Ok);
			Assert.Equal(409, stale.HttpCode);
			Assert.Equal("Deutsch", stale.ProposedValue);
			Assert.NotNull(stale.TargetState);

			// The re-based proposal then applies through the shared write service.
			await freshDecisions.RefreshAsync(proposalId, CancellationToken.None);
			var applied = await freshDecisions.AcceptAsync(proposalId, editor, CancellationToken.None);
			Assert.True(applied.Ok);
		}
		var song = await db.Songs.AsNoTracking().SingleAsync(s => s.Id == songId);
		Assert.Equal("Deutsch", song.Language);
		Assert.Equal(
			ProposalStatus.Accepted,
			(await db.Proposals.AsNoTracking().SingleAsync(p => p.Id == proposalId)).Status);
		var provenance = await db.FieldProvenance.SingleAsync(p =>
			p.EntityType == FieldCatalog.EntityTypeSong && p.EntityId == songId && p.Field == "language");
		Assert.Equal(ProvenanceSource.Ai, provenance.Source);
		Assert.Equal(editor, provenance.ActorAccountId);
	}

	[Fact]
	[Trait("Category", "PostgresProvenance")]
	public async Task LostRowVersionRaceAnswersStaleOnRealPostgres()
	{
		if (!Harness.Configured(output))
			return;
		await using var harness = await Harness.CreateAsync();
		var songId = (await harness.SeedSongAsync("Wettlauf")).Id;

		// Two isolated contexts loading the same row; the second save bumps
		// the stored row version, so the first save loses its race.
		var firstDb = (harness.Scopes.CreateScope()).ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var first = await firstDb.Songs.SingleAsync(s => s.Id == songId);
		await using (var raceScope = harness.Scopes.CreateAsyncScope())
		{
			var raceDb = raceScope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var winner = await raceDb.Songs.SingleAsync(s => s.Id == songId);
			winner.Composer = "Wer zuerst kommt";
			winner.RowVersion++;
			await raceDb.SaveChangesAsync();
		}

		var writes = new CatalogueWriteService(firstDb);
		var outcome = await writes.PatchSongAsync(first,
			new PatchSongRequest(Title: null, Composer: "Wer später kommt",
				Lyricist: null, Lyrics: null, AlternateTitles: null, Language: null, Occasion: null, Tags: null, RowVersion: null),
			first.RowVersion,
			new WriteActor(Guid.NewGuid(), ProvenancePostgresLiveTests.October), null, CancellationToken.None);
		Assert.False(outcome.IsSaved);
		Assert.Equal(WriteOutcomeStatus.Stale, outcome.Status);
		Assert.Equal("Wer zuerst kommt", (await firstDb.Songs.AsNoTracking().SingleAsync(s => s.Id == songId)).Composer);
	}

	// ---- harness -----------------------------------------------------------

	internal sealed class Harness : IAsyncDisposable
	{
		public readonly IServiceScopeFactory Scopes;
		private readonly string? ownedDatabase;
		private readonly ServiceProvider services;

		public static readonly DateTimeOffset October = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
		private static string? Server => Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES") is { Length: > 0 } value ? value : null;

		/// <summary>Connection of this harness's own database.</summary>
		public string Own { get; } = string.Empty;

		/// <summary>Connection address of the throwaway server (the admin target).</summary>
		public string Host { get; } = string.Empty;

		public FixedOctoberTime Time { get; } = new();

		public static bool Configured(ITestOutputHelper output)
		{
			if (Server is not null)
				return true;
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return false;
		}

		private Harness(IServiceScopeFactory scopes, string host, string own, string? owned, ServiceProvider services)
		{
			Scopes = scopes;
			Host = host;
			Own = own;
			ownedDatabase = owned;
			this.services = services;
		}

		public static async Task<Harness> CreateAsync()
		{
			var server = Server ?? throw new InvalidOperationException("ARCHIVE_TEST_POSTGRES is not set.");
			var owned = $"provenance_{Guid.NewGuid():N}";
			await using (var admin = new Npgsql.NpgsqlConnection(server))
			{
				await admin.OpenAsync();
				await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {owned}", admin);
				await create.ExecuteNonQueryAsync();
			}
			var connection = new Npgsql.NpgsqlConnectionStringBuilder(server)
			{
				Database = owned,
				MaxPoolSize = 20,
			}.ConnectionString;

			var collection = new ServiceCollection();
			collection.AddLogging();
			collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			var time = new FixedOctoberTime();
			collection.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(connection));
			collection.AddSingleton<TimeProvider>(time);
			collection.Configure<ProvenanceOptions>(o => o.AutoApplyConfidence = "sicher");
			collection.AddScoped<CatalogueWriteService>();
			collection.AddScoped<AutomatedFieldWriter>();
			collection.AddScoped<SongPublicationHandler>();
			var services = collection.BuildServiceProvider();
			await using (var scope = services.CreateAsyncScope())
			{
				await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
			}
			return new(services.GetRequiredService<IServiceScopeFactory>(), server, connection, owned, services);		}

		public async Task<Song> SeedSongAsync(string title)
		{
			await using var scope = Scopes.CreateAsyncScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var song = new Song
			{
				Title = title,
				CreatedAt = October,
				CreatedByAccountId = Guid.NewGuid(),
				UpdatedAt = October,
				UpdatedByAccountId = Guid.NewGuid(),
				Arrangements =
				[
					new Arrangement
					{
						Label = "Standardfassung",
						CreatedAt = October,
						CreatedByAccountId = Guid.NewGuid(),
						MusicalVersions =
						[
							new MusicalVersion { Label = "Standardfassung", CreatedAt = October, CreatedByAccountId = Guid.NewGuid() },
						],
					},
				],
			};
			db.Songs.Add(song);
			await db.SaveChangesAsync();
			return song;
		}

		public async ValueTask DisposeAsync()
		{
			await services.DisposeAsync();
			if (ownedDatabase is null)
				return;
			await using var admin = new Npgsql.NpgsqlConnection(Server);
			await admin.OpenAsync();
			await using var drop = new Npgsql.NpgsqlCommand(
				$"DROP DATABASE {ownedDatabase} WITH (FORCE)", admin);
			await drop.ExecuteNonQueryAsync();
		}
	}

	/// <summary>Eine feste Zeit für jeden Schreibzugriff in den Live-Tests.</summary>
	internal sealed class FixedOctoberTime : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => ProvenancePostgresLiveTests.October;
	}
}
