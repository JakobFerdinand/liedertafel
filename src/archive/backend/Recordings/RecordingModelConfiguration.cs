using Archive.Backend.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Recordings;

public sealed class RecordingModelConfiguration : IEntityTypeConfiguration<Recording>
{
	public void Configure(EntityTypeBuilder<Recording> builder)
	{
		builder.ToTable("recordings", t => t.HasCheckConstraint("CK_recordings_kind",
			"\"Kind\" IN ('audio', 'video')"));
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Label).HasMaxLength(RecordingEndpoints.LabelMaxLength).IsRequired();
		builder.Property(x => x.Kind).HasMaxLength(20).IsRequired();
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// Restrict everywhere: neither event deletion nor asset removal may
		// silently take a recording or its retained files along; the trash
		// slices (ARC-039/ARC-040) decide that explicitly.
		builder.HasOne(x => x.Event)
			.WithMany()
			.HasForeignKey(x => x.EventId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.EventId);
		// One asset fills exactly one slot of one recording.
		builder.HasOne(x => x.OriginalAsset)
			.WithOne()
			.HasForeignKey<Recording>(x => x.OriginalAssetId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.PlaybackAsset)
			.WithOne()
			.HasForeignKey<Recording>(x => x.PlaybackAssetId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
