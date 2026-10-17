using Archive.Backend.Assets;
using Archive.Backend.Events;
using Archive.Backend.Lieder;

namespace Archive.Backend.Recordings;

/// <summary>
/// A whole recording of one historical event (ARC-030) with a stable identity
/// that later slices key on (ARC-032 passages). An event may carry several
/// independently labelled recordings; a recording is material about the
/// event and never a performance occurrence.
/// Files are event-owned Aufnahme <see cref="Datei"/>en (#171) in two slots:
/// - <see cref="OriginalDateiId"/> is the preserved original, whatever its
///   format;
/// - <see cref="PlaybackDateiId"/> is an optional, externally converted
///   playback copy. While it has no file, a playable original serves both
///   roles as one object (no duplicate is created).
/// Replacing a slot sends the earlier Datei to the trash. The old asset
/// columns are legacy until #172 drops them and are neither read nor written.
/// Publication is explicit and separate from the event's: members see a
/// recording only when both are published.
/// </summary>
public sealed class Recording
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Owning event; recordings never move between events.</summary>
	public Guid EventId { get; set; }

	public ChoirEvent Event { get; set; } = null!;

	/// <summary>Editor-given label that tells recordings of one event apart.</summary>
	public required string Label { get; set; }

	/// <summary>Closed media kind from <see cref="RecordingKinds.Known"/>.</summary>
	public required string Kind { get; set; }

	/// <summary>Legacy (old asset model); dropped with it in #172.</summary>
	public Guid? OriginalAssetId { get; set; }

	public ArchiveAsset? OriginalAsset { get; set; }

	/// <summary>Legacy (old asset model); dropped with it in #172.</summary>
	public Guid? PlaybackAssetId { get; set; }

	public ArchiveAsset? PlaybackAsset { get; set; }

	/// <summary>The preserved original; null until its file is attached.</summary>
	public Guid? OriginalDateiId { get; set; }

	public Datei? OriginalDatei { get; set; }

	/// <summary>The converted playback copy; null while there is none.</summary>
	public Guid? PlaybackDateiId { get; set; }

	public Datei? PlaybackDatei { get; set; }

	/// <summary>
	/// Length of the file members play, in seconds, as measured by the
	/// uploading editor's browser; null while unknown. Cleared whenever that
	/// file changes, so it never describes a different file.
	/// </summary>
	public double? DurationSeconds { get; set; }

	/// <summary>Members may download only when an editor switched this on.</summary>
	public bool DownloadEnabled { get; set; }

	/// <summary>Publication stamp; null while the recording is editor-only.</summary>
	public DateTimeOffset? PublishedAt { get; set; }

	public Guid? PublishedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>Application-bumped optimistic-concurrency token.</summary>
	public uint RowVersion { get; set; }
}

public static class RecordingKinds
{
	public const string Audio = "audio";

	public const string Video = "video";

	public static readonly string[] Known = [Audio, Video];
}

/// <summary>
/// Shared visibility decision for recordings: the event's own decision plus
/// the recording's publication. Later deletion/trash states (ARC-040) extend
/// <see cref="EventVisibility"/> and are picked up here.
/// </summary>
public static class RecordingVisibility
{
	public static bool IsMemberVisible(Recording recording, ChoirEvent choirEvent) =>
		EventVisibility.IsMemberVisible(choirEvent) && recording.PublishedAt is not null;

	/// <summary>
	/// The same decision as a query filter for passages (ARC-032): a passage
	/// is member-visible with its recording. ARC-040 trash conditions extend
	/// this and <see cref="EventVisibility"/>; the song's own publication is
	/// applied where the song is read.
	/// </summary>
	public static IQueryable<RecordingPassage> OfMemberVisible(IQueryable<RecordingPassage> passages) =>
		passages.Where(p => p.Recording.PublishedAt != null && p.Recording.Event.PublishedAt != null);
}
