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
		// Target of the recording passages' composite key (ARC-032): a passage
		// can only point at an occurrence of its recording's event.
		builder.HasAlternateKey(x => new { x.Id, x.EventId });
		// The retry key is unique per event; null keys never collide (PostgreSQL semantics).
		builder.HasIndex(x => new { x.EventId, x.Position }).IsUnique();
		builder.HasIndex(x => new { x.EventId, x.IdempotencyKey }).IsUnique();
		// Restrict keeps Lied deletion from cascading into historical evidence (ARC-037).
		builder.HasOne(x => x.Lied)
			.WithMany()
			.HasForeignKey(x => x.LiedId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.LiedId);
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
