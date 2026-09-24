using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Events;

public sealed class EventModelConfiguration : IEntityTypeConfiguration<ChoirEvent>
{
	public void Configure(EntityTypeBuilder<ChoirEvent> builder)
	{
		builder.ToTable("events");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Kind).HasMaxLength(20).IsRequired();
		builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
		builder.Property(x => x.Venue).HasMaxLength(200);
		builder.Property(x => x.StartTime).HasMaxLength(10);
		builder.Property(x => x.Notes).HasMaxLength(2000);
		builder.Property(x => x.SourceNote).HasMaxLength(500);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		builder.HasIndex(x => x.DateYear);
	}
}

/// <summary>
/// Single shared visibility decision point for the event history (ARC-024 and
/// the later programme/document/recording slices): an event is member-visible
/// exactly when it is published. Member API reads never expose drafts; a draft
/// answers 404 for members and full data for editors/administrators.
/// </summary>
public static class EventVisibility
{
	public static bool IsMemberVisible(ChoirEvent choirEvent) => choirEvent.PublishedAt is not null;
}

/// <summary>
/// Closed ARC-024 kind set for the choir's appearances (PRD §6): concerts,
/// services, weddings, funerals, festivals and other appearances.
/// </summary>
public static class EventKinds
{
	public static readonly string[] Known = ["concert", "service", "wedding", "funeral", "festival", "other"];
}
