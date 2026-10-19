using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Lieder;

/// <summary>
/// An unfinished upload (#178, the only unfinished state of ADR-0001): the
/// form an editor was filling, shown in "Angefangen" on the Verwaltung page.
/// The browser chooses <see cref="Id"/> (its <c>entwurfId</c>). The draft holds
/// field values, which fields were edited by hand (draft editing state only:
/// it never reaches a <see cref="Lied"/> or <see cref="Datei"/>) and its Dateien
/// without their bytes, which are uploaded when the Lied is saved. Nothing
/// records where a value came from.
/// </summary>
public sealed class LiedEntwurf
{
	public Guid Id { get; set; }

	public Guid OwnerAccountId { get; set; }

	public string Title { get; set; } = string.Empty;

	public string Composer { get; set; } = string.Empty;

	public string Arranger { get; set; } = string.Empty;

	public string Lyricist { get; set; } = string.Empty;

	public string VoiceConfiguration { get; set; } = string.Empty;

	public string MusicalKey { get; set; } = string.Empty;

	public string Language { get; set; } = string.Empty;

	public string Lyrics { get; set; } = string.Empty;

	/// <summary>Form field names the editor touched, comma separated.</summary>
	public string EditedFields { get; set; } = string.Empty;

	/// <summary>Fields currently marked as a suggestion, <c>field:sicher</c> or <c>field:unsicher</c>, comma separated.</summary>
	public string Suggestions { get; set; } = string.Empty;

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public List<LiedEntwurfDatei> Dateien { get; set; } = [];
}

/// <summary>A Datei of a draft: its name and the editor's choices, not its bytes. <see cref="Id"/> is the browser's.</summary>
public sealed class LiedEntwurfDatei
{
	public Guid EntwurfId { get; set; }

	public Guid Id { get; set; }

	public int Position { get; set; }

	public string Name { get; set; } = string.Empty;

	public long SizeBytes { get; set; }

	/// <summary>One of <see cref="LiedEntwurfRules.Arten"/>.</summary>
	public string Art { get; set; } = "sonstiges";

	public string Stimme { get; set; } = string.Empty;

	public bool ArtEdited { get; set; }

	public bool StimmeEdited { get; set; }

	/// <summary>The reading state the browser last saw (one of <see cref="LiedEntwurfRules.LesenStatus"/>).</summary>
	public string LesenStatus { get; set; } = "keine";

	public int LesenAnfrageId { get; set; }

	public string LesenMeldung { get; set; } = string.Empty;

	public LiedEntwurf? Entwurf { get; set; }

	/// <summary>What the server's reading of this Datei produced; written by <c>POST /api/lied-lesungen</c>, never by the draft save.</summary>
	public LiedEntwurfLesung? Lesung { get; set; }
}

/// <summary>
/// The server-held outcome of one reading request for a draft Datei (#177
/// answer shape, stored). It lives as long as the Datei in the draft; removing
/// the Datei or discarding the draft drops it, so a late reading stores nothing.
/// </summary>
public sealed class LiedEntwurfLesung
{
	public Guid EntwurfId { get; set; }

	public Guid DateiId { get; set; }

	public int AnfrageId { get; set; }

	/// <summary><c>liest</c>, <c>gelesen</c>, <c>fehlgeschlagen</c> or <c>nicht-verfuegbar</c>.</summary>
	public string Status { get; set; } = "liest";

	public string Meldung { get; set; } = string.Empty;

	/// <summary>JSON list of <c>{ feld, wert, sicher }</c>.</summary>
	public string Felder { get; set; } = "[]";

	public DateTimeOffset UpdatedAt { get; set; }

	public LiedEntwurfDatei? Datei { get; set; }
}

public static class LiedEntwurfRules
{
	public static readonly IReadOnlyList<string> Felder =
		["title", "composer", "arranger", "lyricist", "voiceConfiguration", "musicalKey", "language", "lyrics"];

	/// <summary>The Art choices of the drop form: Partitur is Noten with the Partitur flag.</summary>
	public static readonly IReadOnlyList<string> Arten = ["partitur", "noten", "uebe", "aufnahme", "sonstiges"];

	public static readonly IReadOnlyList<string> LesenStatus =
		["keine", "liest", "gelesen", "fehlgeschlagen", "nicht-verfuegbar"];

	public const int MaxDateien = 50;

	public const int MaxEntwuerfeProKonto = 25;
}

public sealed class LiedEntwurfModelConfiguration :
	IEntityTypeConfiguration<LiedEntwurf>, IEntityTypeConfiguration<LiedEntwurfDatei>, IEntityTypeConfiguration<LiedEntwurfLesung>
{
	public void Configure(EntityTypeBuilder<LiedEntwurf> builder)
	{
		builder.ToTable("lied_entwuerfe");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Id).ValueGeneratedNever();
		builder.Property(x => x.Title).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.Composer).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.Arranger).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.Lyricist).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.VoiceConfiguration).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.MusicalKey).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.Language).HasMaxLength(LiedEndpoints.FieldMaxLength).IsRequired();
		builder.Property(x => x.Lyrics).HasMaxLength(LiedEndpoints.LyricsMaxLength).IsRequired();
		builder.Property(x => x.EditedFields).HasMaxLength(200).IsRequired();
		builder.Property(x => x.Suggestions).HasMaxLength(300).IsRequired();
		builder.HasIndex(x => new { x.OwnerAccountId, x.UpdatedAt });
	}

	public void Configure(EntityTypeBuilder<LiedEntwurfDatei> builder)
	{
		builder.ToTable("lied_entwurf_dateien");
		builder.HasKey(x => new { x.EntwurfId, x.Id });
		builder.Property(x => x.Id).ValueGeneratedNever();
		builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
		builder.Property(x => x.Art).HasMaxLength(20).IsRequired();
		builder.Property(x => x.Stimme).HasMaxLength(200).IsRequired();
		builder.Property(x => x.LesenStatus).HasMaxLength(20).IsRequired();
		builder.Property(x => x.LesenMeldung).HasMaxLength(500).IsRequired();
		builder.HasOne(x => x.Entwurf)
			.WithMany(e => e.Dateien)
			.HasForeignKey(x => x.EntwurfId)
			.OnDelete(DeleteBehavior.Cascade);
	}

	public void Configure(EntityTypeBuilder<LiedEntwurfLesung> builder)
	{
		builder.ToTable("lied_entwurf_lesungen");
		builder.HasKey(x => new { x.EntwurfId, x.DateiId });
		builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
		builder.Property(x => x.Meldung).HasMaxLength(500).IsRequired();
		builder.Property(x => x.Felder).IsRequired();
		builder.HasOne(x => x.Datei)
			.WithOne(d => d.Lesung)
			.HasForeignKey<LiedEntwurfLesung>(x => new { x.EntwurfId, x.DateiId })
			.OnDelete(DeleteBehavior.Cascade);
	}
}
