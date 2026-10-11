using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

/// <summary>
/// The ARC-021 provider seam (implemented with ARC-022): when the chat
/// configuration selects <c>Provider=AzureOpenAI</c> together with an
/// endpoint and a pinned deployment name, the real Azure OpenAI client is
/// built behind the same <see cref="IChatClient"/> seam
/// (<c>AzureOpenAIClient</c> + Entra credential). Authentication is keyless
/// (the resource runs with disableLocalAuth): the hosted container resolves
/// its user-assigned managed identity via <c>AZURE_CLIENT_ID</c>; a developer
/// machine uses <c>az login</c> directly through AzureCliCredential. Avoiding
/// unrelated credential discovery keeps startup inside the no-token budget.
/// Client construction is
/// lazy — no Azure traffic happens here. Without the provider configuration
/// the deterministic <see cref="ScriptedChatClient"/> stays. Program.cs
/// registration and the ARC-021 live evaluation rerun share this one code
/// path.
/// </summary>
public static class AzureOpenAIChatClient
{
	/// <summary>The <see cref="ChatOptions.Provider"/> value selecting the real Azure OpenAI client.</summary>
	public const string ProviderName = "AzureOpenAI";

	/// <summary>
	/// True when the configuration selects the real provider: the Provider
	/// value equals <see cref="ProviderName"/> and both
	/// <see cref="ChatOptions.Endpoint"/> and
	/// <see cref="ChatOptions.DeploymentName"/> are set.
	/// </summary>
	public static bool IsConfigured(ChatOptions options)
		=> string.Equals(options.Provider?.Trim(), ProviderName, StringComparison.Ordinal)
			&& !string.IsNullOrWhiteSpace(options.Endpoint)
			&& !string.IsNullOrWhiteSpace(options.DeploymentName);

	/// <summary>
	/// Builds the real Azure OpenAI <see cref="IChatClient"/> for the pinned
	/// deployment, or null when the configuration does not select the
	/// provider (the scripted stand-in stays).
	/// </summary>
	public static IChatClient? Create(ChatOptions options) => Create(options, credential: null, clientOptions: null);

	/// <summary>
	/// Seam for tests: a fake credential and a recording transport let a test
	/// read the exact request the provider would receive without any Azure
	/// traffic. Production passes neither.
	/// </summary>
	internal static IChatClient? Create(ChatOptions options, TokenCredential? credential, AzureOpenAIClientOptions? clientOptions)
	{
		if (!IsConfigured(options))
			return null;
		if (credential is null)
		{
			var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
			credential = string.IsNullOrWhiteSpace(clientId)
				? new AzureCliCredential()
				: new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId.Trim()));
		}
		var provider = new AzureOpenAIClient(new Uri(options.Endpoint!.Trim()), credential, clientOptions)
			.GetChatClient(options.DeploymentName!.Trim())
			.AsIChatClient();
		// ARC-021-1: GPT-6 Luna rejects function tools on Chat Completions
		// while it reasons, so every provider request asks for no reasoning
		// unless the calling feature chose an effort itself. The options the
		// caller passed in are cloned by the middleware, never modified.
		return new ChatClientBuilder(provider)
			.ConfigureOptions(o => o.Reasoning = new ReasoningOptions
			{
				Effort = o.Reasoning?.Effort ?? ReasoningEffort.None,
				Output = o.Reasoning?.Output,
			})
			.Build();
	}
}
