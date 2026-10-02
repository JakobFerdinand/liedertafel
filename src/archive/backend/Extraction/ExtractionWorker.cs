using System.Diagnostics;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// What the pump applies for one handled message: delete it (terminal —
/// including idempotent duplicates and deterministic failures) or abandon it
/// so it redisplay after the given visibility window (transient failures,
/// concurrent workers, paused maintenance).
/// </summary>
public sealed record MessageDisposition(bool Abandon, TimeSpan Visibility)
{
	/// <summary>The message was fully handled and leaves the queue.</summary>
	public static MessageDisposition Delete { get; } = new(Abandon: false, Visibility: TimeSpan.Zero);
}

/// <summary>
/// ARC-034 per-message extraction handling. The pump hands received messages
/// here; the worker parses the envelope, opens the correlated consumer span,
/// applies the idempotency matrix on the revision-keyed row and runs the
/// bounded PdfPig extraction. It never throws except parent cancellation:
/// deterministic problems delete the message, infrastructure problems take
/// the bounded transient path (row back to Queued, message abandoned with
/// visibility backoff) or the terminal failed state once attempts are
/// exhausted.
/// The caller supplies an absolute per-message deadline clamped by the batch's
/// receive visibility. It spans database work, blob open and parsing. PdfPig cannot interrupt a
/// synchronous document/page parse internally; a single page can overrun the
/// budget, but an expired outcome is never accepted. Decoded expansion remains
/// bounded by container memory, with stale-lease recovery after interruption.
/// </summary>
public sealed class ExtractionWorker(
	ArchiveDbContext db,
	IAssetStorageAdapter storage,
	IOptions<ExtractionOptions> options,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<ExtractionWorker> logger)
{
	/// <summary>ARC-034: larger documents fail with an honest German reason without opening the blob.</summary>
	public const string TooLargeReason = PdfTextExtractor.TooLargeReason;

	/// <summary>ARC-034: the bounded attempt budget is spent; the editor's retry action restarts the work.</summary>
	public const string ExhaustedReason = "Die Verarbeitung ist nach mehreren Versuchen fehlgescheitert.";

	/// <summary>Handles one envelope within the caller's absolute deadline.</summary>
	/// <param name="message">The received extraction envelope and queue receipt.</param>
	/// <param name="deadline">Absolute per-message deadline, clamped by the batch's receive visibility;
	/// covers database work, blob opening and parsing.</param>
	/// <param name="token">Parent cancellation, distinct from deadline expiry.</param>
	public async Task<MessageDisposition> HandleAsync(ExtractionMessage message, DateTimeOffset deadline, CancellationToken token)
	{
		// 1. Envelope: malformed JSON, a missing/unparsable revision id or a
		// foreign type is poison on this extraction-only queue — it can never
		// succeed, so the message is deleted. The body itself is never logged.
		ExtractionEnvelope? envelope = null;
		try
		{
			envelope = JsonSerializer.Deserialize<ExtractionEnvelope>(message.Body, ExtractionEnvelope.SerializerOptions);
		}
		catch (JsonException)
		{
			// Falls through to the poison disposition below.
		}
		if (envelope is null || envelope.Type != ExtractionEnvelope.ExtractionType || envelope.RevisionId == Guid.Empty)
		{
			logger.LogWarning("Extraction message discarded: not a valid extraction envelope");
			return MessageDisposition.Delete;
		}

		// 2. Correlated processing: the sender's trace context arrives as a
		// remote parent, mirroring the LocalServices pattern.
		ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out var parent);
		using (Extensions.Activities.StartActivity("archive.queue.process", ActivityKind.Consumer, parent))
		{
			return await ProcessAsync(envelope.RevisionId, message, deadline, token);
		}
	}

	private async Task<MessageDisposition> ProcessAsync(Guid revisionId, ExtractionMessage message, DateTimeOffset deadline, CancellationToken token)
	{
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
		var remaining = deadline - time.GetUtcNow();
		if (remaining <= TimeSpan.Zero)
			budget.Cancel();
		else
			budget.CancelAfter(remaining);
		var attempt = new AttemptState();
		try
		{
			budget.Token.ThrowIfCancellationRequested();
			return await ProcessAttemptAsync(revisionId, message, budget, attempt, token);
		}
		catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
		{
			logger.LogWarning("Extraction for revision {RevisionId} exceeded its time budget", revisionId);
			// Recovery must survive the expired processing token. Discard any
			// unsaved transition before checking the durable lease identity.
			db.ChangeTracker.Clear();
			if (!attempt.LeaseTaken)
				return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
			var job = await db.ExtractionJobs.FirstOrDefaultAsync(j => j.RevisionId == revisionId, token);
			if (job is null)
				return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
			if (job.Status is ExtractionStatus.Completed or ExtractionStatus.NoText or ExtractionStatus.Failed)
				return MessageDisposition.Delete;
			if (job.Status != ExtractionStatus.Running || job.RowVersion != attempt.LeaseRowVersion)
				return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
			return await RetryAsync(job, revisionId, message, attempt.LeaseAttemptCount, token);
		}
	}

	private async Task<MessageDisposition> ProcessAttemptAsync(Guid revisionId, ExtractionMessage message,
		CancellationTokenSource budget, AttemptState attempt, CancellationToken parentToken)
	{
		var token = budget.Token;
		// 3. Maintenance pause: the row stays untouched and the message
		// redisplay after the backoff window; the pump stops the run itself.
		if (MaintenanceConfiguration.IsEnabled(configuration))
		{
			logger.LogInformation("Extraction paused by maintenance mode for revision {RevisionId}", revisionId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		// 4. The revision is the ownership anchor of the result.
		var revision = await db.FileRevisions
			.Include(r => r.Asset)
			.FirstOrDefaultAsync(r => r.Id == revisionId, token);
		if (revision is null)
		{
			logger.LogInformation("Extraction revision {RevisionId} vanished; message deleted", revisionId);
			return MessageDisposition.Delete;
		}

		// 5. Stale/foreign material: only PDF-based kinds carry extraction work.
		if (!ExtractionService.IsExtractable(revision.Asset.AssetType)
			|| !string.Equals(revision.ContentType, AssetEndpoints.PdfContentType, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation("Extraction revision {RevisionId} is not extractable material; message deleted", revisionId);
			return MessageDisposition.Delete;
		}

		var now = time.GetUtcNow();

		// 6. Self-healing for revisions finalized before ARC-034: the row —
		// not the message — is the durable work record.
		var job = await db.ExtractionJobs
			.FirstOrDefaultAsync(j => j.RevisionId == revisionId, token);
		if (job is null)
		{
			job = ExtractionService.CreateForRevision(revision, now);
			db.ExtractionJobs.Add(job);
			await db.SaveChangesAsync(token);
		}

		// 7. Idempotency on the revision-keyed row: results are never
		// rewritten by a duplicate, and rework runs only through the editor's
		// explicit retry action.
		switch (job.Status)
		{
			case ExtractionStatus.Completed or ExtractionStatus.NoText:
				logger.LogDebug("Extraction for revision {RevisionId} is already terminal; duplicate deleted", revisionId);
				return MessageDisposition.Delete;
			case ExtractionStatus.Failed:
				logger.LogDebug("Extraction for revision {RevisionId} is failed; duplicate of the original send deleted", revisionId);
				return MessageDisposition.Delete;
			case ExtractionStatus.Running when !ExtractionService.HasStaleLease(job, now, options.Value):
				logger.LogInformation("Extraction for revision {RevisionId} is already running; message abandoned", revisionId);
				return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		// An interrupted final attempt has already spent the row's budget.
		// Conclude before re-taking the lease without claiming another attempt.
		if (job.AttemptCount >= options.Value.MaxAttempts)
		{
			logger.LogWarning("Extraction for revision {RevisionId} exhausted its attempts", revisionId);
			job.Status = ExtractionStatus.Failed;
			job.FailureReason = ExhaustedReason;
			job.CompletedAt = null;
			job.LastAttemptAt = now;
			job.UpdatedAt = now;
			job.RowVersion++;
			return await SaveAndConcludeAsync(job, revisionId, MessageDisposition.Delete, token);
		}

		// 8. Oversize precheck: the blob is never opened for a document the
		// bounds forbid; the failure is deterministic.
		if (job.Status != ExtractionStatus.Running && revision.SizeBytes > options.Value.MaxPdfBytes)
		{
			logger.LogInformation("Extraction for revision {RevisionId} refused: the document is too large", revisionId);
			job.Status = ExtractionStatus.Failed;
			job.FailureReason = TooLargeReason;
			job.LastAttemptAt = now;
			job.UpdatedAt = now;
			job.RowVersion++;
			return await SaveAndConcludeAsync(job, revisionId, MessageDisposition.Delete, token);
		}

		// 9. Counted attempt transition; a concurrent writer may win the row —
		// the message is abandoned and the next dequeue re-decides.
		job.Status = ExtractionStatus.Running;
		job.AttemptCount++;
		job.LastAttemptAt = now;
		job.UpdatedAt = now;
		job.RowVersion++;
		try
		{
			await db.SaveChangesAsync(token);
			attempt.LeaseTaken = true;
			attempt.LeaseRowVersion = job.RowVersion;
			attempt.LeaseAttemptCount = job.AttemptCount;
		}
		catch (DbUpdateConcurrencyException)
		{
			// The loser must not poison the shared context's later saves.
			db.Entry(job).State = EntityState.Detached;
			logger.LogInformation("Extraction for revision {RevisionId} lost the running transition; message abandoned", revisionId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		// A stale Running lease with budget remaining is re-taken and counted before the
		// deterministic bounds are reapplied, even for an oversize revision.
		if (revision.SizeBytes > options.Value.MaxPdfBytes)
		{
			job.Status = ExtractionStatus.Failed;
			job.FailureReason = TooLargeReason;
			job.RowVersion++;
			return await SaveAndConcludeAsync(job, revisionId, MessageDisposition.Delete, token);
		}

		// 10. Blob open and parsing share the database work's deadline token.
		Stream? stream = null;
		ExtractionOutcome? outcome = null;
		var transient = false;
		try
		{
			stream = await storage.OpenReadAsync(revision.BlobName, budget.Token);
			if (stream is null)
			{
				// The revision row exists but its object is missing — not a
				// property of the document bytes, so not deterministic.
				transient = true;
			}
			else
			{
				outcome = PdfTextExtractor.Extract(stream, options.Value, budget.Token);
				budget.Token.ThrowIfCancellationRequested();
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Infrastructure failure (storage/network): type only, never
			// provider details or URLs.
			logger.LogWarning("Extraction failed for revision {RevisionId} ({ExceptionType})", revisionId, exception.GetType().Name);
			transient = true;
		}
		finally
		{
			if (stream is not null)
				await stream.DisposeAsync();
		}

		// Disposal can also cross the deadline; no expired result is accepted.
		parentToken.ThrowIfCancellationRequested();
		transient |= budget.IsCancellationRequested;
		if (!transient)
		{
			var completedAt = time.GetUtcNow();
			job.UpdatedAt = completedAt;
			job.RowVersion++;
			if (outcome!.FailureReason is not null)
			{
				// Deterministic: the stored bytes themselves are unreadable.
				job.Status = ExtractionStatus.Failed;
				job.Text = null;
				job.FailureReason = outcome.FailureReason;
				job.CompletedAt = null;
				logger.LogWarning("Extraction for revision {RevisionId} failed deterministically", revisionId);
				return await SaveAndConcludeAsync(job, revisionId, MessageDisposition.Delete, token);
			}
			job.Status = outcome.NoText ? ExtractionStatus.NoText : ExtractionStatus.Completed;
			job.Text = outcome.NoText ? null : outcome.Text;
			job.FailureReason = null;
			job.CompletedAt = completedAt;
			logger.LogInformation("Extraction completed for revision {RevisionId} with status {Status}", revisionId, job.Status);
			return await SaveAndConcludeAsync(job, revisionId, MessageDisposition.Delete, token);
		}

		return await RetryAsync(job, revisionId, message, job.AttemptCount, parentToken);
	}

	private async Task<MessageDisposition> RetryAsync(ExtractionJob job, Guid revisionId,
		ExtractionMessage message, int attemptCount, CancellationToken token)
	{
		// Transient path: exhaustion (row budget or the message's own dequeue
		// count) is evaluated after the attempt bump. Terminal failure ends
		// the work; the editor's retry action restarts it fresh.
		if (attemptCount >= options.Value.MaxAttempts || message.DequeueCount >= options.Value.MaxAttempts)
		{
			logger.LogWarning("Extraction for revision {RevisionId} exhausted its attempts", revisionId);
			job.Status = ExtractionStatus.Failed;
			job.FailureReason = ExhaustedReason;
			job.CompletedAt = null;
			job.UpdatedAt = time.GetUtcNow();
			job.RowVersion++;
			return await SaveAndConcludeAsync(job, revisionId, MessageDisposition.Delete, token);
		}

		// The row must return to Queued before the message is abandoned — a
		// Running row would dead-lock every future dequeue on the abandon path.
		job.Status = ExtractionStatus.Queued;
		job.UpdatedAt = time.GetUtcNow();
		job.RowVersion++;
		var visibility = Backoff(message.DequeueCount);
		return await SaveAndConcludeAsync(job, revisionId,
			new MessageDisposition(Abandon: true, Visibility: visibility), token);
	}

	private sealed class AttemptState
	{
		public bool LeaseTaken { get; set; }
		public uint LeaseRowVersion { get; set; }
		public int LeaseAttemptCount { get; set; }
	}

	/// <summary>
	/// Saves the row; on a lost optimistic-concurrency race the row is
	/// reloaded fresh: a terminal winner means the work is done (delete),
	/// anything else keeps the message for another pass (abandon).
	/// </summary>
	private async Task<MessageDisposition> SaveAndConcludeAsync(ExtractionJob job, Guid revisionId,
		MessageDisposition onSaved, CancellationToken token)
	{
		try
		{
			await db.SaveChangesAsync(token);
			return onSaved;
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(job).State = EntityState.Detached;
			var fresh = await db.ExtractionJobs.AsNoTracking()
				.FirstOrDefaultAsync(j => j.RevisionId == revisionId, token);
			if (fresh is not null
				&& fresh.Status is ExtractionStatus.Completed or ExtractionStatus.NoText or ExtractionStatus.Failed)
			{
				logger.LogInformation("Extraction for revision {RevisionId} reached a terminal state concurrently", revisionId);
				return MessageDisposition.Delete;
			}
			logger.LogInformation("Extraction for revision {RevisionId} raced another writer; message abandoned", revisionId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}
	}

	/// <summary>
	/// Linear visibility backoff per dequeue, capped at
	/// <see cref="ExtractionOptions.RedisplayAfter"/> so repeated failures
	/// cannot starve the queue beyond the configured window.
	/// </summary>
	private TimeSpan Backoff(long dequeueCount) =>
		TimeSpan.FromTicks(Math.Min(
			options.Value.VisibilityBackoff.Ticks * dequeueCount,
			options.Value.RedisplayAfter.Ticks));
}
