namespace Archive.Backend.Auth;

/// <summary>
/// Administrator membership change (ARC-007). Append-only audit: who changed
/// whom, when, and which roles were replaced. Deactivation uses Identity
/// lockout (revoked = locked out); the user row, stable account ID and the
/// invitation/acceptance history are never deleted.
/// </summary>
public enum MemberAdminActionType
{
	Deactivated = 0,
	Reactivated = 1,
	RoleChanged = 2,
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

	public DateTimeOffset OccurredAt { get; set; }
}
