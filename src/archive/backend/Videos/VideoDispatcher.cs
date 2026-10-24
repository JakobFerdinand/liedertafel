using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Videos;

/// <summary>
/// The video worker's drain loop (#161): the shared
/// <see cref="QueueDrain"/> wired to <see cref="VideoWorker"/> and the video
/// bounds — exactly the extraction/reading pump shape.
/// </summary>
public sealed class VideoPump(
	IVideoMessageSource source,
	Func<AsyncServiceScope> createWorkerScope,
	IOptions<VideoOptions> options,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<VideoPump> logger)
{
	private readonly QueueDrain drain = new(source, createWorkerScope,
		(services, message, deadline, token) =>
			services.GetRequiredService<VideoWorker>().HandleAsync(message, deadline, token),
		() => new QueueDrainSettings("Transcode", options.Value.VisibilityFitBatchSize, options.Value.ReceiveVisibility,
			options.Value.TimeBudget, options.Value.MaxMessagesPerRun, options.Value.DrainBudget, options.Value.VisibilityBackoff),
		configuration, time, logger);

	/// <summary>Processes messages until a bound or an empty batch ends the run; returns the processed count.</summary>
	public Task<int> DrainAsync(CancellationToken token) => drain.DrainAsync(token);
}

/// <summary>
/// The outbox sweeper (#161): hands <see cref="VideoStatus.Queued"/> rows
/// whose enqueue stamp is missing or older than the redisplay window to the
/// queue, bounded per run — the extraction sweeper's shape on video-queue rows.
/// The finite <c>--transcode-queue</c> run sweeps before draining (piggyback
/// recovery); <c>--dispatch-videos</c> runs it alone.
/// </summary>
public static class VideoDispatcher
{
	public static async Task<int> DispatchPendingAsync(ArchiveDbContext db, IVideoQueue queue,
		IOptions<VideoOptions> options, TimeProvider time, ILogger logger, CancellationToken token)
	{
		var now = time.GetUtcNow();
		var staleBefore = now - options.Value.RedisplayAfter;
		var pending = await db.VideoJobs
			.Where(j => j.Status == VideoStatus.Queued
				&& (j.LastEnqueuedAt == null || j.LastEnqueuedAt < staleBefore))
			.OrderBy(j => j.CreatedAt)
			.Take(options.Value.MaxDispatchPerRun)
			.ToListAsync(token);
		var dispatched = 0;
		foreach (var job in pending)
		{
			token.ThrowIfCancellationRequested();
			bool accepted;
			try
			{
				accepted = await queue.SendAsync(job.DateiId, token);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Type only: provider messages can carry URLs.
				logger.LogWarning("Video dispatch failed for Datei {DateiId} ({ExceptionType})",
					job.DateiId, exception.GetType().Name);
				continue;
			}
			if (!accepted)
				continue;
			job.LastEnqueuedAt = now;
			try
			{
				await db.SaveChangesAsync(token);
				dispatched++;
			}
			catch (DbUpdateException exception)
			{
				// The send was accepted; a lost stamp race only costs an
				// idempotent duplicate later. Type only, next row.
				logger.LogWarning("Video dispatch stamp failed for Datei {DateiId} ({ExceptionType})",
					job.DateiId, exception.GetType().Name);
				db.Entry(job).State = EntityState.Detached;
			}
		}
		return dispatched;
	}
}
