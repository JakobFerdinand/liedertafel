namespace Archive.Backend.Events;

/// <summary>
/// The ordered programme of one historical event (ARC-026): the aggregate
/// root over its <see cref="ProgrammeRevision"/> rows. Exactly one programme
/// per event, created implicitly by the first draft save; the event row and
/// its own publish stamps stay untouched by every programme mutation (ARC-025
/// asset pattern). Attribution and the application-bumped optimistic
/// concurrency token follow the catalogue pattern: child edits bump the
/// programme <see cref="RowVersion"/>, not the event.
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
	/// <see cref="ChoirEvent.RowVersion"/>: every programme/revision/items
	/// mutation increments it alongside the change.
	/// </summary>
	public uint RowVersion { get; set; }

	public List<ProgrammeRevision> Revisions { get; set; } = [];
}
