using System.Diagnostics;
using System.Diagnostics.Metrics;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

public enum CodeRequestOutcome
{
	Sent,
	ResentSuppressed,
	RateLimited,
}

public enum CodeVerifyOutcome
{
	Verified,
	Invalid,
	RateLimited,
}

/// <summary>
/// Request/verify orchestration on top of ASP.NET Core Identity. Identity owns
/// users, roles, lockout and sessions; this service owns the invite-only
/// policy (uniform responses that never disclose membership), the resend and
/// abuse caps in <see cref="AuthRequestLog"/>, and mail transport. Code
/// cryptography and one-time state live in <see cref="EmailCodeTokenProvider"/>.
/// </summary>
public sealed class SignInCodeService(
	ArchiveDbContext db,
	UserManager<ArchiveUser> users,
	IArchiveMailSender mail,
	IOptions<AuthOptions> options,
	ILogger<SignInCodeService> logger)
{
	private static readonly Meter AuthMeter = new(Extensions.MeterName);
	private static readonly Counter<long> AuthRequests = AuthMeter.CreateCounter<long>("archive.auth.requests");
	private static readonly Counter<long> AuthVerified = AuthMeter.CreateCounter<long>("archive.auth.verified");
	private static readonly Counter<long> AuthFailures = AuthMeter.CreateCounter<long>("archive.auth.failures");

	private readonly AuthOptions auth = options.Value;

	public async Task<CodeRequestOutcome> RequestCodeAsync(
		string normalizedEmail, string ipHash, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.auth.request", ActivityKind.Internal);
		var hourAgo = now.AddHours(-1);
		var emailRequests = await db.AuthRequestLogs.CountAsync(
			l => l.NormalizedEmail == normalizedEmail && l.Kind == AuthRequestKind.CodeRequested && l.OccurredAt >= hourAgo, token);
		var ipRequests = await db.AuthRequestLogs.CountAsync(
			l => l.IpHash == ipHash && l.Kind == AuthRequestKind.CodeRequested && l.OccurredAt >= hourAgo, token);
		if (emailRequests >= auth.MaxRequestsPerEmailPerHour || ipRequests >= auth.MaxRequestsPerIpPerHour)
		{
			db.AuthRequestLogs.Add(new AuthRequestLog
			{
				NormalizedEmail = normalizedEmail, IpHash = ipHash,
				Kind = AuthRequestKind.CodeRequested, Succeeded = false, OccurredAt = now,
			});
			await db.SaveChangesAsync(token);
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "rate_limited");
			logger.LogWarning("Sign-in code request rate limited");
			return CodeRequestOutcome.RateLimited;
		}

		var latest = await db.SignInChallenges
			.Where(c => c.NormalizedEmail == normalizedEmail && c.ConsumedAt == null && c.ExpiresAt > now)
			.OrderByDescending(c => c.CreatedAt)
			.FirstOrDefaultAsync(token);
		if (latest?.LastSentAt is not null && latest.LastSentAt.Value.Add(auth.ResendCooldown) > now)
		{
			db.AuthRequestLogs.Add(new AuthRequestLog
			{
				NormalizedEmail = normalizedEmail, IpHash = ipHash,
				Kind = AuthRequestKind.CodeRequested, Succeeded = true, OccurredAt = now,
			});
			await db.SaveChangesAsync(token);
			AuthRequests.Add(1);
			activity?.SetTag("auth.result", "resent_suppressed");
			logger.LogInformation("Sign-in code request repeated inside cooldown");
			return CodeRequestOutcome.ResentSuppressed;
		}

		var user = await users.FindByEmailAsync(normalizedEmail);
		var usable = user is not null && !await users.IsLockedOutAsync(user);

		db.AuthRequestLogs.Add(new AuthRequestLog
		{
			NormalizedEmail = normalizedEmail, IpHash = ipHash,
			Kind = AuthRequestKind.CodeRequested, Succeeded = true, OccurredAt = now,
		});

		if (!usable || user is null)
		{
			// No disclosure: same timing class, same caller-visible outcome, no mail.
			await db.SaveChangesAsync(token);
			AuthRequests.Add(1);
			activity?.SetTag("auth.result", "requested");
			logger.LogInformation("Sign-in code requested");
			return CodeRequestOutcome.Sent;
		}

		var code = await users.GenerateUserTokenAsync(user, EmailCodeTokenProvider.ProviderName, EmailCodeTokenProvider.SignInPurpose);
		var address = await users.GetEmailAsync(user) ?? normalizedEmail;
		await db.SaveChangesAsync(token);

		try
		{
			await mail.SendSignInCodeAsync(address, code, auth.CodeLifetime, token);
		}
		catch (Exception exception)
		{
			logger.LogError("Sign-in code mail failed ({ExceptionType})", exception.GetType().Name);
			throw;
		}

		AuthRequests.Add(1);
		activity?.SetTag("auth.result", "requested");
		logger.LogInformation("Sign-in code requested");
		return CodeRequestOutcome.Sent;
	}

	public async Task<(CodeVerifyOutcome Outcome, ArchiveUser? User, IList<string> Roles)> VerifyCodeAsync(
		string normalizedEmail, string candidateCode, string ipHash, DateTimeOffset now, CancellationToken token)
	{
		using var activity = Extensions.Activities.StartActivity("archive.auth.verify", ActivityKind.Internal);
		var windowStart = now.AddMinutes(-10);
		var recentFailures = await db.AuthRequestLogs.CountAsync(
			l => l.NormalizedEmail == normalizedEmail && l.Kind == AuthRequestKind.CodeVerifyFailed && l.OccurredAt >= windowStart, token);
		if (recentFailures >= auth.MaxVerifyFailuresPerEmailPerTenMinutes)
		{
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "rate_limited");
			logger.LogWarning("Sign-in code verification rate limited");
			return (CodeVerifyOutcome.RateLimited, null, []);
		}

		var user = await users.FindByEmailAsync(normalizedEmail);
		if (user is null || await users.IsLockedOutAsync(user))
		{
			await LogVerifyFailureAsync(normalizedEmail, ipHash, now, token);
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "failed");
			logger.LogInformation("Sign-in code verification failed");
			return (CodeVerifyOutcome.Invalid, null, []);
		}

		var valid = await users.VerifyUserTokenAsync(
			user, EmailCodeTokenProvider.ProviderName, EmailCodeTokenProvider.SignInPurpose, candidateCode);
		if (!valid)
		{
			// Account-level backstop next to the per-code attempt caps.
			// Concurrent failures race on the Identity concurrency stamp; a
			// lost race is tolerated because AuthRequestLog stays the
			// enforcement source for abuse limits.
			try
			{
				await users.AccessFailedAsync(user);
			}
			catch (DbUpdateConcurrencyException)
			{
				db.Entry(user).State = EntityState.Detached;
			}
			await LogVerifyFailureAsync(normalizedEmail, ipHash, now, token);
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "failed");
			logger.LogInformation("Sign-in code verification failed");
			return (CodeVerifyOutcome.Invalid, null, []);
		}

		try
		{
			await users.ResetAccessFailedCountAsync(user);
		}
		catch (DbUpdateConcurrencyException)
		{
			// Reload for a fresh stamp so the confirmation update below
			// does not fail on the same stale row.
			db.Entry(user).State = EntityState.Detached;
			user = await users.FindByEmailAsync(normalizedEmail);
			if (user is null)
			{
				AuthFailures.Add(1);
				activity?.SetTag("auth.result", "failed");
				logger.LogInformation("Sign-in code verification failed");
				return (CodeVerifyOutcome.Invalid, null, []);
			}
		}
		if (!user.EmailConfirmed)
		{
			// The code proved address ownership: invitation becomes membership.
			user.EmailConfirmed = true;
			var confirmed = await TryUpdateAsync(users, user);
			if (!confirmed)
			{
				// A concurrent request may have confirmed first; continue
				// only if confirmation is now present.
				db.Entry(user).State = EntityState.Detached;
				user = await users.FindByEmailAsync(normalizedEmail);
				if (user is not null && !user.EmailConfirmed)
				{
					user.EmailConfirmed = true;
					confirmed = await TryUpdateAsync(users, user);
				}
			}
			if (user is null || !user.EmailConfirmed || !confirmed)
			{
				if (user is not null)
					db.Entry(user).State = EntityState.Detached;
				AuthFailures.Add(1);
				activity?.SetTag("auth.result", "failed");
				logger.LogInformation("Sign-in code verification failed");
				return (CodeVerifyOutcome.Invalid, null, []);
			}
		}

		var roles = await users.GetRolesAsync(user);
		if (roles.Count == 0)
		{
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "failed");
			logger.LogInformation("Sign-in code verification failed");
			return (CodeVerifyOutcome.Invalid, null, []);
		}

		db.AuthRequestLogs.Add(new AuthRequestLog
		{
			NormalizedEmail = normalizedEmail, IpHash = ipHash,
			Kind = AuthRequestKind.CodeVerified, Succeeded = true, OccurredAt = now,
		});
		await SaveLogResilientAsync(token);
		AuthVerified.Add(1);
		activity?.SetTag("auth.result", "verified");
		logger.LogInformation("Sign-in code verified");
		return (CodeVerifyOutcome.Verified, user, roles);
	}

	/// <summary>User update that reports concurrency races as failure instead of throwing.</summary>
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
	}
	private async Task LogVerifyFailureAsync(string normalizedEmail, string ipHash, DateTimeOffset now, CancellationToken token)
	{
		db.AuthRequestLogs.Add(new AuthRequestLog
		{
			NormalizedEmail = normalizedEmail, IpHash = ipHash,
			Kind = AuthRequestKind.CodeVerifyFailed, Succeeded = false, OccurredAt = now,
		});
		await SaveLogResilientAsync(token);
	}

	/// <summary>
	/// Persists pending log rows even when concurrently raced Identity
	/// bookkeeping left stale tracked entries behind: only Added log rows are
	/// kept, everything else is detached (their owners already saved or
	/// tolerate the lost race, see the call sites).
	/// </summary>
	private async Task SaveLogResilientAsync(CancellationToken token)
	{
		try
		{
			await db.SaveChangesAsync(token);
			return;
		}
		catch (DbUpdateConcurrencyException)
		{
		}
		foreach (var entry in db.ChangeTracker.Entries().ToArray())
		{
			if (entry.Entity is not AuthRequestLog)
				entry.State = EntityState.Detached;
		}
		await db.SaveChangesAsync(token);
	}
}
