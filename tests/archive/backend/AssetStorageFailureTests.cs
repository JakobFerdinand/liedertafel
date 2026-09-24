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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-049 failure seam: the hosted assets account is Entra-only (shared-key
/// access off), so ticket failures must surface as a German 502 instead of an
/// unhandled 500, and the adapter must pick between the Azurite connection
/// string and managed-identity user-delegation SAS without emitting secrets.
/// </summary>
public sealed class AssetStorageFailureTests
{
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task UploadSessionReportsStorageFailureInsteadOfServerError()
	{
		await using var factory = new AuthApiFactory(storage: new FailingStorage());
		await SeedAsync(factory, Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId);

		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", new { }, editorSession);
		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.StorageFailureMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task RenewReportsStorageFailureInsteadOfServerError()
	{
		await using var factory = new AuthApiFactory(storage: new FailingStorage());
		var accountId = await SeedAsync(factory, Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId);

		// A pending session row exists from before the outage; renewal must
		// translate the ticket failure, not die on it.
		Guid sessionId;
		await using (var scope = factory.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var pending = new PendingUpload
			{
				AssetId = assetId,
				BlobName = $"pending/{Guid.CreateVersion7()}",
				ContentType = AssetEndpoints.PdfContentType,
				MaxSizeBytes = 4096,
				UploadTicketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
				State = PendingUploadState.Pending,
				CreatedByAccountId = accountId,
				CreatedAt = DateTimeOffset.UtcNow,
			};
			db.UploadSessions.Add(pending);
			await db.SaveChangesAsync();
			sessionId = pending.Id;
		}

		using var response = await PostJsonAsync(client,
			$"/api/upload-sessions/{sessionId}/renew", new { }, editorSession);
		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.StorageFailureMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task AdapterWithoutAnyConfigurationFailsWithConfigError()
	{
		var adapter = new BlobAssetStorageAdapter(
			new ConfigurationBuilder().Build(),
			Options.Create(new AssetStorageOptions()),
			TimeProvider.System);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => adapter.CreateUploadTicketAsync("pending/x", TimeSpan.FromMinutes(30), CancellationToken.None));
		Assert.Equal("Kein Blob-Verbindungsstring konfiguriert.", exception.Message);
	}

	[Fact]
	public async Task ConnectionStringModeSignsSharedKeyTicketWithoutNetwork()
	{
		// Azurite's well-known dev account key; SAS signing is purely local.
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:archive-blobs"] =
				"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
				"AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
				"BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;",
		}).Build();
		var adapter = new BlobAssetStorageAdapter(
			configuration, Options.Create(new AssetStorageOptions()), TimeProvider.System);

		var url = await adapter.CreateUploadTicketAsync("pending/x", TimeSpan.FromMinutes(30), CancellationToken.None);
		Assert.StartsWith("http://127.0.0.1:10000/devstoreaccount1/", url);
		Assert.Contains("pending/x", url);
		// Write + Create + Read arrive as one ordered permission flag.
		Assert.Matches("sp=[rcw]{3}", url);
		Assert.Contains("sig=", url);
	}

	[Fact]
	public async Task ReadTicketOverridesServeTypeInlineAndDownloadAttachment()
	{
		// Azurite's well-known dev account key; SAS signing is purely local.
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:archive-blobs"] =
				"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
				"AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
				"BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;",
		}).Build();
		var adapter = new BlobAssetStorageAdapter(
			configuration, Options.Create(new AssetStorageOptions()), TimeProvider.System);

		var view = await adapter.CreateReadTicketAsync(
			"revisions/x", TimeSpan.FromMinutes(15), asDownload: false,
			contentType: AssetEndpoints.PdfContentType, CancellationToken.None);
		var download = await adapter.CreateReadTicketAsync(
			"revisions/x", TimeSpan.FromMinutes(15), asDownload: true,
			contentType: AssetEndpoints.PdfContentType, CancellationToken.None);
		// Response-header overrides ride the signed query string and beat the
		// blob's stored properties at serve time.
		Assert.Contains("rsct=application%2Fpdf", view);
		Assert.Contains("rscd=inline", view);
		Assert.Contains("rsct=application%2Fpdf", download);
		Assert.Contains("rscd=attachment", download);
	}

	private sealed class FailingStorage : IAssetStorageAdapter
	{
		public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");

		public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, string? contentType = null, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");

		public Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");

		public Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");

		public Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");

		public Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
	}

	private static async Task<Guid> SeedAsync(AuthApiFactory factory, string email)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roles.RoleExistsAsync(ArchiveRoles.Editor))
			Assert.True((await roles.CreateAsync(new ArchiveRole(ArchiveRoles.Editor))).Succeeded);
		var user = await users.FindByEmailAsync(email);
		if (user is null)
		{
			user = new ArchiveUser { UserName = email, Email = email, DisplayName = "Test", EmailConfirmed = true };
			Assert.True((await users.CreateAsync(user)).Succeeded);
		}
		if (!await users.IsInRoleAsync(user, ArchiveRoles.Editor))
			Assert.True((await users.AddToRoleAsync(user, ArchiveRoles.Editor)).Succeeded);
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

	private static async Task<(Guid SongId, Guid VersionId)> CreateSongWithVersionAsync(
		AuthApiFactory factory, HttpClient client, string editorSession)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title = "Notenlied" }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var songId = Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);

		using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		detailRequest.Headers.Add("Cookie", editorSession);
		using var detailResponse = await client.SendAsync(detailRequest);
		detailResponse.EnsureSuccessStatusCode();
		var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
		var versionId = Guid.Parse(detail.GetProperty("song").GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		return (songId, versionId);
	}

	private static async Task<Guid> CreateAssetAsync(
		HttpClient client, string editorSession, Guid versionId)
	{
		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType = "score" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("id").GetString()!);
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
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
}
