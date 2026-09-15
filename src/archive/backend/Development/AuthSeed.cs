using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Development;

public sealed record SeedAccount(string Email, string Role, Guid AccountId);

/// <summary>
/// Development-only test accounts for all roles so catalogue/event slices need
/// not wait for the ARC-006 admin UI. Identity-native: confirmed users with
/// role memberships, no passwords. Never used in production.
/// </summary>
public static class AuthSeed
{
	public static readonly IReadOnlyList<(string Email, string DisplayName, string Role)> TestAccounts =
	[
		("mitglied@liedertafel.test", "Testmitglied", ArchiveRoles.Member),
		("redaktion@liedertafel.test", "Testredaktion", ArchiveRoles.Editor),
		("verwaltung@liedertafel.test", "Testverwaltung", ArchiveRoles.Administrator),
	];

	public static async Task<IReadOnlyList<SeedAccount>> EnsureTestAccountsAsync(
		UserManager<ArchiveUser> users, RoleManager<ArchiveRole> roles, CancellationToken token)
	{
		foreach (var role in ArchiveRoles.All)
		{
			if (!await roles.RoleExistsAsync(role))
				AssertSucceeded(await roles.CreateAsync(new ArchiveRole(role)));
		}
		var result = new List<SeedAccount>();
		foreach (var (email, displayName, role) in TestAccounts)
		{
			var id = await EnsureUserAsync(users, email.Trim(), displayName, token);
			var user = (await users.FindByEmailAsync(email))!;
			if (!await users.IsInRoleAsync(user, role))
				AssertSucceeded(await users.AddToRoleAsync(user, role));
			result.Add(new SeedAccount(user.Email!, role, id));
		}
		return result;
	}

	/// <summary>
	/// Creates the user unless present, confirms the address (invitation
	/// accepted by the operator), and returns the stable user id.
	/// </summary>
	public static async Task<Guid> EnsureUserAsync(
		UserManager<ArchiveUser> users, string email, string? displayName, CancellationToken token)
	{
		var normalized = AuthSecurity.NormalizeEmail(email);
		var user = await users.FindByEmailAsync(normalized);
		if (user is null)
		{
			user = new ArchiveUser
			{
				UserName = email.Trim(),
				Email = email.Trim(),
				DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
				EmailConfirmed = true,
			};
			AssertSucceeded(await users.CreateAsync(user));
		}
		else if (!user.EmailConfirmed)
		{
			user.EmailConfirmed = true;
			AssertSucceeded(await users.UpdateAsync(user));
		}
		return user.Id;
	}

	private static void AssertSucceeded(IdentityResult result)
	{
		if (!result.Succeeded)
			throw new InvalidOperationException(
				"Identity operation failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
	}
}
