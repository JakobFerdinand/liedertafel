using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Auth;

public sealed class ArchiveUserConfiguration : IEntityTypeConfiguration<ArchiveUser>
{
	public void Configure(EntityTypeBuilder<ArchiveUser> builder)
	{
		builder.Property(x => x.DisplayName).HasMaxLength(200);
	}
}

public sealed class SignInChallengeConfiguration : IEntityTypeConfiguration<SignInChallenge>
{
	public void Configure(EntityTypeBuilder<SignInChallenge> builder)
	{
		builder.ToTable("auth_sign_in_codes");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
		builder.Property(x => x.CodeHash).IsRequired();
		builder.Property(x => x.Salt).IsRequired();
		builder.Property(x => x.AttemptCount).HasDefaultValue(0);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		builder.HasOne(x => x.User)
			.WithMany()
			.HasForeignKey(x => x.UserId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.NormalizedEmail);
		builder.HasIndex(x => x.UserId);
		builder.HasIndex(x => x.ExpiresAt);
		builder.ToTable(t => t.HasCheckConstraint("CK_auth_sign_in_codes_attempt_count", "\"AttemptCount\" >= 0"));
	}
}

public sealed class MemberInvitationConfiguration : IEntityTypeConfiguration<MemberInvitation>
{
	public void Configure(EntityTypeBuilder<MemberInvitation> builder)
	{
		builder.ToTable("member_invitations");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
		builder.Property(x => x.Role).HasMaxLength(50).IsRequired();
		builder.Property(x => x.DisplayName).HasMaxLength(200);
		builder.Property(x => x.MailStatus).HasConversion<int>().IsRequired();
		builder.Property(x => x.LastError).HasMaxLength(500);
		builder.HasOne(x => x.User)
			.WithMany()
			.HasForeignKey(x => x.UserId)
			.OnDelete(DeleteBehavior.Cascade);
		builder.HasIndex(x => x.NormalizedEmail).IsUnique();
		builder.HasIndex(x => x.UserId).IsUnique();
		builder.HasIndex(x => x.InvitedAt);
	}
}

public sealed class MemberAdminActionConfiguration : IEntityTypeConfiguration<MemberAdminAction>
{
	public void Configure(EntityTypeBuilder<MemberAdminAction> builder)
	{
		builder.ToTable("member_admin_actions");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.OldRoles).HasMaxLength(200).IsRequired();
		builder.Property(x => x.NewRoles).HasMaxLength(200).IsRequired();
		builder.Property(x => x.Action).HasConversion<int>().IsRequired();
		builder.HasIndex(x => x.TargetUserId);
		builder.HasIndex(x => x.OccurredAt);
	}
}

public sealed class AuthRequestLogConfiguration : IEntityTypeConfiguration<AuthRequestLog>
{
	public void Configure(EntityTypeBuilder<AuthRequestLog> builder)
	{
		builder.ToTable("auth_request_log");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Id).UseIdentityByDefaultColumn();
		builder.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
		builder.Property(x => x.IpHash).HasMaxLength(128).IsRequired();
		builder.Property(x => x.Kind).HasConversion<int>().IsRequired();
		builder.HasIndex(x => new { x.NormalizedEmail, x.OccurredAt });
		builder.HasIndex(x => new { x.IpHash, x.OccurredAt });
	}
}
