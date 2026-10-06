using Microsoft.Extensions.AI;

namespace Archive.Backend.Ai;

/// <summary>
/// Who is calling the model and on whose account (ARC-022-3). A feature
/// attaches one instance to the <see cref="ChatOptions"/> of its run; the
/// instance travels with the options through the agent and the tool loop to
/// <see cref="BudgetedChatClient"/>, which refuses calls that carry none.
/// It never reaches the provider.
/// </summary>
/// <param name="Feature">Stable feature name for the ledger, for example <c>chat</c>.</param>
/// <param name="OperationId">One id per run or job step; its calls share one ledger row.</param>
/// <param name="AccountId">The triggering member, or null for background work.</param>
public sealed record AiOperation(string Feature, Guid OperationId, Guid? AccountId)
{
	private const string Key = "archive.ai.operation";

	public static AiOperation Start(string feature, Guid? accountId) => new(feature, Guid.CreateVersion7(), accountId);

	public void AttachTo(ChatOptions options)
		=> (options.AdditionalProperties ??= [])[Key] = this;

	public static AiOperation? From(ChatOptions? options)
		=> options?.AdditionalProperties?.TryGetValue(Key, out var value) is true ? value as AiOperation : null;

	/// <summary>Removes the marker from options that are about to be sent to the provider.</summary>
	internal static void RemoveFrom(ChatOptions options)
	{
		if (options.AdditionalProperties is not { } properties)
			return;
		properties.Remove(Key);
		if (properties.Count == 0)
			options.AdditionalProperties = null;
	}
}
