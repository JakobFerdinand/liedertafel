using Archive.Backend.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Catalogue;

public sealed class CatalogueModelConfiguration
	: IEntityTypeConfiguration<Song>, IEntityTypeConfiguration<Arrangement>, IEntityTypeConfiguration<MusicalVersion>,
		IEntityTypeConfiguration<SongTitle>, IEntityTypeConfiguration<SongTag>
{
	public void Configure(EntityTypeBuilder<Song> builder)
	{
		builder.ToTable("songs");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
		builder.Property(x => x.Composer).HasMaxLength(200);
		builder.Property(x => x.Lyricist).HasMaxLength(200);
		builder.Property(x => x.Lyrics).HasMaxLength(5000);
		builder.Property(x => x.Language).HasMaxLength(200);
		builder.Property(x => x.Occasion).HasMaxLength(200);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		builder.HasIndex(x => x.Title);
	}

	public void Configure(EntityTypeBuilder<Arrangement> builder)
	{
		builder.ToTable("arrangements");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Label).HasMaxLength(200).IsRequired();
		builder.Property(x => x.Arranger).HasMaxLength(200);
		builder.Property(x => x.VoiceConfiguration).HasMaxLength(200);
		builder.Property(x => x.Accompaniment).HasMaxLength(200);
		builder.HasOne(x => x.Song)
			.WithMany(s => s.Arrangements)
			.HasForeignKey(x => x.SongId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.SongId);
	}

	public void Configure(EntityTypeBuilder<MusicalVersion> builder)
	{
		builder.ToTable("musical_versions");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Label).HasMaxLength(200).IsRequired();
		builder.Property(x => x.Creator).HasMaxLength(200);
		builder.Property(x => x.MusicalKey).HasMaxLength(200);
		builder.HasOne(x => x.Arrangement)
			.WithMany(a => a.MusicalVersions)
			.HasForeignKey(x => x.ArrangementId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.ArrangementId);
	}

	public void Configure(EntityTypeBuilder<SongTitle> builder)
	{
		builder.ToTable("song_titles");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Value).HasMaxLength(200).IsRequired();
		builder.HasOne(x => x.Song)
			.WithMany(s => s.AlternateTitles)
			.HasForeignKey(x => x.SongId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.SongId);
	}

	public void Configure(EntityTypeBuilder<SongTag> builder)
	{
		builder.ToTable("song_tags");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Value).HasMaxLength(60).IsRequired();
		builder.HasOne(x => x.Song)
			.WithMany(s => s.Tags)
			.HasForeignKey(x => x.SongId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.SongId);
	}
}

/// <summary>
/// Single shared visibility decision point for the catalogue (ARC-013 and the
/// later file/history/trash slices): a song is member-visible exactly when it
/// is published. Member API reads never expose drafts; a draft answers 404 for
/// members and full data for editors/administrators.
/// </summary>
public static class CatalogueVisibility
{
	public static bool IsMemberVisible(Song song) => song.PublishedAt is not null;
}
