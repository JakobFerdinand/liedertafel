namespace Archive.Backend.Auth;

/// <summary>
/// A hashed one-time email code. The plaintext code is never persisted;
/// only <see cref="CodeHash"/> (SHA-256 over salt + code) and <see cref="Salt"/> are stored.
/// </summary>
public sealed class SignInCode
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Null when the address has no active membership (kept for abuse limits, reveals nothing).</summary>
	public Guid? AccountId { get; set; }

	public required string NormalizedEmail { get; set; }

	public required byte[] CodeHash { get; set; }

	public required byte[] Salt { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset ExpiresAt { get; set; }

	public DateTimeOffset? ConsumedAt { get; set; }

	public int AttemptCount { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token. Every mutation
	/// increments it alongside the change, so exactly one concurrent
	/// verifier wins on any provider (Npgsql and InMemory alike).
	/// </summary>
	public uint RowVersion { get; set; }

	public DateTimeOffset? LastSentAt { get; set; }
}
