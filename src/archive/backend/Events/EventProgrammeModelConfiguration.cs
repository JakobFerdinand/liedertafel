using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Events;

public sealed class EventProgrammeModelConfiguration
	: IEntityTypeConfiguration<EventProgramme>, IEntityTypeConfiguration<ProgrammeItem>
{
	public void Configure(EntityTypeBuilder<EventProgramme> builder)
	{
		builder.ToTable("programmes");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// The programme belongs to exactly one event. Cascade removes the
		// programme with its event; the event row itself is never touched by
		// programme mutations.
		builder.HasOne(x => x.Event)
			.WithMany()
			.HasForeignKey(x => x.EventId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.EventId).IsUnique();
	}

	public void Configure(EntityTypeBuilder<ProgrammeItem> builder)
	{
		builder.ToTable("programme_items");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Note).HasMaxLength(500);
		builder.HasOne(x => x.Programme)
			.WithMany(p => p.Items)
			.HasForeignKey(x => x.ProgrammeId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => new { x.ProgrammeId, x.Position }).IsUnique();
		// Restrict keeps Lied deletion from cascading into the record of what was sung.
		builder.HasOne(x => x.Lied)
			.WithMany()
			.HasForeignKey(x => x.LiedId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.LiedId);
	}
}
