namespace Archive.Backend.Auth;

/// <summary>
/// Verified administrator email change (ARC-008). A salted-hash confirmation
/// code is mailed to the <em>new</em> address; only the plaintext code ever
/// leaves the server. The challenge is bound to the stable target account
/// (<see cref="UserId"/> plus <see cref="NormalizedNewEmail"/>), so receiving
/// the mail alone can never attach the new address to somebody else's
/// account: sign-in challenges live in <see cref="SignInChallenge"/> and the
/// sign-in flow has no user row for the not-yet-assigned address.
/// </summary>
public sealed class MemberEmailChange
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid UserId { get; set; }

	public ArchiveUser User { get; set; } = null!;

	public required string NormalizedNewEmail { get; set; }

	public required byte[] CodeHash { get; set; }

	public required byte[] Salt { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset ExpiresAt { get; set; }

	public DateTimeOffset? ConsumedAt { get; set; }

	public int AttemptCount { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token, mirroring
	/// <see cref="SignInChallenge.RowVersion"/>: every mutation increments it
	/// so exactly one concurrent confirmer wins.
	/// </summary>
	public uint RowVersion { get; set; }

	public Guid RequestedByAccountId { get; set; }
}
