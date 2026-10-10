namespace Archive.Backend.Catalogue;

/// <summary>
/// A musical rendition of an <see cref="Arrangement"/> (for example a specific
/// score or recording the archive keeps).
/// </summary>
public sealed class MusicalVersion
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid ArrangementId { get; set; }

	public Arrangement Arrangement { get; set; } = null!;

	public required string Label { get; set; }

	public string? Creator { get; set; }

	public string? MusicalKey { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	/// <summary>
	/// ARC-013-1: application-bumped optimistic-concurrency token of the
	/// musical version itself; version PATCHes carry it and a stale edit
	/// answers 409 without touching the entity.
	/// </summary>
	public uint RowVersion { get; set; }
}
