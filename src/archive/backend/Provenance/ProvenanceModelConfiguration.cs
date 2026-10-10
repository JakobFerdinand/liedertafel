using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Provenance;

public sealed class ProvenanceModelConfiguration
	: IEntityTypeConfiguration<FieldProvenance>, IEntityTypeConfiguration<Proposal>
{
	private const string ProposalKindCheck = "CK_proposals_kind";

	public void Configure(EntityTypeBuilder<FieldProvenance> builder)
	{
		builder.ToTable("field_provenance");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
		builder.Property(x => x.Field).HasMaxLength(60).IsRequired();
		// JSON-safe value history: song lyrics are at most 5000 characters.
		builder.Property(x => x.PreviousValue).HasMaxLength(5000);
		builder.Property(x => x.Model).HasMaxLength(200);
		builder.Property(x => x.PromptVersion).HasMaxLength(100);
		builder.HasIndex(x => new { x.EntityType, x.EntityId, x.Field })
			.IsUnique()
			.HasDatabaseName("IX_field_provenance_target");
		// The removed entity must remove the provenance rows check-free; FKs
		// would only multiply relations, so ownership travels with the entity
		// (delete behaviour is enforced by catalog cascades already).
	}

	public void Configure(EntityTypeBuilder<Proposal> builder)
	{
		builder.ToTable("proposals");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40).IsRequired();
		builder.Property(x => x.TargetEntityType).HasMaxLength(40);
		builder.Property(x => x.Payload).HasMaxLength(5000).IsRequired();
		builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
		builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
		builder.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(20).IsRequired();
		builder.Property(x => x.Model).HasMaxLength(200);
		builder.Property(x => x.PromptVersion).HasMaxLength(100);
		builder.Property(x => x.SourceDescription).HasMaxLength(300);
		builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
		builder.HasIndex(x => new { x.Status, x.CreatedAt })
			.HasDatabaseName("IX_proposals_status_created");
		builder.ToTable("proposals", t =>
		{
			t.HasCheckConstraint(ProposalKindCheck,
				"\"Kind\" IN ('FieldSuggestion', 'SongCreation', 'SongPublication', 'SongDeletion', 'SongMerge', 'MemberAdministration', 'EventPublication', 'EventDeletion')");
			t.HasCheckConstraint("CK_proposals_status", "\"Status\" IN ('Open', 'Accepted', 'Rejected')");
		});
	}
}
