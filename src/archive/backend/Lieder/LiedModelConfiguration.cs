using Archive.Backend.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Lieder;

public sealed class LiedModelConfiguration : IEntityTypeConfiguration<Lied>, IEntityTypeConfiguration<Datei>
{
	public void Configure(EntityTypeBuilder<Lied> builder)
	{
		builder.ToTable("lieder");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
		// The lengths are the field catalog's (#192); Title is the required
		// key the catalog holds separately.
		foreach (var field in FieldCatalog.LiedFields)
			builder.Property(field.EntityProperty).HasMaxLength(field.MaxLength);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		builder.HasIndex(x => x.Title);
	}

	public void Configure(EntityTypeBuilder<Datei> builder)
	{
		builder.ToTable("dateien", t =>
		{
			t.HasCheckConstraint("CK_dateien_owner",
				"((\"LiedId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");
			t.HasCheckConstraint("CK_dateien_kind",
				"\"Kind\" IN ('noten', 'uebe', 'aufnahme', 'sonstiges')");
			t.HasCheckConstraint("CK_dateien_partitur", "NOT \"IsPartitur\" OR \"Kind\" = 'noten'");
		});
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Kind).HasMaxLength(20).IsRequired();
		builder.Property(x => x.Stimme).HasMaxLength(200);
		builder.Property(x => x.Description).HasMaxLength(500);
		builder.Property(x => x.BlobName).HasMaxLength(300).IsRequired();
		builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
		builder.Property(x => x.OriginalFileName).HasMaxLength(300);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// Dateien outlive nothing they do not belong to, but a Lied is only
		// removed by the data reset, so deletion never cascades silently.
		builder.HasOne(x => x.Lied)
			.WithMany(l => l.Dateien)
			.HasForeignKey(x => x.LiedId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.Event)
			.WithMany()
			.HasForeignKey(x => x.EventId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.RecordedAtEvent)
			.WithMany()
			.HasForeignKey(x => x.RecordedAtEventId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.LiedId);
		builder.HasIndex(x => x.EventId);
		builder.HasIndex(x => x.BlobName).IsUnique();
	}
}
