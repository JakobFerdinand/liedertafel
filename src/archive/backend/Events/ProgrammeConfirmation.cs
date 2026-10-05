namespace Archive.Backend.Events;

/// <summary>
/// The editor's statement of what was actually sung at the event (ARC-029):
/// one confirmation per programme, bound to the published revision it was
/// reviewed against. It is deliberately separate from the planned revision
/// rows (which stay frozen) and from the ARC-028 occurrences it owns: the
/// confirmation says "this event's actual programme has been reviewed", the
/// occurrences with this <see cref="Id"/> as <see cref="Performance.ConfirmationId"/>
/// are the sung songs, and a planned entry of <see cref="RevisionId"/>
/// without an occurrence was skipped. Attribution and the application-bumped
/// optimistic concurrency token follow the catalogue pattern (ARC-007).
/// </summary>
public sealed class ProgrammeConfirmation
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Owning programme; unique, so an event has one actual programme.</summary>
	public Guid ProgrammeId { get; set; }

	public EventProgramme Programme { get; set; } = null!;

	/// <summary>The published revision the planned entries were reviewed against.</summary>
	public Guid RevisionId { get; set; }

	public ProgrammeRevision Revision { get; set; } = null!;

	/// <summary>First confirmation; never rewritten.</summary>
	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token: every accepted change
	/// of the actual programme increments it. A retry that changes nothing
	/// does not.
	/// </summary>
	public uint RowVersion { get; set; }
}
