using Archive.Backend.Data;
using Archive.Backend.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>Per-run bounds of a finite queue drain (extraction and AI reading share the loop).</summary>
public sealed record QueueDrainSettings(
	string Label, int VisibilityFitBatchSize, TimeSpan ReceiveVisibility, TimeSpan TimeBudget,
	int MaxMessagesPerRun, TimeSpan DrainBudget, TimeSpan VisibilityBackoff);

/// <summary>
/// The finite workers' drain loop (ARC-034, shared with ARC-034-1's reading
/// job): receives one batch at a time, hands each message to the worker the
/// caller names and applies its disposition. The run is bounded by the
/// message budget and the wall-clock drain budget; an empty receive round
/// ends it — no idle polling (production is re-triggered by the queue scale
/// rule). While maintenance is on, received messages are abandoned and the
/// whole run stops. The scope factory creates and disposes a fresh
/// worker/DbContext per message, so editor retries between rounds cannot be
/// hidden by tracked terminal rows.
/// </summary>
public sealed class QueueDrain(
	IExtractionMessageSource source,
	Func<AsyncServiceScope> createWorkerScope,
	Func<IServiceProvider, ExtractionMessage, DateTimeOffset, CancellationToken, Task<MessageDisposition>> handle,
	Func<QueueDrainSettings> settings,
	IConfiguration configuration,
	TimeProvider time,
	ILogger logger)
{
	/// <summary>Processes messages until a bound or an empty batch ends the run; returns the processed count.</summary>
	public async Task<int> DrainAsync(CancellationToken token)
	{
		var run = settings();
		var processed = 0;
		var drainDeadline = time.GetUtcNow() + run.DrainBudget;
		while (processed < run.MaxMessagesPerRun)
		{
			token.ThrowIfCancellationRequested();
			if (MaintenanceConfiguration.IsEnabled(configuration))
			{
				logger.LogInformation("{Label} drain paused: maintenance mode is enabled", run.Label);
				break;
			}
			if (time.GetUtcNow() >= drainDeadline)
			{
				logger.LogInformation("{Label} drain reached its run budget after {Processed} messages", run.Label, processed);
				break;
			}
			var size = Math.Min(run.VisibilityFitBatchSize, run.MaxMessagesPerRun - processed);
			var batchStart = time.GetUtcNow();
			var deadline = batchStart + run.ReceiveVisibility;
			var messages = await source.ReceiveBatchAsync(size, token);
			if (messages.Count == 0)
			{
				// Queue empty: the finite run exits without idle waiting.
				break;
			}
			var batchExhausted = false;
			foreach (var message in messages)
			{
				token.ThrowIfCancellationRequested();
				var now = time.GetUtcNow();
				batchExhausted |= now >= deadline;
				if (batchExhausted || now >= drainDeadline || MaintenanceConfiguration.IsEnabled(configuration))
				{
					// Release all unprocessed receipts immediately, including the
					// rest of this batch, rather than waiting out receive visibility.
					await ApplyDispositionAsync(message,
						new MessageDisposition(Abandon: true, Visibility: TimeSpan.Zero), run, token);
					continue;
				}
				MessageDisposition disposition;
				try
				{
					await using var workerScope = createWorkerScope();
					var messageDeadline = now + run.TimeBudget;
					if (messageDeadline > deadline)
						messageDeadline = deadline;
					disposition = await handle(workerScope.ServiceProvider, message, messageDeadline, token);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					// The worker owns its failure handling; this is the last
					// resort so one broken message cannot stall the run.
					logger.LogWarning("{Label} message handling failed ({ExceptionType}); message abandoned",
						run.Label, exception.GetType().Name);
					disposition = new MessageDisposition(Abandon: true, Visibility: run.VisibilityBackoff);
				}
				await ApplyDispositionAsync(message, disposition, run, token);
				processed++;
			}
			if (batchExhausted)
				break;
		}
		return processed;
	}

	private async Task ApplyDispositionAsync(ExtractionMessage message, MessageDisposition disposition,
		QueueDrainSettings run, CancellationToken token)
	{
		try
		{
			if (disposition.Abandon)
				await source.AbandonAsync(message.MessageId, message.PopReceipt, disposition.Visibility, token);
			else
				await source.DeleteAsync(message.MessageId, message.PopReceipt, token);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// An expired receipt costs a redisplay, never the entire drain.
			logger.LogWarning("{Label} message disposition failed ({ExceptionType})", run.Label, exception.GetType().Name);
		}
	}
}

/// <summary>
/// The finite extraction worker's drain loop (ARC-034): the shared
/// <see cref="QueueDrain"/> wired to <see cref="ExtractionWorker"/> and the
/// extraction bounds.
/// </summary>
public sealed class ExtractionPump(
	IExtractionMessageSource source,
	Func<AsyncServiceScope> createWorkerScope,
	IOptions<ExtractionOptions> options,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<ExtractionPump> logger)
{
	private readonly QueueDrain drain = new(source, createWorkerScope,
		(services, message, deadline, token) =>
			services.GetRequiredService<ExtractionWorker>().HandleAsync(message, deadline, token),
		() => new QueueDrainSettings("Extraction", options.Value.VisibilityFitBatchSize, options.Value.ReceiveVisibility,
			options.Value.TimeBudget, options.Value.MaxMessagesPerRun, options.Value.DrainBudget, options.Value.VisibilityBackoff),
		configuration, time, logger);

	/// <summary>Processes messages until a bound or an empty batch ends the run; returns the processed count.</summary>
	public Task<int> DrainAsync(CancellationToken token) => drain.DrainAsync(token);
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
				accepted = await queue.SendAsync(job.DateiId, token);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Type only: provider messages can carry URLs.
				logger.LogWarning("Extraction dispatch failed for Datei {DateiId} ({ExceptionType})",
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
				logger.LogWarning("Extraction dispatch stamp failed for Datei {DateiId} ({ExceptionType})",
					job.DateiId, exception.GetType().Name);
				db.Entry(job).State = EntityState.Detached;
			}
		}
		return dispatched;
	}
}
