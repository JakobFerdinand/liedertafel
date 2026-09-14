using System.Diagnostics;
using System.Diagnostics.Metrics;
using Archive.Backend.Data;
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
/// Request/verify business logic. All unauthenticated failures share one generic
/// message so responses never disclose membership; only low-cardinality
/// <c>auth.result</c> tags reach telemetry.
/// </summary>
public sealed class SignInCodeService(
	ArchiveDbContext db,
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

		var latest = await db.SignInCodes
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

		var membership = await db.Memberships
			.Include(m => m.Account)
			.Where(m => m.Account.NormalizedEmail == normalizedEmail && m.Status == MembershipStatus.Active)
			.OrderByDescending(m => m.Role)
			.FirstOrDefaultAsync(token);

		db.AuthRequestLogs.Add(new AuthRequestLog
		{
			NormalizedEmail = normalizedEmail, IpHash = ipHash,
			Kind = AuthRequestKind.CodeRequested, Succeeded = true, OccurredAt = now,
		});

		if (membership is null)
		{
			// No disclosure: same timing class, same caller-visible outcome, no mail.
			await db.SaveChangesAsync(token);
			AuthRequests.Add(1);
			activity?.SetTag("auth.result", "requested");
			logger.LogInformation("Sign-in code requested");
			return CodeRequestOutcome.Sent;
		}

		var code = AuthSecurity.GenerateCode(auth.CodeLength);
		var salt = AuthSecurity.NewSalt();
		var entry = new SignInCode
		{
			AccountId = membership.AccountId,
			NormalizedEmail = normalizedEmail,
			CodeHash = AuthSecurity.HashCode(code, salt),
			Salt = salt,
			CreatedAt = now,
			ExpiresAt = now.Add(auth.CodeLifetime),
			LastSentAt = now,
		};
		db.SignInCodes.Add(entry);
		await db.SaveChangesAsync(token);

		try
		{
			await mail.SendSignInCodeAsync(membership.Account.Email, code, auth.CodeLifetime, token);
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

	public async Task<(CodeVerifyOutcome Outcome, Account? Account, List<ArchiveRole> Roles)> VerifyCodeAsync(
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

		var candidate = await db.SignInCodes
			.Where(c => c.NormalizedEmail == normalizedEmail && c.ConsumedAt == null && c.ExpiresAt > now)
			.OrderByDescending(c => c.CreatedAt)
			.FirstOrDefaultAsync(token);
		if (candidate is null || candidate.AttemptCount >= auth.MaxVerifyAttemptsPerCode)
		{
			await LogVerifyFailureAsync(normalizedEmail, ipHash, now, token);
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "failed");
			logger.LogInformation("Sign-in code verification failed");
			return (CodeVerifyOutcome.Invalid, null, []);
		}

		if (!AuthSecurity.VerifyCode(candidateCode, candidate.Salt, candidate.CodeHash))
		{
			// Bounded attempts persist across instances via the code row.
			await db.SignInCodes
				.Where(c => c.Id == candidate.Id)
				.ExecuteUpdateAsync(s => s.SetProperty(c => c.AttemptCount, c => c.AttemptCount + 1), token);
			await LogVerifyFailureAsync(normalizedEmail, ipHash, now, token);
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "failed");
			logger.LogInformation("Sign-in code verification failed");
			return (CodeVerifyOutcome.Invalid, null, []);
		}

		// Atomically consume: exactly one concurrent submitter wins.
		var consumed = await db.SignInCodes
			.Where(c => c.Id == candidate.Id && c.ConsumedAt == null && c.ExpiresAt > now)
			.ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, _ => now), token);
		if (consumed == 0)
		{
			await LogVerifyFailureAsync(normalizedEmail, ipHash, now, token);
			AuthFailures.Add(1);
			activity?.SetTag("auth.result", "failed");
			logger.LogInformation("Sign-in code verification failed");
			return (CodeVerifyOutcome.Invalid, null, []);
		}

		var accountId = candidate.AccountId;
		Account? account = null;
		List<ArchiveRole> roles = [];
		if (accountId is not null)
		{
			account = await db.Accounts.FindAsync([accountId.Value], token);
			roles = await db.Memberships
				.Where(m => m.AccountId == accountId.Value && m.Status == MembershipStatus.Active)
				.Select(m => m.Role)
				.ToListAsync(token);
		}
		if (account is null || roles.Count == 0)
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
		await db.SaveChangesAsync(token);
		AuthVerified.Add(1);
		activity?.SetTag("auth.result", "verified");
		logger.LogInformation("Sign-in code verified");
		return (CodeVerifyOutcome.Verified, account, roles);
	}

	private async Task LogVerifyFailureAsync(string normalizedEmail, string ipHash, DateTimeOffset now, CancellationToken token)
	{
		db.AuthRequestLogs.Add(new AuthRequestLog
		{
			NormalizedEmail = normalizedEmail, IpHash = ipHash,
			Kind = AuthRequestKind.CodeVerifyFailed, Succeeded = false, OccurredAt = now,
		});
		await db.SaveChangesAsync(token);
	}
}
