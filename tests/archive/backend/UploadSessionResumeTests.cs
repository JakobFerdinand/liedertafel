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
