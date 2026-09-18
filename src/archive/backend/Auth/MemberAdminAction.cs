namespace Archive.Backend.Auth;

/// <summary>
/// Administrator membership change (ARC-007 revocation, ARC-008 repair).
/// Append-only audit: who changed whom, when, and which roles were replaced.
/// Deactivation uses Identity lockout (revoked = locked out); the user row,
/// stable account ID and the invitation/acceptance history are never deleted.
/// Email changes keep the same user row; maintainer repairs use
/// <see cref="MemberAdminActionType.AdministratorRepaired"/> with
/// <see cref="MemberAdminAction.ActorAccountId"/> set to
/// <see cref="Guid.Empty"/> (maintainer, not a member) and the operator name
/// in <see cref="MemberAdminAction.Note"/>.
/// </summary>
public enum MemberAdminActionType
{
	Deactivated = 0,
	Reactivated = 1,
	RoleChanged = 2,
	EmailChanged = 3,
	AdministratorRepaired = 4,
	PasskeyRegistered = 5,
	PasskeyRemoved = 6,
	PasskeyRenamed = 7,
}

public sealed class MemberAdminAction
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid TargetUserId { get; set; }

	public Guid ActorAccountId { get; set; }

	public MemberAdminActionType Action { get; set; }

	/// <summary>Comma-separated roles before the change (empty when none).</summary>
	public string OldRoles { get; set; } = string.Empty;

	/// <summary>Comma-separated roles after the change (empty when none).</summary>
	public string NewRoles { get; set; } = string.Empty;

	/// <summary>
	/// Operator detail for maintainer repairs (operator display name); null
	/// for member-administration actions. Never carries addresses or codes.
	/// </summary>
	public string? Note { get; set; }

	public DateTimeOffset OccurredAt { get; set; }
}
