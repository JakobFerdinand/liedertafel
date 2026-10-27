using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Events;

/// <summary>
/// An unfinished Auftritt upload (#131, "Angefangen" next to the Lied drafts):
/// the form an editor was filling after dropping a programme scan or concert
/// video. It is not an Auftritt: nothing of it is visible to members and no
/// Lied or Programm exists until "Speichern". The browser chooses
/// <see cref="Id"/> and owns the shape of <see cref="Form"/> (a versioned JSON
/// object of the field values, which fields the editor touched, the Programm
/// with its Lied choices and the Dateien without their bytes); the server only
/// bounds it. Nothing records where a value came from.
/// </summary>
public sealed class AuftrittEntwurf
{
	public Guid Id { get; set; }

	public Guid OwnerAccountId { get; set; }

	/// <summary>Shown in "Angefangen"; the form's title at the last save.</summary>
	public string Title { get; set; } = string.Empty;

	/// <summary>How many Dateien the form holds, for the list.</summary>
	public int DateiCount { get; set; }

	/// <summary>The form as the browser sent it (a JSON object).</summary>
	public string Form { get; set; } = "{}";

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

public static class AuftrittEntwurfRules
{
	/// <summary>The form version the browser writes; others are refused until a migration exists.</summary>
	public const int FormVersion = 1;

	public const int MaxFormLength = 100_000;

	public const int MaxDateien = 50;

	public const int MaxEntwuerfeProKonto = 25;
}

public sealed class AuftrittEntwurfModelConfiguration : IEntityTypeConfiguration<AuftrittEntwurf>
{
	public void Configure(EntityTypeBuilder<AuftrittEntwurf> builder)
	{
		builder.ToTable("auftritt_entwuerfe");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Id).ValueGeneratedNever();
		builder.Property(x => x.Title).HasMaxLength(EventEndpoints.TitleMaxLength).IsRequired();
		builder.Property(x => x.Form).HasMaxLength(AuftrittEntwurfRules.MaxFormLength).IsRequired();
		builder.HasIndex(x => new { x.OwnerAccountId, x.UpdatedAt });
	}
}
