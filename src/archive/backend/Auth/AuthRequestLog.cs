namespace Archive.Backend.Auth;

public enum AuthRequestKind
{
	CodeRequested = 0,
	CodeVerified = 1,
	CodeVerifyFailed = 2,
}

/// <summary>
/// Append-only log backing the DB-persisted abuse limits. Raw IPs and
/// email local parts never leave this table in logs or traces; only
/// <see cref="IpHash"/> is stored.
/// </summary>
public sealed class AuthRequestLog
{
	public long Id { get; set; }

	public required string NormalizedEmail { get; set; }

	public required string IpHash { get; set; }

	public AuthRequestKind Kind { get; set; }

	public bool Succeeded { get; set; }

	public DateTimeOffset OccurredAt { get; set; }
}
