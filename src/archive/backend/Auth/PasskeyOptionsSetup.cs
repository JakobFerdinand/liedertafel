using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

/// <summary>
/// ARC-011-1 passkey ceremony configuration. The RP ID is a stable
/// production setting (never derived from request host headers); trusted
/// origins are an exact allow list. Discovery credentials with required
/// user verification enable the username-less login. Outside Development
/// the relying-party ID is mandatory so cookies and ceremonies agree.
/// </summary>
public sealed class PasskeyOptionsSetup(IOptions<AuthOptions> authOptions, IHostEnvironment environment)
	: IConfigureOptions<IdentityPasskeyOptions>
{
	public void Configure(IdentityPasskeyOptions passkeys)
	{
		var auth = authOptions.Value;
		// The production RP-ID requirement is enforced at web-host startup
		// (AddArchiveAuth); finite operator commands never host ceremonies.
		// Dev fallback: "localhost" so the WebAuthn client accepts ceremonies
		// served from the loopback dev origin (rp.id must match the origin
		// domain; request-host fallback like 127.0.0.1 would break pages
		// served via localhost and vice versa).
		passkeys.ServerDomain = string.IsNullOrWhiteSpace(auth.PasskeyRelyingPartyId)
			? (environment.IsDevelopment() ? "localhost" : null)
			: auth.PasskeyRelyingPartyId.Trim().ToLowerInvariant();
		passkeys.UserVerificationRequirement = "required";
		passkeys.ResidentKeyRequirement = "required";
		var allowed = auth.PasskeyOrigins
			.Where(o => !string.IsNullOrWhiteSpace(o))
			.Select(o => o.Trim().TrimEnd('/'))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		passkeys.ValidateOrigin = context =>
		{
			var origin = context.Origin?.Trim().TrimEnd('/');
			if (string.IsNullOrEmpty(origin))
				return ValueTask.FromResult(false);
			if (allowed.Any(a => string.Equals(a, origin, StringComparison.OrdinalIgnoreCase)))
				return ValueTask.FromResult(true);
			// Development fallback for the Aspire loopback setup when no
			// explicit origins are configured: trust loopback hosts only,
			// never arbitrary request headers.
			if (environment.IsDevelopment() && allowed.Length == 0
				&& Uri.TryCreate(origin, UriKind.Absolute, out var uri)
				&& (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]" || uri.Host == "::1"))
				return ValueTask.FromResult(true);
			return ValueTask.FromResult(false);
		};
	}
}
