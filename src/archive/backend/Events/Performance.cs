using Archive.Backend.Lieder;

namespace Archive.Backend.Events;

/// <summary>
/// One recorded occurrence of a Lied at one historical event (ARC-028): a
/// confirmed performance or an unconfirmed programme mention (see
/// <see cref="PerformanceEvidenceStatus"/>) with stable occurrence identity.
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

	/// <summary>The Lied of the occurrence, kept stable via Restrict.</summary>
	public Guid LiedId { get; set; }

	public Lied? Lied { get; set; }

	/// <summary>1-based captured order within the event, appended on create.</summary>
	public int Position { get; set; }

	/// <summary>Closed evidence value from <see cref="PerformanceEvidenceStatus.Known"/>.</summary>
	public required string EvidenceStatus { get; set; }

	/// <summary>Optional editor source note; required for unconfirmed mentions.</summary>
	public string? SourceNote { get; set; }

	/// <summary>Optional retry key: at most one occurrence per (event, key).</summary>
	public string? IdempotencyKey { get; set; }

	/// <summary>
	/// ARC-029: the planned programme entry (frozen published item) this
	/// occurrence confirms; null for historical evidence and encore additions.
	/// At most one occurrence per planned entry.
	/// </summary>
	public Guid? ProgrammeItemId { get; set; }

	/// <summary>
	/// ARC-029: the programme confirmation owning this occurrence (planned
	/// entries sung and added encore Lieder); null for ARC-028 historical
	/// evidence, which confirmations never touch.
	/// </summary>
	public Guid? ConfirmationId { get; set; }

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
