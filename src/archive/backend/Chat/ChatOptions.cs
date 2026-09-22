namespace Archive.Backend.Chat;

/// <summary>
/// Bounded archive chat configuration (ARC-022, decided in ARC-021).
/// All bounds are app-enforced; the monthly budget is a reviewed amount, not
/// an automatic cut-off: exceeding it raises a warning for the maintainer and
/// requires the manual <see cref="Disabled"/> kill switch (the chat is never
/// auto-disabled). <see cref="InputPricePerMillionEur"/> and
/// <see cref="OutputPricePerMillionEur"/> are reference retail prices for the
/// planned model (ARC-021 decision) and only drive the cost estimate.
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

	/// <summary>Monthly reference budget in EUR; a warning is logged when the estimated month exceeds it.</summary>
	public decimal MonthlyBudgetEur { get; set; } = 5;

	/// <summary>Maximum authorized tool calls per run.</summary>
	public int MaxToolCalls { get; set; } = 5;

	/// <summary>Per-iteration abort when no model token arrives within this window.</summary>
	public int NoTokenSeconds { get; set; } = 30;

	/// <summary>Overall run bound across all model calls and tool executions.</summary>
	public int OverallSeconds { get; set; } = 120;

	/// <summary>Maximum accepted question length in characters.</summary>
	public int MaxQuestionChars { get; set; } = 2000;

	/// <summary>Maximum persisted assistant answer length in characters.</summary>
	public int MaxAnswerChars { get; set; } = 8000;

	/// <summary>Reference input price per million tokens used for the cost estimate.</summary>
	public decimal InputPricePerMillionEur { get; set; } = 0.83m;

	/// <summary>Reference output price per million tokens used for the cost estimate.</summary>
	public decimal OutputPricePerMillionEur { get; set; } = 4.95m;

	/// <summary>Combined endpoint gate: available when explicitly enabled and not manually disabled.</summary>
	public bool IsAvailable => Enabled && !Disabled;
}
