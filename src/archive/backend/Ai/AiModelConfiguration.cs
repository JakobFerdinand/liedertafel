using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Ai;

public sealed class AiModelConfiguration
	: IEntityTypeConfiguration<AiUsageEntry>, IEntityTypeConfiguration<AiBudgetMonth>
{
	public void Configure(EntityTypeBuilder<AiUsageEntry> builder)
	{
		builder.ToTable("ai_usage_entries");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.YearMonth).HasMaxLength(7).IsRequired();
		builder.Property(x => x.Feature).HasMaxLength(64).IsRequired();
		builder.Property(x => x.Model).HasMaxLength(128).IsRequired();
		builder.HasIndex(x => x.YearMonth);
		builder.HasIndex(x => new { x.OperationId, x.Model, x.YearMonth }).IsUnique();
	}

	public void Configure(EntityTypeBuilder<AiBudgetMonth> builder)
	{
		builder.ToTable("ai_budget_months");
		builder.HasKey(x => x.YearMonth);
		builder.Property(x => x.YearMonth).HasMaxLength(7);
	}
}
