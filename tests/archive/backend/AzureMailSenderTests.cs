using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-010: shared German templates, Azure sender behaviour (bounded sends,
/// failure diagnostics without PII), transport selection, production guards,
/// and safe resend after sender rejection.
/// </summary>
public sealed class AzureMailSenderTests
{
	private const string Member = "mitglied@liedertafel.test";

	[Fact]
	public void SignInCodeTemplateRendersExactGermanCopy()
	{
		var message = ArchiveMailTemplates.SignInCode("123456", TimeSpan.FromMinutes(10));
		Assert.Equal("Ihr Anmeldecode für das Liedertafel-Archiv", message.Subject);
		Assert.Equal(
			"Guten Tag!\n\nIhr Anmeldecode lautet: 123456\n\n"
				+ "Der Code ist 10 Minuten gültig und kann einmal verwendet werden.\n"
				+ "Falls Sie keinen Code angefordert haben, ignorieren Sie diese Nachricht.\n\n"
				+ "Ihre Liedertafel Mining 1906",
			message.PlainTextBody);
	}

	[Theory]
	[InlineData("Member", "als Mitglied")]
	[InlineData("Editor", "als Editor")]
	[InlineData("Administrator", "als Administrator")]
	[InlineData("Unbekannt", "als Mitglied")]
	public void InvitationTemplateMapsRolesToGermanDesignations(string role, string expected)
	{
		var message = ArchiveMailTemplates.Invitation(null, role);
		Assert.Equal("Einladung zum Liedertafel-Archiv", message.Subject);
		Assert.StartsWith("Guten Tag!\n\nSie wurden zum Liedertafel-Archiv eingeladen " + expected + ".", message.PlainTextBody);
	}

	[Fact]
	public void InvitationTemplateGreetsNamedMemberWithUmlauts()
	{
		var message = ArchiveMailTemplates.Invitation("  Jürgen Ünter  ", ArchiveRoles.Member);
		Assert.StartsWith("Guten Tag, Jürgen Ünter!", message.PlainTextBody);
		Assert.Contains("Melden Sie sich mit dieser E-Mail-Adresse unter /anmelden/ an.", message.PlainTextBody);
	}

	[Fact]
	public void EmailChangeTemplateRendersExactGermanCopy()
	{
		var message = ArchiveMailTemplates.EmailChangeCode("654321", TimeSpan.FromMinutes(10));
		Assert.Equal("Neue E-Mail-Adresse für das Liedertafel-Archiv bestätigen", message.Subject);
		Assert.Contains("Ihr Bestätigungscode lautet: 654321", message.PlainTextBody);
		Assert.Contains("die Adresse wird ohne Code nicht übernommen.", message.PlainTextBody);
	}

	[Fact]
	public void TestMessageIsClearlyMarkedAndMentionsNoCode()
	{
		var message = ArchiveMailTemplates.AzureSendTest();
		Assert.Equal("Archiv: Azure-Versandtest", message.Subject);
		Assert.Contains("Testnachricht", message.PlainTextBody);
		Assert.DoesNotContain("Code", message.PlainTextBody);
	}

	[Fact]
	public async Task AzureSenderPassesSharedTemplateToTransport()
	{
		var transport = new FakeAzureTransport();
		var sender = AzureSender(transport);
		await sender.SendSignInCodeAsync("  Neues@liedertafel.test  ", "123456", TimeSpan.FromMinutes(10), CancellationToken.None);
		var call = Assert.Single(transport.Calls);
		var template = ArchiveMailTemplates.SignInCode("123456", TimeSpan.FromMinutes(10));
		Assert.Equal("archiv@liedertafel-mining.at", call.From);
		Assert.Equal("Neues@liedertafel.test", call.To);
		Assert.Equal(template.Subject, call.Subject);
		Assert.Equal(template.PlainTextBody, call.Body);
	}

	[Fact]
	public async Task AzureSenderHonoursConfiguredMailFrom()
	{
		var transport = new FakeAzureTransport();
		var sender = AzureSender(transport, new Dictionary<string, string?> { ["Auth:MailFrom"] = "chor@liedertafel-mining.at" });
		await sender.SendInvitationAsync("neu@liedertafel.test", null, ArchiveRoles.Editor, CancellationToken.None);
		Assert.Equal("chor@liedertafel-mining.at", Assert.Single(transport.Calls).From);
	}

	[Fact]
	public async Task SenderRejectionPropagatesWithoutRetry()
	{
		var transport = new FakeAzureTransport((_, _) =>
			Task.FromException<string>(new AzureMailSendException(400, "SenderRejected", "op-rejected", "rejected")));
		var sender = AzureSender(transport);
		var failure = await Assert.ThrowsAsync<AzureMailSendException>(() =>
			sender.SendSignInCodeAsync("mitglied@liedertafel.test", "123456", TimeSpan.FromMinutes(10), CancellationToken.None));
		Assert.Equal(400, failure.Status);
		Assert.Single(transport.Calls);
	}

	[Fact]
	public async Task DelayedDeliverySucceedsWithinTimeout()
	{
		var transport = new FakeAzureTransport(async (_, token) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(1), token);
			return "op-delayed";
		});
		var sender = AzureSender(transport, sendTimeout: TimeSpan.FromSeconds(30));
		await sender.SendSignInCodeAsync("mitglied@liedertafel.test", "123456", TimeSpan.FromMinutes(10), CancellationToken.None);
		Assert.Equal("op-delayed", Assert.Single(transport.OperationIds));
	}

	[Fact]
	public async Task HungSendFailsFastWithTimeoutDiagnostic()
	{
		// Note: the registration is intentionally not disposed here. Disposing
		// it on lambda return would unregister the callback before the timeout
		// fires and hang the wait forever; the sender disposes its timeout
		// source (and with it the registration) when the send settles.
		var transport = new FakeAzureTransport((_, token) =>
		{
			var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			token.Register(() => completion.TrySetCanceled(token));
			return completion.Task;
		});
		var sender = AzureSender(transport, sendTimeout: TimeSpan.FromMilliseconds(100));
		var started = DateTimeOffset.UtcNow;
		var failure = await Assert.ThrowsAsync<AzureMailSendException>(() =>
			sender.SendTestMessageAsync("mitglied@liedertafel.test", CancellationToken.None));
		Assert.Equal("SendTimeout", failure.ErrorCode);
		Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task CancelledRequestPropagatesCancellationUnwrapped()
	{
		var transport = new FakeAzureTransport((_, token) =>
		{
			token.ThrowIfCancellationRequested();
			return Task.FromResult("op-never");
		});
		var sender = AzureSender(transport);
		using var cancelled = new CancellationTokenSource();
		await cancelled.CancelAsync();
		var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
			sender.SendTestMessageAsync("mitglied@liedertafel.test", cancelled.Token));
		Assert.IsNotType<AzureMailSendException>(thrown);
	}

	[Fact]
	public async Task AzureSenderLogsOperationIdButNeverContent()
	{
		const string address = "geheim@liedertafel.test";
		const string code = "123456";
		var logger = new TestLogger<AzureCommunicationMailSender>();
		var transport = new FakeAzureTransport();
		var sender = new AzureCommunicationMailSender(
			TestConfiguration(), Options.Create(AzureOptions()), transport, logger);
		await sender.SendSignInCodeAsync(address, code, TimeSpan.FromMinutes(10), CancellationToken.None);
		Assert.NotEmpty(logger.Messages);
		Assert.All(logger.Messages, message =>
		{
			Assert.DoesNotContain(address, message);
			Assert.DoesNotContain(code, message);
			Assert.DoesNotContain("Anmeldecode lautet", message);
		});
		Assert.Contains(logger.Messages, message => message.Contains(Assert.Single(transport.OperationIds)));
	}

	[Fact]
	public async Task AzureSenderFailureLogCarriesOnlySafeDiagnostics()
	{
		const string address = "geheim@liedertafel.test";
		const string code = "123456";
		var logger = new TestLogger<AzureCommunicationMailSender>();
		var transport = new FakeAzureTransport((_, _) =>
			Task.FromException<string>(new AzureMailSendException(403, "SenderNotAllowed", "op-denied", "denied")));
		var sender = new AzureCommunicationMailSender(
			TestConfiguration(), Options.Create(AzureOptions()), transport, logger);
		await Assert.ThrowsAsync<AzureMailSendException>(() =>
			sender.SendSignInCodeAsync(address, code, TimeSpan.FromMinutes(10), CancellationToken.None));
		var diagnostics = Assert.Single(logger.Messages);
		Assert.Contains("403", diagnostics);
		Assert.Contains("SenderNotAllowed", diagnostics);
		Assert.Contains("op-denied", diagnostics);
		Assert.DoesNotContain(address, diagnostics);
		Assert.DoesNotContain(code, diagnostics);
	}

	[Fact]
	public void DefaultSelectionKeepsSmtpCapture()
	{
		var sender = ResolveSender(new Dictionary<string, string?>());
		Assert.IsType<SmtpSignInCodeSender>(sender);
	}

	[Fact]
	public void AzureSelectionResolvesCommunicationSender()
	{
		var sender = ResolveSender(new Dictionary<string, string?>
		{
			["Mail:Provider"] = "Azure",
			["Mail:AzureEndpoint"] = "https://acs-liedertafel-test.communication.azure.com",
		});
		Assert.IsType<AzureCommunicationMailSender>(sender);
	}

	[Theory]
	[InlineData("Production", "Smtp", null, null, true)]
	[InlineData("Production", "Azure", "https://acs-liedertafel-test.communication.azure.com", null, false)]
	[InlineData("Production", "Azure", null, "endpoint=https://x;accesskey=y", true)]
	[InlineData("Development", "Smtp", null, null, false)]
	[InlineData("Development", "Azure", null, "endpoint=https://x;accesskey=y", false)]
	public void ProductionGuardsCaptureAndSenderSecrets(
		string environment, string provider, string? endpoint, string? connectionString, bool throws)
	{
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Mail:Provider"] = provider,
			["Mail:AzureEndpoint"] = endpoint,
			["Mail:AzureConnectionString"] = connectionString,
		}).Build();
		var action = () => MailGuards.ValidateProductionMail(configuration, new StubHostEnvironment(environment));
		if (throws)
			Assert.Throws<InvalidOperationException>(action);
		else
			action();
	}

	[Fact]
	public async Task SenderRejectionKeepsResendSafe()
	{
		await using var factory = new AuthApiFactory();
		await SeedActiveMemberAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Simulate Azure sender rejection: the request fails honestly instead
		// of reporting a sent code.
		factory.Mail.FailCodes(_ => new AzureMailSendException(400, "SenderRejected", null, "rejected"));
		using var rejected = await PostCodeRequestAsync(client, Member);
		Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
		var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Die Anfrage konnte nicht verarbeitet werden.", problem.GetProperty("title").GetString());
		Assert.Empty(factory.Mail.Sent);

		// Once the sender recovers, the very next request (inside the old
		// cooldown window) delivers a fresh code: the undelivered challenge
		// was expired instead of suppressing the resend.
		factory.Mail.FailCodes(_ => null);
		using var recovered = await PostCodeRequestAsync(client, Member);
		Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
		Assert.Single(factory.Mail.Sent);
	}

	private static AzureCommunicationMailSender AzureSender(
		FakeAzureTransport transport,
		IDictionary<string, string?>? settings = null,
		TimeSpan? sendTimeout = null)
	{
		var merged = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
		{
			["Mail:Provider"] = "Azure",
		};
		if (sendTimeout.HasValue)
			merged["Mail:SendTimeout"] = sendTimeout.Value.ToString();
		return new AzureCommunicationMailSender(
			new ConfigurationBuilder().AddInMemoryCollection(merged).Build(),
			Options.Create(new MailOptions { Provider = "Azure", SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(45) }),
			transport,
			new TestLogger<AzureCommunicationMailSender>());
	}

	private static MailOptions AzureOptions() => new()
	{
		Provider = "Azure",
		AzureEndpoint = "https://acs-liedertafel-test.communication.azure.com",
	};

	private static IConfiguration TestConfiguration(IDictionary<string, string?>? settings = null) =>
		new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();

	private static IArchiveMailSender ResolveSender(IDictionary<string, string?> settings)
	{
		var configuration = TestConfiguration(settings);
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddArchiveIdentity(configuration);
		services.RemoveAll<IAzureEmailTransport>();
		services.AddSingleton<IAzureEmailTransport>(new FakeAzureTransport());
		using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		return scope.ServiceProvider.GetRequiredService<IArchiveMailSender>();
	}

	private static async Task SeedActiveMemberAsync(AuthApiFactory factory, string email)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roles.RoleExistsAsync(ArchiveRoles.Member))
			Assert.True((await roles.CreateAsync(new ArchiveRole(ArchiveRoles.Member))).Succeeded);
		var user = new ArchiveUser { UserName = email, Email = email, DisplayName = "Test", EmailConfirmed = true };
		Assert.True((await users.CreateAsync(user)).Succeeded);
		Assert.True((await users.AddToRoleAsync(user, ArchiveRoles.Member)).Succeeded);
	}

	private static async Task<HttpResponseMessage> PostCodeRequestAsync(HttpClient client, string email)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		using var tokenResponse = await client.SendAsync(tokenRequest);
		tokenResponse.EnsureSuccessStatusCode();
		var cookie = Assert.Single(tokenResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var body = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", body.GetProperty("token").GetString()!);
		request.Content = JsonContent.Create(new { email });
		return await client.SendAsync(request);
	}

	private sealed class StubHostEnvironment(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;
		public string ApplicationName { get; set; } = "Archive.Backend.Tests";
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}
}

internal sealed class FakeAzureTransport(
	Func<FakeAzureTransport.Call, CancellationToken, Task<string>>? behavior = null) : IAzureEmailTransport
{
	public sealed record Call(string From, string To, string Subject, string Body);

	private readonly List<Call> calls = [];
	private readonly List<string> operationIds = [];
	private readonly object gate = new();

	public IReadOnlyList<Call> Calls
	{
		get { lock (gate) return [.. calls]; }
	}

	public IReadOnlyList<string> OperationIds
	{
		get { lock (gate) return [.. operationIds]; }
	}

	public int CallCount
	{
		get { lock (gate) return calls.Count; }
	}

	public async Task<string> SendAsync(
		string from, string to, string subject, string plainTextBody, CancellationToken cancellationToken)
	{
		var call = new Call(from, to, subject, plainTextBody);
		lock (gate) calls.Add(call);
		var operationId = behavior is null
			? Guid.NewGuid().ToString("N")
			: await behavior(call, cancellationToken);
		lock (gate) operationIds.Add(operationId);
		return operationId;
	}
}

internal sealed class TestLogger<T> : ILogger<T>
{
	private readonly List<string> messages = [];

	public IReadOnlyList<string> Messages
	{
		get { lock (messages) return [.. messages]; }
	}

	public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel, EventId eventId, TState state, Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		lock (messages) messages.Add(formatter(state, exception));
	}

	private sealed class NullScope : IDisposable
	{
		public static readonly NullScope Instance = new();

		public void Dispose()
		{
		}
	}
}
