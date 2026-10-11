using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Archive.Backend.Ai;
using Archive.Backend.Chat;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using ChatSettings = Archive.Backend.Chat.ChatOptions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-021-1: what the real Azure OpenAI client sends to the provider and
/// which deployment the shipped configuration prices. Nothing here talks to
/// Azure: a recording transport answers the request and a fake credential
/// stands in for az login / the managed identity.
/// </summary>
public sealed class AzureOpenAIChatClientTests
{
	private const string Luna = "gpt-6-luna";

	[Fact]
	public async Task StreamingRequestsAskForNoReasoningEffort()
	{
		// Luna rejects function tools on Chat Completions while it reasons
		// (architecture §14), so every provider request must carry "none".
		var transport = new RecordingTransport();
		var client = Client(transport);

		await foreach (var _ in client.GetStreamingResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], Options()))
		{
		}

		Assert.Equal("none", transport.Single().GetProperty("reasoning_effort").GetString());
	}

	[Fact]
	public async Task TheRequestUsesTheApiVersionTheSdkKnowsAndThatMayRejectReasoningEffort()
	{
		// Not a wish, a tripwire. Azure.AI.OpenAI 2.1.0 sends only
		// api-version=2024-10-21, and Microsoft's API changelog lists
		// reasoning_effort as added in 2024-12-01-preview, so a live provider
		// may answer 400 to the parameter this slice sends. Whether it does can
		// only be seen in a live call (ARC-021-1 ticket: run ChatEvaluationLive
		// against the existing deployment before releasing). Fallback: the
		// /openai/v1/ endpoint, which has no api-version. When this assertion
		// fails the SDK moved: re-read that note and update both.
		var transport = new RecordingTransport();
		var client = Client(transport);

		await client.GetResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], Options());

		Assert.Equal("api-version=2024-10-21", transport.RequestUri.Query.TrimStart('?'));
		Assert.Equal("/openai/deployments/gpt-6-luna/chat/completions", transport.RequestUri.AbsolutePath);
	}

	[Fact]
	public async Task NonStreamingRequestsAskForNoReasoningEffortToo()
	{
		var transport = new RecordingTransport();
		var client = Client(transport);

		await client.GetResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], Options());

		Assert.Equal("none", transport.Single().GetProperty("reasoning_effort").GetString());
	}

	[Fact]
	public async Task AFeatureThatAsksForReasoningKeepsItsOwnEffort()
	{
		// "none" is the default, not a lock: a later slice that does not use
		// tools on Chat Completions may ask for more.
		var transport = new RecordingTransport();
		var client = Client(transport);
		var options = Options();
		options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low };

		await client.GetResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], options);

		Assert.Equal("low", transport.Single().GetProperty("reasoning_effort").GetString());
		// The caller's options are not modified.
		Assert.Equal(ReasoningEffort.Low, options.Reasoning.Effort);
	}

	[Fact]
	public async Task TheCallersOptionsAreNeverModifiedWhenNoneIsApplied()
	{
		var transport = new RecordingTransport();
		var client = Client(transport);
		var options = Options();

		await client.GetResponseAsync([new AiChatMessage(ChatRole.User, "Frage")], options);

		Assert.Null(options.Reasoning);
	}

	[Fact]
	public void ShippedConfigurationPricesLunaAndNothingUnknown()
	{
		var prices = ShippedConfiguration.Prices();

		var luna = prices.FindPrice(Luna);
		Assert.NotNull(luna);
		// GPT-6 Luna Data Zone Standard, short context, Germany West Central,
		// Azure Retail Prices API with currencyCode EUR (6-luna ShortCo
		// Inp/Opt Std DZ), effective 2026-09-01.
		Assert.Equal(0.1056m, luna.InputPricePerMillionEur);
		Assert.Equal(0.528m, luna.OutputPricePerMillionEur);
		// Fail closed: a deployment the configuration does not know has no price.
		Assert.Null(prices.FindPrice("gpt-6-luna-unbekannt"));
		Assert.Null(prices.FindPrice("gpt-6-sol"));
	}

	private static IChatClient Client(RecordingTransport transport)
		=> AzureOpenAIChatClient.Create(
			new ChatSettings { Provider = AzureOpenAIChatClient.ProviderName, Endpoint = "https://example.openai.azure.com/", DeploymentName = Luna },
			new FakeCredential(),
			new AzureOpenAIClientOptions { Transport = transport.Transport, RetryPolicy = new ClientRetryPolicy(0) })
		?? throw new InvalidOperationException("The test configuration must select the provider.");

	private static AiChatOptions Options() => new() { MaxOutputTokens = 200 };

	private sealed class FakeCredential : TokenCredential
	{
		public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
			=> new("fake", DateTimeOffset.UtcNow.AddHours(1));

		public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
			=> new(GetToken(requestContext, cancellationToken));
	}

	private sealed class RecordingTransport
	{
		private readonly List<string> bodies = [];
		private readonly List<Uri> uris = [];

		public RecordingTransport() => Transport = new HttpClientPipelineTransport(new HttpClient(new Handler(bodies, uris)));

		public HttpClientPipelineTransport Transport { get; }

		/// <summary>The one request URI the provider received.</summary>
		public Uri RequestUri => Assert.Single(uris);

		/// <summary>The one request body the provider received, parsed.</summary>
		public System.Text.Json.JsonElement Single()
			=> System.Text.Json.JsonDocument.Parse(Assert.Single(bodies)).RootElement.Clone();

		private sealed class Handler(List<string> bodies, List<Uri> uris) : HttpMessageHandler
		{
			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
				bodies.Add(body);
				uris.Add(request.RequestUri!);
				var streaming = body.Contains("\"stream\":true", StringComparison.Ordinal);
				var content = streaming
					? "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Antwort\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
					: "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Antwort\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}";
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(content, Encoding.UTF8, streaming ? "text/event-stream" : "application/json"),
				};
			}
		}
	}
}

/// <summary>The configuration the backend ships with, read from the repository.</summary>
internal static class ShippedConfiguration
{
	public static AiOptions Prices()
	{
		var configuration = new ConfigurationBuilder()
			.AddJsonFile(Path.Combine(RepositoryRoot(), "src", "archive", "backend", "appsettings.json"), optional: false)
			.Build();
		return configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
	}

	public static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
	}
}
