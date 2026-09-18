using System.Diagnostics;
using System.Security.Claims;
using System.Diagnostics.Metrics;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

public enum PasskeyEnrollOutcome
{
	OptionsIssued,
	Enrolled,
	RateLimited,
	NotFresh,
	Invalid,
}

/// <summary>
/// ARC-011-1 passkey orchestration on top of Identity's WebAuthn support.
/// Identity owns ceremony cryptography (challenges, origin/RP validation,
/// assertion verification); this service owns the Archiv policy: enrollment
/// binds to the authenticated active member with fresh verification, login
/// applies the same active-account/membership checks as the email code, and
/// every issued session flows through the shared
/// <see cref="AuthSetup.SignInMemberAsync(SignInManager{ArchiveUser}, ArchiveUser, DateTimeOffset, string)"/>
/// path. Ceremonies are bounded in time through Identity's protected temp
/// cookie state; replay of stale credentials fails challenge verification.
/// </summary>
public sealed class PasskeyService(
	ArchiveDbContext db,
	UserManager<ArchiveUser> users,
	SignInManager<ArchiveUser> signIn,
	CurrentUserAccessor current,
	IOptions<AuthOptions> options,
	ILogger<PasskeyService> logger)
{
	private static readonly Meter AuthMeter = new(Extensions.MeterName);
	private static readonly Counter<long> PasskeyVerified = AuthMeter.CreateCounter<long>("archive.auth.verified");
	private static readonly Counter<long> PasskeyFailures = AuthMeter.CreateCounter<long>("archive.auth.failures");

	private readonly AuthOptions auth = options.Value;

	public async Task<IResult> MakeEnrollOptionsAsync(ClaimsPrincipal principal, CancellationToken token)
	{
		var user = await RequireActiveFreshUserAsync(principal);
		if (user is null)
			return Results.Problem(statusCode: 403, title: "Die Anmeldung ist zu alt. Bitte erneut anmelden.");
		var existing = await users.GetPasskeysAsync(user);
		if (existing.Count >= auth.PasskeyMaxPerAccount)
			return Results.Problem(statusCode: 409, title: "Maximale Anzahl an Passkeys erreicht.");
		var entity = new PasskeyUserEntity
		{
			Id = user.Id.ToString(),
			Name = user.Email ?? user.Id.ToString(),
			DisplayName = user.DisplayName ?? user.Email ?? user.Id.ToString(),
		};
		var optionsJson = await signIn.MakePasskeyCreationOptionsAsync(entity);
		return Results.Ok(new { creationOptions = optionsJson });
	}

	public async Task<IResult> VerifyEnrollAsync(
		ClaimsPrincipal principal, string? credentialJson, string? name, DateTimeOffset now, CancellationToken token)
	{
		var user = await RequireActiveFreshUserAsync(principal);
		if (user is null)
			return Results.Problem(statusCode: 403, title: "Die Anmeldung ist zu alt. Bitte erneut anmelden.");
		if (string.IsNullOrWhiteSpace(credentialJson))
			return Results.Problem(statusCode: 400, title: "Die Passkey-Registrierung ist ungültig.");
		var existing = await users.GetPasskeysAsync(user);
		if (existing.Count >= auth.PasskeyMaxPerAccount)
			return Results.Problem(statusCode: 409, title: "Maximale Anzahl an Passkeys erreicht.");
		var displayName = (name ?? string.Empty).Trim();
		if (displayName.Length > auth.PasskeyNameMaxLength)
			return Results.Problem(statusCode: 400, title: "Der Name des Passkeys ist zu lang.");
		PasskeyAttestationResult attestation;
		try
		{
			attestation = await signIn.PerformPasskeyAttestationAsync(credentialJson);
		}
		catch (InvalidOperationException exception)
		{
			// Identity surfaces malformed credential JSON and ceremonies
			// without valid temp state as InvalidOperationException; the
			// member-visible contract is a uniform 400 problem, never a 500.
			logger.LogInformation("Passkey attestation rejected ({Message})", exception.Message);
			PasskeyFailures.Add(1);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Registrierung ist ungültig.");
		}
		if (!attestation.Succeeded || attestation.Passkey is null)
		{
			PasskeyFailures.Add(1);
			logger.LogInformation("Passkey attestation failed ({FailureType})", attestation.Failure?.GetType().Name);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Registrierung ist ungültig.");
		}		attestation.Passkey.Name = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
		var stored = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
		if (!stored.Succeeded)
		{
			logger.LogInformation("Passkey registration rejected by Identity");
			return Results.Problem(statusCode: 400, title: "Die Passkey-Registrierung ist ungültig.");
		}
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = user.Id,
			ActorAccountId = user.Id,
			Action = MemberAdminActionType.PasskeyRegistered,
			OldRoles = string.Empty,
			NewRoles = string.Empty,
			OccurredAt = now,
		});
		await db.SaveChangesAsync(token);
		logger.LogInformation("Passkey registered for member {AccountId}", user.Id);
		return Results.Ok(new { registered = true });
	}

	/// <summary>Username-less login: request options with <c>user: null</c> for discoverable credentials.</summary>
	public async Task<IResult> MakeLoginOptionsAsync(CancellationToken token)
	{
		var optionsJson = await signIn.MakePasskeyRequestOptionsAsync(null);
		return Results.Ok(new { requestOptions = optionsJson });
	}

	public async Task<IResult> VerifyLoginAsync(string? credentialJson, DateTimeOffset now, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(credentialJson))
			return Results.Problem(statusCode: 400, title: "Die Passkey-Anmeldung ist ungültig.");
		PasskeyAssertionResult<ArchiveUser> assertion;
		try
		{
			assertion = await signIn.PerformPasskeyAssertionAsync(credentialJson);
		}
		catch (InvalidOperationException exception)
		{
			// Identity surfaces malformed credential JSON and ceremonies
			// without valid temp state (expired/replayed/absent challenge) as
			// InvalidOperationException; the member-visible contract is a
			// uniform 400 problem, never a 500.
			logger.LogInformation("Passkey assertion rejected ({Message})", exception.Message);
			PasskeyFailures.Add(1);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Anmeldung ist ungültig.");
		}
		if (!assertion.Succeeded || assertion.User is null || assertion.Passkey is null)
		{
			PasskeyFailures.Add(1);
			logger.LogInformation("Passkey assertion failed ({FailureType})", assertion.Failure?.GetType().Name);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Anmeldung ist ungültig.");
		}
		var user = assertion.User;
		// A successful assertion alone must never bypass membership rules:
		// lockout, deactivation and role checks decide session issuance, and
		// unconfirmed accounts keep the uniform email-code failure.
		if (!user.EmailConfirmed || await users.IsLockedOutAsync(user))
		{
			PasskeyFailures.Add(1);
			logger.LogInformation("Passkey login denied by account state for {AccountId}", user.Id);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Anmeldung ist ungültig.");
		}
		var roles = await users.GetRolesAsync(user);
		if (roles.Count == 0)
		{
			PasskeyFailures.Add(1);
			logger.LogInformation("Passkey login denied by membership for {AccountId}", user.Id);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Anmeldung ist ungültig.");
		}
		// Persist the updated passkey (sign counter, backup flags) from the
		// assertion; Identity's anti-replay relies on this being stored.
		var stored = await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);
		if (!stored.Succeeded)
		{
			PasskeyFailures.Add(1);
			logger.LogInformation("Passkey assertion persistence failed for {AccountId}", user.Id);
			return Results.Problem(statusCode: 400, title: "Die Passkey-Anmeldung ist ungültig.");
		}
		await AuthSetup.SignInMemberAsync(signIn, user, now, AuthClaims.Passkey);
		PasskeyVerified.Add(1);
		logger.LogInformation("Passkey login verified for member {AccountId}", user.Id);
		return Results.Ok(new
		{
			message = "Anmeldung erfolgreich.",
			accountId = user.Id,
			displayName = user.DisplayName ?? user.Email,
			roles = roles.Distinct().ToArray(),
		});
	}

	public async Task<IResult> ListPasskeysAsync(ClaimsPrincipal principal)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null)
			return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
		var passkeys = await users.GetPasskeysAsync(user);
		return Results.Ok(new
		{
			passkeys = passkeys.Select(p => new
			{
				credentialId = Convert.ToBase64String(p.CredentialId),
				name = p.Name,
				createdAt = p.CreatedAt,
			}).ToArray(),
		});
	}

	public async Task<IResult> RenamePasskeyAsync(
		ClaimsPrincipal principal, string? credentialId, string? name, DateTimeOffset now, CancellationToken token)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null)
			return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
		if (!TryDecodeCredentialId(credentialId, out var id))
			return Results.Problem(statusCode: 400, title: "Der Passkey wurde nicht gefunden.");
		var displayName = (name ?? string.Empty).Trim();
		if (displayName.Length == 0 || displayName.Length > auth.PasskeyNameMaxLength)
			return Results.Problem(statusCode: 400, title: "Der Name des Passkeys ist ungültig.");
		var passkey = await users.GetPasskeyAsync(user, id);
		if (passkey is null)
			return Results.Problem(statusCode: 404, title: "Der Passkey wurde nicht gefunden.");
		passkey.Name = displayName;
		var stored = await users.AddOrUpdatePasskeyAsync(user, passkey);
		if (!stored.Succeeded)
			return Results.Problem(statusCode: 400, title: "Der Passkey konnte nicht umbenannt werden.");
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = user.Id,
			ActorAccountId = user.Id,
			Action = MemberAdminActionType.PasskeyRenamed,
			OldRoles = string.Empty,
			NewRoles = string.Empty,
			Note = displayName,
			OccurredAt = now,
		});
		await db.SaveChangesAsync(token);
		return Results.Ok(new { renamed = true });
	}

	public async Task<IResult> RemovePasskeyAsync(
		ClaimsPrincipal principal, string? credentialId, DateTimeOffset now, CancellationToken token)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null)
			return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
		if (!TryDecodeCredentialId(credentialId, out var id))
			return Results.Problem(statusCode: 400, title: "Der Passkey wurde nicht gefunden.");
		var passkey = await users.GetPasskeyAsync(user, id);
		if (passkey is null)
			return Results.Problem(statusCode: 404, title: "Der Passkey wurde nicht gefunden.");
		var removed = await users.RemovePasskeyAsync(user, id);
		if (!removed.Succeeded)
			return Results.Problem(statusCode: 400, title: "Der Passkey konnte nicht entfernt werden.");
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = user.Id,
			ActorAccountId = user.Id,
			Action = MemberAdminActionType.PasskeyRemoved,
			OldRoles = string.Empty,
			NewRoles = string.Empty,
			OccurredAt = now,
		});
		await db.SaveChangesAsync(token);
		logger.LogInformation("Passkey removed for member {AccountId}", user.Id);
		return Results.Ok(new { removed = true });
	}

	/// <summary>
	/// Active-member + fresh-verification gate for ceremony enrollment. The
	/// user is derived exclusively from the authenticated principal; email
	/// ownership verification stays distinct from authentication freshness.
	/// </summary>
	private async Task<ArchiveUser?> RequireActiveFreshUserAsync(ClaimsPrincipal principal)
	{
		var user = await users.GetUserAsync(principal);
		if (user is null || !user.EmailConfirmed || await users.IsLockedOutAsync(user))
			return null;
		var roles = await users.GetRolesAsync(user);
		if (roles.Count == 0)
			return null;
		return current.RequireFreshVerification() ? user : null;
	}

	private static bool TryDecodeCredentialId(string? credentialId, out byte[] id)
	{
		id = [];
		if (string.IsNullOrWhiteSpace(credentialId) || credentialId.Length > 2048)
			return false;
		try
		{
			id = Convert.FromBase64String(credentialId);
			return id.Length > 0 && id.Length <= 1024;
		}
		catch (FormatException)
		{
			return false;
		}
	}
}
