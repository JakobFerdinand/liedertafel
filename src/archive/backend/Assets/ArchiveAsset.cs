using Archive.Backend.Catalogue;

namespace Archive.Backend.Assets;

/// <summary>
/// A file asset attached to a musical version (ARC-015). Assets start empty
/// (<see cref="CurrentRevisionId"/> null) and gain immutable
/// <see cref="FileRevision"/> rows as uploads finalize; <see cref="AssetType"/>
/// is an open contract (ARC-016 audio/MIDI, ARC-023 event documents), as is
/// <see cref="VoiceLabel"/>.
/// </summary>
public sealed class ArchiveAsset
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid MusicalVersionId { get; set; }

	public MusicalVersion MusicalVersion { get; set; } = null!;

	/// <summary>Required asset discriminator, default "score"; open for later slices.</summary>
	public required string AssetType { get; set; }

	/// <summary>Optional per-voice label; ARC-016 fills it for voice-specific audio.</summary>
	public string? VoiceLabel { get; set; }

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
