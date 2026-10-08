namespace Archive.Backend.Ai;

/// <summary>
/// Shared AI budget configuration (ARC-022-3, architecture §14), bound to
/// <c>Archive:Ai</c>. The cap is a hard monthly limit across every AI
/// feature: chat today, embeddings, scan reading and jobs later. Prices are
/// configuration so a model switch (ARC-021-1) never touches code; a model
/// without a price entry is refused before any provider call.
/// </summary>
public sealed class AiOptions
{
	public const string SectionName = "Archive:Ai";

	/// <summary>Hard monthly cap in EUR for all AI calls of the archive.</summary>
	public decimal MonthlyCapEur { get; set; } = 15m;

	/// <summary>
	/// Hard ceiling for the output tokens of one model call. A feature may
	/// ask for less through <see cref="AiOperation.MaxOutputTokens"/>, never
	/// for more. The bound is sent to the provider and is the output part of
	/// every reservation, so the cost of a call in flight is bounded before
	/// it starts.
	/// </summary>
	public int MaxOutputTokensPerCall { get; set; } = 2000;

	/// <summary>
	/// Prices per model key (the deployment name for Azure OpenAI), in EUR per
	/// one million tokens. Example: <c>Archive:Ai:Models:gpt-5-4-mini:InputPricePerMillionEur</c>.
	/// </summary>
	public Dictionary<string, AiModelPrice> Models { get; set; } = [];

	/// <summary>Looks a model up without regard to case; null when the model has no usable price.</summary>
	public AiModelPrice? FindPrice(string model)
	{
		foreach (var (key, price) in Models)
		{
			if (string.Equals(key, model, StringComparison.OrdinalIgnoreCase))
				return price is { InputPricePerMillionEur: > 0, OutputPricePerMillionEur: > 0 } ? price : null;
		}
		return null;
	}
}

/// <summary>Price of one model in EUR per one million tokens. Both values are required and must be above zero.</summary>
public sealed class AiModelPrice
{
	public decimal InputPricePerMillionEur { get; set; }

	public decimal OutputPricePerMillionEur { get; set; }
}

/// <summary>
/// The model keys the archive's AI features use; each needs a price entry.
/// ARC-021-1 switches the chat model by configuration
/// (<c>Archive:Chat:DeploymentName</c>). A later slice adds its own property
/// here (ARC-052 sets <see cref="Embedding"/>) without touching other code.
/// </summary>
public sealed class AiModels
{
	/// <summary>Model key of the member chat and, until decided otherwise, of every chat-client agent.</summary>
	public required string Chat { get; init; }

	/// <summary>Model key of the embedding generator; null until ARC-052 registers one.</summary>
	public string? Embedding { get; init; }
}
