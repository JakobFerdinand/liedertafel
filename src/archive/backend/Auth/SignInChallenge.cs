namespace Archive.Backend.Auth;

/// <summary>
/// A hashed one-time email-code challenge for an invited user. The plaintext
/// code is never persisted; only <see cref="CodeHash"/> (SHA-256 over salt +
/// code) and <see cref="Salt"/> are stored. Managed by
/// <see cref="EmailCodeTokenProvider"/>; consumed at most once via
/// <see cref="RowVersion"/> optimistic concurrency.
/// </summary>
public sealed class SignInChallenge
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid UserId { get; set; }

	public ArchiveUser User { get; set; } = null!;

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
