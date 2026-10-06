namespace Archive.Backend.Ai;

/// <summary>
/// One row of the AI usage ledger (ARC-022-3; generalised from the ARC-022
/// chat ledger). A row covers one operation of one feature on one model, for
/// example one chat run: every model call of that operation adds its tokens
/// and cost, so a run with several tool-loop iterations records the sum.
/// The month's spend is the sum of <see cref="CostMicroEur"/> and
/// <see cref="ReservedMicroEur"/> over all rows of that month. Content is
/// never recorded here.
/// </summary>
public sealed class AiUsageEntry
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Calendar month in "yyyy-MM" form, Europe/Vienna (see <see cref="AiMonth"/>).</summary>
	public required string YearMonth { get; set; }

	/// <summary>The AI feature that spent the money, for example <c>chat</c>.</summary>
	public required string Feature { get; set; }

	/// <summary>The model key the price was taken from.</summary>
	public required string Model { get; set; }

	/// <summary>Groups all model calls of one run or job step into this row.</summary>
	public Guid OperationId { get; set; }

	/// <summary>The member who triggered the operation; null for background jobs.</summary>
	public Guid? AccountId { get; set; }

	/// <summary>Number of model calls that were admitted for this operation.</summary>
	public int Calls { get; set; }

	public long InputTokens { get; set; }

	public long OutputTokens { get; set; }

	/// <summary>Settled cost in millionths of a EUR.</summary>
	public long CostMicroEur { get; set; }

	/// <summary>
	/// Worst-case cost of calls that were admitted but not settled yet. It
	/// counts against the cap. A process that dies mid-call leaves its
	/// reservation here, which errs on the side of spending less.
	/// </summary>
	public long ReservedMicroEur { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One row per budget month. It holds no amount: the ledger rows are the only
/// source of truth. Every ledger write of a month increments
/// <see cref="Version"/> as an optimistic concurrency token, so two requests
/// (also on two replicas) can never both pass the cap check on the same sum.
/// </summary>
public sealed class AiBudgetMonth
{
	public required string YearMonth { get; set; }

	public long Version { get; set; }
}

/// <summary>The budget month of an instant: the calendar month in Europe/Vienna.</summary>
public static class AiMonth
{
	public const string TimeZoneId = "Europe/Vienna";

	private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

	public static string Key(DateTimeOffset instant)
		=> TimeZoneInfo.ConvertTime(instant, Zone).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
