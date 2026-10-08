using Archive.Backend.Ai;
using Microsoft.Agents.AI;

namespace Archive.Backend.Chat;

using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;

/// <summary>
/// The member chat's named agent (ARC-022-3), built once per process on the
/// gateway's budgeted client and wrapped with the Agent Framework's
/// OpenTelemetry instrumentation (source and meter are created once, not
/// per request). Everything that belongs to one member's run — tools on the
/// request's database context, the thread's history provider, the tool
/// loop and its bounds — is supplied per run by <see cref="ArchiveChatService"/>.
/// </summary>
public sealed class ArchiveChatAgent : IDisposable
{
	public const string Name = "archive-chat";

	public ArchiveChatAgent(AiGateway gateway, ILoggerFactory loggers)
	{
		var agent = new ChatClientAgent(gateway.ChatClient, new ChatClientAgentOptions
		{
			Name = Name,
			Description = "Beantwortet Mitgliederfragen aus dem veröffentlichten Vereinsarchiv.",
			ChatOptions = new AiChatOptions { Instructions = ArchiveChatService.SystemPrompt },
			// The run supplies its own tool loop; no default decorators.
			UseProvidedChatClientAsIs = true,
		}, loggers);
		Agent = agent.AsBuilder()
			.UseOpenTelemetry(Microsoft.Extensions.Hosting.Extensions.AiTelemetryName)
			.Build();
	}

	public AIAgent Agent { get; }

	public void Dispose() => (Agent.GetService<OpenTelemetryAgent>() as IDisposable)?.Dispose();
}
