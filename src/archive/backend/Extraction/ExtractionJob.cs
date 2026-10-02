using Archive.Backend.Assets;

namespace Archive.Backend.Extraction;

/// <summary>
/// ARC-034 lifecycle of one revision's text extraction: <see cref="Queued"/>
/// rows wait for the queue handoff (finalize outbox or a later dispatch
/// sweep), <see cref="Running"/> marks an accepted worker run and
/// <see cref="Completed"/> (text stored), <see cref="NoText"/> (scanned PDF
/// without extractable text — a useful terminal result, not an error) and
/// <see cref="Failed"/> (with a German <see cref="ExtractionJob.FailureReason"/>)
/// are terminal. The editor's retry action resets a failed row to
/// <see cref="Queued"/> with fresh attempts.
/// </summary>
public enum ExtractionStatus
{
	Queued = 0,
	Running = 1,
	Completed = 2,
	NoText = 3,
	Failed = 4,
}

/// <summary>
/// One persisted extraction work item per <see cref="FileRevision"/>
/// (ARC-034). Rows are keyed by revision, which is the versioning and
/// idempotency anchor: late or duplicated queue messages can never overwrite
/// another revision's text because every result is written only into the row
/// of the revision it belongs to, and a completed row is never rewritten by
/// a second message. A finalize insert rides the revision's own SaveChanges
/// (outbox), so a committed upload never loses its extraction request even
/// when the web application stops before the queue send succeeds.
/// </summary>
public sealed class ExtractionJob
{
	/// <summary>Primary key; always the owning revision's id, never generated.</summary>
	public Guid RevisionId { get; set; }

	public FileRevision Revision { get; set; } = null!;

	/// <summary>Denormalized join key for consumers that group by asset.</summary>
	public Guid AssetId { get; set; }

	public ExtractionStatus Status { get; set; }

	/// <summary>
	/// Bounded extracted text; the application caps it
	/// (<see cref="ExtractionOptions.MaxTextCharacters"/>), the schema stays
	/// unbounded (<c>text</c> column).
	/// </summary>
	public string? Text { get; set; }

	/// <summary>German reason; null unless <see cref="Status"/> is <see cref="ExtractionStatus.Failed"/>.</summary>
	public string? FailureReason { get; set; }

	public int AttemptCount { get; set; }

	public DateTimeOffset? LastAttemptAt { get; set; }

	/// <summary>Set only on the terminal <see cref="ExtractionStatus.Completed"/> and <see cref="ExtractionStatus.NoText"/> states.</summary>
	public DateTimeOffset? CompletedAt { get; set; }

	/// <summary>
	/// Null until a queue send was accepted; the dispatch sweeper rediscovers
	/// such rows and hands them to the queue later.
	/// </summary>
	public DateTimeOffset? LastEnqueuedAt { get; set; }

	public Guid TriggeredByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	/// <summary>Application-bumped optimistic-concurrency token, like <see cref="ArchiveAsset.RowVersion"/>.</summary>
	public uint RowVersion { get; set; }
}
