namespace Archive.Backend.Assets;

/// <summary>State machine for direct-to-storage upload sessions.</summary>
public enum PendingUploadState
{
	Pending = 0,
	Finalized = 1,
	Abandoned = 2,

	/// <summary>ARC-017: terminal state after the editor cancelled the session.</summary>
	Cancelled = 3,
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

	/// <summary>
	/// Old model: the asset this session uploads into. Null for sessions of the
	/// new Datei model, which set <see cref="LiedId"/> or <see cref="EventId"/>
	/// instead (exactly one of the three, check constraint). Dropped with the
	/// old model in #172.
	/// </summary>
	public Guid? AssetId { get; set; }

	public ArchiveAsset? Asset { get; set; }

	/// <summary>#166: the Lied the finalized Datei will belong to.</summary>
	public Guid? LiedId { get; set; }

	/// <summary>#171: the Auftritt the finalized Datei will belong to.</summary>
	public Guid? EventId { get; set; }

	/// <summary>#166: the <see cref="Lieder.DateiKinds"/> value of the Datei to create.</summary>
	public string? DateiKind { get; set; }

	public bool IsPartitur { get; set; }

	public string? Stimme { get; set; }

	public string? Description { get; set; }

	/// <summary>#167: Aufnahme of a Lied naming its Auftritt.</summary>
	public Guid? RecordedAtEventId { get; set; }

	/// <summary>#168: the Datei this upload replaces; finalize sends it to the trash.</summary>
	public Guid? ReplacesDateiId { get; set; }

	/// <summary>Idempotency reference to the Datei created on finalization.</summary>
	public Guid? FinalizedDateiId { get; set; }

	/// <summary>The unique pending object name; one session per object.</summary>
	public required string BlobName { get; set; }

	public required string ContentType { get; set; }

	public long MaxSizeBytes { get; set; }

	/// <summary>
	/// ARC-017: file identity declared at initiation and verified at
	/// finalization, so a stale session cannot commit a different file.
	/// </summary>
	public string? DeclaredFileName { get; set; }

	/// <summary>ARC-017: declared byte size matching <see cref="DeclaredFileName"/>.</summary>
	public long? DeclaredSizeBytes { get; set; }

	public DateTimeOffset UploadTicketExpiresAt { get; set; }

	public PendingUploadState State { get; set; }

	/// <summary>Idempotency reference to the revision created on finalization.</summary>
	public Guid? FinalizedRevisionId { get; set; }

	public FileRevision? FinalizedRevision { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}
