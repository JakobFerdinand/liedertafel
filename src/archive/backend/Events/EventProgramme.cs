namespace Archive.Backend.Events;

/// <summary>
/// The Programm of one Auftritt (GLOSSARY): one ordered, editable list of
/// Lieder that is also the record of what was sung. Exactly one per event,
/// created by the first save. There are no revisions, drafts or confirmation
/// steps (ADR-0001): saving publishes. The event row and its own publish
/// stamps stay untouched by every programme mutation. Attribution and the
/// application-bumped optimistic concurrency token follow the catalogue
/// pattern: item edits bump the programme <see cref="RowVersion"/>, not the
/// event.
/// </summary>
public sealed class EventProgramme
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Owning event; unique one-to-one link.</summary>
	public Guid EventId { get; set; }

	public ChoirEvent Event { get; set; } = null!;

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token, mirroring
	/// <see cref="ChoirEvent.RowVersion"/>: every items mutation increments it.
	/// </summary>
	public uint RowVersion { get; set; }

	public List<ProgrammeItem> Items { get; set; } = [];
}
