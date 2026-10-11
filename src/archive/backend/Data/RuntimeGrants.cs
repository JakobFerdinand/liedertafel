using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Archive.Backend.Data;

/// <summary>
/// Gives the runtime role its table and sequence privileges after the
/// migrations ran, so a release needs no separate manual grants step. It is
/// <c>runtime-grants.sql</c> embedded in the image (a byte-identical copy of
/// <c>infrastructure/neon/runtime-grants.sql</c>, which stays the manual
/// fallback; a test keeps the two equal). The script is idempotent and runs
/// in its own transaction. It only runs when connected as
/// <see cref="MigratorRole"/>: local runs connect as an admin and skip it, and
/// the script itself refuses any other role or database.
/// </summary>
public static class RuntimeGrants
{
	public const string MigratorRole = "archive_migrator";

	public const string ResourceName = "runtime-grants.sql";

	/// <summary>
	/// Applies the grants when the connection is the migrator role; returns
	/// whether they ran. A failure throws, so the maintenance window stays open.
	/// </summary>
	public static async Task<bool> ApplyAsync(ArchiveDbContext db, ILogger logger, CancellationToken token)
	{
		await db.Database.OpenConnectionAsync(token);
		try
		{
			var connection = (NpgsqlConnection)db.Database.GetDbConnection();
			await using (var who = new NpgsqlCommand("SELECT current_user", connection))
			{
				var user = (string?)await who.ExecuteScalarAsync(token);
				if (user != MigratorRole)
				{
					logger.LogInformation("Runtime grants skipped: connected as {Role}, not {MigratorRole}", user, MigratorRole);
					return false;
				}
			}
			await using var grants = new NpgsqlCommand(Script(), connection);
			await grants.ExecuteNonQueryAsync(token);
			logger.LogInformation("Runtime grants applied");
			return true;
		}
		finally
		{
			await db.Database.CloseConnectionAsync();
		}
	}

	/// <summary>The embedded script without psql meta-commands (lines starting with a backslash).</summary>
	internal static string Script()
	{
		using var stream = typeof(RuntimeGrants).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException("Embedded runtime-grants.sql is missing.");
		using var reader = new StreamReader(stream);
		var lines = reader.ReadToEnd().Split('\n').Where(line => !line.TrimStart().StartsWith('\\'));
		return string.Join('\n', lines);
	}
}
