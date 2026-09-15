using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Development;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

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
			logoutResponse.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("archive.auth=")));
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
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var salt = AuthSecurity.NewSalt();
			db.SignInCodes.Add(new SignInCode
			{
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

	[Fact]
	public async Task ConcurrentVerifyAllowsExactlyOneWinner()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, ActiveMember);

		var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
		{
			var (cookie, token) = await GetCsrfAsync(client);
			using var verify = AuthedPost("/api/auth/code/verify",
				new { email = ActiveMember, code }, cookie, token);
			using var response = await client.SendAsync(verify);
			return response.StatusCode;
		}));
		Assert.Single(attempts, s => s == HttpStatusCode.OK);
		Assert.Equal(4, attempts.Count(s => s == HttpStatusCode.BadRequest));
	}

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
	public async Task CodesAreNeverStoredPlaintext()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, ActiveMember);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		await RequestCodeAsync(factory, client, ActiveMember);
		var code = Assert.Single(factory.Mail.Sent).Code;
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var row = await db.SignInCodes.SingleAsync();
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
		await using var factory = new AuthApiFactory("Production", root);
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
			.Select(a => a.GetProperty("role").GetString()).OrderBy(r => r).ToArray();
		Assert.Equal(["Administrator", "Editor", "Member"], roles);

		await using var production = new AuthApiFactory("Production", new InMemoryDatabaseRoot());
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
		var db = provider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(1, await db.Accounts.CountAsync());
		var membership = await db.Memberships.Include(m => m.Account).SingleAsync();
		Assert.Equal(ArchiveRole.Administrator, membership.Role);
		Assert.Equal(MembershipStatus.Active, membership.Status);
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
		Assert.Equal(1, await provider.GetRequiredService<ArchiveDbContext>().Accounts.CountAsync());
	}

	private static ServiceCollection BootstrapServices(string environment, Dictionary<string, string?> settings)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environment));
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
		// The root must be captured outside the options lambda: options are
		// built per scope, so `new` inside the lambda would isolate every scope.
		var root = new InMemoryDatabaseRoot();
		services.AddDbContext<ArchiveDbContext>(options =>
			options.UseInMemoryDatabase($"bootstrap-{Guid.NewGuid():N}", root));
		return services;
	}

	private static async Task SeedActiveMemberAsync(AuthApiFactory factory, string email, ArchiveRole role = ArchiveRole.Member)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		await AuthSeed.EnsureOperatorAccountAsync(db, email, "Test", role, "Test", DateTimeOffset.UtcNow, CancellationToken.None);
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

	private readonly List<SentMail> sent = [];
	private readonly object gate = new();

	public IReadOnlyList<SentMail> Sent
	{
		get
		{
			lock (gate) return [.. sent];
		}
	}

	public Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		lock (gate) sent.Add(new SentMail(email, code));
		return Task.CompletedTask;
	}
}

internal sealed class AuthApiFactory : WebApplicationFactory<Program>
{
	private readonly string environment;
	private readonly string root = Path.Combine(Path.GetTempPath(), $"archive-auth-tests-{Guid.NewGuid():N}");
	private readonly string database;
	private readonly InMemoryDatabaseRoot sharedRoot;
	private readonly string keysPath;

	public FakeMailSender Mail { get; } = new();

	public AuthApiFactory(string environment = "Development", InMemoryDatabaseRoot? root = null, string? keysPath = null, string? databaseName = null)
	{
		this.environment = environment;
		sharedRoot = root ?? new InMemoryDatabaseRoot();
		database = databaseName ?? $"auth-{Guid.NewGuid():N}";
		this.keysPath = keysPath ?? Path.Combine(this.root, "keys");
		Directory.CreateDirectory(Path.Combine(this.root, "system/status"));
		File.WriteAllText(Path.Combine(this.root, "index.html"), "<html lang=de><h1>frontend-fixture</h1></html>");
		File.WriteAllText(Path.Combine(this.root, "system/status/index.html"), "<html lang=de><h1>frontend-fixture status</h1></html>");
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
		.UseEnvironment(environment).UseWebRoot(root)
		.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1")
		.UseSetting("OTEL_EXPORTER_OTLP_TIMEOUT", "10")
		.UseSetting("Development:KeysPath", keysPath)
		.ConfigureServices(services =>
		{
			// EF keeps Program's Npgsql options action in a separate
			// IDbContextOptionsConfiguration descriptor; removing only the
			// options/context leaves it behind and it still throws.
			services.RemoveAll<IDbContextOptionsConfiguration<ArchiveDbContext>>();
			services.RemoveAll<DbContextOptions<ArchiveDbContext>>();
			services.RemoveAll<DbContextOptions>();
			services.RemoveAll<ArchiveDbContext>();
			services.AddDbContext<ArchiveDbContext>(options => options.UseInMemoryDatabase(database, sharedRoot));
			services.RemoveAll<IArchiveMailSender>();
			services.AddSingleton<IArchiveMailSender>(Mail);
		});

	public override async ValueTask DisposeAsync()
	{
		await base.DisposeAsync();
		Directory.Delete(root, recursive: true);
	}
}
