namespace Archive.Backend.Auth;

/// <summary>
/// Stable account identity. Survives an approved email change (ARC-008);
/// the current address lives on this row while <see cref="Membership"/> carries access state.
/// </summary>
public sealed class Account
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public required string Email { get; set; }

	public required string NormalizedEmail { get; set; }

	public string? DisplayName { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public ICollection<Membership> Memberships { get; } = [];
}
