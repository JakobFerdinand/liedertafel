namespace Archive.Backend.Catalogue;

/// <summary>
/// A concrete arrangement of a <see cref="Song"/> (for example a four-part
/// setting by a named arranger).
/// </summary>
public sealed class Arrangement
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid SongId { get; set; }

	public Song Song { get; set; } = null!;

	public required string Label { get; set; }

	public string? Arranger { get; set; }

	public string? VoiceConfiguration { get; set; }

	/// <summary>Optional accompaniment/instrumentation (ARC-023); null = unknown.</summary>
	public string? Accompaniment { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	/// <summary>
	/// ARC-013-1: application-bumped optimistic-concurrency token of the
	/// arrangement itself; arrangement PATCHes carry it and a stale edit
	/// answers 409 without touching the entity.
	/// </summary>
	public uint RowVersion { get; set; }

	public List<MusicalVersion> MusicalVersions { get; set; } = [];
}
