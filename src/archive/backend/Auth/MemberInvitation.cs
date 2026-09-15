namespace Archive.Backend.Auth;

/// <summary>
/// Mail-acceptance state for an invitation. <c>Sent</c> means the mail sender
/// accepted the message for delivery; it never promises arrival in the inbox
/// (ARC-010 owns real delivery evidence).
/// </summary>
public enum InvitationMailStatus
{
	Pending = 0,
	Sent = 1,
	Failed = 2,
}

/// <summary>
/// Administrator invitation linking to the stable account ID (<see cref="UserId"/>).
/// Invited = unconfirmed user row, active = confirmed, revoked = locked out
/// (ARC-007). Attribution (<see cref="InvitedByAccountId"/>, <see cref="InvitedAt"/>)
/// is recorded at creation; <see cref="AcceptedAt"/> is stamped on the first
/// email-code verification. Mail failures keep <see cref="MailStatus"/> as
/// <c>Failed</c> so the invitation stays retryable instead of claiming success.
/// </summary>
public sealed class MemberInvitation
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid UserId { get; set; }

	public ArchiveUser User { get; set; } = null!;

	public required string NormalizedEmail { get; set; }

	public required string Role { get; set; }

	public string? DisplayName { get; set; }

	public Guid? InvitedByAccountId { get; set; }

	public DateTimeOffset InvitedAt { get; set; }

	public DateTimeOffset? AcceptedAt { get; set; }

	public DateTimeOffset? LastSentAt { get; set; }

	public Guid? LastSentByAccountId { get; set; }

	public InvitationMailStatus MailStatus { get; set; } = InvitationMailStatus.Pending;

	/// <summary>User-facing German send state; never carries addresses or codes.</summary>
	public string? LastError { get; set; }
}
