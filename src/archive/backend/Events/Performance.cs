using Archive.Backend.Catalogue;

namespace Archive.Backend.Events;

/// <summary>
/// One recorded occurrence of a song at one historical event (ARC-028): a
/// confirmed performance or an unconfirmed programme mention (see
/// <see cref="PerformanceEvidenceStatus"/>) with stable occurrence identity.
/// The optional known chain is resolved at capture time and stored on both
/// columns; an unknown version ("Fassung unbekannt") stays null on both.
/// Attribution and the application-bumped optimistic concurrency token
/// follow the catalogue/auth entities (ARC-007 pattern); the per-event
/// idempotency key returns a lost retry its stored row instead of a second
/// occurrence. Evidence writes never touch the referenced event's stamps.
/// </summary>
public sealed class Performance
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Owning event; occurrences never move between events.</summary>
	public required Guid EventId { get; set; }

	public ChoirEvent Event { get; set; } = null!;

	/// <summary>Recorded song of the occurrence, kept stable via Restrict.</summary>
	public required Guid SongId { get; set; }

	/// <summary>Resolved arrangement of the known chain, set together with the version.</summary>
	public Guid? ArrangementId { get; set; }

	/// <summary>Resolved musical version of the known chain, null when unknown.</summary>
	public Guid? MusicalVersionId { get; set; }

	public MusicalVersion? MusicalVersion { get; set; }

	/// <summary>1-based captured order within the event, appended on create.</summary>
	public int Position { get; set; }

	/// <summary>Closed evidence value from <see cref="PerformanceEvidenceStatus.Known"/>.</summary>
	public required string EvidenceStatus { get; set; }

	/// <summary>Optional editor source note; required for unconfirmed mentions.</summary>
	public string? SourceNote { get; set; }

	/// <summary>Optional retry key: at most one occurrence per (event, key).</summary>
	public string? IdempotencyKey { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token, mirroring
	/// <see cref="ChoirEvent.RowVersion"/>: every mutation increments it
	/// alongside the change (ARC-007 pattern).
	/// </summary>
	public uint RowVersion { get; set; }
}
