using Archive.Backend.Ai;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>The finite reading worker's drain loop: the shared <see cref="QueueDrain"/> wired to <see cref="ReadingWorker"/>.</summary>
public sealed class ReadingPump(
	IReadingMessageSource source,
	Func<AsyncServiceScope> createWorkerScope,
	IOptions<ReadingOptions> options,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<ReadingPump> logger)
{
	private readonly QueueDrain drain = new(source, createWorkerScope,
		(services, message, deadline, token) =>
			services.GetRequiredService<ReadingWorker>().HandleAsync(message, deadline, token),
		() => new QueueDrainSettings("Reading", options.Value.VisibilityFitBatchSize, options.Value.ReceiveVisibility,
			options.Value.TimeBudget, options.Value.MaxMessagesPerRun, options.Value.DrainBudget, options.Value.VisibilityBackoff),
		configuration, time, logger);

	public Task<int> DrainAsync(CancellationToken token) => drain.DrainAsync(token);
}

/// <summary>
/// The reading outbox sweeper (ARC-034-1): hands <see cref="ReadingStatus.Queued"/>
/// rows without a recent enqueue to the queue and, first, wakes work that
/// waits for budget. Parked rows have no queue message, so this sweep is the
/// only thing that resumes them: it asks the budget whether a call of the
/// worst case would be admitted now (a new month, a raised cap) and re-queues
/// the parked rows only then, so a still-full month does not cycle them.
/// </summary>
public static class ReadingDispatcher
{
	public static async Task<int> DispatchPendingAsync(ArchiveDbContext db, IReadingQueue queue, IAiBudget budget,
		ScanReadingModel model, IOptions<ReadingOptions> options, TimeProvider time, ILogger logger, CancellationToken token)
	{
		var now = time.GetUtcNow();
		var parked = await db.ReadingJobs
			.Where(j => j.Status == ReadingStatus.WaitingForBudget)
			.OrderBy(j => j.UpdatedAt)
			.Take(options.Value.MaxDispatchPerRun)
			.ToListAsync(token);
		if (parked.Count > 0 && await budget.WouldAdmitAsync(model.WorstCaseTranscription(), token))
		{
			foreach (var job in parked)
			{
				job.Status = ReadingStatus.Queued;
				job.LastEnqueuedAt = null;
				job.UpdatedAt = now;
				job.RowVersion++;
			}
			try
			{
				await db.SaveChangesAsync(token);
				logger.LogInformation("Reading resumed {Count} jobs that waited for budget", parked.Count);
			}
			catch (DbUpdateConcurrencyException)
			{
				// An editor touched a row meanwhile; the next sweep decides again.
				db.ChangeTracker.Clear();
			}
		}

		var staleBefore = now - options.Value.RedisplayAfter;
		var pending = await db.ReadingJobs
			.Where(j => j.Status == ReadingStatus.Queued && (j.LastEnqueuedAt == null || j.LastEnqueuedAt < staleBefore))
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
				logger.LogWarning("Reading dispatch failed for Datei {DateiId} ({ExceptionType})",
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
				logger.LogWarning("Reading dispatch stamp failed for Datei {DateiId} ({ExceptionType})",
					job.DateiId, exception.GetType().Name);
				db.Entry(job).State = EntityState.Detached;
			}
		}
		return dispatched;
	}
}
