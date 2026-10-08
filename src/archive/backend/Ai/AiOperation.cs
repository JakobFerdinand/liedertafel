using Microsoft.Extensions.AI;

namespace Archive.Backend.Ai;

/// <summary>
/// Who is calling the model and on whose account (ARC-022-3). A feature
/// attaches one instance to the options of its run; the instance travels with
/// the options through the agent and the tool loop to the budget middleware,
/// which refuses calls that carry none. It never reaches the provider.
/// </summary>
/// <param name="Feature">Stable feature name for the ledger, for example <c>chat</c>.</param>
/// <param name="OperationId">One id per run or job step; its calls share one ledger row.</param>
/// <param name="AccountId">The triggering member, or null for background work.</param>
public sealed record AiOperation(string Feature, Guid OperationId, Guid? AccountId)
{
	private const string Key = "archive.ai.operation";

	/// <summary>
	/// Output bound per model call for this feature. It can only lower the
	/// configured ceiling (<see cref="AiOptions.MaxOutputTokensPerCall"/>);
	/// null uses the ceiling.
	/// </summary>
	public int? MaxOutputTokens { get; init; }

	/// <summary>
	/// The feature's own token estimate for all non-text input of one call
	/// (page images, files). Text is estimated by the middleware; a call with
	/// binary parts and no estimate here is refused, because nothing else
	/// could bound its cost.
	/// </summary>
	public long? NonTextInputTokens { get; init; }

	public static AiOperation Start(string feature, Guid? accountId) => new(feature, Guid.CreateVersion7(), accountId);

	public void AttachTo(ChatOptions options) => (options.AdditionalProperties ??= [])[Key] = this;

	public void AttachTo(EmbeddingGenerationOptions options) => (options.AdditionalProperties ??= [])[Key] = this;

	public static AiOperation? From(AdditionalPropertiesDictionary? properties)
		=> properties?.TryGetValue(Key, out var value) is true ? value as AiOperation : null;
}
