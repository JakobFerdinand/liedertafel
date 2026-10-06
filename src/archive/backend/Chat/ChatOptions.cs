namespace Archive.Backend.Chat;

/// <summary>
/// Bounded archive chat configuration (ARC-022, decided in ARC-021). All
/// bounds are app-enforced. Money is not configured here: the hard monthly
/// cap, the output bound per model call and the model prices belong to all
/// AI features and live in <see cref="Ai.AiOptions"/> (ARC-022-3).
/// <see cref="Disabled"/> stays the maintainer's manual kill switch.
/// </summary>
public sealed class ChatOptions
{
	public const string SectionName = "Archive:Chat";

	/// <summary>Endpoint-level availability gate. Default false keeps the chat inert until configured.</summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// Maintainer's manual kill switch (ARC-021): set after a budget review or
	/// an incident. Independent of <see cref="Enabled"/> so a deployment can
	/// never accidentally re-enable the chat.
	/// </summary>
	public bool Disabled { get; set; }

	/// <summary>Maximum model calls per run, the first one included; bounds the tool loop.</summary>
	public int MaxToolCalls { get; set; } = 5;

	/// <summary>Per-iteration abort when no model token arrives within this window.</summary>
	public int NoTokenSeconds { get; set; } = 30;

	/// <summary>Overall run bound across all model calls and tool executions.</summary>
	public int OverallSeconds { get; set; } = 120;

	/// <summary>Maximum accepted question length in characters.</summary>
	public int MaxQuestionChars { get; set; } = 2000;

	/// <summary>Maximum persisted assistant answer length in characters.</summary>
	public int MaxAnswerChars { get; set; } = 8000;

	/// <summary>
	/// Model provider selection (ARC-021 seam): <c>AzureOpenAI</c> selects the
	/// real Azure OpenAI client, but only when both <see cref="Endpoint"/> and
	/// <see cref="DeploymentName"/> are set as well; null or any other value
	/// keeps the deterministic <see cref="ScriptedChatClient"/>.
	/// </summary>
	public string? Provider { get; set; }

	/// <summary>Azure OpenAI resource endpoint (https); used only with <c>Provider=AzureOpenAI</c>.</summary>
	public string? Endpoint { get; set; }

	/// <summary>
	/// Pinned deployment name under that endpoint; used only with
	/// <c>Provider=AzureOpenAI</c>. It is also the model key whose price must
	/// exist under <c>Archive:Ai:Models</c>, otherwise every call is refused.
	/// </summary>
	public string? DeploymentName { get; set; }

	/// <summary>Combined endpoint gate: available when explicitly enabled and not manually disabled.</summary>
	public bool IsAvailable => Enabled && !Disabled;
}
