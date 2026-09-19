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

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public List<MusicalVersion> MusicalVersions { get; set; } = [];
}
