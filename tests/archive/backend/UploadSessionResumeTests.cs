using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-017 large-upload resume at the backend HTTP seam: declared file
/// identity and limits at initiation, ticket renewal, cancellation, finalize
/// identity/limit checks and the abandoned-session cleanup contract.
/// </summary>
public sealed class UploadSessionResumeTests
{
	private const string Editor = "redaktion@liedertafel.test";
	private const string SecondEditor = "zweitredaktion@liedertafel.test";
	private const string Member = "mitglied@liedertafel.test";

	[Fact]
	public async Task RenewReturnsFreshTicketAndExtendsLifetime()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "2048",
			["Archive:Assets:UploadBlockBytes"] = "524288",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		string blobName;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			blobName = session.BlobName;
			Assert.Equal(factory.Storage.Find(uploadUrl)!.BlobName, blobName);
		}
		var ticketsBefore = factory.Storage.Tickets.Count;

		using var response = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/renew", new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(sessionId, Guid.Parse(body.GetProperty("uploadSessionId").GetString()!));
		Assert.Equal(blobName, body.GetProperty("blobName").GetString());
		Assert.Equal(2048, body.GetProperty("maxBytes").GetInt64());
		Assert.Equal(524288, body.GetProperty("blockBytes").GetInt64());
		var expiresAt = DateTimeOffset.Parse(body.GetProperty("expiresAt").GetString()!);
		Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));

		// A fresh upload ticket was issued for the same pending blob.
		var renewedUrl = body.GetProperty("uploadUrl").GetString()!;
		Assert.NotEqual(uploadUrl, renewedUrl);
		Assert.Equal(ticketsBefore + 1, factory.Storage.Tickets.Count);
		var renewedTicket = factory.Storage.Find(renewedUrl)!;
		Assert.Equal(blobName, renewedTicket.BlobName);
		Assert.Equal(TimeSpan.FromMinutes(30), renewedTicket.Lifetime);
	}

	[Fact]
	public async Task RenewalWorksAfterTicketExpiry()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			session.UploadTicketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
			await db.SaveChangesAsync();
		}

		using var response = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/renew", new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var expiresAt = DateTimeOffset.Parse(body.GetProperty("expiresAt").GetString()!);
		Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			Assert.Equal(PendingUploadState.Pending, session.State);
			Assert.InRange(session.UploadTicketExpiresAt,
				DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
		}
	}

	[Fact]
	public async Task RenewRejectsUnknownOtherOwnerFinalizedAndAbandonedSessions()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		var (sessionId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var memberResponse = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/renew", new { }, memberSession))
		{
			Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
			var memberProblem = await memberResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.ForbiddenMessage, memberProblem.GetProperty("title").GetString());
		}

		var (anonCookie, anonToken) = await GetCsrfAsync(client);
		using var anonResponse = await client.SendAsync(AuthedPost(
			$"/api/upload-sessions/{sessionId}/renew", new { }, anonCookie, anonToken));
		Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

		using (var otherResponse = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/renew", new { }, secondSession))
		{
			Assert.Equal(HttpStatusCode.Forbidden, otherResponse.StatusCode);
			var otherProblem = await otherResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadOwnerMessage, otherProblem.GetProperty("title").GetString());
		}

		using (var unknown = await PostJsonAsync(client,
			$"/api/upload-sessions/{Guid.CreateVersion7()}/renew", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
			var unknownProblem = await unknown.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadNotFoundMessage, unknownProblem.GetProperty("title").GetString());
		}

		// Finalized sessions report the distinct already-finalized conflict.
		var finalizedId = await UploadAndFinalizeAsync(factory, client, editorSession, assetId).ContinueWith(
			t => t.Result.SessionId);
		using (var finalized = await PostJsonAsync(client,
			$"/api/upload-sessions/{finalizedId}/renew", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, finalized.StatusCode);
			var finalizedProblem = await finalized.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadAlreadyFinalizedMessage, finalizedProblem.GetProperty("title").GetString());
		}

		// Abandoned sessions stay terminally refused.
		var abandonedFactorySession = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(
				s => s.Id == abandonedFactorySession.SessionId);
			session.State = PendingUploadState.Abandoned;
			await db.SaveChangesAsync();
		}
		using (var abandoned = await PostJsonAsync(client,
			$"/api/upload-sessions/{abandonedFactorySession.SessionId}/renew", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, abandoned.StatusCode);
			var abandonedProblem = await abandoned.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadAbandonedMessage, abandonedProblem.GetProperty("title").GetString());
		}
	}

	[Fact]
	public async Task CreateSessionStoresDeclaredIdentityAndReportsBlockBytes()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "2048",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		var (sessionId, _, body) = await CreateUploadSessionAsync(client, editorSession, assetId,
			new { sizeBytes = 1500, fileName = "  lied.pdf  " });
		Assert.Equal(2048, body.GetProperty("maxBytes").GetInt64());
		Assert.Equal(8 * 1024 * 1024, body.GetProperty("blockBytes").GetInt64());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			Assert.Equal(1500, session.DeclaredSizeBytes);
			Assert.Equal("lied.pdf", session.DeclaredFileName);
		}
	}

	[Fact]
	public async Task DeclaredSizeAbovePerFileLimitIsRejectedWithoutSession()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "1024",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", new { sizeBytes = 1025 }, editorSession);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadTooLargeMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(0, await db.UploadSessions.CountAsync());
		Assert.Empty(factory.Storage.Tickets);
	}

	[Fact]
	public async Task CollectionLimitAtInitiationSpendsPendingAndFinalizedBudget()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "2048",
			["Archive:Assets:MaxCollectionBytes"] = "4096",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetAId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetBId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetCId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetDId = await CreateAssetAsync(client, editorSession, versionId, "score");

		// One finalized revision (1024) and one pending session (max 2048)
		// consume 3072 of the 4096-byte collection budget.
		await UploadAndFinalizeAsync(factory, client, editorSession, assetAId, size: 1024);
		await CreateUploadSessionAsync(client, editorSession, assetBId);

		// Fits exactly (3072 + 1024 = 4096).
		var (_, _, fitted) = await CreateUploadSessionAsync(client, editorSession, assetCId,
			new { sizeBytes = 1024 });
		Assert.Equal(2048, fitted.GetProperty("maxBytes").GetInt64());

		// Exceeds: the session is refused and no row is created.
		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetDId}/upload-session", new { sizeBytes = 1025 }, editorSession);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadTooLargeMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(2, await db.UploadSessions.CountAsync(
				s => s.State == PendingUploadState.Pending));
			Assert.Equal(0, await db.UploadSessions
				.Where(s => s.AssetId == assetDId).CountAsync());
		}
	}

	[Fact]
	public async Task PendingBudgetCountsDeclaredSizesInsteadOfSessionCaps()
	{
		// A pending session without a declared identity reserves its per-file
		// cap; a declared session reserves only its declared size.
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "2048",
			["Archive:Assets:MaxCollectionBytes"] = "4096",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetAId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetBId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetCId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetDId = await CreateAssetAsync(client, editorSession, versionId, "score");

		await CreateUploadSessionAsync(client, editorSession, assetAId,
			new { sizeBytes = 1024, fileName = "a.pdf" });
		await CreateUploadSessionAsync(client, editorSession, assetBId);

		// Fits exactly: 1024 (declared) + 2048 (cap, undeclared) + 1024 = 4096.
		var (_, _, fitted) = await CreateUploadSessionAsync(client, editorSession, assetCId,
			new { sizeBytes = 1024, fileName = "c.pdf" });
		Assert.Equal(2048, fitted.GetProperty("maxBytes").GetInt64());

		// Exceeds: the session is refused and no row is created.
		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetDId}/upload-session", new { sizeBytes = 1025 }, editorSession);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadTooLargeMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(3, await db.UploadSessions.CountAsync(
				s => s.State == PendingUploadState.Pending));
			Assert.Equal(0, await db.UploadSessions
				.Where(s => s.AssetId == assetDId).CountAsync());
		}
	}

	[Fact]
	public async Task NewUploadSessionCancelsPriorPendingSessionsOfSameAssetAndOwner()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var secondEditorId = await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var otherAssetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		// The editor's earlier attempt left a pending session behind, as did
		// a foreign user and an upload to another asset of the version.
		// ARC-025: a second editor can no longer initiate a session on
		// someone else's asset at all, so the foreign row is seeded directly;
		// the supersede pass must still leave other owners untouched.
		var (staleId, staleUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		var staleBlob = factory.Storage.Find(staleUrl)!.BlobName;
		factory.Storage.Store(staleBlob, ValidPdf(128));
		string foreignBlob;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var foreign = new PendingUpload
			{
				AssetId = assetId,
				BlobName = $"pending/{Guid.CreateVersion7()}",
				ContentType = AssetEndpoints.PdfContentType,
				MaxSizeBytes = 10 * 1024L * 1024 * 1024,
				UploadTicketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
				State = PendingUploadState.Pending,
				CreatedByAccountId = secondEditorId,
				CreatedAt = DateTimeOffset.UtcNow,
			};
			db.UploadSessions.Add(foreign);
			await db.SaveChangesAsync();
			foreignBlob = foreign.BlobName;
		}
		factory.Storage.Store(foreignBlob, ValidPdf(128));
		var (otherAssetSessionId, _, _) = await CreateUploadSessionAsync(client, editorSession, otherAssetId);

		var (freshId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(PendingUploadState.Cancelled,
				(await db.UploadSessions.SingleAsync(s => s.Id == staleId)).State);
			Assert.Equal(PendingUploadState.Pending,
				(await db.UploadSessions.SingleAsync(s => s.AssetId == assetId
					&& s.CreatedByAccountId == secondEditorId)).State);
			Assert.Equal(PendingUploadState.Pending,
				(await db.UploadSessions.SingleAsync(s => s.Id == otherAssetSessionId)).State);
			Assert.Equal(PendingUploadState.Pending,
				(await db.UploadSessions.SingleAsync(s => s.Id == freshId)).State);
		}
		Assert.False(factory.Storage.Has(staleBlob));
		Assert.True(factory.Storage.Has(foreignBlob));
	}

	[Fact]
	public async Task RetryOnSameAssetFreesTheCollectionBudget()
	{
		// The production failure shape: repeated attempts on one asset left
		// pending sessions that reserved the per-file cap each and starved
		// the version budget, so even tiny uploads were refused as too large.
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "2048",
			["Archive:Assets:MaxCollectionBytes"] = "4096",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		var identity = new { sizeBytes = 2048, fileName = "lied.pdf" };
		var (firstId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId, identity);

		// The retry supersedes the failed attempt instead of exhausting the budget.
		var (retryId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId, identity);
		Assert.NotEqual(firstId, retryId);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(PendingUploadState.Cancelled,
				(await db.UploadSessions.SingleAsync(s => s.Id == firstId)).State);
			Assert.Equal(PendingUploadState.Pending,
				(await db.UploadSessions.SingleAsync(s => s.Id == retryId)).State);
		}
	}

	[Fact]
	public async Task OverlongDeclaredFileNameIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session",
			new { fileName = new string('x', 301) }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.FileNameTooLongMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(0, await db.UploadSessions.CountAsync());
	}

	[Fact]
	public async Task CancelPendingSessionMarksCancelledAndDeletesPendingBlob()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		var blobName = factory.Storage.Find(uploadUrl)!.BlobName;
		factory.Storage.Store(blobName, ValidPdf(128));

		using var response = await DeleteAsync(client,
			$"/api/upload-sessions/{sessionId}", editorSession);
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			Assert.Equal(PendingUploadState.Cancelled, session.State);
			Assert.Equal(blobName, session.BlobName);
		}
		Assert.False(factory.Storage.Has(blobName));
	}

	[Fact]
	public async Task CancelRejectsUnknownOtherOwnerAndTerminalStates()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		using (var unknown = await DeleteAsync(client,
			$"/api/upload-sessions/{Guid.CreateVersion7()}", editorSession))
		{
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
			var unknownProblem = await unknown.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadNotFoundMessage, unknownProblem.GetProperty("title").GetString());
		}

		var (pendingId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var other = await DeleteAsync(client,
			$"/api/upload-sessions/{pendingId}", secondSession))
		{
			Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
			var otherProblem = await other.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadOwnerMessage, otherProblem.GetProperty("title").GetString());
		}

		// Finalized: a committed revision must not be cancelled.
		var (finalizedId, _, _) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		using (var finalized = await DeleteAsync(client,
			$"/api/upload-sessions/{finalizedId}", editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, finalized.StatusCode);
			var finalizedProblem = await finalized.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadAbandonedMessage, finalizedProblem.GetProperty("title").GetString());
		}

		// Abandoned: terminal refusal.
		var (abandonedId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			(await db.UploadSessions.SingleAsync(s => s.Id == abandonedId)).State =
				PendingUploadState.Abandoned;
			await db.SaveChangesAsync();
		}
		using (var abandoned = await DeleteAsync(client,
			$"/api/upload-sessions/{abandonedId}", editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, abandoned.StatusCode);
			var abandonedProblem = await abandoned.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadAbandonedMessage, abandonedProblem.GetProperty("title").GetString());
		}

		// Cancelled: cancelling twice reports the distinct cancelled conflict.
		var (cancelledId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var first = await DeleteAsync(client,
			$"/api/upload-sessions/{cancelledId}", editorSession))
		{
			Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
		}
		using (var replay = await DeleteAsync(client,
			$"/api/upload-sessions/{cancelledId}", editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
			var replayProblem = await replay.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadCancelledMessage, replayProblem.GetProperty("title").GetString());
		}
	}

	[Fact]
	public async Task FinalizeOnCancelledSessionIsTerminalAndLeavesStateUntouched()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		var blobName = factory.Storage.Find(uploadUrl)!.BlobName;

		using var cancel = await DeleteAsync(client,
			$"/api/upload-sessions/{sessionId}", editorSession);
		Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

		// Finalizing a cancelled session is refused without abandoning or
		// deleting anything further.
		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.Conflict, finalize.StatusCode);
		var problem = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadCancelledMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			Assert.Equal(PendingUploadState.Cancelled, session.State);
			Assert.Null(session.FinalizedRevisionId);
			Assert.Equal(0, await db.FileRevisions.CountAsync());
		}
		Assert.False(factory.Storage.Has(blobName));
	}

	[Fact]
	public async Task RenewOnCancelledSessionIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);

		using var cancel = await DeleteAsync(client,
			$"/api/upload-sessions/{sessionId}", editorSession);
		Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

		using var renew = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/renew", new { }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, renew.StatusCode);
		var problem = await renew.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadAbandonedMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task FinalizeRejectsMismatchedDeclaredIdentityAndStaysPending()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId,
			new { sizeBytes = 1024, fileName = "lied.pdf" });
		var blobName = factory.Storage.Find(uploadUrl)!.BlobName;
		factory.Storage.Store(blobName, ValidPdf(1024));

		// Mismatched declared size: refused before storage is touched.
		using (var sizeMismatch = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/finalize",
			new { sizeBytes = 512, fileName = "lied.pdf" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, sizeMismatch.StatusCode);
			var sizeProblem = await sizeMismatch.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadMismatchMessage, sizeProblem.GetProperty("title").GetString());
			Assert.True(factory.Storage.Has(blobName));
		}

		// Mismatched declared file name: same refusal.
		using (var nameMismatch = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/finalize",
			new { sizeBytes = 1024, fileName = "anders.pdf" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, nameMismatch.StatusCode);
			var nameProblem = await nameMismatch.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadMismatchMessage, nameProblem.GetProperty("title").GetString());
		}

		// The session stays pending and retryable with the correct file.
		using (var retry = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/finalize",
			new { sizeBytes = 1024, fileName = "lied.pdf" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.FileRevisions.CountAsync());
		}
	}

	[Fact]
	public async Task FinalizeComparesOnlyDimensionsPresentOnBothSides()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		// Session without declared identity: a declaring finalize is not a mismatch.
		var (undeclaredId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(1024));
		using (var response = await PostJsonAsync(client,
			$"/api/upload-sessions/{undeclaredId}/finalize", new { sizeBytes = 1024 }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		// Declared size only: an absent fileName dimension cannot conflict.
		var (sizeOnlyId, sizeOnlyUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId,
			new { sizeBytes = 512 });
		factory.Storage.Store(factory.Storage.Find(sizeOnlyUrl)!.BlobName, ValidPdf(512));
		using (var response = await PostJsonAsync(client,
			$"/api/upload-sessions/{sizeOnlyId}/finalize",
			new { sizeBytes = 512, fileName = "irgendwas.pdf" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		// Declared fileName only: a matching name without size still finalizes.
		var (nameOnlyId, nameOnlyUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId,
			new { fileName = "stimme.pdf" });
		factory.Storage.Store(factory.Storage.Find(nameOnlyUrl)!.BlobName, ValidPdf(256));
		using (var response = await PostJsonAsync(client,
			$"/api/upload-sessions/{nameOnlyId}/finalize",
			new { sizeBytes = 999, fileName = "stimme.pdf" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(3, await db.FileRevisions.CountAsync());
		}
	}

	[Fact]
	public async Task FinalizeFallsBackToSessionContentTypeForBlockCommits()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var scoreAssetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var audioAssetId = await CreateAssetAsync(client, editorSession, versionId, "audio");

		// A block-list commit carries no blob content type (the storage
		// reports its default), so finalize falls back to the session's
		// declared whitelisted type.
		var (scoreSessionId, scoreUploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, scoreAssetId);
		factory.Storage.Store(factory.Storage.Find(scoreUploadUrl)!.BlobName,
			ValidPdf(1024), AssetEndpoints.StorageDefaultContentType);
		using (var score = await PostJsonAsync(client,
			$"/api/upload-sessions/{scoreSessionId}/finalize", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, score.StatusCode);
			var revision = await score.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.PdfContentType, revision.GetProperty("contentType").GetString());
		}

		var (audioSessionId, audioUploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, audioAssetId);
		factory.Storage.Store(factory.Storage.Find(audioUploadUrl)!.BlobName,
			new byte[512], AssetEndpoints.StorageDefaultContentType);
		using (var audio = await PostJsonAsync(client,
			$"/api/upload-sessions/{audioSessionId}/finalize", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
			var revision = await audio.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.Mp3ContentType, revision.GetProperty("contentType").GetString());
		}

		// A stored non-whitelisted type is still rejected.
		var (textSessionId, textUploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, scoreAssetId);
		factory.Storage.Store(factory.Storage.Find(textUploadUrl)!.BlobName,
			ValidPdf(512), "text/plain");
		using (var text = await PostJsonAsync(client,
			$"/api/upload-sessions/{textSessionId}/finalize", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
			var problem = await text.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidPdfMessage, problem.GetProperty("title").GetString());
		}
	}

	[Fact]
	public async Task CollectionLimitAtFinalizationAbandonsTheSession()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "4096",
			["Archive:Assets:MaxCollectionBytes"] = "4096",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetAId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetBId = await CreateAssetAsync(client, editorSession, versionId, "score");

		// A finalizes 1024 bytes; B then transfers 3073 bytes, which together
		// exceed the 4096-byte version budget.
		await UploadAndFinalizeAsync(factory, client, editorSession, assetAId, size: 1024);
		var (sessionBId, uploadUrlB, _) = await CreateUploadSessionAsync(client, editorSession, assetBId);
		var blobNameB = factory.Storage.Find(uploadUrlB)!.BlobName;
		factory.Storage.Store(blobNameB, ValidPdf(3073));

		using var response = await FinalizeAsync(client, editorSession, sessionBId);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadTooLargeMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionBId);
			Assert.Equal(PendingUploadState.Abandoned, session.State);
			Assert.Equal(1, await db.FileRevisions.CountAsync(
				r => r.AssetId == assetAId));
			Assert.Null((await db.Assets.SingleAsync(a => a.Id == assetBId)).CurrentRevisionId);
		}
		Assert.False(factory.Storage.Has(blobNameB));
	}

	[Fact]
	public async Task CleanerMarksExpiredPendingSessionsAbandonedWithinGrace()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var expiredAssetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var freshAssetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (expiredId, expiredUrl, _) = await CreateUploadSessionAsync(client, editorSession, expiredAssetId);
		var expiredBlob = factory.Storage.Find(expiredUrl)!.BlobName;
		factory.Storage.Store(expiredBlob, ValidPdf(128));
		var (freshId, freshUrl, _) = await CreateUploadSessionAsync(client, editorSession, freshAssetId);
		var freshBlob = factory.Storage.Find(freshUrl)!.BlobName;
		factory.Storage.Store(freshBlob, ValidPdf(128));

		// One hour grace: the expired session slipped past it, the fresh one
		// expired minutes ago only.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			(await db.UploadSessions.SingleAsync(s => s.Id == expiredId)).UploadTicketExpiresAt =
				DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-5);
			(await db.UploadSessions.SingleAsync(s => s.Id == freshId)).UploadTicketExpiresAt =
				DateTimeOffset.UtcNow.AddMinutes(-30);
			await db.SaveChangesAsync();
		}

		using (var scope = factory.Services.CreateScope())
		{
			var cleaner = scope.ServiceProvider.GetRequiredService<UploadSessionCleaner>();
			Assert.Equal(1, await cleaner.CleanAsync(CancellationToken.None));
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(PendingUploadState.Abandoned,
				(await db.UploadSessions.SingleAsync(s => s.Id == expiredId)).State);
			Assert.Equal(PendingUploadState.Pending,
				(await db.UploadSessions.SingleAsync(s => s.Id == freshId)).State);
		}
		Assert.False(factory.Storage.Has(expiredBlob));
		Assert.True(factory.Storage.Has(freshBlob));

		// A second run has nothing left to clean.
		using (var scope = factory.Services.CreateScope())
		{
			var cleaner = scope.ServiceProvider.GetRequiredService<UploadSessionCleaner>();
			Assert.Equal(0, await cleaner.CleanAsync(CancellationToken.None));
		}
	}

	[Fact]
	public async Task CancelledAndFinalizedSessionsAreNeverCleaned()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, _) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var (cancelledId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var cancel = await DeleteAsync(client,
			$"/api/upload-sessions/{cancelledId}", editorSession))
		{
			Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			foreach (var session in await db.UploadSessions.ToListAsync())
				session.UploadTicketExpiresAt = DateTimeOffset.UtcNow.AddHours(-2);
			await db.SaveChangesAsync();
		}

		using (var scope = factory.Services.CreateScope())
		{
			var cleaner = scope.ServiceProvider.GetRequiredService<UploadSessionCleaner>();
			Assert.Equal(0, await cleaner.CleanAsync(CancellationToken.None));
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == cancelledId);
			Assert.Equal(PendingUploadState.Cancelled, session.State);
			Assert.Equal(PendingUploadState.Finalized,
				(await db.UploadSessions.SingleAsync(s => s.State == PendingUploadState.Finalized)).State);
		}
	}

	private static async Task<(Guid SongId, Guid VersionId)> CreateSongWithVersionAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, string title = "Notenlied")
	{
		var songId = await CreateSongAsync(factory, client, editorSession, title);
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		return (songId, versionId);
	}

	private static async Task<Guid> CreateAssetAsync(
		HttpClient client, string editorSession, Guid versionId, string assetType)
	{
		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("id").GetString()!);
	}

	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> CreateUploadSessionAsync(
		HttpClient client, string editorSession, Guid assetId, object? body = null)
	{
		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", body ?? new { }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (
			Guid.Parse(json.GetProperty("uploadSessionId").GetString()!),
			json.GetProperty("uploadUrl").GetString()!,
			json);
	}

	/// <summary>Creates the session, stores a valid PDF and finalizes.</summary>
	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Revision)> UploadAndFinalizeAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, Guid assetId, long size = 1024)
	{
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(size));
		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		return (sessionId, uploadUrl, revision);
	}

	private static async Task<HttpResponseMessage> FinalizeAsync(
		HttpClient client, string session, Guid sessionId)
		=> await PostJsonAsync(client, $"/api/upload-sessions/{sessionId}/finalize", new { }, session);

	private static byte[] ValidPdf(long size)
	{
		var bytes = new byte[size];
		"%PDF-1.7\n"u8.CopyTo(bytes);
		return bytes;
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
	}

	private static async Task<HttpResponseMessage> DeleteAsync(
		HttpClient client, string path, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		var request = new HttpRequestMessage(HttpMethod.Delete, path);
		request.Headers.Add("Cookie", $"{cookie}; {session}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(new { });
		return await client.SendAsync(request);
	}

	private static async Task<Guid> CreateSongAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, string title)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);
	}

	private static async Task<Guid> SeedAsync(AuthApiFactory factory, string email, string role)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roles.RoleExistsAsync(role))
			Assert.True((await roles.CreateAsync(new ArchiveRole(role))).Succeeded);
		var user = await users.FindByEmailAsync(email);
		if (user is null)
		{
			user = new ArchiveUser { UserName = email, Email = email, DisplayName = "Test", EmailConfirmed = true };
			Assert.True((await users.CreateAsync(user)).Succeeded);
		}
		if (!await users.IsInRoleAsync(user, role))
			Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
		return user.Id;
	}

	private static async Task<string> SignInAsync(AuthApiFactory factory, string email)
	{
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		return Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
	}

	private static async Task<(string Cookie, string Token)> GetCsrfAsync(HttpClient client, string? sessionCookie = null)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		if (sessionCookie is not null)
			tokenRequest.Headers.Add("Cookie", sessionCookie);
		using var response = await client.SendAsync(tokenRequest);
		response.EnsureSuccessStatusCode();
		var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (cookie, body.GetProperty("token").GetString()!);
	}

	private static HttpRequestMessage AuthedPost(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private static async Task<JsonElement> GetSongDetailAsync(HttpClient client, string session, Guid songId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("song");
	}
}
