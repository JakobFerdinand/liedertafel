using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

/// <summary>
/// Stable account claims for ARC-011-1. Identity's security-stamp validator
/// rebuilds principals from this factory, so account ID and display name
/// survive refresh without per-request database reads. Session-specific
/// claims (<see cref="AuthClaims.AuthenticatedAt"/>,
/// <see cref="AuthClaims.AuthenticationMethod"/>) are NOT issued here; they
/// are attached at sign-in and preserved across refresh via
/// <c>SecurityStampValidatorOptions.OnRefreshingPrincipal</c> without
/// resetting the timestamp.
/// </summary>
public sealed class ArchiveClaimsFactory(
	UserManager<ArchiveUser> userManager,
	RoleManager<ArchiveRole> roleManager,
	IOptions<IdentityOptions> options)
	: UserClaimsPrincipalFactory<ArchiveUser, ArchiveRole>(userManager, roleManager, options)
{
	protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ArchiveUser user)
	{
		var identity = await base.GenerateClaimsAsync(user);
		identity.AddClaim(new Claim(AuthClaims.AccountId, user.Id.ToString()));
		var displayName = user.DisplayName ?? user.Email;
		if (!string.IsNullOrWhiteSpace(displayName))
			identity.AddClaim(new Claim(AuthClaims.DisplayName, displayName));
		return identity;
	}
}
