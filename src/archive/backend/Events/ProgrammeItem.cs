using Archive.Backend.Catalogue;
using Archive.Backend.Lieder;

namespace Archive.Backend.Events;

/// <summary>
/// One ordered entry ("Programmpunkt") of a <see cref="ProgrammeRevision"/>
/// (ARC-026): a song in a concrete arrangement and musical version. The same
/// song may appear twice as two distinct entries (distinct stable item IDs).
/// The musical version determines its arrangement parent; the song and
/// arrangement columns keep the resolved chain stable for historical
/// revisions. Positions are 1-based and contiguous within the revision.
/// </summary>
public sealed class ProgrammeItem
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid RevisionId { get; set; }

	public ProgrammeRevision Revision { get; set; } = null!;

	/// <summary>1-based order within the revision.</summary>
	public int Position { get; set; }

	/// <summary>
	/// The Lied of the entry (#170). Set for every entry written since the
	/// flat Lied model; the old Song/Arrangement/Version chain below is
	/// null then and only stays on historical rows until the old model is
	/// dropped.
	/// </summary>
	public Guid? LiedId { get; set; }

	public Lied? Lied { get; set; }

	/// <summary>Old model: song of the historical chain, null for Lied entries.</summary>
	public Guid? SongId { get; set; }

	/// <summary>Old model: arrangement of the historical chain, null for Lied entries.</summary>
	public Guid? ArrangementId { get; set; }

	/// <summary>Old model: musical version of the historical chain, null for Lied entries.</summary>
	public Guid? MusicalVersionId { get; set; }

	public MusicalVersion? MusicalVersion { get; set; }

	/// <summary>Optional member-visible practical note, at most 500 characters.</summary>
	public string? Note { get; set; }
}
