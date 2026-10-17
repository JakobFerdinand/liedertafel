using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Assets;

/// <summary>
/// #172: releases the blobs of the dropped asset/revision model. The contract
/// migration cannot reach Blob storage, so this finite operator command
/// (<c>--cleanup-dropped-blobs</c>) does it afterwards:
/// - everything under <c>revisions/</c> belonged to a file revision that no
///   longer exists and is deleted;
/// - objects under <c>pending/</c> are staged uploads; those no upload session
///   refers to any more (the old sessions went with the migration) are deleted;
/// - <c>dateien/</c> (the Dateien of the flat model) and any other name are
///   never touched.
/// A dry run only counts. Running it again finds nothing left.
/// </summary>
public sealed class DroppedBlobCleaner(
	ArchiveDbContext db, IAssetStorageAdapter storage, ILogger<DroppedBlobCleaner> logger)
{
	public const string RevisionPrefix = "revisions/";

	public const string StagingPrefix = "pending/";

	public sealed record Result(int Revisions, int Staged, int Kept, bool DryRun);

	public async Task<Result> CleanAsync(bool dryRun, CancellationToken cancellationToken)
	{
		var revisions = 0;
		await foreach (var name in storage.ListAsync(RevisionPrefix, cancellationToken))
		{
			if (!dryRun)
				await storage.DeleteAsync(name, cancellationToken);
			revisions++;
		}
		var referenced = (await db.UploadSessions.AsNoTracking().Select(s => s.BlobName).ToListAsync(cancellationToken))
			.ToHashSet(StringComparer.Ordinal);
		var staged = 0;
		var kept = 0;
		await foreach (var name in storage.ListAsync(StagingPrefix, cancellationToken))
		{
			if (referenced.Contains(name))
			{
				kept++;
				continue;
			}
			if (!dryRun)
				await storage.DeleteAsync(name, cancellationToken);
			staged++;
		}
		await foreach (var _ in storage.ListAsync(DateiPrefix, cancellationToken))
			kept++;
		logger.LogInformation(
			"Dropped-blob cleanup{Mode}: {Revisions} revision blobs, {Staged} orphaned staged uploads, {Kept} objects of the flat model kept",
			dryRun ? " (dry run)" : string.Empty, revisions, staged, kept);
		return new Result(revisions, staged, kept, dryRun);
	}

	private const string DateiPrefix = Lieder.DateiUploads.BlobPrefix;
}
