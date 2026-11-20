using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting;
using Npgsql;

namespace Archive.Rust.AppHost.Tests;

internal static class RustStack
{
    private const string BlobHost = "archive-blobs-235";

    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(CancellationToken token) =>
        DistributedApplicationTestingBuilder.CreateAsync<Projects.Archive_Rust_AppHost>(
            [], (options, _) => options.DisableDashboard = false, token);

    // DCP reports container exits as Exited, where the frozen ProjectResource
    // reports Finished. A terminal notification alone never proves success.
    public static bool IsExited(ResourceEvent update) =>
        update.Snapshot.State?.Text == KnownResourceStates.Exited
        || update.Snapshot.State?.Text == KnownResourceStates.Finished;

    public static HttpClient BlobClient(DistributedApplication app)
    {
        var local = app.GetEndpoint("archive-storage", "blob");
        return new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                if (context.DnsEndPoint.Host != BlobHost || context.DnsEndPoint.Port != 10000)
                    throw new InvalidOperationException("Expected a private Azurite ticket for this stack.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(local.Host, local.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
    }

    public static async Task AssertImageOriginAsync(DistributedApplication app, CancellationToken token)
    {
        // A real dashboard supplied the injected endpoint. Treat its URL as
        // sensitive (it may contain a local login token); never log the URI.
        var dashboard = await app.GetDashboardUrlAsync(token);
        Assert.True(dashboard.IsLoopback, "Expected the local Aspire dashboard.");
        using var origin = app.CreateHttpClient("archive-api", "http");
        using var proxy = app.CreateHttpClient("archive-frontend", "http");
        var html = await origin.GetStringAsync("/system/status/", token);
        Assert.Contains("Systemstatus", html);
        var nextAsset = System.Text.RegularExpressions.Regex.Match(html, "src=\"(/_next/[^\"]+\\.js)\"");
        Assert.True(nextAsset.Success, "The image must serve the real Next.js export.");
        Assert.NotEmpty(await origin.GetByteArrayAsync(nextAsset.Groups[1].Value, token));
        var build = await origin.GetFromJsonAsync<JsonElement>("/api/build", token);
        var fromProxy = await proxy.GetFromJsonAsync<JsonElement>("/api/build", token);
        Assert.True(build.GetProperty("development").GetBoolean());
        Assert.True(fromProxy.GetProperty("development").GetBoolean());
        Assert.Equal(build.GetProperty("version").GetString(), fromProxy.GetProperty("version").GetString());
        Assert.Equal(Environment.GetEnvironmentVariable("ARCHIVE_EXPECTED_IMAGE_VERSION")
            ?? throw new InvalidOperationException("Set ARCHIVE_EXPECTED_IMAGE_VERSION to the image's compile-time VERSION."),
            build.GetProperty("version").GetString());
        using var miss = await origin.GetAsync("/api/no-such-rust-image-route", token);
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        Assert.Equal("application/problem+json", miss.Content.Headers.ContentType?.MediaType);
    }

    public static async Task AssertPendingBaselineAsync(DistributedApplication app, CancellationToken token)
    {
        await using var connection = await OpenDatabaseAsync(app, runtime: false, token);
        await AssertSqlAsync(connection, """
            SELECT count(*) = 0
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r','p','S','v','m','f')
            """, token);
        await AssertSqlAsync(connection, """
            SELECT NOT EXISTS (SELECT FROM pg_collation c JOIN pg_namespace n
                ON c.collnamespace = n.oid WHERE n.nspname = 'public' AND c.collname = 'archive_culture')
            """, token);
    }

    public static async Task AssertAppliedBaselineAndGrantsAsync(DistributedApplication app, CancellationToken token)
    {
        await using var connection = await OpenDatabaseAsync(app, runtime: false, token);
        await AssertSqlAsync(connection, """
            SELECT array_agg(version ORDER BY version) = ARRAY[1,2,3,4]::bigint[]
                AND bool_and(success AND length(checksum) = 48)
                AND (array_agg(description ORDER BY version))[1] = 'frozen ef baseline'
            FROM public._sqlx_migrations
            """, token);
        await using (var history = new NpgsqlCommand(
            "SELECT version, checksum FROM public._sqlx_migrations ORDER BY version", connection))
        await using (var reader = await history.ExecuteReaderAsync(token))
        {
            var migrations = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "sqlx"), "*.sql");
            var seen = 0;
            while (await reader.ReadAsync(token))
            {
                var number = reader.GetInt64(0);
                var file = Assert.Single(migrations, path => Path.GetFileName(path).StartsWith($"{number:0000}_", StringComparison.Ordinal));
                Assert.Equal(SHA384.HashData(await File.ReadAllBytesAsync(file, token)), reader.GetFieldValue<byte[]>(1));
                seen++;
            }
            Assert.Equal(migrations.Length, seen);
        }

        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "baseline-provenance.json"), token));
        var expected = baseline.RootElement.GetProperty("ef_history").EnumerateArray().Select(
            row => (row.GetProperty("MigrationId").GetString(), row.GetProperty("ProductVersion").GetString())).ToArray();
        var actual = new List<(string?, string?)>();
        await using (var history = new NpgsqlCommand(
            """SELECT "MigrationId", "ProductVersion" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" COLLATE "C" """, connection))
        await using (var reader = await history.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                actual.Add((reader.GetString(0), reader.GetString(1)));
        Assert.Equal(expected, actual);

        await AssertSqlAsync(connection, """
            SELECT bool_and(to_regclass('public.' || name) IS NOT NULL)
            FROM unnest(ARRAY[
                '"AspNetUsers"', 'auth_sign_in_codes', 'auth_request_log',
                'archive_sessions', 'archive_auth_allocation_budgets',
                'events', 'programmes', 'programme_items', 'recording_passages',
                'lieder', 'dateien', 'upload_sessions'
            ]) AS required(name)
            """, token);
        await AssertSqlAsync(connection, """
            SELECT bool_and(to_regclass('public.' || name) IS NULL)
            FROM unnest(ARRAY['songs','arrangements','musical_versions','assets','file_revisions']) AS obsolete(name)
            """, token);
        await AssertSqlAsync(connection, """
            SELECT to_regclass('public."IX_auth_sign_in_codes_ExpiresAt"') IS NOT NULL
                AND to_regclass('public."IX_programmes_EventId"') IS NOT NULL
                AND to_regclass('public."IX_recording_passages_RecordingId_ProgrammeItemId"') IS NOT NULL
                AND EXISTS (SELECT FROM pg_collation WHERE oid = 'public.archive_culture'::regcollation AND NOT collisdeterministic)
                AND (SELECT count(*) = 2 FROM pg_class WHERE relname IN ('_sqlx_migrations','__EFMigrationsHistory')
                    AND pg_get_userbyid(relowner) = 'archive_migrator')
            """, token);

        await using var runtime = await OpenDatabaseAsync(app, runtime: true, token);
        await AssertSqlAsync(runtime, """
            SELECT current_user = 'archive_runtime'
                AND has_schema_privilege('public', 'USAGE')
                AND NOT has_schema_privilege('public', 'CREATE')
                AND NOT has_database_privilege(current_database(), 'TEMP')
                AND NOT pg_has_role('archive_migrator', 'MEMBER')
                AND NOT has_table_privilege('public._sqlx_migrations', 'SELECT,INSERT,UPDATE,DELETE')
                AND NOT has_table_privilege('public."__EFMigrationsHistory"', 'SELECT,INSERT,UPDATE,DELETE')
                AND EXISTS (SELECT FROM public.lieder HAVING count(*) = 0)
            """, token);
        foreach (var sql in new[]
        {
            "CREATE TABLE public.denied (id integer)", "CREATE TEMP TABLE denied (id integer)",
            "SET ROLE archive_migrator", "SELECT * FROM public._sqlx_migrations",
            """SELECT * FROM public."__EFMigrationsHistory" """,
        })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(token));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    private static async Task<NpgsqlConnection> OpenDatabaseAsync(DistributedApplication app, bool runtime, CancellationToken token)
    {
        var builder = new NpgsqlConnectionStringBuilder(await app.GetConnectionStringAsync("archive-db", token))
        {
            Username = runtime ? "archive_runtime" : "archive_admin",
            Pooling = false,
        };
        if (runtime) builder.Password = "rust235-runtime";
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task AssertAdoptionAsync(DistributedApplication app, ResourceCommandService commands, CancellationToken token)
    {
        await app.ResourceNotifications.WaitForResourceHealthyAsync("archive-adoption-db", token);
        var config = new NpgsqlConnectionStringBuilder(
            await app.GetConnectionStringAsync("archive-adoption-db", token)) { Pooling = false };
        await using var database = new NpgsqlConnection(config.ConnectionString);
        await database.OpenAsync(token);
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "baseline-schema.sql"), token);
        var history = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "baseline-history.sql"), token);
        await using (var seed = new NpgsqlCommand("DROP SCHEMA public;\n" + schema + history + """

            INSERT INTO public.lieder
                ("Id", "Title", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
            VALUES ('23500000-0000-4000-8000-000000000001', 'Vorhandenes synthetisches Lied', now(),
                '23500000-0000-4000-8000-000000000002', now(), '23500000-0000-4000-8000-000000000002', 12);
            """, database))
            await seed.ExecuteNonQueryAsync(token);
        await AssertSqlAsync(database, "SELECT to_regclass('public._sqlx_migrations') IS NULL", token);
        await app.ResourceNotifications.WaitForResourceAsync("archive-adopt-check", KnownResourceStates.NotStarted, token);
        var started = await commands.ExecuteCommandAsync("archive-adopt-check", "start", token);
        Assert.True(started.Success);
        var finished = await app.ResourceNotifications.WaitForResourceAsync("archive-adopt-check", update =>
            update.Snapshot.ExitCode is not null, token);
        Assert.True(IsExited(finished));
        Assert.Equal(0, finished.Snapshot.ExitCode);
        await AssertSqlAsync(database, """
            SELECT (SELECT count(*) = 44 FROM public."__EFMigrationsHistory")
                AND (SELECT array_agg(version ORDER BY version) = ARRAY[1,2,3,4]::bigint[]
                     AND bool_and(success) FROM public._sqlx_migrations)
                AND EXISTS (SELECT FROM public.lieder WHERE "Id" = '23500000-0000-4000-8000-000000000001'
                       AND "Title" = 'Vorhandenes synthetisches Lied' AND "RowVersion" = 12)
            """, token);
    }

    private static async Task AssertSqlAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        Assert.Equal(true, await command.ExecuteScalarAsync(token));
    }

    public static async Task AssertPlayableH264Async(byte[] copy, CancellationToken token)
    {
        // Use the candidate image's own tools; stdin is enough for the
        // faststart MP4. No host ffmpeg, bind mounts or external data.
        var tag = Environment.GetEnvironmentVariable("Archive__TestImageTag")
            ?? throw new InvalidOperationException("Set Archive__TestImageTag.");
        var probe = await RunToolAsync(tag, "ffprobe", copy, token,
            "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", "-i", "pipe:0");
        using var json = JsonDocument.Parse(probe);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Contains(streams, stream => stream.GetProperty("codec_type").GetString() == "video"
            && stream.GetProperty("codec_name").GetString() == "h264");
        Assert.Contains(streams, stream => stream.GetProperty("codec_type").GetString() == "audio"
            && stream.GetProperty("codec_name").GetString() == "aac");
        await RunToolAsync(tag, "ffmpeg", copy, token,
            "-v", "error", "-xerror", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-");
    }

    private static async Task<string> RunToolAsync(string tag, string tool, byte[] bytes, CancellationToken token, params string[] args)
    {
        var runtime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") ?? "docker";
        var start = new ProcessStartInfo(runtime)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[] { "run", "--rm", "--pull=never", "--network=none", "-i", "--entrypoint", tool, $"localhost/liedertafel-archive:{tag}" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.BaseStream.WriteAsync(bytes, token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
            return await stdout;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
