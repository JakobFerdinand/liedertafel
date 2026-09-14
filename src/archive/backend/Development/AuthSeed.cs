using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Development;

public sealed record SeedAccount(string Email, ArchiveRole Role, Guid AccountId);

/// <summary>
/// Development-only test accounts for all roles so catalogue/event slices need
/// not wait for the ARC-006 admin UI. Never used in production.
/// </summary>
public static class AuthSeed
{
	public static readonly IReadOnlyList<(string Email, string DisplayName, ArchiveRole Role)> TestAccounts =
	[
		("mitglied@liedertafel.test", "Testmitglied", ArchiveRole.Member),
		("redaktion@liedertafel.test", "Testredaktion", ArchiveRole.Editor),
		("verwaltung@liedertafel.test", "Testverwaltung", ArchiveRole.Administrator),
	];

	public static async Task<IReadOnlyList<SeedAccount>> EnsureTestAccountsAsync(ArchiveDbContext db, DateTimeOffset now, CancellationToken token)
	{
		var result = new List<SeedAccount>();
		foreach (var (email, displayName, role) in TestAccounts)
		{
			var normalized = AuthSecurity.NormalizeEmail(email);
			var account = await db.Accounts.SingleOrDefaultAsync(a => a.NormalizedEmail == normalized, token);
			if (account is null)
			{
				account = new Account
				{
					Email = email, NormalizedEmail = normalized, DisplayName = displayName,
					CreatedAt = now, UpdatedAt = now,
				};
				db.Accounts.Add(account);
				await db.SaveChangesAsync(token);
			}
			var membership = await db.Memberships
				.Where(m => m.AccountId == account.Id && m.Role == role)
				.SingleOrDefaultAsync(token);
			if (membership is null)
			{
				db.Memberships.Add(new Membership
				{
					AccountId = account.Id, Role = role, Status = MembershipStatus.Active,
					InvitedBy = "Entwicklungsstart", InvitedAt = now, ActivatedAt = now, UpdatedAt = now,
				});
			}
			else if (membership.Status != MembershipStatus.Active)
			{
				membership.Status = MembershipStatus.Active;
				membership.ActivatedAt = now;
				membership.DeactivatedAt = null;
				membership.UpdatedAt = now;
			}
			result.Add(new SeedAccount(account.Email, role, account.Id));
		}
		await db.SaveChangesAsync(token);
		return result;
	}

	public static async Task<Guid> EnsureOperatorAccountAsync(
		ArchiveDbContext db, string email, string? displayName, ArchiveRole role, string invitedBy, DateTimeOffset now, CancellationToken token)
	{
		var normalized = AuthSecurity.NormalizeEmail(email);
		var account = await db.Accounts.SingleOrDefaultAsync(a => a.NormalizedEmail == normalized, token);
		if (account is null)
		{
			account = new Account
			{
				Email = email.Trim(), NormalizedEmail = normalized,
				DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
				CreatedAt = now, UpdatedAt = now,
			};
			db.Accounts.Add(account);
			await db.SaveChangesAsync(token);
		}
		var membership = await db.Memberships
			.Where(m => m.AccountId == account.Id && m.Role == role)
			.SingleOrDefaultAsync(token);
		if (membership is null)
		{
			db.Memberships.Add(new Membership
			{
				AccountId = account.Id, Role = role, Status = MembershipStatus.Active,
				InvitedBy = invitedBy, InvitedAt = now, ActivatedAt = now, UpdatedAt = now,
			});
		}
		else if (membership.Status != MembershipStatus.Active)
		{
			membership.Status = MembershipStatus.Active;
			membership.ActivatedAt = now;
			membership.DeactivatedAt = null;
			membership.UpdatedAt = now;
		}
		await db.SaveChangesAsync(token);
		return account.Id;
	}
}
