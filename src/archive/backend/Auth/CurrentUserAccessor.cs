using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

/// <summary>
/// Shared current-user and fresh-verification contract for later admin slices
/// (ARC-006 invitations, ARC-007 revocation, ARC-008 repair). Sensitive actions
/// require <see cref="RequireFreshVerification"/> within the configured window.
/// </summary>
public sealed class CurrentUserAccessor(IHttpContextAccessor httpContext, IOptions<AuthOptions> options, TimeProvider time)
{
	private readonly AuthOptions auth = options.Value;

	public ArchiveCurrentUser? Current
		=> httpContext.HttpContext?.User.ToArchiveCurrentUser(time.GetUtcNow());

	public bool IsFreshVerification()
	{
		var current = Current;
		return current is not null && current.IsFresh(auth.FreshVerificationWindow, time.GetUtcNow());
	}

	/// <summary>Guard for sensitive account/role changes: returns false unless freshly re-verified.</summary>
	public bool RequireFreshVerification() => IsFreshVerification();
}
