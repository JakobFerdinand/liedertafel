using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-033 score corrections at the HTTP seam: a replacement adds an
/// immutable revision under the same asset, members and old links follow the
/// current pointer, editors see the retained history with attribution, read
/// old revisions through their own tickets and can make an earlier revision
/// current again.
/// </summary>
public sealed partial class AssetApiTests
{
	[Fact]
	public async Task ReplacingAScoreKeepsTheAssetAndServesTheNewFileToMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 1024);
		using (var publish = await PostJsonAsync(client, $"/api/songs/{songId}/publish", new { }, editorSession))
			Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		var firstBlob = await RevisionBlobAsync(factory, RevisionId(first));

		var (_, _, second) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 2048);

		Assert.Equal(2, second.GetProperty("revisionNumber").GetInt32());
		Assert.NotEqual(RevisionId(first), RevisionId(second));
		var secondBlob = await RevisionBlobAsync(factory, RevisionId(second));
		Assert.NotEqual(firstBlob, secondBlob);
		// The earlier file is retained, not overwritten.
		Assert.True(factory.Storage.Has(firstBlob));
		Assert.True(factory.Storage.Has(secondBlob));

		// The link a member already holds (same asset id) now resolves the correction.
		var access = await GetJsonAsync(client, $"/api/assets/{assetId}/access", memberSession);
		Assert.Equal(RevisionId(second), Guid.Parse(access.GetProperty("revisionId").GetString()!));
		Assert.Equal(2048, access.GetProperty("sizeBytes").GetInt64());
		Assert.Equal(secondBlob, factory.Storage.Find(access.GetProperty("viewUrl").GetString()!)!.BlobName);
		Assert.Equal(secondBlob, factory.Storage.Find(access.GetProperty("downloadUrl").GetString()!)!.BlobName);

		// A correction is a file revision, never a new arrangement, version or material entry.
		var detail = await GetSongDetailAsync(client, memberSession, songId);
		var arrangement = Assert.Single(detail.GetProperty("arrangements").EnumerateArray());
		var version = Assert.Single(arrangement.GetProperty("musicalVersions").EnumerateArray());
		Assert.Equal(versionId, Guid.Parse(version.GetProperty("id").GetString()!));
		var asset = Assert.Single(version.GetProperty("assets").EnumerateArray());
		Assert.Equal(assetId, Guid.Parse(asset.GetProperty("id").GetString()!));
		Assert.Equal(2, asset.GetProperty("currentRevision").GetProperty("revisionNumber").GetInt32());
		// Member payloads carry no revision history.
		Assert.False(asset.TryGetProperty("revisions", out _));
	}

	[Fact]
	public async Task EditorHistoryListsRetainedRevisionsWithAttribution()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var firstEditorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var secondEditorId = await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		await NameAsync(factory, firstEditorId, "Anna Erstredaktion");
		await NameAsync(factory, secondEditorId, "Bernd Zweitredaktion");
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);

		// An asset without a file still belongs to the editor who started it.
		using (var foreign = await PostJsonAsync(client, $"/api/assets/{assetId}/upload-session", new { }, secondSession))
			Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

		var first = await UploadNamedAsync(factory, client, editorSession, assetId, "wandern.pdf", 1024);
		// Any editor may correct a score that already exists.
		var second = await UploadNamedAsync(factory, client, secondSession, assetId, "wandern-korrigiert.pdf", 2048);

		var history = await GetJsonAsync(client, $"/api/assets/{assetId}/revisions", secondSession);
		Assert.Equal(RevisionId(second), Guid.Parse(history.GetProperty("currentRevisionId").GetString()!));
		var revisions = history.GetProperty("revisions").EnumerateArray().ToList();
		Assert.Equal(2, revisions.Count);
		Assert.Equal(2, revisions[0].GetProperty("revisionNumber").GetInt32());
		Assert.True(revisions[0].GetProperty("isCurrent").GetBoolean());
		Assert.Equal("wandern-korrigiert.pdf", revisions[0].GetProperty("fileName").GetString());
		Assert.Equal("Bernd Zweitredaktion", revisions[0].GetProperty("createdBy").GetString());
		Assert.Equal(2048, revisions[0].GetProperty("sizeBytes").GetInt64());
		Assert.Equal(1, revisions[1].GetProperty("revisionNumber").GetInt32());
		Assert.Equal(RevisionId(first), Guid.Parse(revisions[1].GetProperty("revisionId").GetString()!));
		Assert.False(revisions[1].GetProperty("isCurrent").GetBoolean());
		Assert.Equal("wandern.pdf", revisions[1].GetProperty("fileName").GetString());
		Assert.Equal("Anna Erstredaktion", revisions[1].GetProperty("createdBy").GetString());
		var changes = history.GetProperty("changes").EnumerateArray().ToList();
		Assert.Equal(2, changes.Count);
		Assert.Equal("upload", changes[0].GetProperty("kind").GetString());
		Assert.Equal(2, changes[0].GetProperty("revisionNumber").GetInt32());
		Assert.Equal(1, changes[0].GetProperty("previousRevisionNumber").GetInt32());
		Assert.Equal("Bernd Zweitredaktion", changes[0].GetProperty("changedBy").GetString());
		Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("previousRevisionNumber").ValueKind);
		// Storage locations never leave the server.
		Assert.DoesNotContain("revisions/", history.GetRawText());

		using var memberResponse = await GetAsync(client, $"/api/assets/{assetId}/revisions", memberSession);
		Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
		using var anonymous = await client.GetAsync($"/api/assets/{assetId}/revisions");
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		using var unknown = await GetAsync(client, $"/api/assets/{Guid.NewGuid()}/revisions", editorSession);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
	}

	[Fact]
	public async Task EarlierRevisionTicketsAreIssuedToEditorsOnly()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var otherAssetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 1024);
		await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 2048);
		var (_, _, other) = await UploadAndFinalizeAsync(factory, client, editorSession, otherAssetId);
		using (var publish = await PostJsonAsync(client, $"/api/songs/{songId}/publish", new { }, editorSession))
			Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		var firstBlob = await RevisionBlobAsync(factory, RevisionId(first));
		var path = $"/api/assets/{assetId}/revisions/{RevisionId(first)}/access";

		var access = await GetJsonAsync(client, path, editorSession);
		Assert.Equal(1, access.GetProperty("revisionNumber").GetInt32());
		var view = factory.Storage.Find(access.GetProperty("viewUrl").GetString()!)!;
		var download = factory.Storage.Find(access.GetProperty("downloadUrl").GetString()!)!;
		Assert.Equal(firstBlob, view.BlobName);
		Assert.Equal(firstBlob, download.BlobName);
		Assert.True(download.Download);
		Assert.Equal(TimeSpan.FromMinutes(15), view.Lifetime);

		var ticketsBefore = factory.Storage.Tickets.Count;
		using var memberResponse = await GetAsync(client, path, memberSession);
		Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
		using var anonymous = await client.GetAsync(path);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		// A revision is only reachable under its own asset.
		using var mismatched = await GetAsync(client,
			$"/api/assets/{assetId}/revisions/{RevisionId(other)}/access", editorSession);
		Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
		var problem = await mismatched.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.RevisionNotFoundMessage, problem.GetProperty("title").GetString());
		Assert.Equal(ticketsBefore, factory.Storage.Tickets.Count);
	}

	[Fact]
	public async Task MakingAnEarlierRevisionCurrentKeepsHistoryAndSwitchesMembers()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await NameAsync(factory, editorId, "Anna Erstredaktion");
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 1024);
		var (_, _, second) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 2048);
		using (var publish = await PostJsonAsync(client, $"/api/songs/{songId}/publish", new { }, editorSession))
			Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		var firstBlob = await RevisionBlobAsync(factory, RevisionId(first));
		Assert.Equal([RevisionId(first), RevisionId(second)], queue.Attempts);

		using var restore = await PostJsonAsync(client, $"/api/assets/{assetId}/current-revision",
			new { revisionId = RevisionId(first), expectedCurrentRevisionId = RevisionId(second) }, editorSession);

		Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
		var history = await restore.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(RevisionId(first), Guid.Parse(history.GetProperty("currentRevisionId").GetString()!));
		// No revision was added, removed or renumbered.
		var revisions = history.GetProperty("revisions").EnumerateArray().ToList();
		Assert.Equal([2, 1], revisions.Select(r => r.GetProperty("revisionNumber").GetInt32()));
		Assert.Equal([RevisionId(second), RevisionId(first)],
			revisions.Select(r => Guid.Parse(r.GetProperty("revisionId").GetString()!)));
		Assert.Equal([false, true], revisions.Select(r => r.GetProperty("isCurrent").GetBoolean()));
		var changes = history.GetProperty("changes").EnumerateArray().ToList();
		Assert.Equal(3, changes.Count);
		Assert.Equal("restore", changes[0].GetProperty("kind").GetString());
		Assert.Equal(1, changes[0].GetProperty("revisionNumber").GetInt32());
		Assert.Equal(2, changes[0].GetProperty("previousRevisionNumber").GetInt32());
		Assert.Equal("Anna Erstredaktion", changes[0].GetProperty("changedBy").GetString());

		// Members and the song detail follow the pointer.
		var access = await GetJsonAsync(client, $"/api/assets/{assetId}/access", memberSession);
		Assert.Equal(RevisionId(first), Guid.Parse(access.GetProperty("revisionId").GetString()!));
		Assert.Equal(firstBlob, factory.Storage.Find(access.GetProperty("viewUrl").GetString()!)!.BlobName);
		var detail = await GetSongDetailAsync(client, memberSession, songId);
		Assert.Equal(1, detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
			.GetProperty("assets")[0].GetProperty("currentRevision").GetProperty("revisionNumber").GetInt32());

		// Extraction is keyed by revision: the restored revision reuses its row.
		Assert.Equal(2, queue.Attempts.Count);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(2, await db.ExtractionJobs.CountAsync());
			Assert.Equal(2, await db.FileRevisions.CountAsync());
		}

		// Repeating the request is idempotent and adds no history entry.
		using var repeat = await PostJsonAsync(client, $"/api/assets/{assetId}/current-revision",
			new { revisionId = RevisionId(first), expectedCurrentRevisionId = RevisionId(second) }, editorSession);
		Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
		var repeated = await repeat.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(3, repeated.GetProperty("changes").GetArrayLength());

		// The retained newer revision can become current again; history only grows.
		using var forward = await PostJsonAsync(client, $"/api/assets/{assetId}/current-revision",
			new { revisionId = RevisionId(second), expectedCurrentRevisionId = RevisionId(first) }, editorSession);
		Assert.Equal(HttpStatusCode.OK, forward.StatusCode);
		var forwarded = await forward.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(4, forwarded.GetProperty("changes").GetArrayLength());
		Assert.Equal(2, forwarded.GetProperty("revisions").GetArrayLength());
	}

	[Fact]
	public async Task MakingARevisionCurrentRejectsStaleHistoryForeignRevisionsAndMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var otherAssetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var (_, _, second) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var (_, _, third) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var (_, _, other) = await UploadAndFinalizeAsync(factory, client, editorSession, otherAssetId);
		var path = $"/api/assets/{assetId}/current-revision";

		// The editor's history still showed revision 2 as current.
		using var stale = await PostJsonAsync(client, path,
			new { revisionId = RevisionId(first), expectedCurrentRevisionId = RevisionId(second) }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.ConcurrencyMessage, problem.GetProperty("title").GetString());

		using var foreign = await PostJsonAsync(client, path, new { revisionId = RevisionId(other) }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
		using var missing = await PostJsonAsync(client, path, new { }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
		using var member = await PostJsonAsync(client, path, new { revisionId = RevisionId(first) }, memberSession);
		Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
		using var anonymous = await client.PostAsJsonAsync(path, new { revisionId = RevisionId(first) });
		Assert.Equal(HttpStatusCode.BadRequest, anonymous.StatusCode);

		var history = await GetJsonAsync(client, $"/api/assets/{assetId}/revisions", editorSession);
		Assert.Equal(RevisionId(third), Guid.Parse(history.GetProperty("currentRevisionId").GetString()!));
		Assert.Equal(3, history.GetProperty("changes").GetArrayLength());
	}

	[Fact]
	public async Task RestoringARevisionWithoutExtractionRowQueuesItsExtraction()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		// A revision finalized before extraction existed has no work row.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.ExtractionJobs.Remove(await db.ExtractionJobs.SingleAsync(j => j.RevisionId == RevisionId(first)));
			await db.SaveChangesAsync();
		}
		var sendsBefore = queue.Attempts.Count;

		using var restore = await PostJsonAsync(client, $"/api/assets/{assetId}/current-revision",
			new { revisionId = RevisionId(first) }, editorSession);

		Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
		Assert.Equal([RevisionId(first)], queue.Attempts.Skip(sendsBefore));
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var job = await db.ExtractionJobs.SingleAsync(j => j.RevisionId == RevisionId(first));
			Assert.Equal(ExtractionStatus.Queued, job.Status);
			Assert.NotNull(job.LastEnqueuedAt);
		}
	}

	[Fact]
	public async Task CompetingReplacementsEachKeepTheirOwnRevisionAndFile()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, original) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 1000);

		// Both editors start a correction from the same current revision.
		var (sessionA, urlA, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		var (sessionB, urlB, _) = await CreateUploadSessionAsync(client, secondSession, assetId);
		factory.Storage.Store(factory.Storage.Find(urlA)!.BlobName, ValidPdf(2000));
		factory.Storage.Store(factory.Storage.Find(urlB)!.BlobName, ValidPdf(3000));
		using var finalizeA = await FinalizeAsync(client, editorSession, sessionA);
		using var finalizeB = await FinalizeAsync(client, secondSession, sessionB);
		Assert.Equal(HttpStatusCode.OK, finalizeA.StatusCode);
		Assert.Equal(HttpStatusCode.OK, finalizeB.StatusCode);
		var revisionA = await finalizeA.Content.ReadFromJsonAsync<JsonElement>();
		var revisionB = await finalizeB.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(2, revisionA.GetProperty("revisionNumber").GetInt32());
		Assert.Equal(3, revisionB.GetProperty("revisionNumber").GetInt32());

		// A late retry of the first finalize answers its own revision and
		// does not take the current pointer back.
		using var retryA = await FinalizeAsync(client, editorSession, sessionA);
		Assert.Equal(HttpStatusCode.OK, retryA.StatusCode);
		var retried = await retryA.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(RevisionId(revisionA), RevisionId(retried));

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var rows = await db.FileRevisions.OrderBy(r => r.RevisionNumber).ToListAsync();
		Assert.Equal([1, 2, 3], rows.Select(r => r.RevisionNumber));
		Assert.Equal([1000L, 2000L, 3000L], rows.Select(r => r.SizeBytes));
		Assert.Equal(3, rows.Select(r => r.BlobName).Distinct().Count());
		Assert.All(rows, row => Assert.True(factory.Storage.Has(row.BlobName)));
		Assert.Equal(RevisionId(original), rows[0].Id);
		Assert.Equal(RevisionId(revisionB), (await db.Assets.SingleAsync()).CurrentRevisionId);
	}

	[Fact]
	public async Task FinalizeThatLosesTheRaceStaysRetryableAndTouchesNothingElse()
	{
		// The first save that would add revision 2 fails the way a lost
		// optimistic-concurrency race does; nothing of it is persisted.
		var lostOnce = false;
		var interceptor = new ExtractionSaveInterceptor((db, _) =>
		{
			if (!lostOnce && db.ChangeTracker.Entries<FileRevision>()
				.Any(e => e.State == EntityState.Added && e.Entity.RevisionNumber == 2))
			{
				lostOnce = true;
				throw new DbUpdateConcurrencyException("Simulated lost race.");
			}
			return Task.CompletedTask;
		});
		await using var factory = new AuthApiFactory(saveChangesInterceptor: interceptor);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId, size: 1000);
		var firstBlob = await RevisionBlobAsync(factory, RevisionId(first));
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		var pendingBlob = factory.Storage.Find(uploadUrl)!.BlobName;
		factory.Storage.Store(pendingBlob, ValidPdf(2000));

		using var lost = await FinalizeAsync(client, editorSession, sessionId);

		Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
		var problem = await lost.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.ConcurrencyMessage, problem.GetProperty("title").GetString());
		// The staged upload survives for the retry; the losing copy is gone
		// and the current file is untouched.
		Assert.True(factory.Storage.Has(pendingBlob));
		Assert.True(factory.Storage.Has(firstBlob));
		Assert.Equal(2, factory.Storage.ObjectCount);
		var access = await GetJsonAsync(client, $"/api/assets/{assetId}/access", editorSession);
		Assert.Equal(RevisionId(first), Guid.Parse(access.GetProperty("revisionId").GetString()!));

		using var retry = await FinalizeAsync(client, editorSession, sessionId);

		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var second = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(2, second.GetProperty("revisionNumber").GetInt32());
		var history = await GetJsonAsync(client, $"/api/assets/{assetId}/revisions", editorSession);
		Assert.Equal(2, history.GetProperty("revisions").GetArrayLength());
		Assert.Equal(2, history.GetProperty("changes").GetArrayLength());
		Assert.True(factory.Storage.Has(firstBlob));
	}

	[Fact]
	public async Task UploadCleanupNeverRemovesRetainedRevisions()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, first) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var firstBlob = await RevisionBlobAsync(factory, RevisionId(first));
		// Everything about the earlier revision is far older than the seven-day trash window.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var old = DateTimeOffset.UtcNow.AddDays(-400);
			(await db.FileRevisions.SingleAsync(r => r.Id == RevisionId(first))).CreatedAt = old;
			foreach (var session in await db.UploadSessions.ToListAsync())
			{
				session.CreatedAt = old;
				session.UploadTicketExpiresAt = old;
			}
			await db.SaveChangesAsync();
		}

		using (var scope = factory.Services.CreateScope())
		{
			var cleaner = scope.ServiceProvider.GetRequiredService<UploadSessionCleaner>();
			Assert.Equal(0, await cleaner.CleanAsync(CancellationToken.None));
		}

		Assert.True(factory.Storage.Has(firstBlob));
		var access = await GetJsonAsync(client,
			$"/api/assets/{assetId}/revisions/{RevisionId(first)}/access", editorSession);
		Assert.Equal(firstBlob, factory.Storage.Find(access.GetProperty("viewUrl").GetString()!)!.BlobName);
	}

	private static Guid RevisionId(JsonElement revision) =>
		Guid.Parse(revision.GetProperty("revisionId").GetString()!);

	private static async Task<string> RevisionBlobAsync(AuthApiFactory factory, Guid revisionId)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		return (await db.FileRevisions.SingleAsync(r => r.Id == revisionId)).BlobName;
	}

	private static async Task NameAsync(AuthApiFactory factory, Guid accountId, string displayName)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		(await db.Users.SingleAsync(u => u.Id == accountId)).DisplayName = displayName;
		await db.SaveChangesAsync();
	}

	/// <summary>Uploads with a declared file identity, as the browser does.</summary>
	private static async Task<JsonElement> UploadNamedAsync(
		AuthApiFactory factory, HttpClient client, string session, Guid assetId, string fileName, long size)
	{
		using var create = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", new { sizeBytes = size, fileName }, session);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var body = await create.Content.ReadFromJsonAsync<JsonElement>();
		factory.Storage.Store(factory.Storage.Find(body.GetProperty("uploadUrl").GetString()!)!.BlobName, ValidPdf(size));
		using var finalize = await PostJsonAsync(client,
			$"/api/upload-sessions/{body.GetProperty("uploadSessionId").GetString()}/finalize",
			new { sizeBytes = size, fileName }, session);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		return await finalize.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string session)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", session);
		return await client.SendAsync(request);
	}

	private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, string session)
	{
		using var response = await GetAsync(client, path, session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}
}
