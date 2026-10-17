using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Extraction;

public sealed class ExtractionModelConfiguration : IEntityTypeConfiguration<ExtractionJob>
{
	public void Configure(EntityTypeBuilder<ExtractionJob> builder)
	{
		builder.ToTable("extraction_jobs");
		// Datei-keyed idempotency (ARC-034): one row per immutable
		// Datei; late or duplicated messages always land in this same row.
		builder.HasKey(x => x.DateiId);
		// The app bounds the extracted text (ExtractionOptions), so the
		// schema keeps an unbounded text column.
		builder.Property(x => x.FailureReason).HasMaxLength(300);
		// ARC-034-1: 0 = embedded PDF text, 1 = transcribed from a scan.
		builder.Property(x => x.TextSource).HasDefaultValue(ExtractionTextSource.Embedded);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		/// <summary>
		/// The Datei is immutable and deletion flows through the
		/// retained-reference contract (ARC-037); extraction rows must never
		/// disappear with a Datei row, so the database refuses cascades.
		/// </summary>
		builder.HasOne(x => x.Datei)
			.WithMany()
			.HasForeignKey(x => x.DateiId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
