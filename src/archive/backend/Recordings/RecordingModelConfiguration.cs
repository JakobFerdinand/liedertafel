using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Recordings;

public sealed class RecordingModelConfiguration : IEntityTypeConfiguration<Recording>
{
	public void Configure(EntityTypeBuilder<Recording> builder)
	{
		builder.ToTable("recordings", t =>
		{
			t.HasCheckConstraint("CK_recordings_kind", "\"Kind\" IN ('audio', 'video')");
			// One Datei cannot be both the preserved original and its own playback
			// copy; "serves both roles" is an empty playback slot.
			t.HasCheckConstraint("CK_recordings_dateien",
				"\"PlaybackDateiId\" IS NULL OR \"PlaybackDateiId\" <> \"OriginalDateiId\"");
		});
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Label).HasMaxLength(RecordingEndpoints.LabelMaxLength).IsRequired();
		builder.Property(x => x.Kind).HasMaxLength(20).IsRequired();
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// Restrict everywhere: neither event deletion nor file removal may
		// silently take a recording or its retained files along; the trash
		// slices (ARC-039/ARC-040) decide that explicitly.
		builder.HasOne(x => x.Event)
			.WithMany()
			.HasForeignKey(x => x.EventId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.EventId);
		// Target of the passages' composite key: a passage can only point at
		// a recording of its own event (ARC-032).
		builder.HasAlternateKey(x => new { x.Id, x.EventId });
		// One Datei fills exactly one slot of one recording.
		builder.HasOne(x => x.OriginalDatei)
			.WithOne()
			.HasForeignKey<Recording>(x => x.OriginalDateiId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.PlaybackDatei)
			.WithOne()
			.HasForeignKey<Recording>(x => x.PlaybackDateiId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
