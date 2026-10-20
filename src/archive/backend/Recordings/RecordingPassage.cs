using Archive.Backend.Events;
using Archive.Backend.Lieder;

namespace Archive.Backend.Recordings;

/// <summary>
/// A Zeitmarke (GLOSSARY): where one entry of the Auftritt's Programm
/// (<see cref="ProgrammeItem"/>) starts and ends inside one whole recording of
/// that Auftritt. The mark is set by hand and links to the stable entry id and
/// never copies it, so a second recording can mark the same entry.
/// A timestamp is only meaningful for the file it was taken against:
/// <see cref="PlaybackDateiId"/> names that Aufnahme Datei (#171). When the
/// Datei members play is a different one (replaced or deleted file) the
/// Zeitmarke is "in need of review" — computed from this comparison on every
/// read, never stored, so restoring the earlier file from the trash makes
/// the marks trustworthy again without any bookkeeping.
/// The recording belongs to <see cref="EventId"/> by a composite key; the
/// endpoints check that the Programm entry belongs to the same event.
/// </summary>
public sealed class RecordingPassage
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid RecordingId { get; set; }

	public Recording Recording { get; set; } = null!;

	public Guid ProgrammeItemId { get; set; }

	public ProgrammeItem ProgrammeItem { get; set; } = null!;

	/// <summary>The event both the recording and the Programm entry belong to.</summary>
	public Guid EventId { get; set; }

	/// <summary>The playback Aufnahme Datei the two timestamps were taken against; null on legacy Zeitmarken.</summary>
	public Guid? PlaybackDateiId { get; set; }

	public Datei? PlaybackDatei { get; set; }

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
