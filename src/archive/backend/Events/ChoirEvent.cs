namespace Archive.Backend.Events;

/// <summary>
/// A historical choir appearance (ARC-024): concert, service, wedding,
/// funeral, festival or other appearance of the "Auftritte" section.
/// Published state is explicit: <see cref="PublishedAt"/> null means draft.
/// The date model keeps partial information honest (<see cref="DateYear"/>,
/// <see cref="DateMonth"/>, <see cref="DateDay"/> with
/// <see cref="DateApproximate"/>) instead of inventing a calendar date.
/// Attribution and the application-bumped optimistic concurrency token follow
/// the catalogue/auth entities (ARC-007 pattern).
/// </summary>
public sealed class ChoirEvent
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Closed appearance kind from <see cref="EventKinds.Known"/>.</summary>
	public required string Kind { get; set; }

	public required string Title { get; set; }

	public string? Venue { get; set; }

	/// <summary>Optional start time in the validated "HH:mm" form.</summary>
	public string? StartTime { get; set; }

	/// <summary>Optional member-visible practical notes.</summary>
	public string? Notes { get; set; }

	/// <summary>Optional source context for the entry.</summary>
	public string? SourceNote { get; set; }

	/// <summary>Historical year (1800–2100) or null for an unknown date.</summary>
	public int? DateYear { get; set; }

	/// <summary>Historical month (1–12); only set together with a year.</summary>
	public int? DateMonth { get; set; }

	/// <summary>Historical day; only set when the month is known and the day exists.</summary>
	public int? DateDay { get; set; }

	/// <summary>"um/ca." marker, independent of the date precision.</summary>
	public bool DateApproximate { get; set; }

	/// <summary>Publication stamp; null while the event is a draft.</summary>
	public DateTimeOffset? PublishedAt { get; set; }

	public Guid? PublishedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token, mirroring the catalogue
	/// entities: every mutation increments it alongside the change.
	/// </summary>
	public uint RowVersion { get; set; }
}
