using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Reading;

/// <summary>
/// Shared reading logic (ARC-034-1) used by the extraction hand-off, the
/// editor endpoints and the worker: row creation, the bounded quiet enqueue
/// and the rule which material is read at all. Only scores are read:
/// event documents carry no catalogue fields (a programme scan is ARC-025-1).
/// </summary>
public static class ReadingService
{
	private const int EnqueueAttempts = 2;

	public static bool IsReadable(string assetType) => assetType == AssetEndpoints.ScoreAssetType;

	public static ReadingJob CreateForRevision(FileRevision revision, Guid accountId, int pageLimit, DateTimeOffset now) => new()
	{
		RevisionId = revision.Id,
		AssetId = revision.AssetId,
		Status = ReadingStatus.Queued,
		PageLimit = pageLimit,
		TriggeredByAccountId = accountId,
		CreatedAt = now,
		UpdatedAt = now,
	};

	public static bool HasStaleLease(ReadingJob job, DateTimeOffset now, ReadingOptions options)
		=> job.LastAttemptAt is null || job.LastAttemptAt < now - options.StaleRunningAfter;

	/// <summary>
	/// Bounded quiet enqueue, like the extraction's: a rejected or failed send
	/// leaves <see cref="ReadingJob.LastEnqueuedAt"/> null so the sweep
	/// rediscovers the row. Only cancellation propagates.
	/// </summary>
	public static async Task<bool> TryEnqueueAsync(ArchiveDbContext db, IReadingQueue queue,
		ILogger logger, Guid revisionId, TimeProvider time, CancellationToken token)
	{
		for (var attempt = 0; attempt < EnqueueAttempts; attempt++)
		{
			var accepted = false;
			try
			{
				if (!await queue.SendAsync(revisionId, token))
					return false;
				accepted = true;
				var job = await db.ReadingJobs.FirstOrDefaultAsync(j => j.RevisionId == revisionId, token);
				if (job is not null)
				{
					job.LastEnqueuedAt = time.GetUtcNow();
					await db.SaveChangesAsync(token);
				}
				return true;
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogWarning("Reading enqueue failed: {ExceptionType}", exception.GetType().Name);
				if (accepted)
					return true;
			}
		}
		return false;
	}
}
