using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Ai;

/// <summary>
/// The only way to the model provider (ARC-022-3). Agents and jobs take their
/// <see cref="IChatClient"/> from here and never inject the raw provider
/// client: the pipeline is OpenTelemetry (one span and the GenAI metrics per
/// model call) → <see cref="BudgetedChatClient"/> → provider. Calls must
/// carry an <see cref="AiOperation"/> in their options.
/// </summary>
public sealed class AiGateway
{
	public AiGateway(IChatClient provider, IAiBudget budget, IOptionsMonitor<AiOptions> options, AiModels models,
		ILoggerFactory loggers)
	{
		Models = models;
		ChatClient = new ChatClientBuilder(new BudgetedChatClient(provider, budget, options, models.Chat))
			.UseOpenTelemetry(loggers, Microsoft.Extensions.Hosting.Extensions.AiTelemetryName)
			.Build();
	}

	public IChatClient ChatClient { get; }

	public AiModels Models { get; }
}

public static class AiServiceCollectionExtensions
{
	/// <summary>
	/// Registers the shared AI budget and the gateway. The host must also
	/// register the provider <see cref="IChatClient"/>, the archive database
	/// context and a <see cref="TimeProvider"/>. A job host that calls the
	/// model (later slices) calls this too.
	/// </summary>
	public static IServiceCollection AddArchiveAi(this IServiceCollection services, AiModels models)
	{
		services.AddOptions<AiOptions>().BindConfiguration(AiOptions.SectionName);
		services.AddSingleton(models);
		services.AddSingleton<IAiBudget, AiBudgetLedger>();
		services.AddSingleton<AiGateway>();
		return services;
	}
}
