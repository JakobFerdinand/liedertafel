using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Archive.Backend.Assets;

/// <summary>How a revision became the current one of its asset (ARC-033).</summary>
public enum RevisionChangeKind
{
	/// <summary>A finalized upload created the revision and made it current.</summary>
	Upload = 0,

	/// <summary>An editor made an earlier retained revision current again.</summary>
	Restore = 1,
}

/// <summary>
/// One entry of an asset's editor-only pointer history (ARC-033): which
/// revision became current, which one it replaced, who did it and when.
/// Rows are append-only; file revisions themselves stay immutable, so making
/// an earlier revision current again adds an entry instead of a new revision.
/// </summary>
public sealed class RevisionChange
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid AssetId { get; set; }

	public ArchiveAsset Asset { get; set; } = null!;

	/// <summary>The revision that became current.</summary>
	public Guid RevisionId { get; set; }

	public FileRevision Revision { get; set; } = null!;

	/// <summary>The revision that was current before; null for the first upload.</summary>
	public Guid? PreviousRevisionId { get; set; }

	public RevisionChangeKind Kind { get; set; }

	public Guid ChangedByAccountId { get; set; }

	public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>
/// The revision-change contract (ARC-015 extension point, implemented by
/// ARC-033): the only place that moves
/// <see cref="ArchiveAsset.CurrentRevisionId"/>. A finalized upload and an
/// editor's restore both go through <see cref="MakeCurrentAsync"/>, so every
/// consumer sees one behaviour:
/// - the pointer swap, the token bump and the history entry are staged
///   together and committed by the caller's single <c>SaveChanges</c>;
/// - extraction (ARC-034) is keyed by revision, so the newly current PDF
///   revision gains its Queued row in the same save when it has none yet;
///   an existing row (and its text) is reused, never rewritten;
/// - search and other readers (ARC-035) resolve text and files through the
///   pointer, so eligibility follows the swap without a second signal.
/// </summary>
public static class RevisionChanges
{
	/// <summary>
	/// Stages the swap on the tracked <paramref name="asset"/>. Returns true
	/// when a new extraction row was staged and the caller should hand it to
	/// the queue after saving (<see cref="ExtractionService.TryEnqueueAsync"/>).
	/// </summary>
	public static async Task<bool> MakeCurrentAsync(
		ArchiveDbContext db, ArchiveAsset asset, FileRevision revision, RevisionChangeKind kind,
		Guid accountId, DateTimeOffset now, CancellationToken token)
	{
		db.RevisionChanges.Add(new RevisionChange
		{
			AssetId = asset.Id,
			RevisionId = revision.Id,
			PreviousRevisionId = asset.CurrentRevisionId,
			Kind = kind,
			ChangedByAccountId = accountId,
			ChangedAt = now,
		});
		asset.CurrentRevisionId = revision.Id;
		asset.RowVersion++;
		if (!ExtractionService.IsExtractable(asset.AssetType)
			|| !string.Equals(revision.ContentType, AssetEndpoints.PdfContentType, StringComparison.OrdinalIgnoreCase))
			return false;
		if (await db.ExtractionJobs.AnyAsync(j => j.RevisionId == revision.Id, token))
			return false;
		db.ExtractionJobs.Add(ExtractionService.CreateForRevision(asset, revision, accountId, now));
		return true;
	}

	/// <summary>
	/// True when the save definitely did not commit because a competing
	/// change won: the asset's token moved, or a unique key (revision number,
	/// extraction row) is already taken. Any other failure has an unknown
	/// outcome and must not be answered as a clean conflict.
	/// </summary>
	public static bool IsLostRace(DbUpdateException exception) =>
		exception is DbUpdateConcurrencyException
		|| exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
