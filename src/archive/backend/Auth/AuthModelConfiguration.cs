using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Auth;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
	public void Configure(EntityTypeBuilder<Account> builder)
	{
		builder.ToTable("auth_accounts");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
		builder.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
		builder.Property(x => x.DisplayName).HasMaxLength(200);
		builder.HasIndex(x => x.NormalizedEmail).IsUnique();
	}
}

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
	public void Configure(EntityTypeBuilder<Membership> builder)
	{
		builder.ToTable("auth_memberships");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.Role).HasConversion<int>().IsRequired();
		builder.Property(x => x.Status).HasConversion<int>().IsRequired();
		builder.Property(x => x.InvitedBy).HasMaxLength(200);
		builder.HasOne(x => x.Account)
			.WithMany(a => a.Memberships)
			.HasForeignKey(x => x.AccountId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex(x => x.AccountId);
		builder.HasIndex(x => x.Status);
	}
}

public sealed class SignInCodeConfiguration : IEntityTypeConfiguration<SignInCode>
{
	public void Configure(EntityTypeBuilder<SignInCode> builder)
	{
		builder.ToTable("auth_sign_in_codes");
		builder.HasKey(x => x.Id);
		builder.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
		builder.Property(x => x.CodeHash).IsRequired();
		builder.Property(x => x.Salt).IsRequired();
		builder.Property(x => x.AttemptCount).HasDefaultValue(0);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		builder.HasIndex(x => x.NormalizedEmail);
		builder.HasIndex(x => x.AccountId);
		builder.HasIndex(x => x.ExpiresAt);
		builder.ToTable(t => t.HasCheckConstraint("CK_auth_sign_in_codes_attempt_count", "\"AttemptCount\" >= 0"));
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
