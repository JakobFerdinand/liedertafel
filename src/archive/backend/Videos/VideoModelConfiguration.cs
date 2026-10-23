using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Videos;

public sealed class VideoModelConfiguration : IEntityTypeConfiguration<VideoJob>
{
	public void Configure(EntityTypeBuilder<VideoJob> builder)
	{
		builder.ToTable("video_jobs");
		// Datei-keyed idempotency (#161): one row per immutable Datei; late or
		// duplicated queue messages always land in this same row.
		builder.HasKey(x => x.DateiId);
		builder.Property(x => x.FailureReason).HasMaxLength(300);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// The Datei is immutable; playback copies must never disappear with a
		// Datei row, so the database refuses cascades.
		builder.HasOne(x => x.Datei)
			.WithMany()
			.HasForeignKey(x => x.DateiId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.PlaybackDatei)
			.WithMany()
			.HasForeignKey(x => x.PlaybackDateiId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
