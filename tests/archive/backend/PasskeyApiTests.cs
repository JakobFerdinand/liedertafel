using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-011-1 passkey API gates. Real WebAuthn ceremonies (attestation and
/// assertion) are covered by browser tests with Chromium's virtual
/// authenticator; these tests cover the surrounding policy: antiforgery,
/// authentication, fresh verification, list/rename/remove lifecycle and the
/// session-claim contract.
/// </summary>
public sealed class PasskeyApiTests
{
	private const string ActiveMember = "mitglied@liedertafel.test";

	[Fact]
	public async Task LoginOptionsIssueJsonWithoutSession()
	{
		await using var factory = new AuthApiFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var response = await client.PostAsync("/api/auth/passkeys/login/options", null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var options = body.GetProperty("requestOptions").GetString();
		Console.WriteLine("REQUESTOPTIONS: " + options);
		Assert.False(string.IsNullOrWhiteSpace(options));
		using var parsed = JsonDocument.Parse(options!);
		var rp = parsed.RootElement.GetProperty("rpId").GetString();
		Assert.Equal("localhost", rp);
		var challenge = parsed.RootElement.GetProperty("challenge").GetString();
		Assert.False(string.IsNullOrWhiteSpace(challenge));
	}

	[Fact]
	public async Task LoginVerifyRejectsInvalidCredentialUniformly()
	{
		await using var factory = new AuthApiFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/passkeys/login/verify", new { credential = "{not-json}" }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Die Passkey-Anmeldung ist ungültig.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task EnrollmentEndpointsRequireAuthenticationAndCsrf()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Anonymous calls never reach ceremony state.
		var (anonCookie, anonToken) = await GetCsrfAsync(client);
		using var anon = AuthedPost("/api/auth/passkeys/register/options", new { }, anonCookie, anonToken);
		using var anonResponse = await client.SendAsync(anon);
		Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

		// Session but forged token stays a 400 Problem.
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var session = await VerifyAndGetSessionAsync(client, ActiveMember, code);
		var (sessionCsrfCookie, sessionToken) = await GetCsrfAsync(client, session);
		using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/auth/passkeys/register/options")
		{
			Content = JsonContent.Create(new { }),
		};
		forged.Headers.Add("Cookie", $"{sessionCsrfCookie}; {session}");
		forged.Headers.Add("X-CSRF-TOKEN", "forged");
		using var forgedResponse = await client.SendAsync(forged);
		Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);
	}

	[Fact]
	public async Task EnrollmentRequiresFreshVerification()
	{
		await using var factory = new AuthApiFactory(freshVerificationWindow: TimeSpan.Zero);
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var session = await VerifyAndGetSessionAsync(client, ActiveMember, code);
		var (csrfCookie, token) = await GetCsrfAsync(client, session);
		using var options = AuthedPost("/api/auth/passkeys/register/options", new { }, csrfCookie, token);
		options.Headers.Add("Cookie", $"{csrfCookie}; {session}");
		using var optionsResponse = await client.SendAsync(options);
		Assert.Equal(HttpStatusCode.Forbidden, optionsResponse.StatusCode);
		var problem = await optionsResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Die Anmeldung ist zu alt. Bitte erneut anmelden.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task EnrollmentIssuesCreationOptionsForFreshSession()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var session = await VerifyAndGetSessionAsync(client, ActiveMember, code);
		var (csrfCookie, token) = await GetCsrfAsync(client, session);
		using var options = AuthedPost("/api/auth/passkeys/register/options", new { }, csrfCookie, token);
		options.Headers.Add("Cookie", $"{csrfCookie}; {session}");
		using var optionsResponse = await client.SendAsync(options);
		Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
		var body = await optionsResponse.Content.ReadFromJsonAsync<JsonElement>();
		var creationOptions = body.GetProperty("creationOptions").GetString();
		using var parsed = JsonDocument.Parse(creationOptions!);
		Assert.Equal("localhost", parsed.RootElement.GetProperty("rp").GetProperty("id").GetString());
		Assert.Equal(
			AuthSecurity.NormalizeEmail(ActiveMember).ToLowerInvariant(),
			parsed.RootElement.GetProperty("user").GetProperty("name").GetString());
	}

	[Fact]
	public async Task PasskeyListRenameRemoveLifecycleWithoutCredentials()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var session = await VerifyAndGetSessionAsync(client, ActiveMember, code);

		var (csrfCookie, token) = await GetCsrfAsync(client, session);

		using var list = HeaderedGet("/api/auth/passkeys", session);
		using var listResponse = await client.SendAsync(list);
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
		var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Empty(listBody.GetProperty("passkeys").EnumerateArray());

		var unknownId = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
		using var rename = AuthedPost("/api/auth/passkeys/rename", new { credentialId = unknownId, name = "Laptop" }, csrfCookie, token);
		rename.Headers.Add("Cookie", $"{csrfCookie}; {session}");
		using var renameResponse = await client.SendAsync(rename);
		Assert.Equal(HttpStatusCode.NotFound, renameResponse.StatusCode);

		using var remove = AuthedPost("/api/auth/passkeys/remove", new { credentialId = unknownId }, csrfCookie, token);
		remove.Headers.Add("Cookie", $"{csrfCookie}; {session}");
		using var removeResponse = await client.SendAsync(remove);
		Assert.Equal(HttpStatusCode.NotFound, removeResponse.StatusCode);
	}

	[Fact]
	public async Task MeReportsEmailCodeMethodAndPreservesAuthenticatedAtAcrossRefresh()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var session = await VerifyAndGetSessionAsync(client, ActiveMember, code);

		using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		me.Headers.Add("Cookie", session);
		using var meResponse = await client.SendAsync(me);
		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
		var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(meBody.GetProperty("authenticated").GetBoolean());
		Assert.Equal("email_code", meBody.GetProperty("authMethod").GetString());
		var verifiedAt = meBody.GetProperty("verifiedAt").GetString();

		// A second call runs through the stamp validator (fresh ticket has no
		// last-validation time) and must not reset the authenticated-at claim.
		using var meAgain = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		meAgain.Headers.Add("Cookie", session);
		using var meAgainResponse = await client.SendAsync(meAgain);
		var meAgainBody = await meAgainResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(verifiedAt, meAgainBody.GetProperty("verifiedAt").GetString());
		Assert.Equal("email_code", meAgainBody.GetProperty("authMethod").GetString());
	}

	[Fact]
	public async Task PasskeyLoginKeepsEmailCodeFailureUniformForUnknownAccounts()
	{
		// Without any registered passkey, the username-less ceremony can only
		// fail; a passkey login must never confirm an email or accept an
		// invitation, so the response stays a uniform 400 problem.
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/passkeys/login/verify", new { credential = "{}" }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
		Assert.False(body.GetProperty("authenticated").GetBoolean());
	}

	private static HttpRequestMessage HeaderedGet(string path, string sessionCookie)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", sessionCookie);
		return request;
	}

	private static async Task SeedActiveMemberAsync(AuthApiFactory factory, string email)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roles.RoleExistsAsync(ArchiveRoles.Member))
			Assert.True((await roles.CreateAsync(new ArchiveRole(ArchiveRoles.Member))).Succeeded);
		var user = await users.FindByEmailAsync(AuthSecurity.NormalizeEmail(email));
		if (user is null)
		{
			user = new ArchiveUser { UserName = email, Email = email, DisplayName = "Mitglied", EmailConfirmed = true };
			Assert.True((await users.CreateAsync(user)).Succeeded);
		}
		if (!await users.IsInRoleAsync(user, ArchiveRoles.Member))
			Assert.True((await users.AddToRoleAsync(user, ArchiveRoles.Member)).Succeeded);
	}

	private static async Task<string> RequestCodeAsync(AuthApiFactory factory, HttpClient client, string email)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		return factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
	}

	private static async Task<string> VerifyAndGetSessionAsync(HttpClient client, string email, string code)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify", new { email, code }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
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
		var request = new HttpRequestMessage(HttpMethod.Post, path)
		{
			Content = JsonContent.Create(body),
		};
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		return request;
	}
}
