namespace Archive.Backend.Ai;

/// <summary>
/// The one budget every AI call of the archive is charged to (ARC-022-3,
/// architecture §14). A caller reserves the worst-case cost of a call before
/// it reaches the provider and settles the real usage afterwards. Chat-client
/// calls do this through <see cref="BudgetedChatClient"/>; a later slice that
/// calls something else (for example an embedding generator, ARC-052) uses
/// the same two methods around its own provider call.
/// </summary>
public interface IAiBudget
{
	/// <summary>
	/// Admits one call or refuses it. The check and the reservation are one
	/// atomic step in the database, so concurrent callers cannot both pass on
	/// the same remaining amount.
	/// </summary>
	/// <exception cref="AiBudgetExceededException">The month's cap would be exceeded. No provider call may happen.</exception>
	/// <exception cref="AiBudgetUnavailableException">The model has no price or the ledger cannot be reached. No provider call may happen.</exception>
	Task<AiReservation> ReserveAsync(AiCall call, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces a reservation by the real usage, or by the full reservation
	/// when the provider reported none (<see cref="AiUsage.Unreported"/>).
	/// Call it exactly once per reservation, also when the call failed or was
	/// cancelled; it is not cancellable and never throws. A settlement that
	/// cannot be written after retries is logged at error and counted.
	/// </summary>
	Task SettleAsync(AiReservation reservation, AiUsage usage);

	/// <summary>
	/// Whether a call of this worst case would be admitted right now. Queued
	/// work that waits for budget asks this before it is re-enqueued, so a
	/// nearly full month does not wake work that is refused again. False also
	/// when the model has no price or the ledger cannot be read.
	/// </summary>
	Task<bool> WouldAdmitAsync(AiCall call, CancellationToken cancellationToken);

	/// <summary>The current month's spend (settled plus reserved) against the cap, for operators.</summary>
	Task<AiBudgetStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>One provider call about to be made.</summary>
/// <param name="Feature">Stable feature name for the ledger, for example <c>chat</c>.</param>
/// <param name="OperationId">Groups the calls of one run or job step into one ledger row.</param>
/// <param name="AccountId">The triggering member, or null for background work.</param>
/// <param name="Model">Model key with a price entry in <see cref="AiOptions.Models"/>.</param>
/// <param name="EstimatedInputTokens">Conservative estimate of the input size.</param>
/// <param name="MaxOutputTokens">The output bound sent to the provider; zero for calls without output tokens.</param>
public sealed record AiCall(
	string Feature, Guid OperationId, Guid? AccountId, string Model, long EstimatedInputTokens, int MaxOutputTokens);

/// <summary>An admitted call; hand it back to <see cref="IAiBudget.SettleAsync"/>.</summary>
public sealed record AiReservation(
	Guid EntryId, string YearMonth, string Feature, string Model, long ReservedMicroEur,
	long EstimatedInputTokens, int MaxOutputTokens);

/// <summary>Tokens a call really used (or the best estimate when the provider reported none).</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens)
{
	/// <summary>
	/// The call ended without a usage report from the provider (failure,
	/// cancellation, abandoned stream). It is settled at its full reservation.
	/// </summary>
	public static AiUsage Unreported { get; } = new(-1, -1);

	public bool IsReported => !ReferenceEquals(this, Unreported);
}

public sealed record AiBudgetStatus(string YearMonth, long SpentMicroEur, long CapMicroEur)
{
	public long RemainingMicroEur => Math.Max(0, CapMicroEur - SpentMicroEur);
}

/// <summary>The monthly cap is reached; the call was not made.</summary>
public sealed class AiBudgetExceededException(string yearMonth)
	: Exception($"AI budget for {yearMonth} is exhausted.")
{
	public string YearMonth { get; } = yearMonth;
}

/// <summary>The budget could not be checked (unpriced model or ledger failure); the call was not made.</summary>
public sealed class AiBudgetUnavailableException(string reason, Exception? inner = null)
	: Exception(reason, inner);
