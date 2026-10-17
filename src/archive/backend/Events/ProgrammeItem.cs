using Archive.Backend.Lieder;

namespace Archive.Backend.Events;

/// <summary>
/// One ordered entry ("Programmpunkt") of a <see cref="ProgrammeRevision"/>
/// (ARC-026): one <see cref="Lieder.Lied"/>. The same Lied may appear twice
/// as two distinct entries (distinct stable item IDs). Positions are 1-based
/// and contiguous within the revision.
/// </summary>
public sealed class ProgrammeItem
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid RevisionId { get; set; }

	public ProgrammeRevision Revision { get; set; } = null!;

	/// <summary>1-based order within the revision.</summary>
	public int Position { get; set; }

	/// <summary>The Lied of the entry; kept stable via Restrict.</summary>
	public Guid LiedId { get; set; }

	public Lied? Lied { get; set; }

	/// <summary>Optional member-visible practical note, at most 500 characters.</summary>
	public string? Note { get; set; }
}
