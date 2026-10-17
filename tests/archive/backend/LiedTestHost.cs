using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// Shared HTTP-seam helpers for the Lied and Datei tests (#166): seeding and
/// signing in accounts, CSRF-protected requests and the Lied/Datei calls the
/// later tickets (#167, #168, #171) build their tests on. Additive only.
/// </summary>
internal sealed class LiedTestHost : IAsyncDisposable
{
	public const string Editor = "redaktion@liedertafel.test";
	public const string SecondEditor = "zweitredaktion@liedertafel.test";
	public const string Member = "mitglied@liedertafel.test";

	public AuthApiFactory Factory { get; }

	public HttpClient Client { get; }

	/// <summary>The service provider of the host the client talks to.</summary>
	public IServiceProvider Services { get; }

	private readonly WebApplicationFactory<Program> app;

	private readonly Dictionary<string, string> sessions = [];

	public LiedTestHost(Dictionary<string, string?>? settings = null, IDateiFinalizedHook? hook = null)
	{
		Factory = settings is null ? new AuthApiFactory() : new AuthApiFactory(settings: settings);
		app = hook is null
			? Factory
			: Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(hook)));
		Services = app.Services;
		Client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
	}

	/// <summary>Seeds the account in the role and signs it in; returns the session cookie.</summary>
	public async Task<string> SignInAsync(string email, string role)
	{
		using (var scope = Services.CreateScope())
		{
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
		}
		var (cookie, token) = await CsrfAsync(null);
		using var request = Post("/api/auth/code/request", new { email }, cookie, token);
		using var response = await Client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = Factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await CsrfAsync(null);
		using var verify = Post("/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
		using var verifyResponse = await Client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		var session = Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
		sessions[email] = session;
		return session;
	}

	public Task<string> EditorAsync() => SignInAsync(Editor, ArchiveRoles.Editor);

	public Task<string> MemberAsync() => SignInAsync(Member, ArchiveRoles.Member);

	public async Task<HttpResponseMessage> PostAsync(string path, object body, string session)
		=> await SendAsync(HttpMethod.Post, path, body, session);

	public async Task<HttpResponseMessage> PutAsync(string path, object body, string session)
		=> await SendAsync(HttpMethod.Put, path, body, session);

	public async Task<HttpResponseMessage> PatchAsync(string path, object body, string session)
		=> await SendAsync(HttpMethod.Patch, path, body, session);

	public async Task<HttpResponseMessage> DeleteAsync(string path, string session)
		=> await SendAsync(HttpMethod.Delete, path, new { }, session);

	public async Task<HttpResponseMessage> GetAsync(string path, string? session)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (session is not null)
			request.Headers.Add("Cookie", session);
		return await Client.SendAsync(request);
	}

	public async Task<JsonElement> GetJsonAsync(string path, string session)
	{
		using var response = await GetAsync(path, session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	/// <summary>Creates a Lied through the API as the given session; returns its JSON.</summary>
	public async Task<JsonElement> CreateLiedAsync(string session, object body)
	{
		using var response = await PostAsync("/api/lieder", body, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lied");
	}

	public async Task<Guid> CreateLiedIdAsync(string session, string title)
		=> Guid.Parse((await CreateLiedAsync(session, new { title })).GetProperty("id").GetString()!);

	/// <summary>Opens an upload session for a new Datei of the Lied and returns (session id, upload url, JSON).</summary>
	public async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> OpenUploadAsync(
		string session, Guid liedId, object body)
	{
		using var response = await PostAsync($"/api/lieder/{liedId}/dateien/upload-session", body, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (Guid.Parse(json.GetProperty("uploadSessionId").GetString()!),
			json.GetProperty("uploadUrl").GetString()!, json);
	}

	/// <summary>Opens a session, stores a valid PDF in the fake storage and finalizes; returns the Datei JSON.</summary>
	public async Task<JsonElement> UploadNotenAsync(
		string session, Guid liedId, string? stimme, bool isPartitur = false, string fileName = "noten.pdf", long size = 1024)
	{
		var (sessionId, uploadUrl, _) = await OpenUploadAsync(session, liedId,
			new { kind = "noten", isPartitur, stimme, fileName, sizeBytes = size });
		Factory.Storage.Store(Factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(size));
		using var finalize = await PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, session);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		return (await finalize.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("datei");
	}

	public static byte[] ValidPdf(long size)
	{
		var bytes = new byte[size];
		"%PDF-1.7\n"u8.CopyTo(bytes);
		return bytes;
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, string session)
	{
		var (cookie, token) = await CsrfAsync(session);
		using var request = new HttpRequestMessage(method, path);
		request.Headers.Add("Cookie", $"{cookie}; {session}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return await Client.SendAsync(request);
	}

	private async Task<(string Cookie, string Token)> CsrfAsync(string? sessionCookie)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		if (sessionCookie is not null)
			tokenRequest.Headers.Add("Cookie", sessionCookie);
		using var response = await Client.SendAsync(tokenRequest);
		response.EnsureSuccessStatusCode();
		var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (cookie, body.GetProperty("token").GetString()!);
	}

	private static HttpRequestMessage Post(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		if (!ReferenceEquals(app, Factory))
			await app.DisposeAsync();
		await Factory.DisposeAsync();
	}
}
