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
/// #172 contract step on real PostgreSQL: the database is migrated to the last
/// migration before the drop, seeded with accounts, a passkey and the old
/// catalogue (songs, assets, revisions, Auftritt rows that only know a song)
/// beside rows of the new model, and then migrated to the drop. The accounts and
/// the new model must survive untouched; the old tables, the old columns and
/// every row that only made sense through them must be gone.
/// Skipped without <c>ARCHIVE_TEST_POSTGRES</c>.
/// </summary>
public sealed class OldModelContractPostgresTests(ITestOutputHelper output)
{
	private const string PreviousMigration = "20261017185113_AuftrittDateien";

	/// <summary>The migration under test; later migrations (like the Programm simplification) have their own tests.</summary>
	private const string TargetMigration = "20261017232616_DropOldCatalogue";

	private static readonly string[] OldTables =
	[
		"songs", "song_titles", "song_tags", "arrangements", "musical_versions",
		"assets", "file_revisions", "asset_revision_changes",
	];

	[Fact]
	[Trait("Category", "PostgresContract")]
	public async Task TheDropKeepsAccountsAndTheNewModelAndRemovesTheOldOne()
	{
		var server = Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES");
		if (string.IsNullOrEmpty(server))
		{
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return;
		}
		var owned = $"contract_{Guid.NewGuid():N}";
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
				await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().GetService<IMigrator>().MigrateAsync(TargetMigration);

			// Accounts, roles and the passkey are untouched.
			Assert.Equal(1, await Count(raw, "\"AspNetUsers\""));
			Assert.Equal(1, await Count(raw, "\"AspNetRoles\""));
			Assert.Equal(1, await Count(raw, "\"AspNetUserRoles\""));
			Assert.Equal(1, await Count(raw, "\"AspNetUserPasskeys\""));

			// The old tables are gone.
			foreach (var table in OldTables)
				Assert.False(await TableExists(raw, table), $"{table} must be dropped");
			// So are the old columns.
			foreach (var (table, column) in new[]
			{
				("programme_items", "SongId"), ("programme_items", "ArrangementId"), ("programme_items", "MusicalVersionId"),
				("performances", "SongId"), ("performances", "ArrangementId"), ("performances", "MusicalVersionId"),
				("recordings", "OriginalAssetId"), ("recordings", "PlaybackAssetId"),
				("recording_passages", "PlaybackRevisionId"),
				("upload_sessions", "AssetId"), ("upload_sessions", "FinalizedRevisionId"),
			})
				Assert.False(await ColumnExists(raw, table, column), $"{table}.{column} must be dropped");

			// New model rows survive; rows that only knew the old chain are gone.
			Assert.Equal(1, await Count(raw, "lieder"));
			Assert.Equal(2, await Count(raw, "dateien"));
			Assert.Equal(1, await Count(raw, "events"));
			Assert.Equal(1, await Count(raw, "programme_items"));
			Assert.Equal(1, await Count(raw, "performances"));
			Assert.Equal(1, await Count(raw, "recordings"));
			Assert.Equal(1, await Count(raw, "recording_passages"));
			// Only the new-model upload session remains.
			Assert.Equal(1, await Count(raw, "upload_sessions"));

			// The owner rule of upload sessions now covers Lied or Auftritt only.
			await using var bothNull = new NpgsqlCommand(
				"INSERT INTO upload_sessions (\"Id\", \"BlobName\", \"ContentType\", \"MaxSizeBytes\", \"UploadTicketExpiresAt\", \"State\", \"CreatedByAccountId\", \"CreatedAt\")"
				+ " VALUES (gen_random_uuid(), 'pending/none', 'application/pdf', 1, now(), 0, gen_random_uuid(), now())", raw);
			Assert.Equal("CK_upload_sessions_owner", (await Assert.ThrowsAsync<PostgresException>(() => bothNull.ExecuteNonQueryAsync())).ConstraintName);
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

	private static async Task<bool> ColumnExists(NpgsqlConnection connection, string table, string column)
	{
		await using var command = new NpgsqlCommand(
			"SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c", connection);
		command.Parameters.AddWithValue("t", table);
		command.Parameters.AddWithValue("c", column);
		return (long)(await command.ExecuteScalarAsync())! > 0;
	}

	// Ids are fixed so the statements can reference each other. "u" is the account everything is created by.
	private const string SeedSql = """
		INSERT INTO "AspNetUsers" ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "Email", "NormalizedEmail", "UserName", "NormalizedUserName")
			VALUES ('00000000-0000-0000-0000-00000000000a', true, false, false, false, 0, 'redaktion@liedertafel.test', 'REDAKTION@LIEDERTAFEL.TEST', 'redaktion@liedertafel.test', 'REDAKTION@LIEDERTAFEL.TEST');
		INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName") VALUES ('00000000-0000-0000-0000-00000000000b', 'Editor', 'EDITOR');
		INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES ('00000000-0000-0000-0000-00000000000a', '00000000-0000-0000-0000-00000000000b');
		INSERT INTO "AspNetUserPasskeys" ("CredentialId", "UserId", "Data") VALUES ('\x0102'::bytea, '00000000-0000-0000-0000-00000000000a', '{}'::jsonb);

		-- Old catalogue: song -> arrangement -> version -> asset -> revision, with a history entry and a session.
		INSERT INTO songs ("Id", "Title", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('10000000-0000-0000-0000-000000000001', 'Altes Lied', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO song_titles ("Id", "Position", "SongId", "Value", "CreatedAt", "CreatedByAccountId")
			VALUES (gen_random_uuid(), 0, '10000000-0000-0000-0000-000000000001', 'Anderer Titel', now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO song_tags ("Id", "Position", "SongId", "Value", "CreatedAt", "CreatedByAccountId")
			VALUES (gen_random_uuid(), 0, '10000000-0000-0000-0000-000000000001', 'Advent', now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO arrangements ("Id", "SongId", "Label", "CreatedAt", "CreatedByAccountId")
			VALUES ('10000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'Chorsatz', now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO musical_versions ("Id", "ArrangementId", "Label", "CreatedAt", "CreatedByAccountId")
			VALUES ('10000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000002', 'Original', now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO assets ("Id", "MusicalVersionId", "CreatedByAccountId", "CreatedAt", "RowVersion")
			VALUES ('10000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-00000000000a', now(), 1);
		INSERT INTO file_revisions ("Id", "AssetId", "RevisionNumber", "BlobName", "ContentType", "SizeBytes", "CreatedByAccountId", "CreatedAt")
			VALUES ('10000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000004', 1, 'revisions/alt', 'application/pdf', 10, '00000000-0000-0000-0000-00000000000a', now());
		UPDATE assets SET "CurrentRevisionId" = '10000000-0000-0000-0000-000000000005' WHERE "Id" = '10000000-0000-0000-0000-000000000004';
		INSERT INTO asset_revision_changes ("Id", "AssetId", "RevisionId", "Kind", "ChangedByAccountId", "ChangedAt")
			VALUES (gen_random_uuid(), '10000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000005', 0, '00000000-0000-0000-0000-00000000000a', now());
		INSERT INTO upload_sessions ("Id", "AssetId", "BlobName", "ContentType", "MaxSizeBytes", "UploadTicketExpiresAt", "State", "CreatedByAccountId", "CreatedAt")
			VALUES (gen_random_uuid(), '10000000-0000-0000-0000-000000000004', 'pending/alt', 'application/pdf', 10, now(), 0, '00000000-0000-0000-0000-00000000000a', now());

		-- New model: a Lied with a Noten Datei, and a session for it.
		INSERT INTO lieder ("Id", "Title", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('20000000-0000-0000-0000-000000000001', 'Neues Lied', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO dateien ("Id", "LiedId", "Kind", "IsPartitur", "BlobName", "ContentType", "SizeBytes", "CreatedByAccountId", "CreatedAt", "RowVersion")
			VALUES ('20000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000001', 'noten', true, 'dateien/neu', 'application/pdf', 10, '00000000-0000-0000-0000-00000000000a', now(), 1);
		INSERT INTO upload_sessions ("Id", "LiedId", "DateiKind", "BlobName", "ContentType", "MaxSizeBytes", "UploadTicketExpiresAt", "State", "CreatedByAccountId", "CreatedAt")
			VALUES (gen_random_uuid(), '20000000-0000-0000-0000-000000000001', 'noten', 'pending/neu', 'application/pdf', 10, now(), 0, '00000000-0000-0000-0000-00000000000a', now());

		-- One Auftritt with a programme of two items (old chain / Lied), two performances, two recordings, passages.
		INSERT INTO events ("Id", "Kind", "Title", "DateApproximate", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000001', 'concert', 'Adventkonzert', false, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO programmes ("Id", "EventId", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1);
		INSERT INTO programme_revisions ("Id", "ProgrammeId", "Number", "CreatedAt", "CreatedByAccountId")
			VALUES ('30000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000002', 1, now(), '00000000-0000-0000-0000-00000000000a');
		INSERT INTO programme_items ("Id", "RevisionId", "Position", "SongId", "ArrangementId", "MusicalVersionId")
			VALUES ('30000000-0000-0000-0000-000000000004', '30000000-0000-0000-0000-000000000003', 0,
				'10000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000003');
		INSERT INTO programme_items ("Id", "RevisionId", "Position", "LiedId")
			VALUES ('30000000-0000-0000-0000-000000000005', '30000000-0000-0000-0000-000000000003', 1, '20000000-0000-0000-0000-000000000001');
		INSERT INTO performances ("Id", "EventId", "Position", "EvidenceStatus", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "SongId", "ProgrammeItemId")
			VALUES ('30000000-0000-0000-0000-000000000006', '30000000-0000-0000-0000-000000000001', 0, 'mentioned', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1,
				'10000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000004');
		INSERT INTO performances ("Id", "EventId", "Position", "EvidenceStatus", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "LiedId", "ProgrammeItemId")
			VALUES ('30000000-0000-0000-0000-000000000007', '30000000-0000-0000-0000-000000000001', 1, 'mentioned', now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1,
				'20000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000005');
		INSERT INTO dateien ("Id", "EventId", "Kind", "IsPartitur", "BlobName", "ContentType", "SizeBytes", "CreatedByAccountId", "CreatedAt", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000008', '30000000-0000-0000-0000-000000000001', 'aufnahme', false, 'dateien/aufnahme', 'audio/mpeg', 10, '00000000-0000-0000-0000-00000000000a', now(), 1);
		INSERT INTO assets ("Id", "EventId", "CreatedByAccountId", "CreatedAt", "RowVersion")
			VALUES ('30000000-0000-0000-0000-000000000009', '30000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-00000000000a', now(), 1);
		INSERT INTO recordings ("Id", "EventId", "Label", "Kind", "DownloadEnabled", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "OriginalDateiId")
			VALUES ('30000000-0000-0000-0000-00000000000a', '30000000-0000-0000-0000-000000000001', 'Neu', 'audio', false, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000008');
		INSERT INTO recordings ("Id", "EventId", "Label", "Kind", "DownloadEnabled", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "OriginalAssetId")
			VALUES ('30000000-0000-0000-0000-00000000000b', '30000000-0000-0000-0000-000000000001', 'Alt', 'audio', false, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000009');
		INSERT INTO recording_passages ("Id", "RecordingId", "PerformanceId", "EventId", "StartSeconds", "EndSeconds", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "PlaybackDateiId")
			VALUES (gen_random_uuid(), '30000000-0000-0000-0000-00000000000a', '30000000-0000-0000-0000-000000000007', '30000000-0000-0000-0000-000000000001', 1, 5, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '30000000-0000-0000-0000-000000000008');
		INSERT INTO recording_passages ("Id", "RecordingId", "PerformanceId", "EventId", "StartSeconds", "EndSeconds", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion", "PlaybackRevisionId")
			VALUES (gen_random_uuid(), '30000000-0000-0000-0000-00000000000b', '30000000-0000-0000-0000-000000000006', '30000000-0000-0000-0000-000000000001', 1, 5, now(), '00000000-0000-0000-0000-00000000000a', now(), '00000000-0000-0000-0000-00000000000a', 1, '10000000-0000-0000-0000-000000000005');
		""";
}
