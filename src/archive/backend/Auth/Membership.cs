namespace Archive.Backend.Auth;

public enum ArchiveRole
{
	Member = 0,
	Editor = 1,
	Administrator = 2,
}

public enum MembershipStatus
{
	Invited = 0,
	Active = 1,
	Deactivated = 2,
}

/// <summary>
/// Access grant for an account. Revocation (ARC-007) flips <see cref="Status"/>
/// to <see cref="MembershipStatus.Deactivated"/>; history rows are never deleted.
/// </summary>
public sealed class Membership
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid AccountId { get; set; }

	public Account Account { get; set; } = null!;

	public ArchiveRole Role { get; set; } = ArchiveRole.Member;

	public MembershipStatus Status { get; set; } = MembershipStatus.Invited;

	/// <summary>Actor description for invitation/role-change attribution.</summary>
	public string? InvitedBy { get; set; }

	public DateTimeOffset InvitedAt { get; set; }

	public DateTimeOffset? ActivatedAt { get; set; }

	public DateTimeOffset? DeactivatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}
