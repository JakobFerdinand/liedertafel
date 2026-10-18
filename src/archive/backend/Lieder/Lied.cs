namespace Archive.Backend.Lieder;

/// <summary>
/// A Lied (GLOSSARY): one playable piece as the choir sings it, with its
/// Dateien. Flat by design (ADR-0001): no arrangements, no alternate titles,
/// no tags and no published state, so a saved Lied is visible to every member.
/// </summary>
public sealed class Lied
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public required string Title { get; set; }

	public string? Composer { get; set; }

	/// <summary>Bearbeiter.</summary>
	public string? Arranger { get; set; }

	public string? Lyricist { get; set; }

	/// <summary>Besetzung, for example "TTBB"; the Stimme choices derive from it.</summary>
	public string? VoiceConfiguration { get; set; }

	/// <summary>Tonart.</summary>
	public string? MusicalKey { get; set; }

	public string? Language { get; set; }

	/// <summary>Liedtext.</summary>
	public string? Lyrics { get; set; }

	/// <summary>Anlass: one free label.</summary>
	public string? Occasion { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>Application-bumped optimistic-concurrency token; every mutation increments it.</summary>
	public uint RowVersion { get; set; }

	public List<Datei> Dateien { get; set; } = [];
}
