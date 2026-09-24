using Archive.Backend.Catalogue;
using Archive.Backend.Events;

namespace Archive.Backend.Assets;

/// <summary>
/// A file asset with exactly one owner (ARC-015): either a musical version
/// (scores, audio, MIDI) or, since ARC-025, a historical event (documents,
/// photographs). Assets start empty (<see cref="CurrentRevisionId"/> null) and
/// gain immutable <see cref="FileRevision"/> rows as uploads finalize;
/// <see cref="AssetType"/> is an open contract, as is
/// <see cref="VoiceLabel"/>. The database check constraint enforces that
/// exactly one owner is set; application code keeps the same invariant.
/// </summary>
public sealed class ArchiveAsset
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Owning musical version; null for event-owned assets (ARC-025).</summary>
	public Guid? MusicalVersionId { get; set; }

	public MusicalVersion? MusicalVersion { get; set; }

	/// <summary>Owning historical event (ARC-025); null for version-owned assets.</summary>
	public Guid? EventId { get; set; }

	public ChoirEvent? Event { get; set; }

	/// <summary>Required asset discriminator, default "score"; open for later slices.</summary>
	public required string AssetType { get; set; }

	/// <summary>Optional per-voice label; ARC-016 fills it for voice-specific audio.</summary>
	public string? VoiceLabel { get; set; }

	/// <summary>Optional free-text description (ARC-016), at most 500 characters.</summary>
	public string? Description { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token, mirroring
	/// <see cref="Catalogue.Song.RowVersion"/>.
	/// </summary>
	public uint RowVersion { get; set; }

	public List<FileRevision> Revisions { get; set; } = [];

	/// <summary>Pointer to the current revision; null until the first finalized upload.</summary>
	public Guid? CurrentRevisionId { get; set; }

	public FileRevision? CurrentRevision { get; set; }
}
