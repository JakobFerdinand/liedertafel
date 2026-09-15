using System.Diagnostics;
using System.Diagnostics.Metrics;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Auth;

public enum InviteOutcome
{
	/// <summary>New invitation created and mail accepted by the sender.</summary>
	Invited,
	/// <summary>Existing pending invitation resent; role left unchanged.</summary>
	Resent,
	/// <summary>Address already belongs to an active member; nothing changed.</summary>
	AlreadyActive,
	/// <summary>Pending invitation has a different role; nothing changed.</summary>
	RoleConflict,
	/// <summary>Account is deactivated (locked out); nothing changed.</summary>
	Deactivated,
	/// <summary>Mail sender rejected the message; invitation stays retryable.</summary>
	MailFailed,
}

public enum ResendOutcome
{
	Resent,
	AlreadyAccepted,
	Deactivated,
	NotFound,
	MailFailed,
}

public sealed record MemberListItem(
	Guid AccountId,
	string Email,
	string? DisplayName,
	IReadOnlyList<string> Roles,
	string Status,
	Guid? InvitationId,
	DateTimeOffset? InvitedAt,
	Guid? InvitedByAccountId,
	DateTimeOffset? AcceptedAt,
	DateTimeOffset? LastInvitationSentAt,
	string InvitationMailStatus);

/// <summary>
/// Administrator invitation logic on top of ASP.NET Core Identity. Creates
/// unconfirmed users with the intended role, sends the German invitation mail,
/// and keeps a retryable invitation row when the sender rejects the message.
/// Repeated submissions never duplicate members and never change roles
/// silently. Acceptance is stamped by the email-code verification flow and
/// always links to the stable account ID.
/// </summary>
public sealed class MemberInvitationService(
	ArchiveDbContext db,
	UserManager<ArchiveUser> users,
	RoleManager<ArchiveRole> roles,
	IArchiveMailSender mail,
	ILogger<MemberInvitationService> logger)
{
	private static readonly Meter MembersMeter = new(Extensions.MeterName);
	private static readonly Counter<long> MembersInvited = MembersMeter.CreateCounter<long>("archive.members.invited");
	private static readonly Counter<long> MembersResent = MembersMeter.CreateCounter<long>("archive.members.resent");
	private static readonly Counter<long> MembersFailures = MembersMeter.CreateCounter<long>("archive.members.failures");

	public const string MailAcceptedMessage =
		"Einladung erstellt. Die E-Mail wurde zum Versand angenommen; die Zustellung wird nicht bestätigt.";

	public const string MailResentMessage =
		"Einladung erneut zum Versand übergeben. Die Zustellung wird nicht bestätigt.";

	public const string MailFailedMessage =
		"Die E-Mail konnte nicht übergeben werden. Die Einladung bleibt gespeichert und kann erneut gesendet werden.";

	public const string AlreadyActiveMessage =
		"Diese Adresse ist bereits registriert. Es wurde keine neue Einladung erstellt und die Rolle nicht geändert.";

	public const string RoleConflictMessage =
		"Für diese Adresse liegt bereits eine Einladung mit einer anderen Rolle vor. Die Rolle wurde nicht geändert.";

	public const string DeactivatedMessage =
		"Dieses Konto ist deaktiviert. Reaktivierung gehört zur Mitgliederverwaltung, nicht zur Einladung.";

	public const string AlreadyAcceptedMessage =
		"Diese Einladung wurde bereits angenommen. Es ist keine erneute Einladung nötig.";

	public const string FreshVerificationMessage =
		"Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich.";

	public const string ForbiddenMessage = "Keine Berechtigung für die Mitgliederverwaltung.";

	public async Task<IReadOnlyList<MemberListItem>> ListAsync(CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.list", ActivityKind.Internal);
		var allUsers = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(token);
		var invitations = await db.MemberInvitations.AsNoTracking().ToListAsync(token);
		var byUserId = invitations.ToDictionary(i => i.UserId);
		var result = new List<MemberListItem>(allUsers.Count);
		foreach (var user in allUsers)
		{
			var userRoles = await users.GetRolesAsync(user);
			var lockedOut = await users.IsLockedOutAsync(user);
			var status = lockedOut ? "deactivated" : user.EmailConfirmed ? "active" : "invited";
			byUserId.TryGetValue(user.Id, out var invitation);
			result.Add(new MemberListItem(
				user.Id,
				user.Email ?? string.Empty,
				user.DisplayName,
				userRoles.OrderBy(r => r).ToArray(),
				status,
				invitation?.Id,
				invitation?.InvitedAt,
				invitation?.InvitedByAccountId,
				invitation?.AcceptedAt,
				invitation?.LastSentAt,
				invitation is null ? "none" : invitation.MailStatus switch
				{
					InvitationMailStatus.Sent => "sent",
					InvitationMailStatus.Failed => "failed",
					_ => "pending",
				}));
		}
		activity?.SetTag("members.count", result.Count);
		return result;
	}

	public async Task<(InviteOutcome Outcome, Guid? AccountId, Guid? InvitationId)> InviteAsync(
		string normalizedEmail, string rawEmail, string? displayName, string role,
		Guid inviterAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.invite", ActivityKind.Internal);
		if (!ArchiveRoles.All.Contains(role))
		{
			MembersFailures.Add(1);
			throw new InvalidOperationException($"Unknown role '{role}'.");
		}
		if (!await roles.RoleExistsAsync(role))
		{
			try
			{
				var created = await roles.CreateAsync(new ArchiveRole(role));
				if (!created.Succeeded && !await roles.RoleExistsAsync(role))
					throw new InvalidOperationException($"Role '{role}' could not be created.");
			}
			catch (DbUpdateException)
			{
				if (!await roles.RoleExistsAsync(role))
					throw;
			}
		}

		var existing = await users.FindByEmailAsync(normalizedEmail);
		if (existing is not null)
		{
			if (await users.IsLockedOutAsync(existing))
			{
				MembersFailures.Add(1);
				activity?.SetTag("members.result", "deactivated");
				return (InviteOutcome.Deactivated, existing.Id, null);
			}
			var existingRoles = await users.GetRolesAsync(existing);
			if (existing.EmailConfirmed)
			{
				MembersFailures.Add(1);
				activity?.SetTag("members.result", "already_active");
				logger.LogInformation("Invitation skipped: address already active");
				return (InviteOutcome.AlreadyActive, existing.Id, null);
			}
			if (!existingRoles.Contains(role))
			{
				MembersFailures.Add(1);
				activity?.SetTag("members.result", "role_conflict");
				logger.LogInformation("Invitation skipped: pending invitation has a different role");
				return (InviteOutcome.RoleConflict, existing.Id, null);
			}
			// Pending invitation with the same role: resend without changes.
			var resend = await ResendPendingAsync(existing, inviterAccountId, now, token);
			if (!resend.Mailed)
			{
				MembersFailures.Add(1);
				activity?.SetTag("members.result", "mail_failed");
				return (InviteOutcome.MailFailed, existing.Id, resend.InvitationId);
			}
			MembersResent.Add(1);
			activity?.SetTag("members.result", "resent");
			logger.LogInformation("Invitation resent");
			return (InviteOutcome.Resent, existing.Id, resend.InvitationId);
		}

		var trimmedDisplay = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
		var user = new ArchiveUser
		{
			UserName = rawEmail.Trim(),
			Email = rawEmail.Trim(),
			DisplayName = trimmedDisplay,
			EmailConfirmed = false,
		};
		try
		{
			var created = await users.CreateAsync(user);
			if (!created.Succeeded)
			{
				// A concurrent invite may have won the race; re-read and let
				// the duplicate path decide without duplicating or changing roles.
				var raced = await users.FindByEmailAsync(normalizedEmail);
				if (raced is null)
				{
					MembersFailures.Add(1);
					throw new InvalidOperationException(
						"Identity operation failed: " + string.Join("; ", created.Errors.Select(e => e.Description)));
				}
				return await HandleRacedUserAsync(raced, role, inviterAccountId, now, token, activity);
			}
		}
		catch (DbUpdateException)
		{
			var raced = await users.FindByEmailAsync(normalizedEmail);
			if (raced is null)
				throw;
			return await HandleRacedUserAsync(raced, role, inviterAccountId, now, token, activity);
		}

		if (!await users.IsInRoleAsync(user, role))
		{
			try
			{
				var inRole = await users.AddToRoleAsync(user, role);
				if (!inRole.Succeeded && !await users.IsInRoleAsync(user, role))
					throw new InvalidOperationException("Role could not be assigned.");
			}
			catch (DbUpdateException)
			{
				if (!await users.IsInRoleAsync(user, role))
					throw;
			}
		}

		var invitation = new MemberInvitation
		{
			UserId = user.Id,
			NormalizedEmail = normalizedEmail,
			Role = role,
			DisplayName = trimmedDisplay,
			InvitedByAccountId = inviterAccountId,
			InvitedAt = now,
			LastSentByAccountId = inviterAccountId,
		};
		db.MemberInvitations.Add(invitation);
		await db.SaveChangesAsync(token);

		var mailed = await TrySendInvitationMailAsync(invitation, rawEmail.Trim(), trimmedDisplay, role, now, token);
		if (!mailed)
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "mail_failed");
			return (InviteOutcome.MailFailed, user.Id, invitation.Id);
		}
		MembersInvited.Add(1);
		activity?.SetTag("members.result", "invited");
		logger.LogInformation("Member invited (role {Role})", role);
		return (InviteOutcome.Invited, user.Id, invitation.Id);
	}

	public async Task<(ResendOutcome Outcome, Guid? AccountId, Guid? InvitationId)> ResendAsync(
		string normalizedEmail, Guid senderAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.resend", ActivityKind.Internal);
		var user = await users.FindByEmailAsync(normalizedEmail);
		if (user is null)
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "not_found");
			return (ResendOutcome.NotFound, null, null);
		}
		if (await users.IsLockedOutAsync(user))
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "deactivated");
			return (ResendOutcome.Deactivated, user.Id, null);
		}
		if (user.EmailConfirmed)
		{
			var invitation = await db.MemberInvitations.FirstOrDefaultAsync(i => i.UserId == user.Id, token);
			if (invitation is not null && invitation.AcceptedAt is null)
			{
				invitation.AcceptedAt = now;
				await SaveInvitationResilientAsync(token);
			}
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "already_accepted");
			return (ResendOutcome.AlreadyAccepted, user.Id, invitation?.Id);
		}
		var pending = await ResendPendingAsync(user, senderAccountId, now, token);
		if (!pending.Mailed)
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "mail_failed");
			return (ResendOutcome.MailFailed, user.Id, pending.InvitationId);
		}
		MembersResent.Add(1);
		activity?.SetTag("members.result", "resent");
		logger.LogInformation("Invitation resent");
		return (ResendOutcome.Resent, user.Id, pending.InvitationId);
	}

	/// <summary>
	/// Stamps invitation acceptance after the email-code flow confirms the
	/// address. Links acceptance to the stable account ID; never changes roles.
	/// </summary>
	public async Task MarkAcceptedAsync(Guid userId, DateTimeOffset now, CancellationToken token)
	{
		var invitation = await db.MemberInvitations.FirstOrDefaultAsync(i => i.UserId == userId, token);
		if (invitation is null || invitation.AcceptedAt is not null)
			return;
		invitation.AcceptedAt = now;
		await SaveInvitationResilientAsync(token);
	}

	private async Task<(InviteOutcome Outcome, Guid? AccountId, Guid? InvitationId)> HandleRacedUserAsync(
		ArchiveUser raced, string role, Guid inviterAccountId, DateTimeOffset now,
		CancellationToken token, Activity? activity)
	{
		if (await users.IsLockedOutAsync(raced))
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "deactivated");
			return (InviteOutcome.Deactivated, raced.Id, null);
		}
		var racedRoles = await users.GetRolesAsync(raced);
		if (raced.EmailConfirmed)
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "already_active");
			return (InviteOutcome.AlreadyActive, raced.Id, null);
		}
		if (!racedRoles.Contains(role))
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "role_conflict");
			return (InviteOutcome.RoleConflict, raced.Id, null);
		}
		var racedResend = await ResendPendingAsync(raced, inviterAccountId, now, token);
		if (!racedResend.Mailed)
		{
			MembersFailures.Add(1);
			activity?.SetTag("members.result", "mail_failed");
			return (InviteOutcome.MailFailed, raced.Id, racedResend.InvitationId);
		}
		MembersResent.Add(1);
		activity?.SetTag("members.result", "resent");
		logger.LogInformation("Invitation resent");
		return (InviteOutcome.Resent, raced.Id, racedResend.InvitationId);
	}

	private sealed record PendingResend(bool Mailed, Guid? InvitationId);

	private async Task<PendingResend> ResendPendingAsync(
		ArchiveUser user, Guid senderAccountId, DateTimeOffset now, CancellationToken token)
	{
		var invitation = await db.MemberInvitations.FirstOrDefaultAsync(i => i.UserId == user.Id, token);
		if (invitation is null)
		{
			// User exists without an invitation row (seed/bootstrap) and is
			// still unconfirmed (callers guarantee this): record attribution
			// now so acceptance history stays complete.
			var userRoles = await users.GetRolesAsync(user);
			invitation = new MemberInvitation
			{
				UserId = user.Id,
				NormalizedEmail = user.NormalizedEmail ?? AuthSecurity.NormalizeEmail(user.Email ?? string.Empty),
				Role = userRoles.OrderBy(r => r).FirstOrDefault() ?? ArchiveRoles.Member,
				DisplayName = user.DisplayName,
				InvitedByAccountId = senderAccountId,
				InvitedAt = now,
				LastSentByAccountId = senderAccountId,
				AcceptedAt = user.EmailConfirmed ? now : null,
			};
			db.MemberInvitations.Add(invitation);
			await db.SaveChangesAsync(token);
		}
		var address = user.Email ?? invitation.NormalizedEmail;
		var mailed = await TrySendInvitationMailAsync(invitation, address, user.DisplayName, invitation.Role, now, token);
		invitation.LastSentByAccountId = senderAccountId;
		await SaveInvitationResilientAsync(token);
		return new PendingResend(mailed, invitation.Id);
	}

	private async Task<bool> TrySendInvitationMailAsync(
		MemberInvitation invitation, string address, string? displayName, string role,
		DateTimeOffset now, CancellationToken token)
	{
		try
		{
			await mail.SendInvitationAsync(address, displayName, role, token);
		}
		catch (Exception exception)
		{
			invitation.MailStatus = InvitationMailStatus.Failed;
			invitation.LastError = MailFailedMessage;
			await SaveInvitationResilientAsync(token);
			logger.LogError("Invitation mail failed ({ExceptionType})", exception.GetType().Name);
			return false;
		}
		invitation.MailStatus = InvitationMailStatus.Sent;
		invitation.LastError = null;
		invitation.LastSentAt = now;
		await SaveInvitationResilientAsync(token);
		return true;
	}

	private async Task SaveInvitationResilientAsync(CancellationToken token)
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
			if (entry.Entity is not MemberInvitation)
				entry.State = EntityState.Detached;
		}
		await db.SaveChangesAsync(token);
	}
}
