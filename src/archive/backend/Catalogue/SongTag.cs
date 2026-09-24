namespace Archive.Backend.Catalogue;

/// <summary>
/// A free-form tag of a <see cref="Song"/> (ARC-023), a supplement to the
/// structured relations without a controlled vocabulary. Tags are stored in
/// entry order via an explicit <see cref="Position"/> (Guid v7 IDs share the
/// same millisecond within one save and cannot express the order).
/// </summary>
public sealed class SongTag
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Zero-based entry order within the song.</summary>
	public int Position { get; set; }

	public Guid SongId { get; set; }

	public Song Song { get; set; } = null!;

	public required string Value { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }
}
