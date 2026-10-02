using Archive.Backend.Data;
using Archive.Backend.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// The finite extraction worker's drain loop (ARC-034): receives one batch at
/// a time, hands each message to the <see cref="ExtractionWorker"/> and
/// applies its disposition. The run is bounded by the message budget and the
/// wall-clock <see cref="ExtractionOptions.DrainBudget"/>; an empty receive
/// round ends it — no idle polling (production is re-triggered by the queue
/// scale rule). While maintenance is on, received messages are abandoned and
/// the whole run stops.
/// </summary>
public sealed class ExtractionPump(
	IExtractionMessageSource source,
	ExtractionWorker worker,
	IOptions<ExtractionOptions> options,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<ExtractionPump> logger)
{
	/// <summary>Processes messages until a bound or an empty batch ends the run; returns the processed count.</summary>
	public async Task<int> DrainAsync(CancellationToken token)
	{
		var processed = 0;
		var deadline = time.GetUtcNow() + options.Value.DrainBudget;
		while (processed < options.Value.MaxMessagesPerRun)
		{
			token.ThrowIfCancellationRequested();
			if (MaintenanceConfiguration.IsEnabled(configuration))
			{
				logger.LogInformation("Extraction drain paused: maintenance mode is enabled");
				break;
			}
			if (time.GetUtcNow() >= deadline)
			{
				logger.LogInformation("Extraction drain reached its run budget after {Processed} messages", processed);
				break;
			}
			var size = Math.Min(options.Value.BatchSize, options.Value.MaxMessagesPerRun - processed);
			var messages = await source.ReceiveBatchAsync(size, token);
			if (messages.Count == 0)
			{
				// Queue empty: the finite run exits without idle waiting.
				break;
			}
			foreach (var message in messages)
			{
				MessageDisposition disposition;
				try
				{
					disposition = await worker.HandleAsync(message, token);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					// The worker owns its failure handling; this is the last
					// resort so one broken message cannot stall the run.
					logger.LogWarning("Extraction message handling failed ({ExceptionType}); message abandoned",
						exception.GetType().Name);
					disposition = new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
				}
				if (disposition.Abandon)
					await source.AbandonAsync(message.MessageId, message.PopReceipt, disposition.Visibility, token);
				else
					await source.DeleteAsync(message.MessageId, message.PopReceipt, token);
				processed++;
			}
		}
		return processed;
	}
}

/// <summary>
/// The outbox sweeper (ARC-034): hands <see cref="ExtractionStatus.Queued"/>
/// rows whose enqueue stamp is missing or older than the redisplay window to
/// the queue, bounded per run. One broken row must not block the sweep; rows
/// whose send was not accepted (no queue backend) stay un-stamped and are
/// rediscovered later. The finite <c>--extract-queue</c> run sweeps before
/// draining (piggyback recovery); <c>--dispatch-extraction</c> runs it alone.
/// </summary>
public static class ExtractionDispatcher
{
	public static async Task<int> DispatchPendingAsync(ArchiveDbContext db, IExtractionQueue queue,
		IOptions<ExtractionOptions> options, TimeProvider time, ILogger logger, CancellationToken token)
	{
		var now = time.GetUtcNow();
		var staleBefore = now - options.Value.RedisplayAfter;
		var pending = await db.ExtractionJobs
			.Where(j => j.Status == ExtractionStatus.Queued
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
				accepted = await queue.SendAsync(job.RevisionId, token);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Type only: provider messages can carry URLs.
				logger.LogWarning("Extraction dispatch failed for revision {RevisionId} ({ExceptionType})",
					job.RevisionId, exception.GetType().Name);
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
				logger.LogWarning("Extraction dispatch stamp failed for revision {RevisionId} ({ExceptionType})",
					job.RevisionId, exception.GetType().Name);
				db.Entry(job).State = EntityState.Detached;
			}
		}
		return dispatched;
	}
}
