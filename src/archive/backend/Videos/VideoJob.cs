using Archive.Backend.Lieder;

namespace Archive.Backend.Videos;

/// <summary>
/// #161 lifecycle of one video Datei's MP4 playback copy: <see cref="Queued"/>
/// rows wait for the queue handoff (finalize outbox or a later dispatch
/// sweep), <see cref="Running"/> marks an accepted worker run and
/// <see cref="Completed"/> (playback copy stored), <see cref="NotNeeded"/>
/// (the original already plays up to 1080p in browsers — a useful terminal
/// result, not an error) and <see cref="Failed"/> (with a German
/// <see cref="VideoJob.FailureReason"/>) are terminal. The editor's retry
/// action resets a failed row to <see cref="Queued"/> with fresh attempts.
/// </summary>
public enum VideoStatus
{
	Queued = 0,
	Running = 1,
	Completed = 2,
	NotNeeded = 3,
	Failed = 4,
}

/// <summary>
/// One persisted transcode work item per <see cref="Datei"/> (#161), the
/// same idempotency anchor as the extraction row: the Datei is immutable, so
/// its id is the key and a completed row is never rewritten by a duplicate
/// message. The result is NOT a second extraction text but a real playback
/// <see cref="Datei"/> of the same owner (<see cref="PlaybackDateiId"/>);
/// the worker attaches it to an Auftritt recording's playback slot, where
/// members play it, while the original stays downloadable. A finalize insert
/// rides the Datei's own SaveChanges (outbox), so a committed upload never
/// loses its transcode request.
/// </summary>
public sealed class VideoJob
{
	/// <summary>Primary key; always the transcribed original Datei's id, never generated.</summary>
	public Guid DateiId { get; set; }

	public Datei Datei { get; set; } = null!;

	public VideoStatus Status { get; set; }

	/// <summary>Set only on the terminal <see cref="VideoStatus.Completed"/> state.</summary>
	public Guid? PlaybackDateiId { get; set; }

	public Datei? PlaybackDatei { get; set; }

	/// <summary>German reason; null unless <see cref="Status"/> is <see cref="VideoStatus.Failed"/>.</summary>
	public string? FailureReason { get; set; }

	public int AttemptCount { get; set; }

	public DateTimeOffset? LastAttemptAt { get; set; }

	/// <summary>Set only on the terminal <see cref="VideoStatus.Completed"/> and <see cref="VideoStatus.NotNeeded"/> states.</summary>
	public DateTimeOffset? CompletedAt { get; set; }

	/// <summary>
	/// Null until a queue send was accepted; the dispatch sweeper rediscovers
	/// such rows and hands them to the queue later.
	/// </summary>
	public DateTimeOffset? LastEnqueuedAt { get; set; }

	public Guid TriggeredByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	/// <summary>Application-bumped optimistic-concurrency token.</summary>
	public uint RowVersion { get; set; }
}
