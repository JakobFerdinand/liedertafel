namespace Archive.Backend.Auth;

/// <summary>
/// Concrete ARC-005 authentication settings. Values are the slice contract;
/// later slices (ARC-010/011) own cloud mail and production key persistence,
/// not these limits.
/// </summary>
public sealed class AuthOptions
{
	public const string SectionName = "Authentication";

	/// <summary>Digits per code. Six digits keep phone entry usable; entropy is compensated by short life and caps.</summary>
	public int CodeLength { get; set; } = 6;

	public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

	public int MaxVerifyAttemptsPerCode { get; set; } = 5;

	public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

	public int MaxRequestsPerEmailPerHour { get; set; } = 5;

	public int MaxRequestsPerIpPerHour { get; set; } = 30;

	public int MaxVerifyFailuresPerEmailPerTenMinutes { get; set; } = 10;

	public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

	/// <summary>Window in which a fresh code verification satisfies sensitive-action guards.</summary>
	public TimeSpan FreshVerificationWindow { get; set; } = TimeSpan.FromMinutes(10);

	public string CookieName { get; set; } = "archive.auth";
}
