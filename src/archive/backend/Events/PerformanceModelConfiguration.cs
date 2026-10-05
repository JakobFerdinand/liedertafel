using Archive.Backend.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Events;

public sealed class PerformanceModelConfiguration : IEntityTypeConfiguration<Performance>
{
	public void Configure(EntityTypeBuilder<Performance> builder)
	{
		builder.ToTable("performances");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.EvidenceStatus).HasMaxLength(20).IsRequired();
		builder.Property(x => x.SourceNote).HasMaxLength(2000);
		builder.Property(x => x.IdempotencyKey).HasMaxLength(200);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		/// <summary>
		/// The occurrence belongs to exactly one event (ARC-028); the event
		/// row itself is never touched by evidence mutations.
		/// </summary>
		builder.HasOne(x => x.Event)
			.WithMany()
			.HasForeignKey(x => x.EventId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.EventId);
		/// <summary>
		/// Catalogue references (ARC-028): Restrict keeps catalogue deletion
		/// from cascading into historical evidence (the retained-reference
		/// contract, ARC-037). The arrangement column mirrors the resolved
		/// chain and is set together with the version; the endpoint
		/// validates that the version belongs to the arrangement and the
		/// song, so no database check constraint is needed. The retry key is
		/// unique per event; null keys never collide (PostgreSQL semantics).
		/// </summary>
		builder.HasIndex(x => new { x.EventId, x.Position }).IsUnique();
		builder.HasIndex(x => new { x.EventId, x.IdempotencyKey }).IsUnique();
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
		/// <summary>
		/// ARC-029 programme confirmation links: a planned entry is confirmed
		/// by at most one occurrence (unique, nulls never collide), and the
		/// confirmation owns its occurrences. Restrict keeps programme
		/// history from being deleted underneath retained evidence.
		/// </summary>
		builder.HasOne<ProgrammeItem>()
			.WithMany()
			.HasForeignKey(x => x.ProgrammeItemId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.ProgrammeItemId).IsUnique();
		builder.HasOne<ProgrammeConfirmation>()
			.WithMany()
			.HasForeignKey(x => x.ConfirmationId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.ConfirmationId);
	}
}
