using System.Diagnostics;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

/// <summary>
/// ASP.NET Core Identity two-factor token provider for German email sign-in
/// codes (purpose <c>signin</c>). The plaintext code is generated here, mailed
/// by <see cref="SignInCodeService"/>, and verified against the salted-hash
/// <see cref="SignInChallenge"/> row with the same expiry, attempt and
/// one-time semantics as before — now behind the documented Identity
/// <c>GenerateUserTokenAsync</c>/<c>VerifyUserTokenAsync</c> surface.
/// </summary>
public sealed class EmailCodeTokenProvider(
	ArchiveDbContext db,
	IOptions<AuthOptions> options,
	TimeProvider time,
	ILogger<EmailCodeTokenProvider> logger) : IUserTwoFactorTokenProvider<ArchiveUser>
{
	public const string ProviderName = "EmailCode";

	public const string SignInPurpose = "signin";

	private readonly AuthOptions auth = options.Value;

	public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ArchiveUser> manager, ArchiveUser user)
		=> Task.FromResult(manager is not null && user is not null);

	public async Task<string> GenerateAsync(string purpose, UserManager<ArchiveUser> manager, ArchiveUser user)
	{
		ArgumentNullException.ThrowIfNull(manager);
		ArgumentNullException.ThrowIfNull(user);
		if (!string.Equals(purpose, SignInPurpose, StringComparison.Ordinal))
			throw new InvalidOperationException($"Unknown email-code purpose '{purpose}'.");
		using var activity = Extensions.Activities.StartActivity("archive.auth.challenge", ActivityKind.Internal);
		var now = time.GetUtcNow();
		var code = AuthSecurity.GenerateCode(auth.CodeLength);
		var salt = AuthSecurity.NewSalt();
		db.SignInChallenges.Add(new SignInChallenge
		{
			UserId = user.Id,
			NormalizedEmail = user.NormalizedEmail ?? AuthSecurity.NormalizeEmail(user.Email ?? string.Empty),
			CodeHash = AuthSecurity.HashCode(code, salt),
			Salt = salt,
			CreatedAt = now,
			ExpiresAt = now.Add(auth.CodeLifetime),
			LastSentAt = now,
		});
		await db.SaveChangesAsync();
		return code;
	}

	public async Task<bool> ValidateAsync(string purpose, string token, UserManager<ArchiveUser> manager, ArchiveUser user)
	{
		if (!string.Equals(purpose, SignInPurpose, StringComparison.Ordinal) || manager is null || user is null)
			return false;
		var candidate = AuthSecurity.NormalizeCode(token);
		if (candidate.Length != auth.CodeLength)
			return false;
		var now = time.GetUtcNow();
		var challenge = await db.SignInChallenges
			.Where(c => c.UserId == user.Id && c.ConsumedAt == null && c.ExpiresAt > now)
			.OrderByDescending(c => c.CreatedAt)
			.FirstOrDefaultAsync();
		if (challenge is null || challenge.AttemptCount >= auth.MaxVerifyAttemptsPerCode)
			return false;
		if (!AuthSecurity.VerifyCode(candidate, challenge.Salt, challenge.CodeHash))
		{
			challenge.AttemptCount++;
			challenge.RowVersion++;
			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateConcurrencyException)
			{
				db.Entry(challenge).State = EntityState.Detached;
				var retry = await db.SignInChallenges
					.Where(c => c.Id == challenge.Id && c.ConsumedAt == null && c.ExpiresAt > now)
					.FirstOrDefaultAsync();
				if (retry is not null && retry.AttemptCount < auth.MaxVerifyAttemptsPerCode)
				{
					retry.AttemptCount++;
					retry.RowVersion++;
					try
					{
						await db.SaveChangesAsync();
					}
					catch (DbUpdateConcurrencyException)
					{
						db.Entry(retry).State = EntityState.Detached;
					}
				}
			}
			return false;
		}
		challenge.ConsumedAt = now;
		challenge.RowVersion++;
		try
		{
			await db.SaveChangesAsync();
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(challenge).State = EntityState.Detached;
			return false;
		}
		return true;
	}
}
