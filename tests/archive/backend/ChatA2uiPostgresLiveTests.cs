using System.Text.Json.Nodes;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-022-4 on real PostgreSQL (the API tests run on EF InMemory): the
/// migration adds <c>chat_messages."A2uiJson"</c> as <c>jsonb</c> without
/// touching existing messages and is reversible, the stored messages round-trip
/// through jsonb, and the card resolvers and the load-time re-check run their
/// queries (<c>Contains</c> over ids, published-only filters) as real SQL.
/// Every test creates, migrates and drops its own database. Skipped without
/// <c>ARCHIVE_TEST_POSTGRES</c>.
/// </summary>
public sealed class ChatA2uiPostgresLiveTests(ITestOutputHelper output)
{
	private const string PreviousMigration = "20261010174016_FieldProvenanceAndProposals";

	private static readonly DateTimeOffset October = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

	[Fact]
	[Trait("Category", "PostgresA2ui")]
	public async Task MigrationAddsAJsonbColumnKeepsExistingMessagesAndIsReversible()
	{
		if (!Configured(output))
			return;
		await using var database = await Database.CreateAsync();
		var threadId = Guid.NewGuid();
		var oldMessageId = Guid.NewGuid();
		await using (var scope = database.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			await MigrateToAsync(db, PreviousMigration);
			await ExecuteAsync(database, "INSERT INTO chat_threads (\"Id\", \"AccountId\", \"CreatedAt\", \"UpdatedAt\") VALUES (@t, @a, now(), now())",
				("t", threadId), ("a", Guid.NewGuid()));
			await ExecuteAsync(database,
				"INSERT INTO chat_messages (\"Id\", \"ThreadId\", \"Role\", \"Content\", \"CreatedAt\") VALUES (@m, @t, 'assistant', 'Alte Antwort', now())",
				("m", oldMessageId), ("t", threadId));
			Assert.Equal(0, await ColumnCountAsync(database));

			await db.Database.MigrateAsync();
		}

		await using (var scope = database.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal("jsonb", await ScalarAsync(database,
				"SELECT data_type FROM information_schema.columns WHERE table_name = 'chat_messages' AND column_name = 'A2uiJson'"));
			// The message that existed before keeps its text and has no surfaces.
			var old = await db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == oldMessageId);
			Assert.Equal("Alte Antwort", old.Content);
			Assert.Null(old.A2uiJson);

			// A stored surface round-trips through jsonb (key order is jsonb's, content is the same).
			db.ChatMessages.Add(new ChatMessage
			{
				ThreadId = threadId,
				Role = "assistant",
				Content = "Neue Antwort",
				CreatedAt = October,
				A2uiJson = "[{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"s1\",\"catalogId\":\"" + A2uiCatalog.CatalogId + "\"}}]",
			});
			await db.SaveChangesAsync();
			var stored = await db.ChatMessages.AsNoTracking().SingleAsync(m => m.Content == "Neue Antwort");
			var node = JsonNode.Parse(stored.A2uiJson!)!.AsArray();
			Assert.Equal(A2uiCatalog.CatalogId, node[0]!["createSurface"]!["catalogId"]!.GetValue<string>());
			// jsonb can be queried as such, which text could not.
			Assert.Equal("1", await ScalarAsync(database,
				"SELECT count(*) FROM chat_messages WHERE \"A2uiJson\" @> '[{\"version\":\"v0.9\"}]'::jsonb"));
		}

		await using (var scope = database.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			await MigrateToAsync(db, PreviousMigration);
			Assert.Equal(0, await ColumnCountAsync(database));
			Assert.Equal("2", await ScalarAsync(database, "SELECT count(*) FROM chat_messages"));
			await db.Database.MigrateAsync();
			Assert.Equal(1, await ColumnCountAsync(database));
		}
	}

	[Fact]
	[Trait("Category", "PostgresA2ui")]
	public async Task ResolversAndTheLoadTimeRecheckRunAsRealSqlAndHidePrivateRecords()
	{
		if (!Configured(output))
			return;
		await using var database = await Database.CreateAsync();
		await using var scope = database.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		await db.Database.MigrateAsync();
		var owner = Guid.NewGuid();
		var published = NewSong("Die Waldfahrt", owner, publishedAt: October);
		var other = NewSong("Abendlied", owner, publishedAt: October);
		var draft = NewSong("Geheime Probe", owner, publishedAt: null);
		var concert = NewEvent("Frühlingskonzert", owner, October);
		var internalEvent = NewEvent("Interne Beratung", owner, null);
		db.Songs.AddRange(published, other, draft);
		db.Events.AddRange(concert, internalEvent);
		await db.SaveChangesAsync();

		// ShowAsync: the model's request resolves against published records only.
		var state = new ChatRunState();
		var result = await A2uiCatalog.ShowAsync(db,
		[
			new SurfaceRequest("LiedKarte", LiedId: published.Id.ToString()),
			new SurfaceRequest("LiedKarte", LiedId: draft.Id.ToString()),
			new SurfaceRequest("AuftrittKarte", AuftrittId: concert.Id.ToString()),
			new SurfaceRequest("AuftrittKarte", AuftrittId: internalEvent.Id.ToString()),
			new SurfaceRequest("Quellen", LiedIds: [published.Id.ToString(), draft.Id.ToString(), other.Id.ToString(), "nein"]),
		], state, CancellationToken.None);

		Assert.Contains("\"verworfen\":2", result);
		Assert.Equal(3, state.SurfaceCount);
		var shown = state.SurfacesJson!;
		Assert.DoesNotContain("Geheime Probe", shown);
		Assert.DoesNotContain("Interne Beratung", shown);
		Assert.Contains(JsonNode.Parse(shown)!.AsArray(), m => m!["updateComponents"]?["components"]?[0]?["titel"]?.GetValue<string>() == "Frühlingskonzert");
		var sources = JsonNode.Parse(shown)!.AsArray()
			.Select(m => m!["updateComponents"]?["components"]?[0])
			.First(c => c?["component"]?.GetValue<string>() == "Quellen")!["quellen"]!.AsArray();
		Assert.Equal([published.Id.ToString(), other.Id.ToString()], sources.Select(q => q!["liedId"]!.GetValue<string>()).ToArray());

		// The stored form re-checks against today's visibility on load.
		published.PublishedAt = null;
		concert.PublishedAt = null;
		await db.SaveChangesAsync();
		var restored = await A2uiCatalog.RecheckAsync(db, shown, CancellationToken.None);
		var remaining = JsonNode.Parse(new JsonArray(restored.ToArray<JsonNode?>()).ToJsonString())!.AsArray()
			.Select(m => m!["updateComponents"]?["components"]?[0]).Where(c => c is not null).ToList();
		var only = Assert.Single(remaining);
		Assert.Equal("Quellen", only!["component"]!.GetValue<string>());
		Assert.Equal([other.Id.ToString()], only["quellen"]!.AsArray().Select(q => q!["liedId"]!.GetValue<string>()).ToArray());
	}

	private static Task MigrateToAsync(ArchiveDbContext db, string target)
		=> Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db.Database)
			.MigrateAsync(target);

	private static Song NewSong(string title, Guid owner, DateTimeOffset? publishedAt) => new()
	{
		Title = title,
		Composer = "Friedrich Silcher",
		Lyrics = "Die Sänger wandern gemeinsam durch den Wald.\nUnd singen ihre Lieder.",
		PublishedAt = publishedAt,
		PublishedByAccountId = publishedAt is null ? null : owner,
		CreatedAt = October,
		CreatedByAccountId = owner,
		UpdatedAt = October,
		UpdatedByAccountId = owner,
	};

	private static ChoirEvent NewEvent(string title, Guid owner, DateTimeOffset? publishedAt) => new()
	{
		Kind = "concert",
		Title = title,
		Venue = "Festsaal",
		DateYear = 1932,
		DateMonth = 5,
		PublishedAt = publishedAt,
		PublishedByAccountId = publishedAt is null ? null : owner,
		CreatedAt = October,
		CreatedByAccountId = owner,
		UpdatedAt = October,
		UpdatedByAccountId = owner,
	};

	private static async Task<int> ColumnCountAsync(Database database)
		=> int.Parse(await ScalarAsync(database,
			"SELECT count(*) FROM information_schema.columns WHERE table_name = 'chat_messages' AND column_name = 'A2uiJson'"));

	private static async Task ExecuteAsync(Database database, string sql, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(database.Connection);
		await connection.OpenAsync();
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
			command.Parameters.AddWithValue(name, value);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<string> ScalarAsync(Database database, string sql)
	{
		await using var connection = new NpgsqlConnection(database.Connection);
		await connection.OpenAsync();
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
	}

	private static string? Server => Environment.GetEnvironmentVariable("ARCHIVE_TEST_POSTGRES") is { Length: > 0 } value ? value : null;

	private static bool Configured(ITestOutputHelper output)
	{
		if (Server is not null)
			return true;
		output.WriteLine("Skipped: set ARCHIVE_TEST_POSTGRES to a throwaway PostgreSQL connection string.");
		return false;
	}

	/// <summary>A migrated-on-demand database of its own, dropped on disposal.</summary>
	private sealed class Database : IAsyncDisposable
	{
		private readonly string name;

		private Database(string name, string connection, ServiceProvider services)
		{
			this.name = name;
			Connection = connection;
			Services = services;
		}

		public string Connection { get; }

		public ServiceProvider Services { get; }

		public static async Task<Database> CreateAsync()
		{
			var server = Server ?? throw new InvalidOperationException("ARCHIVE_TEST_POSTGRES is not set.");
			var name = $"a2ui_{Guid.NewGuid():N}";
			await using (var admin = new NpgsqlConnection(server))
			{
				await admin.OpenAsync();
				await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
				await create.ExecuteNonQueryAsync();
			}
			var connection = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
			var collection = new ServiceCollection();
			collection.AddLogging();
			collection.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
			collection.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(connection));
			return new Database(name, connection, collection.BuildServiceProvider());
		}

		public async ValueTask DisposeAsync()
		{
			await Services.DisposeAsync();
			NpgsqlConnection.ClearAllPools();
			await using var admin = new NpgsqlConnection(Server);
			await admin.OpenAsync();
			await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
			await drop.ExecuteNonQueryAsync();
		}
	}
}
