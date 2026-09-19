namespace Archive.Backend.Assets;

/// <summary>State machine for direct-to-storage upload sessions.</summary>
public enum PendingUploadState
{
	Pending = 0,
	Finalized = 1,
	Abandoned = 2,
}

/// <summary>
/// A direct-to-storage upload session for one <see cref="ArchiveAsset"/>
/// (ARC-015). The browser uploads to the ticket's blob-scoped SAS; the API
/// verifies the object and promotes it into a <see cref="FileRevision"/>.
/// Abandoned sessions are cleaned by a bounded maintenance policy (ARC-017).
/// </summary>
public sealed class PendingUpload
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid AssetId { get; set; }

	public ArchiveAsset Asset { get; set; } = null!;

	/// <summary>The unique pending object name; one session per object.</summary>
	public required string BlobName { get; set; }

	public required string ContentType { get; set; }

	public long MaxSizeBytes { get; set; }

	public DateTimeOffset UploadTicketExpiresAt { get; set; }

	public PendingUploadState State { get; set; }

	/// <summary>Idempotency reference to the revision created on finalization.</summary>
	public Guid? FinalizedRevisionId { get; set; }

	public FileRevision? FinalizedRevision { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}
