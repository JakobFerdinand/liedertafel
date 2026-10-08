using Archive.Backend.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Ai;

// The chat slice's configuration type shares its name with Microsoft.Extensions.AI.ChatOptions.
using ChatSettings = Archive.Backend.Chat.ChatOptions;

/// <summary>
/// The only way to a model provider (ARC-022-3). The raw provider clients
/// are registered under <see cref="ProviderKey"/> and are taken by nothing
/// but this class; what the container hands out as <see cref="IChatClient"/>
/// is <see cref="ChatClient"/>: OpenTelemetry (one span and the GenAI metrics
/// per model call) → <see cref="BudgetedChatClient"/> → provider. Calls must
/// carry an <see cref="AiOperation"/> in their options.
/// </summary>
public sealed class AiGateway
{
	/// <summary>Service key of the raw provider clients. Only the gateway resolves it.</summary>
	public const string ProviderKey = "archive.ai.provider";

	public AiGateway([FromKeyedServices(ProviderKey)] IChatClient provider, IServiceProvider services, IAiBudget budget,
		IOptionsMonitor<AiOptions> options, AiModels models)
	{
		Models = models;
		// No logger for the instrumentation: it would log provider exceptions
		// with their message. Failures are logged by the caller, type only.
		ChatClient = new ChatClientBuilder(new BudgetedChatClient(provider, budget, options, models.Chat))
			.UseOpenTelemetry(sourceName: Microsoft.Extensions.Hosting.Extensions.AiTelemetryName)
			.Build();
		// No embedding provider is registered yet. ARC-052 registers one under
		// ProviderKey and sets AiModels.Embedding; it is then budgeted here.
		if (services.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(ProviderKey) is { } embeddings)
		{
			var model = models.Embedding
				?? throw new InvalidOperationException("An embedding provider is registered but AiModels.Embedding is not set.");
			EmbeddingGenerator = new EmbeddingGeneratorBuilder<string, Embedding<float>>(
					new BudgetedEmbeddingGenerator(embeddings, budget, model))
				.UseOpenTelemetry(sourceName: Microsoft.Extensions.Hosting.Extensions.AiTelemetryName)
				.Build();
		}
	}

	/// <summary>The budgeted chat client of <see cref="AiModels.Chat"/>.</summary>
	public IChatClient ChatClient { get; }

	/// <summary>The budgeted embedding generator, or null while no provider is registered.</summary>
	public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; }

	public AiModels Models { get; }
}

public static class AiServiceCollectionExtensions
{
	/// <summary>
	/// Registers everything an archive host needs to call a model: provider
	/// selection, model keys, the usage ledger, the budget and the gateway.
	/// The API host and any job host that calls a model (later slices) make
	/// this one call; the host must also register the archive database
	/// context and a <see cref="TimeProvider"/>.
	/// </summary>
	public static IServiceCollection AddArchiveAi(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<AiOptions>().BindConfiguration(AiOptions.SectionName);
		// ARC-021 provider seam: when Archive:Chat selects Provider=AzureOpenAI
		// with Endpoint and DeploymentName, the real Azure OpenAI client is
		// used — keyless via the hosted container's user-assigned managed
		// identity through AZURE_CLIENT_ID (set by Bicep), or
		// AzureCliCredential with az login on a developer machine. Without
		// that configuration, Development and all tests keep the
		// deterministic ScriptedChatClient. The deployment name is the model
		// key that must have a price under Archive:Ai:Models.
		var chat = configuration.GetSection(ChatSettings.SectionName).Get<ChatSettings>() ?? new ChatSettings();
		if (AzureOpenAIChatClient.Create(chat) is { } azure)
		{
			services.AddKeyedSingleton<IChatClient>(AiGateway.ProviderKey, azure);
			services.AddSingleton(new AiModels { Chat = chat.DeploymentName!.Trim() });
		}
		else
		{
			services.AddKeyedSingleton<IChatClient, ScriptedChatClient>(AiGateway.ProviderKey);
			services.AddSingleton(new AiModels { Chat = ScriptedChatClient.ModelKey });
		}
		services.AddSingleton<IAiLedgerStore, PostgresAiLedgerStore>();
		services.AddSingleton<IAiBudget, AiBudgetLedger>();
		services.AddSingleton<AiGateway>();
		// Injecting IChatClient anywhere yields the budgeted pipeline.
		services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<AiGateway>().ChatClient);
		return services;
	}
}
