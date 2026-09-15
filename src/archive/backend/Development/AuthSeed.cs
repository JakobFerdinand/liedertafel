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
/// All writes tolerate a lost unique-constraint race (parallel seeds, repair
/// reruns): after a conflict the desired end state is re-read and only a
/// still-missing state throws.
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
			{
				var created = await TryAsync(() => roles.CreateAsync(new ArchiveRole(role)));
				if (!created.Succeeded && !await roles.RoleExistsAsync(role))
					AssertSucceeded(created);
			}
		}
		var result = new List<SeedAccount>();
		foreach (var (email, displayName, role) in TestAccounts)
		{
			var id = await EnsureUserAsync(users, email.Trim(), displayName, token);
			var user = (await users.FindByEmailAsync(email))!;
			if (!await users.IsInRoleAsync(user, role))
			{
				var assigned = await TryAsync(() => users.AddToRoleAsync(user, role));
				if (!assigned.Succeeded && !await users.IsInRoleAsync(user, role))
					AssertSucceeded(assigned);
			}
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
			var created = await TryAsync(() => users.CreateAsync(user));
			if (!created.Succeeded)
			{
				user = await users.FindByEmailAsync(normalized);
				if (user is null)
					AssertSucceeded(created);
			}
		}
		else if (!user.EmailConfirmed)
		{
			user.EmailConfirmed = true;
			var confirmed = await TryAsync(() => users.UpdateAsync(user!));
			if (!confirmed.Succeeded)
			{
				user = await users.FindByEmailAsync(normalized);
				if (user is null || !user.EmailConfirmed)
					AssertSucceeded(confirmed);
			}
		}
		return user!.Id;
	}

	/// <summary>Runs an Identity write, reporting store races as failure instead of throwing.</summary>
	private static async Task<IdentityResult> TryAsync(Func<Task<IdentityResult>> write)
	{
		try
		{
			return await write();
		}
		catch (Exception exception) when (exception is DbUpdateException or DbUpdateConcurrencyException)
		{
			return IdentityResult.Failed(new IdentityError
			{
				Code = "ConcurrencyConflict",
				Description = "Concurrent seed write lost a store race; the caller re-reads state.",
			});
		}
	}

	private static void AssertSucceeded(IdentityResult result)
	{
		if (!result.Succeeded)
			throw new InvalidOperationException(
				"Identity operation failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
	}
}
