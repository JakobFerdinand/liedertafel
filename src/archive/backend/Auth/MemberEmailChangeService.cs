using System.Diagnostics;
using System.Diagnostics.Metrics;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

public enum EmailChangeRequestOutcome
{
	Requested,
	NotFound,
	SameAddress,
	Collision,
	MailFailed,
}

public enum EmailChangeConfirmOutcome
{
	Confirmed,
	NotFound,
	Invalid,
	Collision,
}

/// <summary>
/// Verified administrator email change (ARC-008). The stable account ID is
/// retained: confirmation rewrites <c>Email</c>/<c>UserName</c> on the same
/// user row, follows <c>member_invitations.NormalizedEmail</c>, deletes
/// obsolete sign-in challenges and consumes pending change rows. Change codes
/// are bound to the target account and the new address, so receiving the mail
/// alone can never attach the address to another account. Confirmation bumps
/// the security stamp and audits <c>EmailChanged</c>; the per-request session
/// guard then kills the target's open sessions until a fresh code at the new
/// address is used.
/// </summary>
public sealed class MemberEmailChangeService(
	ArchiveDbContext db,
	UserManager<ArchiveUser> users,
	IArchiveMailSender mail,
	IOptions<AuthOptions> options,
	ILogger<MemberEmailChangeService> logger)
{
	private static readonly Meter EmailChangeMeter = new(Extensions.MeterName);
	private static readonly Counter<long> EmailChangeRequested = EmailChangeMeter.CreateCounter<long>("archive.members.email_change_requested");
	private static readonly Counter<long> EmailChanged = EmailChangeMeter.CreateCounter<long>("archive.members.email_changed");
	private static readonly Counter<long> EmailChangeFailures = EmailChangeMeter.CreateCounter<long>("archive.members.email_change_failures");

	private readonly AuthOptions auth = options.Value;

	public const string CodeSentMessage =
		"Bestätigungscode an die neue Adresse gesendet. Die Änderung wird erst mit dem Code wirksam.";

	public const string ConfirmedMessage =
		"Adresse geändert. Frühere Sitzungen des Kontos wurden abgemeldet; bitte mit der neuen Adresse erneut per Code anmelden. Kennung und Verlauf bleiben erhalten.";

	public const string CollisionMessage =
		"Diese Adresse gehört bereits zu einem anderen Konto. Es wurde nichts geändert und keine E-Mail gesendet.";

	public const string SameAddressMessage =
		"Die neue Adresse ist mit der bisherigen identisch.";

	public const string InvalidCodeMessage =
		"Der Code ist ungültig oder abgelaufen.";

	public const string NotFoundMessage =
		"Für diese Kennung wurde kein Mitglied gefunden.";

	public const string MailFailedMessage =
		"Die E-Mail konnte nicht übergeben werden. Die Änderung bleibt ausstehend und kann erneut angefordert werden.";

	public async Task<(EmailChangeRequestOutcome Outcome, Guid? AccountId)> RequestAsync(
		Guid targetAccountId, string normalizedNewEmail, string rawNewEmail,
		Guid actorAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.email_change.request", ActivityKind.Internal);
		var user = await users.FindByIdAsync(targetAccountId.ToString());
		if (user is null)
		{
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "not_found");
			return (EmailChangeRequestOutcome.NotFound, null);
		}
		if (string.Equals(user.NormalizedEmail, normalizedNewEmail, StringComparison.Ordinal))
		{
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "same_address");
			return (EmailChangeRequestOutcome.SameAddress, user.Id);
		}
		var collision = await users.FindByEmailAsync(normalizedNewEmail);
		if (collision is not null && collision.Id != user.Id)
		{
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "collision");
			logger.LogInformation("Email change refused: new address already belongs to another account");
			return (EmailChangeRequestOutcome.Collision, user.Id);
		}

		// A newer request supersedes earlier pendings for the same account.
		var superseded = await db.MemberEmailChanges
			.Where(c => c.UserId == user.Id && c.ConsumedAt == null)
			.ToListAsync(token);
		foreach (var previous in superseded)
			previous.ConsumedAt = now;

		var code = AuthSecurity.GenerateCode(auth.CodeLength);
		var salt = AuthSecurity.NewSalt();
		db.MemberEmailChanges.Add(new MemberEmailChange
		{
			UserId = user.Id,
			NormalizedNewEmail = normalizedNewEmail,
			CodeHash = AuthSecurity.HashCode(code, salt),
			Salt = salt,
			CreatedAt = now,
			ExpiresAt = now.Add(auth.CodeLifetime),
			RequestedByAccountId = actorAccountId,
		});
		await db.SaveChangesAsync(token);

		try
		{
			await mail.SendEmailChangeCodeAsync(rawNewEmail.Trim(), code, auth.CodeLifetime, token);
		}
		catch (Exception exception)
		{
			logger.LogError("Email change code mail failed ({ExceptionType})", exception.GetType().Name);
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "mail_failed");
			return (EmailChangeRequestOutcome.MailFailed, user.Id);
		}

		EmailChangeRequested.Add(1);
		activity?.SetTag("members.result", "requested");
		logger.LogInformation("Email change code requested");
		return (EmailChangeRequestOutcome.Requested, user.Id);
	}

	public async Task<(EmailChangeConfirmOutcome Outcome, ArchiveUser? User)> ConfirmAsync(
		Guid targetAccountId, string normalizedNewEmail, string rawNewEmail, string candidateCode,
		Guid actorAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.email_change.confirm", ActivityKind.Internal);
		var user = await users.FindByIdAsync(targetAccountId.ToString());
		if (user is null)
		{
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "not_found");
			return (EmailChangeConfirmOutcome.NotFound, null);
		}
		var code = AuthSecurity.NormalizeCode(candidateCode);
		if (code.Length != auth.CodeLength)
		{
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "invalid");
			return (EmailChangeConfirmOutcome.Invalid, null);
		}
		var challenge = await db.MemberEmailChanges
			.Where(c => c.UserId == user.Id && c.NormalizedNewEmail == normalizedNewEmail
				&& c.ConsumedAt == null && c.ExpiresAt > now)
			.OrderByDescending(c => c.CreatedAt)
			.FirstOrDefaultAsync(token);
		if (challenge is null || challenge.AttemptCount >= auth.MaxVerifyAttemptsPerCode)
		{
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "invalid");
			logger.LogInformation("Email change confirmation failed");
			return (EmailChangeConfirmOutcome.Invalid, null);
		}
		if (!AuthSecurity.VerifyCode(code, challenge.Salt, challenge.CodeHash))
		{
			challenge.AttemptCount++;
			challenge.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				db.Entry(challenge).State = EntityState.Detached;
			}
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "invalid");
			logger.LogInformation("Email change confirmation failed");
			return (EmailChangeConfirmOutcome.Invalid, null);
		}

		// The code proved ownership of the new address. Re-check the
		// collision: another account may have taken the address meanwhile.
		// Receiving this mail alone never attached it elsewhere because
		// change codes are bound to this account and the sign-in flow has no
		// user row for the unassigned address.
		var collision = await users.FindByEmailAsync(normalizedNewEmail);
		if (collision is not null && collision.Id != user.Id)
		{
			challenge.ConsumedAt = now;
			challenge.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				db.Entry(challenge).State = EntityState.Detached;
			}
			EmailChangeFailures.Add(1);
			activity?.SetTag("members.result", "collision");
			logger.LogInformation("Email change refused: new address already belongs to another account");
			return (EmailChangeConfirmOutcome.Collision, user);
		}

		challenge.ConsumedAt = now;
		challenge.RowVersion++;

		var oldRoles = await users.GetRolesAsync(user);
		user.Email = rawNewEmail.Trim();
		user.UserName = rawNewEmail.Trim();
		if (!await TryUpdateAsync(users, user))
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetAccountId.ToString());
			if (user is null)
			{
				EmailChangeFailures.Add(1);
				activity?.SetTag("members.result", "not_found");
				return (EmailChangeConfirmOutcome.NotFound, null);
			}
			if (!string.Equals(user.NormalizedEmail, normalizedNewEmail, StringComparison.Ordinal))
			{
				EmailChangeFailures.Add(1);
				activity?.SetTag("members.result", "concurrency_conflict");
				throw new InvalidOperationException("Membership change conflicted; please retry.");
			}
		}

		// Keep the invitation history linked to the stable account ID.
		var invitation = await db.MemberInvitations.FirstOrDefaultAsync(i => i.UserId == user.Id, token);
		if (invitation is not null)
			invitation.NormalizedEmail = normalizedNewEmail;

		// Obsolete challenges must die: codes mailed to the old address (or
		// superseded change codes) can never complete a later sign-in.
		var obsoleteSignIns = await db.SignInChallenges.Where(c => c.UserId == user.Id).ToListAsync(token);
		db.SignInChallenges.RemoveRange(obsoleteSignIns);
		var obsoleteChanges = await db.MemberEmailChanges
			.Where(c => c.UserId == user.Id && c.Id != challenge.Id && c.ConsumedAt == null)
			.ToListAsync(token);
		foreach (var obsolete in obsoleteChanges)
			obsolete.ConsumedAt = now;

		// Kill open sessions server-side; the per-request guard below keeps
		// tickets issued before this change dead until a fresh code at the
		// new address is used.
		try
		{
			await users.UpdateSecurityStampAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetAccountId.ToString());
		}

		var roles = user is null ? oldRoles : await users.GetRolesAsync(user);
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = targetAccountId,
			ActorAccountId = actorAccountId,
			Action = MemberAdminActionType.EmailChanged,
			OldRoles = string.Join(",", oldRoles.OrderBy(r => r)),
			NewRoles = string.Join(",", roles.OrderBy(r => r)),
			OccurredAt = now,
		});
		await SaveAuditResilientAsync(token);
		EmailChanged.Add(1);
		activity?.SetTag("members.result", "confirmed");
		logger.LogInformation("Member email changed (roles preserved)");
		return (EmailChangeConfirmOutcome.Confirmed, user);
	}

	private static async Task<bool> TryUpdateAsync(UserManager<ArchiveUser> users, ArchiveUser user)
	{
		try
		{
			return (await users.UpdateAsync(user)).Succeeded;
		}
		catch (DbUpdateConcurrencyException)
		{
			return false;
		}
		catch (DbUpdateException)
		{
			return false;
		}
	}

	private async Task SaveAuditResilientAsync(CancellationToken token)
	{
		try
		{
			await db.SaveChangesAsync(token);
			return;
		}
		catch (DbUpdateConcurrencyException)
		{
		}
		catch (DbUpdateException)
		{
		}
		foreach (var entry in db.ChangeTracker.Entries().ToArray())
		{
			if (entry.Entity is MemberAdminAction or MemberEmailChange)
				continue;
			entry.State = EntityState.Detached;
		}
		await db.SaveChangesAsync(token);
	}
}
