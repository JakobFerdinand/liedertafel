using Microsoft.AspNetCore.Identity;

namespace Archive.Backend.Auth;

/// <summary>
/// Archive member identity. Passwordless-only: no password hash is ever stored;
/// sign-in happens exclusively through the <see cref="EmailCodeTokenProvider"/>.
/// Invitation state follows Identity semantics: invited = unconfirmed row,
/// active = confirmed, revoked = locked out (ARC-007).
/// </summary>
public sealed class ArchiveUser : IdentityUser<Guid>
{
	public ArchiveUser()
	{
		Id = Guid.CreateVersion7();
	}

	public string? DisplayName { get; set; }
}

/// <summary>Identity role. Valid names are declared on <see cref="ArchiveRoles"/>.</summary>
public sealed class ArchiveRole : IdentityRole<Guid>
{
	public ArchiveRole()
	{
		Id = Guid.CreateVersion7();
	}

	public ArchiveRole(string roleName)
		: this()
	{
		Name = roleName;
	}
}

/// <summary>Role names for Member/Editor/Administrator policies.</summary>
public static class ArchiveRoles
{
	public const string Member = "Member";
	public const string Editor = "Editor";
	public const string Administrator = "Administrator";

	public static readonly IReadOnlyList<string> All = [Member, Editor, Administrator];
}
