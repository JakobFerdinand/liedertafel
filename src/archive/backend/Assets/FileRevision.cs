namespace Archive.Backend.Assets;

/// <summary>
/// An immutable file revision of an <see cref="ArchiveAsset"/>. Immutable once
/// created: corrections upload a new revision with a bumped
/// <see cref="RevisionNumber"/> (ARC-031) instead of mutating rows. The id is
/// consumed by ARC-032 extraction idempotency.
/// </summary>
public sealed class FileRevision
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid AssetId { get; set; }

	public ArchiveAsset Asset { get; set; } = null!;

	/// <summary>Monotonic per-asset revision counter.</summary>
	public int RevisionNumber { get; set; }

	public required string BlobName { get; set; }

	public required string ContentType { get; set; }

	public long SizeBytes { get; set; }

	/// <summary>
	/// ARC-033: the file name declared for the upload, shown in the editor's
	/// revision history; null when the upload declared none.
	/// </summary>
	public string? OriginalFileName { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}
