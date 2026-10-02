using Archive.Backend.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Extraction;

public sealed class ExtractionModelConfiguration : IEntityTypeConfiguration<ExtractionJob>
{
	public void Configure(EntityTypeBuilder<ExtractionJob> builder)
	{
		builder.ToTable("extraction_jobs");
		// Revision-keyed idempotency (ARC-034): one row per immutable
		// revision; late or duplicated messages always land in this same row.
		builder.HasKey(x => x.RevisionId);
		// The app bounds the extracted text (ExtractionOptions), so the
		// schema keeps an unbounded text column.
		builder.Property(x => x.FailureReason).HasMaxLength(300);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		/// <summary>
		/// The revision is immutable and deletion flows through the
		/// retained-reference contract (ARC-037); extraction rows must never
		/// disappear with a revision row, so the database refuses cascades.
		/// </summary>
		builder.HasOne(x => x.Revision)
			.WithMany()
			.HasForeignKey(x => x.RevisionId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
