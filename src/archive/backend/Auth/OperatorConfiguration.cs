using Archive.Backend.Data;
using Archive.Backend.Development;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Auth;

/// <summary>
/// Finite operator commands. <c>--bootstrap-admin</c> creates the very first
/// administrator account through <see cref="UserManager{TUser}"/>; it refuses
/// once any user exists. <c>--repair-admin</c> (ARC-008) invites or repairs
/// an administrator when the old inbox is unavailable: it ensures a confirmed
/// active Administrator for <c>--email</c>, or moves the account <c>--accountId</c>
/// to a free <c>--email</c>. Both non-development invocations require
/// <c>Archive:OperatorToken</c> plus <c>--operator-token</c>; the actor is
/// logged explicitly and no login secret is ever printed.
/// <c>--seed-dev-auth</c> is Development-only test data.
/// </summary>
public static class OperatorConfiguration
{
	public static string Connection(IConfiguration configuration)
	{
		var migrations = configuration.GetConnectionString("archive-migrations");
		if (!string.IsNullOrWhiteSpace(migrations))
			return DatabaseConfiguration.Connection(configuration, "archive-migrations");
		return DatabaseConfiguration.Connection(configuration, "archive-db");
	}

	public static async Task SeedDevAuthAsync(IServiceProvider provider, CancellationToken token)
	{
		var environment = provider.GetRequiredService<IHostEnvironment>();
		if (!environment.IsDevelopment())
			throw new InvalidOperationException("Local service commands require Development.");
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Operator");
		var accounts = await AuthSeed.EnsureTestAccountsAsync(users, roles, token);
		logger.LogInformation("Development test accounts ensured ({Count})", accounts.Count);
	}

	public static async Task BootstrapAdminAsync(
		IServiceProvider provider, IConfiguration configuration, string[] args, CancellationToken token)
	{
		var environment = provider.GetRequiredService<IHostEnvironment>();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Operator");
		var email = Option(args, "--email");
		var displayName = Option(args, "--name");
		var operatorName = Option(args, "--operator") ?? "Betreiber";
		if (!AuthSecurity.TryNormalizeEmail(email, out var normalized))
			throw new InvalidOperationException("Bootstrap requires --email <address>.");
		if (!environment.IsDevelopment())
		{
			var expected = configuration["Archive:OperatorToken"];
			var provided = Option(args, "--operator-token");
			if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided)
				|| !CryptographicEquals(expected, provided))
				throw new InvalidOperationException("Bootstrap outside Development requires Archive:OperatorToken and --operator-token.");
		}
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		var db = provider.GetRequiredService<ArchiveDbContext>();
		var time = provider.GetRequiredService<TimeProvider>();
		if (await db.Users.AnyAsync(token))
			throw new InvalidOperationException("Archive already has accounts; use member administration instead of bootstrap.");
		if (!await roles.RoleExistsAsync(ArchiveRoles.Administrator))
		{
			try
			{
				var created = await roles.CreateAsync(new ArchiveRole(ArchiveRoles.Administrator));
				if (!created.Succeeded && !await roles.RoleExistsAsync(ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be created.");
			}
			catch (DbUpdateException)
			{
				// A parallel bootstrap may have won the race; only the
				// still-missing role is an error.
				if (!await roles.RoleExistsAsync(ArchiveRoles.Administrator))
					throw;
			}
		}
		var id = await AuthSeed.EnsureUserAsync(users, email!.Trim(), displayName, token);
		var admin = (await users.FindByIdAsync(id.ToString()))!;
		if (!await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
		{
			try
			{
				var inRole = await users.AddToRoleAsync(admin, ArchiveRoles.Administrator);
				if (!inRole.Succeeded && !await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be assigned.");
			}
			catch (DbUpdateException)
			{
				if (!await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
					throw;
			}
		}
		// Log the domain only; never the full address from an operator command.
		logger.LogInformation(
			"Bootstrap administrator ensured for domain {Domain} by {Operator}",
			AuthSecurity.DomainOf(normalized).ToLowerInvariant(), operatorName);
	}

	/// <summary>
	/// Maintainer repair for administrative access (ARC-008). Without
	/// <c>--accountId</c>, ensures a confirmed, active Administrator owns
	/// <c>--email</c> (reactivating, confirming and promoting the existing row
	/// when present, creating it with an accepted invitation row when absent).
	/// With <c>--accountId --email</c>, moves that account to a free address
	/// when the old inbox is unavailable. Invalidates obsolete sign-in
	/// challenges and pending email changes, bumps the security stamp when
	/// access changed so stale tickets die, and audits
	/// <c>AdministratorRepaired</c> with the operator name. Never stores a
	/// password and never prints a login secret: the repaired administrator
	/// signs in with the ordinary email-code flow.
	/// </summary>
	public static async Task<Guid> RepairAdminAsync(
		IServiceProvider provider, IConfiguration configuration, string[] args, CancellationToken token)
	{
		var environment = provider.GetRequiredService<IHostEnvironment>();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Operator");
		var email = Option(args, "--email");
		var accountIdOption = Option(args, "--accountId") ?? Option(args, "--account-id");
		var displayName = Option(args, "--name");
		var operatorName = Option(args, "--operator") ?? "Betreiber";
		if (!AuthSecurity.TryNormalizeEmail(email, out var normalized))
			throw new InvalidOperationException("Repair requires --email <address>.");
		if (!environment.IsDevelopment())
		{
			var expected = configuration["Archive:OperatorToken"];
			var provided = Option(args, "--operator-token");
			if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided)
				|| !CryptographicEquals(expected, provided))
				throw new InvalidOperationException("Repair outside Development requires Archive:OperatorToken and --operator-token.");
		}
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		var db = provider.GetRequiredService<ArchiveDbContext>();
		var time = provider.GetRequiredService<TimeProvider>();
		var now = time.GetUtcNow();
		if (!await roles.RoleExistsAsync(ArchiveRoles.Administrator))
		{
			try
			{
				var created = await roles.CreateAsync(new ArchiveRole(ArchiveRoles.Administrator));
				if (!created.Succeeded && !await roles.RoleExistsAsync(ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be created.");
			}
			catch (DbUpdateException)
			{
				if (!await roles.RoleExistsAsync(ArchiveRoles.Administrator))
					throw;
			}
		}

		if (!string.IsNullOrWhiteSpace(accountIdOption))
			return await RepairAccountEmailAsync(
				provider, users, db, time, logger, accountIdOption, email!.Trim(), normalized, operatorName, now, token);
		return await EnsureAdministratorAsync(
			provider, users, db, logger, email!.Trim(), normalized, displayName, operatorName, now, token);
	}

	private static async Task<Guid> RepairAccountEmailAsync(
		IServiceProvider provider, UserManager<ArchiveUser> users, ArchiveDbContext db,
		TimeProvider time, ILogger logger, string accountIdOption, string rawEmail,
		string normalized, string operatorName, DateTimeOffset now, CancellationToken token)
	{
		if (!Guid.TryParse(accountIdOption, out var targetId) || targetId == Guid.Empty)
			throw new InvalidOperationException("Repair requires --accountId <guid> when moving an address.");
		var user = await users.FindByIdAsync(targetId.ToString());
		if (user is null)
			throw new InvalidOperationException("Repair found no account for --accountId.");
		var collision = await users.FindByEmailAsync(normalized);
		if (collision is not null && collision.Id != user.Id)
			throw new InvalidOperationException("Repair address already belongs to another account.");
		var oldRoles = await users.GetRolesAsync(user);
		user.Email = rawEmail;
		user.UserName = rawEmail;
		user.EmailConfirmed = true;
		user.LockoutEnd = null;
		if (!await TryOperatorUpdateAsync(users, user))
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetId.ToString())
				?? throw new InvalidOperationException("Repair found no account for --accountId.");
			user.Email = rawEmail;
			user.UserName = rawEmail;
			user.EmailConfirmed = true;
			user.LockoutEnd = null;
			if (!await TryOperatorUpdateAsync(users, user))
				throw new InvalidOperationException("Repair could not update the account; please retry.");
		}
		try
		{
			await users.ResetAccessFailedCountAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetId.ToString())
				?? throw new InvalidOperationException("Repair found no account for --accountId.");
		}
		if (!await users.IsInRoleAsync(user, ArchiveRoles.Administrator))
		{
			try
			{
				var inRole = await users.AddToRoleAsync(user, ArchiveRoles.Administrator);
				if (!inRole.Succeeded && !await users.IsInRoleAsync(user, ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be assigned.");
			}
			catch (DbUpdateException)
			{
				if (!await users.IsInRoleAsync(user, ArchiveRoles.Administrator))
					throw;
			}
		}
		await InvalidateChallengesAsync(db, user.Id, now, token);
		await RevokePasskeysAsync(users, logger, user, operatorName);
		try
		{
			await users.UpdateSecurityStampAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByIdAsync(targetId.ToString());
		}
		var invitation = await db.MemberInvitations.FirstOrDefaultAsync(i => i.UserId == targetId, token);
		if (invitation is not null)
			invitation.NormalizedEmail = normalized;
		var newRoles = user is null ? oldRoles.Concat([ArchiveRoles.Administrator]).Distinct().OrderBy(r => r).ToArray()
			: (await users.GetRolesAsync(user)).OrderBy(r => r).ToArray();
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = targetId,
			ActorAccountId = Guid.Empty,
			Action = MemberAdminActionType.AdministratorRepaired,
			OldRoles = string.Join(",", oldRoles.OrderBy(r => r)),
			NewRoles = string.Join(",", newRoles),
			Note = $"Betreiber-Reparatur durch {operatorName}",
			OccurredAt = now,
		});
		await SaveOperatorAuditAsync(db, token);
		// Log the domain only; never the full address from an operator command.
		logger.LogInformation(
			"Repair moved administrator {TargetId} to domain {Domain} by {Operator}",
			targetId, AuthSecurity.DomainOf(normalized).ToLowerInvariant(), operatorName);
		return targetId;
	}

	private static async Task<Guid> EnsureAdministratorAsync(
		IServiceProvider provider, UserManager<ArchiveUser> users, ArchiveDbContext db,
		ILogger logger, string rawEmail, string normalized, string? displayName,
		string operatorName, DateTimeOffset now, CancellationToken token)
	{
		var existing = await users.FindByEmailAsync(normalized);
		if (existing is not null)
		{
			var oldRoles = await users.GetRolesAsync(existing);
			var changed = false;
			if (!existing.EmailConfirmed)
			{
				existing.EmailConfirmed = true;
				changed = true;
			}
			if (await users.IsLockedOutAsync(existing))
			{
				existing.LockoutEnd = null;
				changed = true;
			}
			if (changed && !await TryOperatorUpdateAsync(users, existing))
			{
				db.Entry(existing).State = EntityState.Detached;
				existing = await users.FindByEmailAsync(normalized)
					?? throw new InvalidOperationException("Repair found no account for the address.");
				existing.EmailConfirmed = true;
				existing.LockoutEnd = null;
				if (!await TryOperatorUpdateAsync(users, existing))
					throw new InvalidOperationException("Repair could not update the account; please retry.");
			}
			if (changed)
			{
				try
				{
					await users.ResetAccessFailedCountAsync(existing);
				}
				catch (DbUpdateConcurrencyException)
				{
					db.Entry(existing).State = EntityState.Detached;
					existing = await users.FindByEmailAsync(normalized)
						?? throw new InvalidOperationException("Repair found no account for the address.");
				}
			}
			if (!await users.IsInRoleAsync(existing, ArchiveRoles.Administrator))
			{
				try
				{
					var inRole = await users.AddToRoleAsync(existing, ArchiveRoles.Administrator);
					if (!inRole.Succeeded && !await users.IsInRoleAsync(existing, ArchiveRoles.Administrator))
						throw new InvalidOperationException("Administrator role could not be assigned.");
				}
				catch (DbUpdateException)
				{
					if (!await users.IsInRoleAsync(existing, ArchiveRoles.Administrator))
						throw;
				}
				changed = true;
			}
			if (!changed)
			{
				logger.LogInformation(
					"Repair found active administrator {TargetId} for domain {Domain} by {Operator}",
					existing.Id, AuthSecurity.DomainOf(normalized).ToLowerInvariant(), operatorName);
				return existing.Id;
			}
			await InvalidateChallengesAsync(db, existing.Id, now, token);
			try
			{
				await users.UpdateSecurityStampAsync(existing);
			}
			catch (DbUpdateConcurrencyException)
			{
				db.Entry(existing).State = EntityState.Detached;
				existing = await users.FindByEmailAsync(normalized);
			}
			if (existing is not null)
				await RevokePasskeysAsync(users, logger, existing, operatorName);
			var newRoles = existing is null ? [ArchiveRoles.Administrator]
				: (await users.GetRolesAsync(existing)).OrderBy(r => r).ToArray();
			db.MemberAdminActions.Add(new MemberAdminAction
			{
				TargetUserId = existing?.Id ?? Guid.Empty,
				ActorAccountId = Guid.Empty,
				Action = MemberAdminActionType.AdministratorRepaired,
				OldRoles = string.Join(",", oldRoles.OrderBy(r => r)),
				NewRoles = string.Join(",", newRoles),
				Note = $"Betreiber-Reparatur durch {operatorName}",
				OccurredAt = now,
			});
			await SaveOperatorAuditAsync(db, token);
			logger.LogInformation(
				"Repair ensured administrator {TargetId} for domain {Domain} by {Operator}",
				existing?.Id ?? Guid.Empty, AuthSecurity.DomainOf(normalized).ToLowerInvariant(), operatorName);
			return existing?.Id ?? Guid.Empty;
		}

		var id = await AuthSeed.EnsureUserAsync(users, rawEmail, displayName, token);
		var admin = (await users.FindByIdAsync(id.ToString()))!;
		if (!await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
		{
			try
			{
				var inRole = await users.AddToRoleAsync(admin, ArchiveRoles.Administrator);
				if (!inRole.Succeeded && !await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be assigned.");
			}
			catch (DbUpdateException)
			{
				if (!await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
					throw;
			}
		}
		db.MemberInvitations.Add(new MemberInvitation
		{
			UserId = admin.Id,
			NormalizedEmail = normalized,
			Role = ArchiveRoles.Administrator,
			DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
			InvitedByAccountId = null,
			InvitedAt = now,
			AcceptedAt = now,
			LastSentByAccountId = null,
			MailStatus = InvitationMailStatus.Pending,
		});
		db.MemberAdminActions.Add(new MemberAdminAction
		{
			TargetUserId = admin.Id,
			ActorAccountId = Guid.Empty,
			Action = MemberAdminActionType.AdministratorRepaired,
			OldRoles = string.Empty,
			NewRoles = ArchiveRoles.Administrator,
			Note = $"Betreiber-Reparatur durch {operatorName}",
			OccurredAt = now,
		});
		await SaveOperatorAuditAsync(db, token);
		logger.LogInformation(
			"Repair invited administrator {TargetId} for domain {Domain} by {Operator}",
			admin.Id, AuthSecurity.DomainOf(normalized).ToLowerInvariant(), operatorName);
		return admin.Id;
	}

	/// <summary>
	/// Compromise recovery (ARC-011-1): a maintainer repair revokes every
	/// registered passkey of the repaired account. Credential revocation is
	/// defined separately from session revocation: ordinary email changes
	/// keep passkeys (they belong to the stable account ID), only this
	/// repair path revokes them. Fails loudly so the operator can retry.
	/// </summary>
	private static async Task RevokePasskeysAsync(
		UserManager<ArchiveUser> users, ILogger logger, ArchiveUser user, string operatorName)
	{
		var passkeys = await users.GetPasskeysAsync(user);
		foreach (var passkey in passkeys)
		{
			var removed = await users.RemovePasskeyAsync(user, passkey.CredentialId);
			if (!removed.Succeeded)
				throw new InvalidOperationException("Repair could not revoke a registered passkey.");
		}
		if (passkeys.Count > 0)
			logger.LogInformation(
				"Repair revoked {Count} passkey(s) of {TargetId} by {Operator}",
				passkeys.Count, user.Id, operatorName);
	}

	private static async Task InvalidateChallengesAsync(ArchiveDbContext db, Guid userId, DateTimeOffset now, CancellationToken token)
	{
		var signIns = await db.SignInChallenges.Where(c => c.UserId == userId).ToListAsync(token);
		db.SignInChallenges.RemoveRange(signIns);
		var changes = await db.MemberEmailChanges.Where(c => c.UserId == userId && c.ConsumedAt == null).ToListAsync(token);
		foreach (var change in changes)
			change.ConsumedAt = now;
		try
		{
			await db.SaveChangesAsync(token);
		}
		catch (DbUpdateConcurrencyException)
		{
			foreach (var entry in db.ChangeTracker.Entries().ToArray())
			{
				if (entry.Entity is SignInChallenge or MemberEmailChange)
					continue;
				entry.State = EntityState.Detached;
			}
			await db.SaveChangesAsync(token);
		}
	}

	private static async Task<bool> TryOperatorUpdateAsync(UserManager<ArchiveUser> users, ArchiveUser user)
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

	private static async Task SaveOperatorAuditAsync(ArchiveDbContext db, CancellationToken token)
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
			if (entry.Entity is MemberAdminAction or MemberInvitation or MemberEmailChange)
				continue;
			entry.State = EntityState.Detached;
		}
		await db.SaveChangesAsync(token);
	}

	private static string? Option(string[] args, string name)
	{
		for (var i = 0; i < args.Length; i++)
		{
			if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				return args[i + 1];
		}
		return null;
	}

	private static bool CryptographicEquals(string a, string b)
	{
		var ab = System.Text.Encoding.UTF8.GetBytes(a);
		var bb = System.Text.Encoding.UTF8.GetBytes(b);
		return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ab, bb);
	}
}
