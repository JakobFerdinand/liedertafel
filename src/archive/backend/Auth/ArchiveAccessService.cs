using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Archive.Backend.Auth;

/// <summary>
/// Shared authorization decision for member endpoints (ARC-007 handoff).
/// Loads the current database state instead of trusting stale cookie role
/// claims: lockout, confirmation and roles are read from Identity on every
/// decision, so all replicas agree on the next authorized request. Already
/// issued tickets expire naturally; renewal after revocation is denied
/// because the decision no longer authorizes the holder.
/// </summary>
public sealed record ArchiveAccessDecision(
	bool IsAuthenticated,
	Guid AccountId,
	string Email,
	IReadOnlyList<string> Roles,
	bool IsActive,
	bool IsAdministrator);

public sealed class ArchiveAccessService(UserManager<ArchiveUser> users)
{
	public async Task<ArchiveAccessDecision?> GetDecisionAsync(ClaimsPrincipal principal)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null)
			return null;
		var lockedOut = await users.IsLockedOutAsync(user);
		var roles = await users.GetRolesAsync(user);
		var active = user.EmailConfirmed && !lockedOut && roles.Count > 0;
		return new ArchiveAccessDecision(
			IsAuthenticated: active,
			AccountId: user.Id,
			Email: user.Email ?? string.Empty,
			Roles: roles.OrderBy(r => r).ToArray(),
			IsActive: active,
			IsAdministrator: active && roles.Contains(ArchiveRoles.Administrator));
	}

	public async Task<ArchiveAccessDecision?> GetDecisionAsync(Guid accountId)
	{
		var user = await users.FindByIdAsync(accountId.ToString());
		if (user is null)
			return null;
		var lockedOut = await users.IsLockedOutAsync(user);
		var roles = await users.GetRolesAsync(user);
		var active = user.EmailConfirmed && !lockedOut && roles.Count > 0;
		return new ArchiveAccessDecision(
			IsAuthenticated: active,
			AccountId: user.Id,
			Email: user.Email ?? string.Empty,
			Roles: roles.OrderBy(r => r).ToArray(),
			IsActive: active,
			IsAdministrator: active && roles.Contains(ArchiveRoles.Administrator));
	}
}
