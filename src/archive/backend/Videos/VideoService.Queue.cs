using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Archive.Backend.Videos;

public partial class VideoService
{
	/// <summary>
	/// Creates the Queued row for a finalized video Datei (#161); the caller
	/// adds it to the change tracker so it is written in the same
	/// <c>SaveChanges</c> as the Datei (outbox). Also the worker's self-healing
	/// path: the Datei itself supplies the triggering account.
	/// </summary>
	public static VideoJob CreateForDatei(Datei datei, DateTimeOffset now) => new()
	{
		DateiId = datei.Id,
		Status = VideoStatus.Queued,
		TriggeredByAccountId = datei.CreatedByAccountId,
		CreatedAt = now,
		UpdatedAt = now,
	};

	/// <summary>Shared lease rule for worker recovery and the editor's retry action.</summary>
	public static bool HasStaleLease(VideoJob job, DateTimeOffset now, VideoOptions options) =>
		job.LastAttemptAt is null || job.LastAttemptAt < now - options.StaleRunningAfter;

	/// <summary>
	/// Bounded quiet enqueue (#161): up to two send attempts, catching the
	/// queue's failures and propagating only cancellation. On an accepted send
	/// the row's <see cref="VideoJob.LastEnqueuedAt"/> is stamped and saved; a
	/// rejected or failed send leaves it null so the dispatch sweeper
	/// rediscovers the row.
	/// </summary>
	public static async Task<bool> TryEnqueueAsync(ArchiveDbContext db, IVideoQueue queue,
		ILogger logger, Guid dateiId, TimeProvider time, CancellationToken token)
	{
		for (var attempt = 0; attempt < EnqueueAttempts; attempt++)
		{
			var accepted = false;
			try
			{
				if (!await queue.SendAsync(dateiId, token))
					return false;
				accepted = true;
				// Identity resolution returns the tracked row when the caller
				// just saved it, so the stamp rides the same context.
				var job = await db.VideoJobs.FirstOrDefaultAsync(j => j.DateiId == dateiId, token);
				if (job is not null)
				{
					job.LastEnqueuedAt = time.GetUtcNow();
					await db.SaveChangesAsync(token);
				}
				return true;
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Provider messages can contain signed URLs; log only the type.
				logger.LogWarning("Video transcode enqueue failed: {ExceptionType}", exception.GetType().Name);
				// An accepted message remains queued even when its stamp cannot
				// be saved. A later sweep may safely send an idempotent duplicate.
				if (accepted)
					return true;
			}
		}
		return false;
	}
}
