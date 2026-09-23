using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Development;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Archive.Backend.Tests;

public sealed class AuthApiTests
{
	private const string ActiveMember = "mitglied@liedertafel.test";

	[Fact]
	public async Task RequestVerifyMeLogoutRoundTrip()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email = ActiveMember }, cookie, token);
		using var requestResponse = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);
		var requestBody = await requestResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AuthEndpoints.RequestMessage, requestBody.GetProperty("message").GetString());
		var code = Assert.Single(factory.Mail.Sent).Code;

		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);		using var verify = AuthedPost("/api/auth/code/verify",
			new { email = ActiveMember, code }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		var sessionCookie = Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie"));
		Assert.StartsWith("archive.auth=", sessionCookie);
		Assert.Contains("httponly", sessionCookie);
		Assert.Contains("samesite=strict", sessionCookie);
		var sessionValue = sessionCookie.Split(';')[0];

		using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		me.Headers.Add("Cookie", sessionValue);
		using var meResponse = await client.SendAsync(me);
		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
		var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(meBody.GetProperty("authenticated").GetBoolean());
		Assert.Contains("Member", meBody.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		Assert.Contains("no-store", meResponse.Headers.CacheControl!.ToString());

		// Browsers fetch the token with the session cookie attached, which
		// binds the antiforgery token to the authenticated user.
		var (logoutCookie, logoutToken) = await GetCsrfAsync(client, sessionValue);
		using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
		logout.Headers.Add("Cookie", $"{logoutCookie}; {sessionValue}");
		logout.Headers.Add("X-CSRF-TOKEN", logoutToken);
		logout.Content = JsonContent.Create(new { });
		using var logoutResponse = await client.SendAsync(logout);
		Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);
		// Cookie authentication is stateless: logout instructs the browser to
		// drop the cookie. The cleared cookie must be expired server-side.
		var cleared = Assert.Single(
			logoutResponse.Headers.GetValues("Set-Cookie"), c => c.StartsWith("archive.auth="));
		Assert.Contains("expires=", cleared);

		// Without the dropped cookie the member area no longer authenticates.
		var meAfterBody = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
		Assert.False(meAfterBody.GetProperty("authenticated").GetBoolean());
	}

	[Fact]
	public async Task UninvitedEmailGetsIndistinguishableResponse()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (knownCookie, knownToken) = await GetCsrfAsync(client);
		using var known = AuthedPost("/api/auth/code/request", new { email = ActiveMember }, knownCookie, knownToken);
		using var knownResponse = await client.SendAsync(known);

		var (unknownCookie, unknownToken) = await GetCsrfAsync(client);
		using var unknown = AuthedPost("/api/auth/code/request", new { email = "fremd@liedertafel.test" }, unknownCookie, unknownToken);
		using var unknownResponse = await client.SendAsync(unknown);

		Assert.Equal(knownResponse.StatusCode, unknownResponse.StatusCode);
		Assert.Equal(
			await knownResponse.Content.ReadAsStringAsync(),
			await unknownResponse.Content.ReadAsStringAsync());
		Assert.DoesNotContain(factory.Mail.Sent, m => m.Email == "fremd@liedertafel.test");

		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify",
			new { email = "fremd@liedertafel.test", code = "123456" }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.BadRequest, verifyResponse.StatusCode);
		var problem = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Code ist ungültig oder abgelaufen.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ExpiredCodeIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		var normalized = AuthSecurity.NormalizeEmail(ActiveMember);
		using (var scope = factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = (await users.FindByEmailAsync(ActiveMember))!;
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var salt = AuthSecurity.NewSalt();
			db.SignInChallenges.Add(new SignInChallenge
			{
				UserId = user.Id,
				NormalizedEmail = normalized,
				CodeHash = AuthSecurity.HashCode("654321", salt),
				Salt = salt,
				CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
				ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
				LastSentAt = DateTimeOffset.UtcNow.AddMinutes(-20),
			});
			await db.SaveChangesAsync();
		}
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify",
			new { email = ActiveMember, code = "654321" }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task ReusedCodeIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		await VerifyCodeAsync(client, ActiveMember, code, HttpStatusCode.OK);

		var (cookie, token) = await GetCsrfAsync(client);
		using var replay = AuthedPost("/api/auth/code/verify",
			new { email = ActiveMember, code }, cookie, token);
		using var replayResponse = await client.SendAsync(replay);
		Assert.Equal(HttpStatusCode.BadRequest, replayResponse.StatusCode);
		Assert.False(replayResponse.Headers.Contains("Set-Cookie"));
	}

	// NOTE: the five-way concurrent race lives in the AppHost suite on real
	// PostgreSQL. InMemory generates integer rate-log keys per context, so
	// parallel inserts collide there (Npgsql identity columns do not).
	// Sequential single-use is covered by ReusedCodeIsRejected above.

	[Fact]
	public async Task VerifyAttemptLimitLocksChallenge()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		for (var i = 0; i < 5; i++)
			await VerifyCodeAsync(client, ActiveMember, "000000", HttpStatusCode.BadRequest);
		await VerifyCodeAsync(client, ActiveMember, code, HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task ResendCooldownSuppressesMailButKeepsSameResponse()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		await RequestCodeAsync(factory, client, ActiveMember);
		await RequestCodeAsync(factory, client, ActiveMember);
		Assert.Single(factory.Mail.Sent);
	}

	[Fact]
	public async Task RequestAbuseLimitReturns429WithoutDisclosure()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		HttpStatusCode? last = null;
		for (var i = 0; i < 6; i++)
		{
			var (cookie, token) = await GetCsrfAsync(client);
			using var request = AuthedPost("/api/auth/code/request", new { email = ActiveMember }, cookie, token);
			using var response = await client.SendAsync(request);
			last = response.StatusCode;
		}
		Assert.Equal(HttpStatusCode.TooManyRequests, last);
	}

	[Theory]
	[InlineData("POST", "/api/auth/code/request")]
	[InlineData("POST", "/api/auth/code/verify")]
	[InlineData("POST", "/api/auth/logout")]
	public async Task AuthMutationsRejectMissingOrForgedCsrf(string method, string path)
	{
		await using var factory = new AuthApiFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var missing = new HttpRequestMessage(new HttpMethod(method), path);
		// Real clients always send a JSON body; without a content type the
		// request never reaches a body-bound endpoint. The CSRF check itself
		// must reject the call with 400.
		missing.Content = JsonContent.Create(new { email = ActiveMember, code = "123456" });
		using var missingResponse = await client.SendAsync(missing);
		Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);

		await client.GetAsync("/api/antiforgery");
		using var forged = new HttpRequestMessage(new HttpMethod(method), path);
		forged.Headers.Add("X-CSRF-TOKEN", "forged");
		forged.Content = JsonContent.Create(new { email = ActiveMember, code = "123456" });
		using var forgedResponse = await client.SendAsync(forged);
		Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);
	}

	[Fact]
	public async Task MeIsAnonymousEnvelopeWithoutCookie()
	{
		await using var factory = new AuthApiFactory();
		using var client = factory.CreateClient();
		var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
		Assert.False(body.GetProperty("authenticated").GetBoolean());
	}

	[Fact]
	public async Task AuthTelemetryRedactsCodesEmailsAndCookies()
	{
		var received = new ConcurrentDictionary<string, string>();
		var collectorBuilder = WebApplication.CreateBuilder();
		collectorBuilder.Logging.ClearProviders();
		await using var collector = collectorBuilder.Build();
		collector.Urls.Add("http://127.0.0.1:0");
		collector.MapPost("/v1/{signal}", async (HttpContext context, string signal) =>
		{
			using var body = new MemoryStream();
			await context.Request.Body.CopyToAsync(body);
			received[signal] = received.GetValueOrDefault(signal, "") + Encoding.UTF8.GetString(body.ToArray());
			context.Response.ContentType = "application/x-protobuf";
		});
		await collector.StartAsync();

		await using var factory = new AuthApiFactory(otlpEndpoint: collector.Urls.Single());
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var session = await VerifyAndGetSessionAsync(client, ActiveMember, code);
		using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		me.Headers.Add("Cookie", session);
		using var meResponse = await client.SendAsync(me);
		meResponse.EnsureSuccessStatusCode();

		var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (received.TryGetValue("traces", out var traces)
				&& traces.Contains("archive.auth.verify", StringComparison.Ordinal)
				&& received.TryGetValue("metrics", out var metrics)
				&& metrics.Contains("archive.auth.requests", StringComparison.Ordinal)
				&& received.ContainsKey("logs"))
				break;
			await Task.Delay(500);
		}
		Assert.Contains("archive.auth.request", received.GetValueOrDefault("traces", ""));
		Assert.Contains("archive.auth.verify", received.GetValueOrDefault("traces", ""));
		Assert.Contains("archive.mail.send", received.GetValueOrDefault("traces", ""));
		Assert.Contains("archive.auth.requests", received.GetValueOrDefault("metrics", ""));
		Assert.Contains("archive.auth.verified", received.GetValueOrDefault("metrics", ""));
		foreach (var payload in received.Values)
		{
			Assert.DoesNotContain(code, payload);
			Assert.DoesNotContain(ActiveMember, payload);
			Assert.DoesNotContain(session, payload);
		}
	}

	[Fact]
	public async Task CodesAreNeverStoredPlaintext()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		await RequestCodeAsync(factory, client, ActiveMember);
		var code = Assert.Single(factory.Mail.Sent).Code;
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var row = await db.SignInChallenges.SingleAsync();
		Assert.Equal(32, row.CodeHash.Length);
		Assert.Equal(16, row.Salt.Length);
		Assert.True(AuthSecurity.VerifyCode(code, row.Salt, row.CodeHash));
		Assert.False(row.CodeHash.SequenceEqual(Encoding.UTF8.GetBytes(code)));
	}

	[Fact]
	public async Task SessionSurvivesRestartWhenKeysPathRetained()
	{
		var root = new InMemoryDatabaseRoot();
		var database = $"auth-{Guid.NewGuid():N}";
		var keys = Path.Combine(Path.GetTempPath(), $"archive-auth-keys-{Guid.NewGuid():N}");
		string session;
		await using (var first = new AuthApiFactory("Development", root, keys, database))
		{
			await SeedActiveMemberAsync(first, ActiveMember);
			using var client = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
			var code = await RequestCodeAsync(first, client, ActiveMember);
			session = await VerifyAndGetSessionAsync(client, ActiveMember, code);
			Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
		}
		await using var second = new AuthApiFactory("Development", root, keys, database);
		using var secondClient = second.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		me.Headers.Add("Cookie", session);
		using var meResponse = await secondClient.SendAsync(me);
		var body = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(body.GetProperty("authenticated").GetBoolean());

		var freshKeys = Path.Combine(Path.GetTempPath(), $"archive-auth-keys-{Guid.NewGuid():N}");
		await using var third = new AuthApiFactory("Development", root, freshKeys, database);
		using var thirdClient = third.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var retry = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		retry.Headers.Add("Cookie", session);
		using var retryResponse = await thirdClient.SendAsync(retry);
		var retryBody = await retryResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(retryBody.GetProperty("authenticated").GetBoolean());
	}

	[Fact]
	public async Task SessionCookieIsHttpOnlySameSiteAndSecureInProduction()
	{
		var root = new InMemoryDatabaseRoot();
		// ARC-010: Production requires Mail:Provider=Azure at startup; the
		// sender itself stays faked, so no network is involved.
		await using var factory = new AuthApiFactory("Production", root, settings: AuthApiFactory.ProductionMailSettings);
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://localhost"),
			HandleCookies = false,
		});
		var code = await RequestCodeAsync(factory, client, ActiveMember);
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify",
			new { email = ActiveMember, code }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var sessionCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
		Assert.Contains("httponly", sessionCookie);
		Assert.Contains("samesite=strict", sessionCookie);
		Assert.Contains("secure", sessionCookie);
	}

	[Fact]
	public async Task HostedForwardedHttpsServesAntiforgeryWithoutServerFailure()
	{
		// ARC-011: Container Apps terminates TLS at the front proxy and
		// forwards plain HTTP. The app must honor X-Forwarded-Proto, or every
		// hosted antiforgery POST dies with an SSL 500 before any endpoint
		// runs and no hosted sign-in can complete.
		await using var factory = new AuthApiFactory("Production", new InMemoryDatabaseRoot(), settings: AuthApiFactory.ProductionMailSettings);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("http://localhost"),
			HandleCookies = false,
		});
		client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
		using var tokenResponse = await client.GetAsync("/api/antiforgery");
		Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
		Assert.Contains("secure", Assert.Single(tokenResponse.Headers.GetValues("Set-Cookie")));
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
		request.Content = JsonContent.Create(new { email = "gast@liedertafel.test" });
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Ungültiger Sicherheitstoken.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task DevSeedCreatesAllRolesAndStaysAbsentInProduction()
	{
		await using var factory = new AuthApiFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var seed = AuthedPost("/api/dev/auth/seed", new { }, cookie, token);
		using var response = await client.SendAsync(seed);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var roles = body.GetProperty("accounts").EnumerateArray()
			.Select(a => a.GetProperty("role").GetString() ?? string.Empty).OrderBy(r => r).ToArray();
		Assert.Equal(["Administrator", "Editor", "Member"], roles);

		await using var production = new AuthApiFactory("Production", new InMemoryDatabaseRoot(), settings: AuthApiFactory.ProductionMailSettings);
		using var prodClient = production.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var prodSeed = await prodClient.PostAsync("/api/dev/auth/seed", JsonContent.Create(new { }));
		Assert.Equal(HttpStatusCode.NotFound, prodSeed.StatusCode);
		Assert.Equal("application/problem+json", prodSeed.Content.Headers.ContentType?.MediaType);
	}

	[Fact]
	public async Task BootstrapCreatesFirstAdminAndRefusesSecond()
	{
		var services = BootstrapServices("Development", new Dictionary<string, string?>());
		await using var provider = services.BuildServiceProvider();
		var configuration = provider.GetRequiredService<IConfiguration>();
		await OperatorConfiguration.BootstrapAdminAsync(provider, configuration,
			["--email", "erste@liedertafel.test", "--name", "Erste", "--operator", "Test"], CancellationToken.None);
		// Bare providers isolate the InMemory store per scope, so resolve from
		// the same root scope the operator command used (the web host shares
		// across scopes; see AuthApiFactory-based tests for that path).
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var db = provider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(1, await db.Users.CountAsync());
		var admin = await users.FindByEmailAsync("erste@liedertafel.test");
		Assert.NotNull(admin);
		Assert.True(admin.EmailConfirmed);
		Assert.True(await users.IsInRoleAsync(admin, ArchiveRoles.Administrator));
		await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorConfiguration.BootstrapAdminAsync(
			provider, configuration, ["--email", "zweite@liedertafel.test"], CancellationToken.None));
	}

	[Fact]
	public async Task BootstrapOutsideDevelopmentRequiresOperatorToken()
	{
		var services = BootstrapServices("Production", new Dictionary<string, string?>
		{
			["Archive:OperatorToken"] = "geheim",
		});
		await using var provider = services.BuildServiceProvider();
		var configuration = provider.GetRequiredService<IConfiguration>();
		await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorConfiguration.BootstrapAdminAsync(
			provider, configuration, ["--email", "admin@liedertafel.test"], CancellationToken.None));
		await OperatorConfiguration.BootstrapAdminAsync(provider, configuration,
			["--email", "admin@liedertafel.test", "--operator-token", "geheim"], CancellationToken.None);
		Assert.Equal(1, await provider.GetRequiredService<ArchiveDbContext>().Users.CountAsync());
	}

	private static ServiceCollection BootstrapServices(string environment, Dictionary<string, string?> settings)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environment));
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
		services.AddIdentity<ArchiveUser, ArchiveRole>()
			.AddEntityFrameworkStores<ArchiveDbContext>()
			.AddDefaultTokenProviders()
			.AddTokenProvider<EmailCodeTokenProvider>(EmailCodeTokenProvider.ProviderName);
		// The root must be captured outside the options lambda: options are
		// built per scope, so `new` inside the lambda would isolate every scope.
		var root = new InMemoryDatabaseRoot();
		services.AddDbContext<ArchiveDbContext>(options =>
			options.UseInMemoryDatabase($"bootstrap-{Guid.NewGuid():N}", root));
		return services;
	}

	private static async Task SeedActiveMemberAsync(AuthApiFactory factory, string email, string role = ArchiveRoles.Member)
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
	}

	private static async Task<string> RequestCodeAsync(AuthApiFactory factory, HttpClient client, string email)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		return factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
	}

	private static async Task VerifyCodeAsync(HttpClient client, string email, string code, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify", new { email, code }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(expected, response.StatusCode);
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
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private sealed class TestHostEnvironment(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;
		public string ApplicationName { get; set; } = "Archive.Backend.Tests";
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}
}

internal sealed class FakeMailSender : IArchiveMailSender
{
	public sealed record SentMail(string Email, string Code);

	public sealed record SentInvitation(string Email, string? DisplayName, string Role);

	public sealed record SentEmailChange(string Email, string Code);

	public sealed record SentTestMessage(string Email);

	private readonly List<SentMail> sent = [];
	private readonly List<SentInvitation> invitations = [];
	private readonly List<SentEmailChange> emailChanges = [];
	private readonly List<SentTestMessage> testMessages = [];
	private readonly object gate = new();
	private Func<string, Exception?>? invitationFailure;
	private Func<string, Exception?>? codeFailure;

	public IReadOnlyList<SentMail> Sent
	{
		get
		{
			lock (gate) return [.. sent];
		}
	}

	public IReadOnlyList<SentInvitation> SentInvitations
	{
		get
		{
			lock (gate) return [.. invitations];
		}
	}

	public IReadOnlyList<SentEmailChange> SentEmailChanges
	{
		get
		{
			lock (gate) return [.. emailChanges];
		}
	}

	public IReadOnlyList<SentTestMessage> SentTestMessages
	{
		get
		{
			lock (gate) return [.. testMessages];
		}
	}

	public void FailInvitations(Func<string, Exception?> failure) => invitationFailure = failure;

	public void FailCodes(Func<string, Exception?> failure) => codeFailure = failure;

	public Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		// Mirror the production sender's span so telemetry assertions cover
		// the mail path even with mail capture faked out.
		using var _ = Extensions.Activities.StartActivity("archive.mail.send", ActivityKind.Client);
		Func<string, Exception?>? failure;
		lock (gate) failure = codeFailure;
		var error = failure?.Invoke(email);
		if (error is not null)
			throw error;
		lock (gate) sent.Add(new SentMail(email, code));
		return Task.CompletedTask;
	}

	public Task SendInvitationAsync(string email, string? displayName, string role, CancellationToken cancellationToken)
	{
		using var _ = Extensions.Activities.StartActivity("archive.mail.invite.send", ActivityKind.Client);
		Func<string, Exception?>? failure;
		lock (gate) failure = invitationFailure;
		var error = failure?.Invoke(email);
		if (error is not null)
			throw error;
		lock (gate) invitations.Add(new SentInvitation(email, displayName, role));
		return Task.CompletedTask;
	}

	public Task SendEmailChangeCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		using var _ = Extensions.Activities.StartActivity("archive.mail.email_change.send", ActivityKind.Client);
		Func<string, Exception?>? failure;
		lock (gate) failure = invitationFailure;
		var error = failure?.Invoke(email);
		if (error is not null)
			throw error;
		lock (gate) emailChanges.Add(new SentEmailChange(email, code));
		return Task.CompletedTask;
	}

	public Task SendTestMessageAsync(string email, CancellationToken cancellationToken)
	{
		using var _ = Extensions.Activities.StartActivity("archive.mail.test.send", ActivityKind.Client);
		lock (gate) testMessages.Add(new SentTestMessage(email));
		return Task.CompletedTask;
	}
}

internal sealed class AuthApiFactory : WebApplicationFactory<Program>
{
	/// <summary>
	/// ARC-010: Production hosts require <c>Mail:Provider=Azure</c> at
	/// startup. Tests keep the faked sender, so no network is involved; the
	/// endpoint value is never contacted.
	/// ARC-011: Production hosts additionally require Blob/Key Vault key
	/// persistence. Tests stay ephemeral via
	/// <c>Authentication:AllowEphemeralKeysForTests</c>; real hosted
	/// deployments must leave that unset and supply both URIs.
	/// </summary>
	internal static IDictionary<string, string?> ProductionMailSettings { get; } = new Dictionary<string, string?>
	{
		["Mail:Provider"] = "Azure",
		["Mail:AzureEndpoint"] = "https://acs-liedertafel-test.communication.azure.com",
		["Authentication:AllowEphemeralKeysForTests"] = "true",
		["Authentication:PasskeyRelyingPartyId"] = "archiv.liedertafel.test",
	};

	private readonly string environment;
	private readonly string root = Path.Combine(Path.GetTempPath(), $"archive-auth-tests-{Guid.NewGuid():N}");
	private readonly string database;
	private readonly InMemoryDatabaseRoot sharedRoot;
	private readonly string keysPath;
	private readonly string? otlpEndpoint;
	private readonly TimeSpan? freshVerificationWindow;

	public FakeMailSender Mail { get; } = new();

	/// <summary>ARC-015: in-memory blob storage so no real provider is touched.</summary>
	public FakeAssetStorage Storage { get; } = new();

	/// <summary>Captures formatted log lines for chat budget/cap assertions.</summary>
	public CapturingLogProvider Logs { get; } = new();

	private readonly IAssetStorageAdapter? storageOverride;

	private readonly IChatClient? chatClientOverride;

	private readonly ISaveChangesInterceptor? saveChangesInterceptor;

	private readonly IDictionary<string, string?>? extraSettings;

	public AuthApiFactory(string environment = "Development", InMemoryDatabaseRoot? root = null, string? keysPath = null, string? databaseName = null, string? otlpEndpoint = null, TimeSpan? freshVerificationWindow = null, IDictionary<string, string?>? settings = null, IAssetStorageAdapter? storage = null, IChatClient? chatClient = null, ISaveChangesInterceptor? saveChangesInterceptor = null)
	{
		this.environment = environment;
		sharedRoot = root ?? new InMemoryDatabaseRoot();
		database = databaseName ?? $"auth-{Guid.NewGuid():N}";
		this.keysPath = keysPath ?? Path.Combine(this.root, "keys");
		this.otlpEndpoint = otlpEndpoint;
		this.freshVerificationWindow = freshVerificationWindow;
		this.extraSettings = settings;
		storageOverride = storage;
		chatClientOverride = chatClient;
		this.saveChangesInterceptor = saveChangesInterceptor;
		Directory.CreateDirectory(Path.Combine(this.root, "system/status"));
		File.WriteAllText(Path.Combine(this.root, "index.html"), "<html lang=de><h1>frontend-fixture</h1></html>");
		File.WriteAllText(Path.Combine(this.root, "system/status/index.html"), "<html lang=de><h1>frontend-fixture status</h1></html>");
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder
			.UseEnvironment(environment).UseWebRoot(root)
			.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint ?? "http://127.0.0.1:1")
			.UseSetting("OTEL_EXPORTER_OTLP_TIMEOUT", "10")
			.UseSetting("Development:KeysPath", keysPath);
		if (extraSettings is not null)
		{
			// Per-test configuration (e.g. the ARC-010 Azure mail provider
			// for Production hosts, where mail stays faked below).
			foreach (var (key, value) in extraSettings)
				builder.UseSetting(key, value);
		}
		if (otlpEndpoint is not null)
		{
			// A live test collector: short export intervals so the test
			// observes all three signals without waiting a minute.
			builder
				.UseSetting("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
				.UseSetting("OTEL_BSP_SCHEDULE_DELAY", "500")
				.UseSetting("OTEL_BLRP_SCHEDULE_DELAY", "500")
				.UseSetting("OTEL_METRIC_EXPORT_INTERVAL", "1000");
		}
		builder.ConfigureServices(services =>
		{
			// EF keeps Program's Npgsql options action in a separate
			// IDbContextOptionsConfiguration descriptor; removing only the
			// options/context leaves it behind and it still throws.
			services.RemoveAll<IDbContextOptionsConfiguration<ArchiveDbContext>>();
			services.RemoveAll<DbContextOptions<ArchiveDbContext>>();
			services.RemoveAll<DbContextOptions>();
			services.RemoveAll<ArchiveDbContext>();
			services.AddDbContext<ArchiveDbContext>(options =>
			{
				options.UseInMemoryDatabase(database, sharedRoot);
				// Per-test save interception (e.g. the ARC-022 chat persistence
				// atomicity check) rides on the same in-memory database.
				if (saveChangesInterceptor is not null)
					options.AddInterceptors(saveChangesInterceptor);
			});
			services.RemoveAll<IArchiveMailSender>();
			services.AddSingleton<IArchiveMailSender>(Mail);
			// Program registers both the concrete adapter and a forwarding
			// interface registration; both must go before the fake replaces
			// the real Blob storage path (no network, no credentials).
			services.RemoveAll<IAssetStorageAdapter>();
			services.RemoveAll<BlobAssetStorageAdapter>();
			services.AddSingleton<IAssetStorageAdapter>(storageOverride ?? Storage);
			// Program registers the deterministic ScriptedChatClient behind the
			// IChatClient seam; a per-test override (ARC-022 bound checks) must
			// replace that single descriptor exactly like the mail fake.
			services.RemoveAll<IChatClient>();
			if (chatClientOverride is not null)
				services.AddSingleton<IChatClient>(chatClientOverride);
			else
				services.AddSingleton<IChatClient, Archive.Backend.Chat.ScriptedChatClient>();
			// Capturing provider so chat tests can assert maintainer warnings
			// without touching log content beyond the searched phrase.
			services.AddSingleton<ILoggerProvider>(Logs);
			if (freshVerificationWindow.HasValue)
			{
				var window = freshVerificationWindow.Value;
				services.Configure<AuthOptions>(options => options.FreshVerificationWindow = window);
			}
		});
	}

	public override async ValueTask DisposeAsync()
	{
		await base.DisposeAsync();
		Directory.Delete(root, recursive: true);
	}
}
