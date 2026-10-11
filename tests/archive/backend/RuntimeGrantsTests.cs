using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// The runtime grants run inside <c>--migrate</c> so a hosted release needs no
/// manual grants step. The embedded script must stay the manual fallback's
/// twin; the PostgreSQL case runs the real script with the real roles on a
/// throwaway server (<c>ARCHIVE_TEST_POSTGRES</c>, a superuser) and skips
/// without it.
/// </summary>
public sealed class RuntimeGrantsTests(ITestOutputHelper output)
{
	[Fact]
	public void EmbeddedScriptIsTheInfrastructureScript()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
			directory = directory.Parent;
		Assert.NotNull(directory);
		var manual = File.ReadAllText(Path.Combine(directory.FullName, "infrastructure", "neon", "runtime-grants.sql"));

		using var stream = typeof(RuntimeGrants).Assembly.GetManifestResourceStream(RuntimeGrants.ResourceName);
		Assert.NotNull(stream);
		using var reader = new StreamReader(stream);

		Assert.Equal(manual, reader.ReadToEnd());
	}

	[Fact]
	public void ScriptDropsPsqlMetaCommandsAndKeepsTheStatements()
	{
		var script = RuntimeGrants.Script();

		Assert.DoesNotContain("\\set", script);
		Assert.Contains("BEGIN;", script);
		Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO archive_runtime;", script);
		Assert.Contains("REVOKE ALL ON TABLE public.\"__EFMigrationsHistory\" FROM archive_runtime;", script);
		Assert.Contains("COMMIT;", script);
	}

	[Fact]
	[Trait("Category", "PostgresRuntimeGrants")]
	public async Task MigratorRunGrantsTheRuntimeRoleAndAdminRunSkips()
	{
		var server = Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES");
		if (string.IsNullOrEmpty(server))
		{
			output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
			return;
		}

		const string password = "grants-test-only";
		await using (var admin = new NpgsqlConnection(server))
		{
			await admin.OpenAsync();
			await Execute(admin, "DROP DATABASE IF EXISTS archive WITH (FORCE)");
			await Execute(admin, "DROP ROLE IF EXISTS archive_runtime");
			await Execute(admin, "DROP ROLE IF EXISTS archive_migrator");
			await Execute(admin, $"CREATE ROLE archive_migrator LOGIN PASSWORD '{password}'");
			await Execute(admin, "CREATE ROLE archive_runtime LOGIN");
			await Execute(admin, "CREATE DATABASE archive OWNER archive_migrator");
		}

		try
		{
			var migrator = new NpgsqlConnectionStringBuilder(server)
			{
				Database = "archive", Username = "archive_migrator", Password = password, Pooling = false,
			}.ConnectionString;
			var adminOnArchive = new NpgsqlConnectionStringBuilder(server) { Database = "archive", Pooling = false }.ConnectionString;

			// An admin connection is not the migrator: nothing is granted.
			using (var adminContext = Context(adminOnArchive, out var adminServices))
			{
				using var _ = adminServices;
				Assert.False(await RuntimeGrants.ApplyAsync(adminContext, NullLogger.Instance, CancellationToken.None));
			}

			using var services = BuildServices(migrator);
			await using (var scope = services.CreateAsyncScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
				await db.Database.MigrateAsync();
				Assert.False(await HasPrivilege(db, "field_provenance", "INSERT"));

				Assert.True(await RuntimeGrants.ApplyAsync(db, NullLogger.Instance, CancellationToken.None));

				Assert.True(await HasPrivilege(db, "field_provenance", "SELECT, INSERT, UPDATE, DELETE"));
				Assert.True(await HasPrivilege(db, "proposals", "SELECT, INSERT, UPDATE, DELETE"));
				Assert.False(await HasPrivilege(db, "\"__EFMigrationsHistory\"", "SELECT"));

				// A table a later migration creates is covered by the default privileges.
				await db.Database.ExecuteSqlRawAsync("CREATE TABLE later_feature (id integer PRIMARY KEY)");
				Assert.True(await HasPrivilege(db, "later_feature", "SELECT, INSERT, UPDATE, DELETE"));

				// Running it again is a no-op.
				Assert.True(await RuntimeGrants.ApplyAsync(db, NullLogger.Instance, CancellationToken.None));
			}
		}
		finally
		{
			await using var admin = new NpgsqlConnection(server);
			await admin.OpenAsync();
			await Execute(admin, "DROP DATABASE IF EXISTS archive WITH (FORCE)");
			await Execute(admin, "DROP ROLE IF EXISTS archive_runtime");
			await Execute(admin, "DROP ROLE IF EXISTS archive_migrator");
		}
	}

	private static ServiceProvider BuildServices(string connection)
	{
		var collection = new ServiceCollection();
		collection.AddLogging();
		collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
		collection.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(connection));
		return collection.BuildServiceProvider();
	}

	private static ArchiveDbContext Context(string connection, out ServiceProvider services)
	{
		services = BuildServices(connection);
		return services.GetRequiredService<ArchiveDbContext>();
	}

	private static async Task<bool> HasPrivilege(ArchiveDbContext db, string table, string privileges)
	{
		var connection = (NpgsqlConnection)db.Database.GetDbConnection();
		await db.Database.OpenConnectionAsync();
		try
		{
			await using var command = new NpgsqlCommand(
				$"SELECT has_table_privilege('archive_runtime', 'public.{table}', '{privileges}')", connection);
			return (bool)(await command.ExecuteScalarAsync())!;
		}
		finally
		{
			await db.Database.CloseConnectionAsync();
		}
	}

	private static async Task Execute(NpgsqlConnection connection, string sql)
	{
		await using var command = new NpgsqlCommand(sql, connection);
		await command.ExecuteNonQueryAsync();
	}
}
