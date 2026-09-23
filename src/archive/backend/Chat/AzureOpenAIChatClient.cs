using Azure.AI.OpenAI;
using Archive.Backend.Auth;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

/// <summary>
/// The ARC-021 provider seam (implemented with ARC-022): when the chat
/// configuration selects <c>Provider=AzureOpenAI</c> together with an
/// endpoint and a pinned deployment name, the real Azure OpenAI client is
/// built behind the same <see cref="IChatClient"/> seam
/// (<c>AzureOpenAIClient</c> + Entra credential). Authentication is keyless
/// (the resource runs with disableLocalAuth): the credential comes from
/// <see cref="DataProtectionConfiguration.CreateCredential"/>, so the hosted
/// container resolves its user-assigned managed identity via
/// <c>AZURE_CLIENT_ID</c> and a developer machine falls back to
/// <c>az login</c> through DefaultAzureCredential. Client construction is
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
	public static IChatClient? Create(ChatOptions options)
	{
		if (!IsConfigured(options))
			return null;
		return new AzureOpenAIClient(new Uri(options.Endpoint!.Trim()), DataProtectionConfiguration.CreateCredential())
			.GetChatClient(options.DeploymentName!.Trim())
			.AsIChatClient();
	}
}
