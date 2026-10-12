using Archive.Backend.Assets;

namespace Archive.Backend.Reading;

/// <summary>
/// ARC-034-1 lifecycle of one revision's AI reading: <see cref="Queued"/>
/// rows wait for the queue handoff, <see cref="Running"/> marks an accepted
/// worker run, <see cref="WaitingForBudget"/> parks work the monthly cap
/// refused (its queue message is acknowledged; the dispatch sweep re-queues
/// the row when the budget admits a call again), <see cref="Completed"/>
/// and <see cref="Failed"/> are terminal until the editor asks again.
/// </summary>
public enum ReadingStatus
{
	Queued = 0,
	Running = 1,
	WaitingForBudget = 2,
	Completed = 3,
	Failed = 4,
}

/// <summary>
/// One persisted AI reading work item per <see cref="FileRevision"/> of a
/// score (ARC-034-1). Like <c>ExtractionJob</c> it is keyed by the immutable
/// revision, so a late or duplicated queue message can only ever land in the
/// row of the revision it names. The extraction worker inserts the row in the
/// same save that completes the extraction (outbox); the model-free
/// extraction job therefore hands every score over, whether it found text or
/// not. Results never leave the revision: the transcription goes into the
/// revision's extraction row, catalogue fields go through the shared write
/// path, and only while the revision is still the asset's current file.
/// </summary>
public sealed class ReadingJob
{
	/// <summary>Primary key; always the owning revision's id, never generated.</summary>
	public Guid RevisionId { get; set; }

	public FileRevision Revision { get; set; } = null!;

	public Guid AssetId { get; set; }

	public ReadingStatus Status { get; set; }

	/// <summary>Pages of the scan, known after the first run; null for a score that needed no transcription.</summary>
	public int? PagesTotal { get; set; }

	/// <summary>Pages already transcribed. Progress is saved after each request, so a resumed run never pays twice.</summary>
	public int PagesRead { get; set; }

	/// <summary>
	/// The highest page this request may read: one run reads at most the
	/// configured number of pages. The editor's "Rest lesen" raises it.
	/// </summary>
	public int PageLimit { get; set; }

	/// <summary>True once the catalogue-field step ran for the current text; a resumed run skips it.</summary>
	public bool FieldsDone { get; set; }

	/// <summary>Fields written with a badge (regex or AI) by the last field step.</summary>
	public int FieldsApplied { get; set; }

	/// <summary>Fields handed to the "Vorschläge" queue instead of being written.</summary>
	public int FieldsProposed { get; set; }

	/// <summary>German reason; null unless <see cref="Status"/> is <see cref="ReadingStatus.Failed"/>.</summary>
	public string? FailureReason { get; set; }

	public int AttemptCount { get; set; }

	public DateTimeOffset? LastAttemptAt { get; set; }

	public DateTimeOffset? CompletedAt { get; set; }

	/// <summary>Null until a queue send was accepted; the dispatch sweep rediscovers such rows.</summary>
	public DateTimeOffset? LastEnqueuedAt { get; set; }

	public Guid TriggeredByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	/// <summary>Application-bumped optimistic-concurrency token.</summary>
	public uint RowVersion { get; set; }

	/// <summary>"teilweise gelesen": the scan has more pages than were read so far.</summary>
	public bool IsPartial => PagesTotal is { } total && PagesRead < total;
}
