using Archive.Backend.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Assets;

public sealed class AssetModelConfiguration
	: IEntityTypeConfiguration<ArchiveAsset>, IEntityTypeConfiguration<FileRevision>, IEntityTypeConfiguration<PendingUpload>
{
	public void Configure(EntityTypeBuilder<ArchiveAsset> builder)
	{
		builder.ToTable("assets");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.AssetType).HasMaxLength(100).IsRequired().HasDefaultValue("score");
		builder.Property(x => x.VoiceLabel).HasMaxLength(200);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		/// <summary>
		/// Deletion flows through the retained-reference contract (ARC-037),
		/// which must clear the current-revision pointer and consider retained
		/// revisions before removal; catalogue deletion never cascades into
		/// retained files.
		/// </summary>
		builder.HasOne(x => x.MusicalVersion)
			.WithMany()
			.HasForeignKey(x => x.MusicalVersionId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.MusicalVersionId);
		/// <summary>
		/// Retained-reference contract: the pointer must be cleared before a
		/// revision can be removed, so the database refuses cascade deletion.
		/// </summary>
		builder.HasOne(x => x.CurrentRevision)
			.WithOne()
			.HasForeignKey<ArchiveAsset>(x => x.CurrentRevisionId)
			.OnDelete(DeleteBehavior.Restrict);
	}

	public void Configure(EntityTypeBuilder<FileRevision> builder)
	{
		builder.ToTable("file_revisions");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.BlobName).HasMaxLength(300).IsRequired();
		builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
		builder.HasOne(x => x.Asset)
			.WithMany(a => a.Revisions)
			.HasForeignKey(x => x.AssetId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => new { x.AssetId, x.RevisionNumber }).IsUnique();
		builder.HasIndex(x => x.BlobName).IsUnique();
	}

	public void Configure(EntityTypeBuilder<PendingUpload> builder)
	{
		builder.ToTable("upload_sessions");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.BlobName).HasMaxLength(300).IsRequired();
		builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
		builder.HasOne(x => x.Asset)
			.WithMany()
			.HasForeignKey(x => x.AssetId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.BlobName).IsUnique();
		/// <summary>Idempotency reference; Restrict so finalized revisions are never deleted under a session.</summary>
		builder.HasOne(x => x.FinalizedRevision)
			.WithOne()
			.HasForeignKey<PendingUpload>(x => x.FinalizedRevisionId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
