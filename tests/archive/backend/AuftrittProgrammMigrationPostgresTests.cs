using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// #160 on real PostgreSQL: the database is migrated to the last migration
/// before <c>AuftrittProgramm</c>, seeded with an Auftritt whose Programm has a
/// published and a newer working revision, a confirmation, evidence rows and
/// Zeitmarken on them, and then migrated to head. The latest revision's items
/// must become the single Programm, the revisions, confirmation and evidence
/// tables must be gone, and only Zeitmarken that can be mapped to a surviving
/// Programm entry may remain, now pointing at that entry.
/// Skipped without <c>ARCHIVE_TEST_POSTGRES</c>.
/// </summary>
public sealed class AuftrittProgrammMigrationPostgresTests(ITestOutputHelper output)
{
	private const string PreviousMigration = "20261019102646_LiedEntwuerfe";

	[Fact]
	[Trait("Category", "PostgresContract")]
	public async Task TheLatestRevisionBecomesTheProgrammeAndMarksFollowTheirEntries()
	{
		var server = Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES");
		if (string.IsNullOrEmpty(server))
		{
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return;
		}
		var owned = $"programm_{Guid.NewGuid():N}";
		await using (var admin = new NpgsqlConnection(server))
		{
			await admin.OpenAsync();
			await using var create = new NpgsqlCommand($"CREATE DATABASE {owned}", admin);
			await create.ExecuteNonQueryAsync();
		}
		var connection = new NpgsqlConnectionStringBuilder(server) { Database = owned }.ConnectionString;
		try
		{
			var services = new ServiceCollection();
			services.AddLogging();
			services.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			services.AddDbContext<ArchiveDbContext>(o => o.UseNpgsql(connection));
			await using var provider = services.BuildServiceProvider();
			await using (var scope = provider.CreateAsyncScope())
				await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().GetService<IMigrator>().MigrateAsync(PreviousMigration);

			await using var raw = new NpgsqlConnection(connection);
			await raw.OpenAsync();
			await using (var seed = new NpgsqlCommand(SeedSql, raw))
				await seed.ExecuteNonQueryAsync();

			await using (var scope = provider.CreateAsyncScope())
				await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();

			foreach (var table in new[] { "programme_revisions", "programme_confirmations", "performances" })
				Assert.False(await TableExists(raw, table), $"{table} must be dropped");

			// The newest revision (the working draft) is the Programm; the older one is gone.
			var items = await Items(raw);
			Assert.Equal(
				[("cc000000-0000-0000-0000-000000000001", 1), ("cc000000-0000-0000-0000-000000000002", 2)],
				items.Select(i => (i.Id, i.Position)).ToList());
			Assert.All(items, i => Assert.Equal("30000000-0000-0000-0000-000000000002", i.ProgrammeId));
			Assert.Equal(1, await Count(raw, "programmes"));

			// Only the mark on an entry of the newest revision survives, now on that entry.
			await using var marks = new NpgsqlCommand("SELECT \"ProgrammeItemId\"::text FROM recording_passages", raw);
			var remaining = new List<string>();
			await using (var reader = await marks.ExecuteReaderAsync())
				while (await reader.ReadAsync())
					remaining.Add(reader.GetString(0));
			Assert.Equal(["cc000000-0000-0000-0000-000000000001"], remaining);

			// A saved Auftritt is visible at once: the publication columns are gone, the event stays.
			Assert.Equal(1, await Count(raw, "events"));
			await using var columns = new NpgsqlCommand(
				"SELECT count(*) FROM information_schema.columns WHERE table_name = 'events' AND column_name IN ('PublishedAt', 'PublishedByAccountId')", raw);
			Assert.Equal(0L, (long)(await columns.ExecuteScalarAsync())!);

			// Accounts, Lieder and the recording are untouched.
			Assert.Equal(1, await Count(raw, "\"AspNetUsers\""));
			Assert.Equal(2, await Count(raw, "lieder"));
			Assert.Equal(1, await Count(raw, "recordings"));
		}
		finally
		{
			NpgsqlConnection.ClearAllPools();
			await using var admin = new NpgsqlConnection(server);
			await admin.OpenAsync();
			await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {owned} WITH (FORCE)", admin);
			await drop.ExecuteNonQueryAsync();
		}
	}

	private static async Task<List<(string Id, int Position, string ProgrammeId)>> Items(NpgsqlConnection connection)
	{
		await using var command = new NpgsqlCommand(
			"SELECT \"Id\"::text, \"Position\", \"ProgrammeId\"::text FROM programme_items ORDER BY \"Position\"", connection);
		var items = new List<(string, int, string)>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
			items.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
		return items;
	}

	private static async Task<long> Count(NpgsqlConnection connection, string table)
	{
		await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<bool> TableExists(NpgsqlConnection connection, string table)
	{
		await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t", connection);
		command.Parameters.AddWithValue("t", table);
		return (long)(await command.ExecuteScalarAsync())! > 0;
	}

	// "a" is the account everything is created by. Revision 1 (published) has items 1-2, revision 2
	// (the newer working draft) items 3-4. Passages: on a performance confirming a revision 2 item
	// (kept), on one confirming a revision 1 item (dropped), on a hand-entered performance (dropped).
	private const string SeedSql = """
		INSERT INTO "AspNetUsers" ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "Email", "NormalizedEmail", "UserName", "NormalizedUserName")
			VALUES ('00000000-0000-0000-0000-00000000000a', true, false, false, false, 0, 'redaktion@liedertafel.test', 'REDAKTION@LIEDERTAFEL.TEST', 'redaktion@liedertafel.test', 'REDAKTION@LIEDERTAFEL.TEST');
		INSERT INTO lieder ("Id", "Title", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('20000000-0000-0000-0000-000000000001', 'Erstes Lied', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1),
				('20000000-0000-0000-0000-000000000002', 'Zweites Lied', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO events ("Id", "Kind", "Title", "DateApproximate", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000001', 'concert', 'Adventkonzert', false, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO programmes ("Id", "EventId", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 3);
		INSERT INTO programme_revisions ("Id", "ProgrammeId", "Number", "PublishedAt", "CreatedAt", "CreatedByAccountId")
			VALUES ('30000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000002', 1, now(), now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO programme_revisions ("Id", "ProgrammeId", "Number", "CreatedAt", "CreatedByAccountId")
			VALUES ('30000000-0000-0000-0000-000000000004', '30000000-0000-0000-0000-000000000002', 2, now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO programme_items ("Id", "RevisionId", "Position", "LiedId")
			VALUES ('aa000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000003', 1, '20000000-0000-0000-0000-000000000001'),
				('aa000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000003', 2, '20000000-0000-0000-0000-000000000002'),
				('cc000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000004', 1, '20000000-0000-0000-0000-000000000002'),
				('cc000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000004', 2, '20000000-0000-0000-0000-000000000001');
		INSERT INTO programme_confirmations ("Id", "ProgrammeId", "RevisionId", "CreatedAt", "CreatedByAccountId", "RowVersion", "UpdatedAt", "UpdatedByAccountId")
			VALUES ('30000000-0000-0000-0000-000000000005', '30000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000003', now(), '00000000-0000-0000-0000-00000000000a', 1, now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO performances ("Id", "EventId", "LiedId", "Position", "EvidenceStatus", "ProgrammeItemId", "ConfirmationId", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('bb000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000002', 1, 'confirmed', 'cc000000-0000-0000-0000-000000000001', NULL, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1),
				('bb000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', 2, 'confirmed', 'aa000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000005', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1),
				('bb000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', 3, 'mention', NULL, NULL, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO dateien ("Id", "EventId", "Kind", "IsPartitur", "BlobName", "ContentType", "SizeBytes", "CreatedByAccountId", "CreatedAt", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000008', '30000000-0000-0000-0000-000000000001', 'aufnahme', false, 'dateien/aufnahme', 'audio/mpeg', 10, '00000000-0000-0000-0000-00000000000a', now(), 1);
		INSERT INTO recordings ("Id", "EventId", "Label", "Kind", "DownloadEnabled", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "OriginalDateiId")
			VALUES ('30000000-0000-0000-0000-00000000000a', '30000000-0000-0000-0000-000000000001', 'Konzert', 'audio', false, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000008');
		INSERT INTO recording_passages ("Id", "RecordingId", "PerformanceId", "EventId", "StartSeconds", "EndSeconds", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "PlaybackDateiId")
			VALUES (gen_random_uuid(), '30000000-0000-0000-0000-00000000000a', 'bb000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001', 1, 5, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000008'),
				(gen_random_uuid(), '30000000-0000-0000-0000-00000000000a', 'bb000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001', 6, 9, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000008'),
				(gen_random_uuid(), '30000000-0000-0000-0000-00000000000a', 'bb000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000001', 10, 12, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000008');
		""";
}
