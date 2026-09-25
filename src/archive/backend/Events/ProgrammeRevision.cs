namespace Archive.Backend.Events;

/// <summary>
/// One version of an <see cref="EventProgramme"/> (ARC-026). From the first
/// publication the programme carries separate working/published revision
/// identities: at most one revision has <see cref="PublishedAt"/> null (the
/// working draft), members see only the newest published revision (highest
/// <see cref="Number"/> among the published ones), and older published
/// revisions remain frozen history for ARC-027 — superseding means
/// publishing a newer revision, there is no unpublish.
/// </summary>
public sealed class ProgrammeRevision
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid ProgrammeId { get; set; }

	public EventProgramme Programme { get; set; } = null!;

	/// <summary>1-based revision number, unique per programme.</summary>
	public int Number { get; set; }

	/// <summary>Publication stamp; null while this revision is the working draft.</summary>
	public DateTimeOffset? PublishedAt { get; set; }

	public Guid? PublishedByAccountId { get; set; }

	/// <summary>Set once at creation; revisions are never re-attributed.</summary>
	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public List<ProgrammeItem> Items { get; set; } = [];
}
