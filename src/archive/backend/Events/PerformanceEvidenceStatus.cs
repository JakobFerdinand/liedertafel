namespace Archive.Backend.Events;

/// <summary>
/// Closed ARC-028 evidence set for recorded occurrences (mirrors
/// <see cref="EventKinds"/>): a song was certainly performed
/// (<see cref="Confirmed"/>) or a source merely mentions it in a programme
/// without confirming the actual singing (<see cref="Mention"/>). No other
/// values are accepted by the API or the model.
/// </summary>
public static class PerformanceEvidenceStatus
{
	public const string Confirmed = "confirmed";

	public const string Mention = "mention";

	public static readonly string[] Known = [Confirmed, Mention];
}
