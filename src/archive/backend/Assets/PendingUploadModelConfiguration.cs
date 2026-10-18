using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Assets;

public sealed class PendingUploadModelConfiguration : IEntityTypeConfiguration<PendingUpload>
{
	public void Configure(EntityTypeBuilder<PendingUpload> builder)
	{
		builder.ToTable("upload_sessions", t => t.HasCheckConstraint("CK_upload_sessions_owner",
			"((\"LiedId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1"));
		builder.HasKey(x => x.Id);
		builder.Property(x => x.DateiKind).HasMaxLength(20);
		builder.Property(x => x.Stimme).HasMaxLength(200);
		builder.Property(x => x.Description).HasMaxLength(500);
		builder.Property(x => x.BlobName).HasMaxLength(300).IsRequired();
		builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
		builder.Property(x => x.DeclaredFileName).HasMaxLength(300);
		builder.HasOne<Lieder.Lied>().WithMany().HasForeignKey(x => x.LiedId).OnDelete(DeleteBehavior.Cascade);
		builder.HasOne<Events.ChoirEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.LiedId);
		builder.HasIndex(x => x.BlobName).IsUnique();
		// Two finalizes of one session race on the state: the loser's save fails
		// instead of creating a second Datei.
		builder.Property(x => x.State).IsConcurrencyToken();
		builder.HasIndex(x => x.FinalizedDateiId).IsUnique().HasFilter("\"FinalizedDateiId\" IS NOT NULL");
	}
}
