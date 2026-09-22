namespace Archive.Backend.Catalogue;

/// <summary>
/// An alternative title of a <see cref="Song"/> (ARC-020), for example a
/// known first line or a former title. Alternate titles are stored in entry
/// order: Guid v7 IDs keep the insertion order stable.
/// </summary>
public sealed class SongTitle
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid SongId { get; set; }

	public Song Song { get; set; } = null!;

	public required string Value { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }
}
