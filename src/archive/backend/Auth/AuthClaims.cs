using System.Security.Claims;

namespace Archive.Backend.Auth;

public static class AuthClaims
{
	public const string AccountId = "archive.account_id";
	public const string AuthenticatedAt = "archive.authenticated_at";
	public const string DisplayName = "archive.display_name";
	public const string AuthenticationMethod = "archive.auth_method";
	public const string EmailCode = "email_code";
	public const string Passkey = "passkey";
}

public static class AuthPolicies
{
	public const string Member = "ArchiveMember";
	public const string Editor = "ArchiveEditor";
	public const string Administrator = "ArchiveAdministrator";
}

public sealed record ArchiveCurrentUser(
	Guid AccountId,
	string Email,
	string? DisplayName,
	IReadOnlyList<string> Roles,
	DateTimeOffset AuthenticatedAt,
	string AuthenticationMethod)
{
	public bool IsFresh(TimeSpan window, DateTimeOffset now)
		=> AuthenticatedAt.Add(window) >= now;

	public bool IsInRole(string role) => Roles.Contains(role);
}

public static class ClaimsPrincipalExtensions
{
	public static ArchiveCurrentUser? ToArchiveCurrentUser(this ClaimsPrincipal principal, DateTimeOffset now)
	{
		var idValue = principal.FindFirst(AuthClaims.AccountId)?.Value;
		if (!Guid.TryParse(idValue, out var accountId))
			return null;
		var authenticatedAtValue = principal.FindFirst(AuthClaims.AuthenticatedAt)?.Value;
		if (!DateTimeOffset.TryParse(authenticatedAtValue, out var authenticatedAt))
			return null;
		var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
		var displayName = principal.FindFirst(AuthClaims.DisplayName)?.Value;
		var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
		if (roles.Length == 0)
			return null;
		// Sessions issued before ARC-011-1 carry no method claim; treat them
		// as email-code sessions so existing 30-day tickets keep working.
		var method = principal.FindFirst(AuthClaims.AuthenticationMethod)?.Value;
		if (method != AuthClaims.Passkey && method != AuthClaims.EmailCode)
			method = AuthClaims.EmailCode;
		return new ArchiveCurrentUser(accountId, email, displayName, roles, authenticatedAt, method);
	}
}
