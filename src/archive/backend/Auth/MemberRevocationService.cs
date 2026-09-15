using System.Diagnostics;
using System.Diagnostics.Metrics;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Auth;

public enum DeactivateOutcome
{
	Deactivated,
	NotFound,
	AlreadyDeactivated,
	LastAdministrator,
}

public enum ReactivateOutcome
{
	Reactivated,
	NotFound,
	AlreadyActive,
}

public enum RoleChangeOutcome
{
	Changed,
	Unchanged,
	NotFound,
	LastAdministrator,
}

/// <summary>
/// Administrator membership revocation (ARC-007). Deactivation uses Identity
/// lockout so the stable account ID, invitation/acceptance history and archive
/// content are preserved; reactivation restores the same row. Every change
/// bumps the security stamp so previously issued tickets fail validation on
/// their next use instead of staying valid for 30 days, and appends an
/// audit row recording the actor. The last active administrator can neither
/// be deactivated nor demoted; repair of a lost administration belongs to
/// the ARC-008 maintainer path, not to a session-revival shortcut.
/// </summary>
public sealed class MemberRevocationService(
	ArchiveDbContext db,
	UserManager<ArchiveUser> users,
	ILogger<MemberRevocationService> logger)
{
	private static readonly Meter RevocationMeter = new(Extensions.MeterName);
	private static readonly Counter<long> Revoked = RevocationMeter.CreateCounter<long>("archive.members.deactivated");
	private static readonly Counter<long> Restored = RevocationMeter.CreateCounter<long>("archive.members.reactivated");
	private static readonly Counter<long> RolesChanged = RevocationMeter.CreateCounter<long>("archive.members.role_changed");
	private static readonly Counter<long> RevocationFailures = RevocationMeter.CreateCounter<long>("archive.members.revocation_failures");

	public const string DeactivatedMessage =
		"Mitglied deaktiviert. Bestehende Sitzungen werden abgemeldet; Inhalte und Verlauf bleiben erhalten.";

	public const string ReactivatedMessage =
		"Mitglied reaktiviert. Eine erneute Anmeldung mit Code ist erforderlich; frühere Sitzungen bleiben ungültig.";

	public const string RoleChangedMessage =
		"Rolle aktualisiert. Die Änderung gilt ab der nächsten Anfrage.";

	public const string AlreadyDeactivatedMessage =
		"Dieses Konto ist bereits deaktiviert.";

	public const string AlreadyActiveMessage =
		"Dieses Konto ist bereits aktiv.";

	public const string LastAdministratorMessage =
		"Die letzte Administratorin oder der letzte Administrator kann nicht entfernt werden. Reparatur gehört zum Wartungsweg, nicht zur Selbstentsperrung.";

	public const string NotFoundMessage =
		"Für diese Kennung wurde kein Mitglied gefunden.";

	public async Task<(DeactivateOutcome Outcome, ArchiveUser? User)> DeactivateAsync(
		Guid targetAccountId, Guid actorAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.deactivate", ActivityKind.Internal);
		var user = await users.FindByIdAsync(targetAccountId.ToString());
		if (user is null)
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "not_found");
			return (DeactivateOutcome.NotFound, null);
		}
		if (await users.IsLockedOutAsync(user))
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "already_deactivated");
			return (DeactivateOutcome.AlreadyDeactivated, user);
		}
		if (await IsLastActiveAdministratorAsync(user, token))
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "last_administrator");
			logger.LogWarning("Deactivation refused: last active administrator would be removed");
			return (DeactivateOutcome.LastAdministrator, user);
		}
		var oldRoles = await users.GetRolesAsync(user);
		user.LockoutEnabled = true;
		user.LockoutEnd = now.AddYears(100);
		if (!await TryUpdateAsync(user))
		{
			db.Entry(user).State = EntityState.Detached;
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "concurrency_conflict");
			throw new InvalidOperationException("Membership change conflicted; please retry.");
		}
		// Kill existing tickets server-side; reactivation bumps again so old
		// sessions never revive silently.
		try
		{
			await users.UpdateSecurityStampAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetAccountId.ToString());
		}
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = targetAccountId,
			ActorAccountId = actorAccountId,
			Action = MemberAdminActionType.Deactivated,
			OldRoles = string.Join(",", oldRoles.OrderBy(r => r)),
			NewRoles = string.Join(",", oldRoles.OrderBy(r => r)),
			OccurredAt = now,
		});
		await SaveAuditResilientAsync(token);
		Revoked.Add(1);
		activity?.SetTag("members.result", "deactivated");
		logger.LogInformation("Member deactivated (roles preserved)");
		return (DeactivateOutcome.Deactivated, user);
	}

	public async Task<(ReactivateOutcome Outcome, ArchiveUser? User)> ReactivateAsync(
		Guid targetAccountId, Guid actorAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.reactivate", ActivityKind.Internal);
		var user = await users.FindByIdAsync(targetAccountId.ToString());
		if (user is null)
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "not_found");
			return (ReactivateOutcome.NotFound, null);
		}
		if (!await users.IsLockedOutAsync(user))
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "already_active");
			return (ReactivateOutcome.AlreadyActive, user);
		}
		var roles = await users.GetRolesAsync(user);
		user.LockoutEnd = null;
		try
		{
			await users.ResetAccessFailedCountAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetAccountId.ToString());
			if (user is null)
				return (ReactivateOutcome.NotFound, null);
			user.LockoutEnd = null;
		}
		if (!await TryUpdateAsync(user))
		{
			db.Entry(user).State = EntityState.Detached;
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "concurrency_conflict");
			throw new InvalidOperationException("Membership change conflicted; please retry.");
		}
		// A second stamp bump keeps tickets issued before deactivation dead:
		// reactivation requires a fresh email-code sign-in.
		try
		{
			await users.UpdateSecurityStampAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetAccountId.ToString());
		}
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = targetAccountId,
			ActorAccountId = actorAccountId,
			Action = MemberAdminActionType.Reactivated,
			OldRoles = string.Join(",", roles.OrderBy(r => r)),
			NewRoles = string.Join(",", roles.OrderBy(r => r)),
			OccurredAt = now,
		});
		await SaveAuditResilientAsync(token);
		Restored.Add(1);
		activity?.SetTag("members.result", "reactivated");
		logger.LogInformation("Member reactivated; fresh sign-in required");
		return (ReactivateOutcome.Reactivated, user);
	}

	public async Task<(RoleChangeOutcome Outcome, IReadOnlyList<string>? Roles)> ChangeRoleAsync(
		Guid targetAccountId, string newRole, Guid actorAccountId, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.members.role_change", ActivityKind.Internal);
		if (!ArchiveRoles.All.Contains(newRole))
			throw new InvalidOperationException($"Unknown role '{newRole}'.");
		var user = await users.FindByIdAsync(targetAccountId.ToString());
		if (user is null)
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "not_found");
			return (RoleChangeOutcome.NotFound, null);
		}
		var currentRoles = (await users.GetRolesAsync(user)).OrderBy(r => r).ToArray();
		if (currentRoles.Length == 1 && string.Equals(currentRoles[0], newRole, StringComparison.Ordinal))
		{
			activity?.SetTag("members.result", "unchanged");
			return (RoleChangeOutcome.Unchanged, currentRoles);
		}
		var targetIsActiveAdmin = user.EmailConfirmed
			&& !await users.IsLockedOutAsync(user)
			&& currentRoles.Contains(ArchiveRoles.Administrator);
		if (targetIsActiveAdmin && !string.Equals(newRole, ArchiveRoles.Administrator, StringComparison.Ordinal)
			&& await CountActiveAdministratorsAsync(token) <= 1)
		{
			RevocationFailures.Add(1);
			activity?.SetTag("members.result", "last_administrator");
			logger.LogWarning("Role change refused: last active administrator would be demoted");
			return (RoleChangeOutcome.LastAdministrator, currentRoles);
		}
		var toRemove = currentRoles.Where(r => !string.Equals(r, newRole, StringComparison.Ordinal)).ToArray();
		if (toRemove.Length > 0)
		{
			try
			{
				var removed = await users.RemoveFromRolesAsync(user, toRemove);
				if (!removed.Succeeded)
				{
					var fresh = await users.GetRolesAsync(user);
					if (fresh.Except([newRole]).Any())
						throw new InvalidOperationException("Role could not be changed.");
				}
			}
			catch (DbUpdateException)
			{
				db.Entry(user).State = EntityState.Detached;
				user = await users.FindByIdAsync(targetAccountId.ToString())
					?? throw new InvalidOperationException("Membership change conflicted; please retry.");
				var fresh = await users.GetRolesAsync(user);
				toRemove = fresh.Where(r => !string.Equals(r, newRole, StringComparison.Ordinal)).ToArray();
				if (toRemove.Length > 0)
				{
					var removed = await users.RemoveFromRolesAsync(user, toRemove);
					if (!removed.Succeeded)
						throw new InvalidOperationException("Role could not be changed.");
				}
			}
		}
		if (!await users.IsInRoleAsync(user, newRole))
		{
			try
			{
				var added = await users.AddToRoleAsync(user, newRole);
				if (!added.Succeeded && !await users.IsInRoleAsync(user, newRole))
					throw new InvalidOperationException("Role could not be changed.");
			}
			catch (DbUpdateException)
			{
				db.Entry(user).State = EntityState.Detached;
				user = await users.FindByIdAsync(targetAccountId.ToString())
					?? throw new InvalidOperationException("Membership change conflicted; please retry.");
				if (!await users.IsInRoleAsync(user, newRole))
				{
					var added = await users.AddToRoleAsync(user, newRole);
					if (!added.Succeeded)
						throw new InvalidOperationException("Role could not be changed.");
				}
			}
		}
		try
		{
			await users.UpdateSecurityStampAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
		}
		var newRoles = await users.GetRolesAsync(user).ContinueWith(t =>
			t.IsCompletedSuccessfully ? t.Result.OrderBy(r => r).ToArray() : new[] { newRole });
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = targetAccountId,
			ActorAccountId = actorAccountId,
			Action = MemberAdminActionType.RoleChanged,
			OldRoles = string.Join(",", currentRoles),
			NewRoles = string.Join(",", newRoles),
			OccurredAt = now,
		});
		await SaveAuditResilientAsync(token);
		RolesChanged.Add(1);
		activity?.SetTag("members.result", "role_changed");
		logger.LogInformation("Member role changed (old {OldCount} roles, new {NewRole})", currentRoles.Length, newRole);
		return (RoleChangeOutcome.Changed, newRoles);
	}

	private async Task<bool> IsLastActiveAdministratorAsync(ArchiveUser target, CancellationToken token)
	{
		if (!target.EmailConfirmed)
			return false;
		var roles = await users.GetRolesAsync(target);
		if (!roles.Contains(ArchiveRoles.Administrator))
			return false;
		return await CountActiveAdministratorsAsync(token) <= 1;
	}

	private async Task<int> CountActiveAdministratorsAsync(CancellationToken token)
	{
		var admins = await users.GetUsersInRoleAsync(ArchiveRoles.Administrator);
		var count = 0;
		foreach (var admin in admins)
		{
			if (!admin.EmailConfirmed)
				continue;
			if (await users.IsLockedOutAsync(admin))
				continue;
			count++;
		}
		return count;
	}

	private async Task<bool> TryUpdateAsync(ArchiveUser user)
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
			if (entry.Entity is MemberAdminAction)
				continue;
			entry.State = EntityState.Detached;
		}
		await db.SaveChangesAsync(token);
	}
}
