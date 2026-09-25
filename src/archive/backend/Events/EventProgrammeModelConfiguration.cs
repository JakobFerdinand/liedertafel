using Archive.Backend.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Events;

public sealed class EventProgrammeModelConfiguration
	: IEntityTypeConfiguration<EventProgramme>, IEntityTypeConfiguration<ProgrammeRevision>,
		IEntityTypeConfiguration<ProgrammeItem>
{
	public void Configure(EntityTypeBuilder<EventProgramme> builder)
	{
		builder.ToTable("programmes");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		/// <summary>
		/// The programme belongs to exactly one event (ARC-026). Cascade
		/// removes the programme with its event; the event row itself is
		/// never touched by programme mutations.
		/// </summary>
		builder.HasOne(x => x.Event)
			.WithMany()
			.HasForeignKey(x => x.EventId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.EventId).IsUnique();
	}

	public void Configure(EntityTypeBuilder<ProgrammeRevision> builder)
	{
		builder.ToTable("programme_revisions");
		builder.HasKey(x => x.Id);
		builder.HasOne(x => x.Programme)
			.WithMany(p => p.Revisions)
			.HasForeignKey(x => x.ProgrammeId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => new { x.ProgrammeId, x.Number }).IsUnique();
		/// <summary>At most one working draft (PublishedAt null) per programme.</summary>
		builder.HasIndex(x => x.ProgrammeId).IsUnique().HasFilter("\"PublishedAt\" IS NULL");
	}

	public void Configure(EntityTypeBuilder<ProgrammeItem> builder)
	{
		builder.ToTable("programme_items");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Note).HasMaxLength(500);
		builder.HasOne(x => x.Revision)
			.WithMany(r => r.Items)
			.HasForeignKey(x => x.RevisionId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => new { x.RevisionId, x.Position }).IsUnique();
		/// <summary>
		/// Catalogue references (ARC-026): the musical version determines its
		/// arrangement parent; song/arrangement columns mirror the resolved
		/// chain. Restrict keeps catalogue deletion from cascading into
		/// programme history (the retained-reference contract, ARC-037).
		/// </summary>
		builder.HasOne(x => x.MusicalVersion)
			.WithMany()
			.HasForeignKey(x => x.MusicalVersionId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.MusicalVersionId);
		builder.HasOne<Song>()
			.WithMany()
			.HasForeignKey(x => x.SongId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.SongId);
		builder.HasOne<Arrangement>()
			.WithMany()
			.HasForeignKey(x => x.ArrangementId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.ArrangementId);
	}
}

/// <summary>
/// Single shared visibility decision point for the programme (ARC-026): the
/// working draft is editor-only and members see only the newest published
/// revision (highest Number among the published ones). Programme reads are
/// additionally gated by the event's own visibility (see
/// <see cref="EventVisibility"/>), so a draft event's programme never
/// reaches members — the event answers its indistinguishable 404.
/// </summary>
public static class ProgrammeVisibility
{
	/// <summary>The newest published revision, or null before the first publication.</summary>
	public static ProgrammeRevision? NewestPublished(EventProgramme programme)
		=> programme.Revisions
			.Where(r => r.PublishedAt is not null)
			.OrderByDescending(r => r.Number)
			.FirstOrDefault();

	/// <summary>The working draft (PublishedAt null), or null between publications.</summary>
	public static ProgrammeRevision? WorkingDraft(EventProgramme programme)
		=> programme.Revisions.FirstOrDefault(r => r.PublishedAt is null);
}
