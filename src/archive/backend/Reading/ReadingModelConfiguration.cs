using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Reading;

public sealed class ReadingModelConfiguration : IEntityTypeConfiguration<ReadingJob>
{
	public void Configure(EntityTypeBuilder<ReadingJob> builder)
	{
		builder.ToTable("reading_jobs", t =>
		{
			t.HasCheckConstraint("CK_reading_jobs_status", "\"Status\" IN (0, 1, 2, 3, 4)");
			t.HasCheckConstraint("CK_reading_jobs_pages", "\"PagesRead\" >= 0 AND \"PageLimit\" >= 0");
		});
		builder.HasKey(x => x.DateiId);
		builder.Property(x => x.FailureReason).HasMaxLength(300);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// Dispatch sweep: re-queue rows and parked work by status.
		builder.HasIndex(x => x.Status);
		builder.Ignore(x => x.IsPartial);
		// The Datei is immutable; reading rows never disappear with it.
		builder.HasOne(x => x.Datei)
			.WithMany()
			.HasForeignKey(x => x.DateiId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
