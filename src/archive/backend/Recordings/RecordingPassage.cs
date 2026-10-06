using Archive.Backend.Assets;
using Archive.Backend.Events;

namespace Archive.Backend.Recordings;

/// <summary>
/// A marked passage (ARC-032): where one performance occurrence
/// (<see cref="Performance"/>, ARC-028/029) starts and ends inside one whole
/// recording (ARC-030). The passage links to the stable performance id and
/// never copies it, so a second recording can document the same occurrence
/// and recordings still add nothing to any history count.
/// A timestamp is only meaningful for the file it was taken against:
/// <see cref="PlaybackRevisionId"/> names that playback revision. When the
/// file members play is a different revision (replaced copy, restored
/// earlier file) the passage is "in need of review" — computed from this
/// comparison on every read, never stored, so restoring the earlier file
/// makes the marks trustworthy again without any bookkeeping.
/// Event ownership is a database fact: the composite keys
/// (<see cref="RecordingId"/>, <see cref="EventId"/>) and
/// (<see cref="PerformanceId"/>, <see cref="EventId"/>) only match when the
/// recording and the occurrence belong to the same event.
/// </summary>
public sealed class RecordingPassage
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid RecordingId { get; set; }

	public Recording Recording { get; set; } = null!;

	public Guid PerformanceId { get; set; }

	public Performance Performance { get; set; } = null!;

	/// <summary>The event both the recording and the performance belong to.</summary>
	public Guid EventId { get; set; }

	/// <summary>The playback file revision the two timestamps were taken against.</summary>
	public Guid PlaybackRevisionId { get; set; }

	public FileRevision PlaybackRevision { get; set; } = null!;

	/// <summary>Start in seconds from the beginning of the file, at least 0.</summary>
	public double StartSeconds { get; set; }

	/// <summary>End in seconds, after <see cref="StartSeconds"/>.</summary>
	public double EndSeconds { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>Application-bumped optimistic-concurrency token.</summary>
	public uint RowVersion { get; set; }
}
