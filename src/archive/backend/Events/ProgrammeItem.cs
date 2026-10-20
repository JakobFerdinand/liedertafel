using Archive.Backend.Lieder;

namespace Archive.Backend.Events;

/// <summary>
/// One ordered entry ("Programmpunkt") of an <see cref="EventProgramme"/>:
/// one <see cref="Lieder.Lied"/>. The same Lied may appear twice as two
/// distinct entries (distinct stable item IDs, which Zeitmarken hang on).
/// Positions are 1-based and contiguous within the programme.
/// </summary>
public sealed class ProgrammeItem
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid ProgrammeId { get; set; }

	public EventProgramme Programme { get; set; } = null!;

	/// <summary>1-based order within the programme.</summary>
	public int Position { get; set; }

	/// <summary>The Lied of the entry; kept stable via Restrict.</summary>
	public Guid LiedId { get; set; }

	public Lied? Lied { get; set; }

	/// <summary>Optional member-visible practical note, at most 500 characters.</summary>
	public string? Note { get; set; }
}
