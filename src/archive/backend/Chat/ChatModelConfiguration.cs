using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Chat;

public sealed class ChatModelConfiguration
	: IEntityTypeConfiguration<ChatThread>, IEntityTypeConfiguration<ChatMessage>
{
	public void Configure(EntityTypeBuilder<ChatThread> builder)
	{
		builder.ToTable("chat_threads");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.AccountId).IsRequired();
		builder.HasIndex(x => x.AccountId);
		builder.HasMany(x => x.Messages)
			.WithOne()
			.HasForeignKey(m => m.ThreadId)
			.OnDelete(DeleteBehavior.Cascade);
	}

	public void Configure(EntityTypeBuilder<ChatMessage> builder)
	{
		builder.ToTable("chat_messages");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Role).HasMaxLength(20).IsRequired();
		builder.Property(x => x.Content).HasMaxLength(8000).IsRequired();
		builder.HasIndex(x => x.ThreadId);
	}
}
