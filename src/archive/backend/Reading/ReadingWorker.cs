using System.Diagnostics;
using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>
/// ARC-034-1 per-message handling of the "AI reading" job. It mirrors the
/// extraction worker's contract — revision-keyed row, counted lease, bounded
/// retries, never throws except parent cancellation — and adds what the model
/// needs: step one transcribes the pages of a scan into the revision's text
/// (saving progress after each request), step two fills empty catalogue
/// fields from that text through <see cref="ScoreFieldPrefill"/>. A reached
/// monthly cap parks the row as <see cref="ReadingStatus.WaitingForBudget"/>
/// and acknowledges the message; the dispatch sweep wakes it later.
/// </summary>
public sealed class ReadingWorker(
	ArchiveDbContext db,
	IAssetStorageAdapter storage,
	IPdfPageRenderer renderer,
	ScanReadingModel model,
	ScoreFieldPrefill prefill,
	IOptions<ReadingOptions> options,
	IOptions<ExtractionOptions> extractionOptions,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<ReadingWorker> logger)
{
	public const string ExhaustedReason = "Das Lesen ist nach mehreren Versuchen fehlgeschlagen.";

	public const string TooLargeReason = "Das Dokument ist zu groß, um es automatisch zu lesen.";

	public const string UnreadableReason = "Das Dokument konnte nicht als Bilder gelesen werden.";

	public const string NothingReadReason = "Auf den Seiten konnte kein Text gelesen werden.";

	public const string ExtractionFailedReason = "Die Textauswertung ist gescheitert; das Lesen der Seiten kann nicht beginnen.";

	public const string InputTooLargeReason = "Die Seiten sind für die automatische Lesung zu groß.";

	/// <summary>Handles one envelope within the caller's absolute deadline.</summary>
	public async Task<MessageDisposition> HandleAsync(ExtractionMessage message, DateTimeOffset deadline, CancellationToken token)
	{
		ExtractionEnvelope? envelope = null;
		try
		{
			envelope = JsonSerializer.Deserialize<ExtractionEnvelope>(message.Body, ExtractionEnvelope.SerializerOptions);
		}
		catch (JsonException)
		{
			// Poison below.
		}
		if (envelope is null || envelope.Type != AzureReadingQueue.ReadingType || envelope.RevisionId == Guid.Empty)
		{
			logger.LogWarning("Reading message discarded: not a valid reading envelope");
			return MessageDisposition.Delete;
		}
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
			return await ProcessAttemptAsync(revisionId, message, budget.Token, attempt, token);
		}
		catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
		{
			logger.LogWarning("Reading for revision {RevisionId} exceeded its time budget", revisionId);
			return await RecoverAsync(revisionId, message, attempt, token);
		}
		catch (DbUpdateConcurrencyException)
		{
			// The editor changed the row while the job ran (retry, continue).
			db.ChangeTracker.Clear();
			logger.LogInformation("Reading for revision {RevisionId} raced another writer; message abandoned", revisionId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}
	}

	/// <summary>After an expired deadline: re-decide from the durable row, never from the interrupted run.</summary>
	private async Task<MessageDisposition> RecoverAsync(Guid revisionId, ExtractionMessage message, AttemptState attempt, CancellationToken token)
	{
		db.ChangeTracker.Clear();
		if (!attempt.LeaseTaken)
			return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
		var job = await db.ReadingJobs.FirstOrDefaultAsync(j => j.RevisionId == revisionId, token);
		if (job is null)
			return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
		if (job.Status is ReadingStatus.Completed or ReadingStatus.Failed or ReadingStatus.WaitingForBudget)
			return MessageDisposition.Delete;
		if (job.Status != ReadingStatus.Running || job.RowVersion < attempt.LeaseRowVersion)
			return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
		return await RetryAsync(job, revisionId, message, token);
	}

	private async Task<MessageDisposition> ProcessAttemptAsync(Guid revisionId, ExtractionMessage message,
		CancellationToken budget, AttemptState attempt, CancellationToken parentToken)
	{
		if (MaintenanceConfiguration.IsEnabled(configuration))
		{
			logger.LogInformation("Reading paused by maintenance mode for revision {RevisionId}", revisionId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}
		var revision = await db.FileRevisions.Include(r => r.Asset)
			.FirstOrDefaultAsync(r => r.Id == revisionId, budget);
		if (revision is null || !ReadingService.IsReadable(revision.Asset.AssetType)
			|| !string.Equals(revision.ContentType, AssetEndpoints.PdfContentType, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation("Reading revision {RevisionId} is not readable material; message deleted", revisionId);
			return MessageDisposition.Delete;
		}
		// The row, not the message, is the durable work record.
		var job = await db.ReadingJobs.FirstOrDefaultAsync(j => j.RevisionId == revisionId, budget);
		if (job is null)
		{
			logger.LogInformation("Reading revision {RevisionId} has no work row; message deleted", revisionId);
			return MessageDisposition.Delete;
		}
		var now = time.GetUtcNow();
		switch (job.Status)
		{
			case ReadingStatus.Completed or ReadingStatus.Failed:
				return MessageDisposition.Delete;
			case ReadingStatus.WaitingForBudget:
				// Parked work has no message: the sweep re-queues it, so a stray one is just dropped.
				return MessageDisposition.Delete;
			case ReadingStatus.Running when !ReadingService.HasStaleLease(job, now, options.Value):
				return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}
		if (job.AttemptCount >= options.Value.MaxAttempts)
			return await FinishAsync(job, ReadingStatus.Failed, ExhaustedReason, now, token: budget);

		job.Status = ReadingStatus.Running;
		job.AttemptCount++;
		job.LastAttemptAt = now;
		job.UpdatedAt = now;
		job.RowVersion++;
		try
		{
			await db.SaveChangesAsync(budget);
			attempt.LeaseTaken = true;
			attempt.LeaseRowVersion = job.RowVersion;
		}
		catch (DbUpdateConcurrencyException)
		{
			db.Entry(job).State = EntityState.Detached;
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		var extraction = await db.ExtractionJobs.FirstOrDefaultAsync(j => j.RevisionId == revisionId, budget);
		if (extraction is null || extraction.Status is ExtractionStatus.Queued or ExtractionStatus.Running)
			return await RetryAsync(job, revisionId, message, parentToken);
		if (extraction.Status == ExtractionStatus.Failed)
			return await FinishAsync(job, ReadingStatus.Failed, ExtractionFailedReason, time.GetUtcNow(), token: budget);

		StepOutcome outcome;
		var progress = new PrefillProgress();
		try
		{
			outcome = await ExecuteAsync(job, extraction, revision, progress, budget);
		}
		catch (OperationCanceledException) when (budget.IsCancellationRequested)
		{
			throw;
		}
		catch (DbUpdateConcurrencyException)
		{
			throw;
		}
		catch (Exception exception) when (Find<AiBudgetExceededException>(exception))
		{
			outcome = StepOutcome.Waiting;
		}
		catch (Exception exception) when (Find<AiInputTooLargeException>(exception))
		{
			outcome = StepOutcome.Fail(InputTooLargeReason);
		}
		catch (Exception exception) when (Find<UnreadablePdfException>(exception))
		{
			outcome = StepOutcome.Fail(UnreadableReason);
		}
		catch (Exception exception)
		{
			// Provider, storage or parsing trouble: type only, never the
			// message (it can carry URLs or text of the scan).
			logger.LogWarning("Reading failed for revision {RevisionId} ({ExceptionType})", revisionId, exception.GetType().Name);
			outcome = StepOutcome.Transient;
		}
		finally
		{
			job.FieldsApplied += progress.Applied;
			job.FieldsProposed += progress.Proposed;
		}

		var finished = time.GetUtcNow();
		switch (outcome.Kind)
		{
			case StepKind.Done:
				job.FieldsDone = true;
				return await FinishAsync(job, ReadingStatus.Completed, null, finished, token: parentToken);
			case StepKind.Failed:
				return await FinishAsync(job, ReadingStatus.Failed, outcome.Reason, finished, token: parentToken);
			case StepKind.Waiting:
				// The cap, not the attempt, ended the run: it does not use up one.
				job.AttemptCount = Math.Max(0, job.AttemptCount - 1);
				return await FinishAsync(job, ReadingStatus.WaitingForBudget, null, finished, token: parentToken);
			default:
				return await RetryAsync(job, revisionId, message, parentToken);
		}
	}

	/// <summary>Runs step one (pages to text) and step two (text to fields) with progress saved as it goes.</summary>
	private async Task<StepOutcome> ExecuteAsync(ReadingJob job, ExtractionJob extraction, FileRevision revision,
		PrefillProgress progress, CancellationToken token)
	{
		var settings = options.Value;
		var cap = extractionOptions.Value.MaxTextCharacters;
		if (extraction.Status == ExtractionStatus.NoText || extraction.TextSource == ExtractionTextSource.Reading)
		{
			byte[]? pdf = null;
			while (job.PagesTotal is null || (job.PagesRead < job.PagesTotal && job.PagesRead < job.PageLimit))
			{
				token.ThrowIfCancellationRequested();
				if ((extraction.Text?.Length ?? 0) >= cap)
					break;
				if (pdf is null)
				{
					if (revision.SizeBytes > settings.MaxPdfBytes)
						return StepOutcome.Fail(TooLargeReason);
					pdf = await LoadPdfAsync(revision, token);
					if (pdf is null)
						return StepOutcome.Transient;
				}
				job.PagesTotal ??= renderer.PageCount(pdf);
				var remaining = Math.Min(job.PageLimit, job.PagesTotal.Value) - job.PagesRead;
				if (remaining <= 0)
					break;
				var pages = renderer.Render(pdf, job.PagesRead + 1, Math.Min(settings.ImagesPerCall, remaining),
					settings.MaxImageLongSide, settings.JpegQuality, token);
				if (pages.Count == 0)
				{
					job.PagesTotal = job.PagesRead;
					break;
				}
				var text = await model.TranscribeAsync(pages, job.TriggeredByAccountId, token);
				Append(extraction, text, cap, time.GetUtcNow());
				job.PagesRead += pages.Count;
				job.UpdatedAt = time.GetUtcNow();
				job.RowVersion++;
				// Pages already paid for are never read twice: progress is durable.
				await db.SaveChangesAsync(token);
			}
			if (string.IsNullOrWhiteSpace(extraction.Text))
				return StepOutcome.Fail(NothingReadReason);
		}
		if (!job.FieldsDone && !string.IsNullOrWhiteSpace(extraction.Text))
		{
			await prefill.RunAsync(revision.Asset, revision, extraction.Text, job.TriggeredByAccountId, progress, token);
		}
		return StepOutcome.Finished;
	}

	/// <summary>Adds a transcription to the revision's text (bounded) and makes the row a text row.</summary>
	private static void Append(ExtractionJob extraction, string transcription, int cap, DateTimeOffset now)
	{
		if (string.IsNullOrWhiteSpace(transcription))
			return;
		var existing = extraction.Text ?? string.Empty;
		var joined = existing.Length == 0 ? transcription : existing + "\n" + transcription;
		extraction.Text = joined.Length > cap ? joined[..cap] : joined;
		extraction.Status = ExtractionStatus.Completed;
		extraction.TextSource = ExtractionTextSource.Reading;
		extraction.FailureReason = null;
		extraction.CompletedAt ??= now;
		extraction.UpdatedAt = now;
		extraction.RowVersion++;
	}

	private async Task<byte[]?> LoadPdfAsync(FileRevision revision, CancellationToken token)
	{
		await using var stream = await storage.OpenReadAsync(revision.BlobName, token);
		if (stream is null)
			return null;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, token);
		return buffer.ToArray();
	}

	private async Task<MessageDisposition> FinishAsync(ReadingJob job, ReadingStatus status, string? reason,
		DateTimeOffset now, CancellationToken token)
	{
		job.Status = status;
		job.FailureReason = status == ReadingStatus.Failed ? reason : null;
		job.CompletedAt = status == ReadingStatus.Completed ? now : null;
		job.UpdatedAt = now;
		job.RowVersion++;
		try
		{
			await db.SaveChangesAsync(token);
			return MessageDisposition.Delete;
		}
		catch (DbUpdateConcurrencyException)
		{
			db.ChangeTracker.Clear();
			var fresh = await db.ReadingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.RevisionId == job.RevisionId, token);
			if (fresh is not null && fresh.Status is ReadingStatus.Completed or ReadingStatus.Failed or ReadingStatus.WaitingForBudget)
				return MessageDisposition.Delete;
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}
	}

	private async Task<MessageDisposition> RetryAsync(ReadingJob job, Guid revisionId, ExtractionMessage message, CancellationToken token)
	{
		if (job.AttemptCount >= options.Value.MaxAttempts || message.DequeueCount >= options.Value.MaxAttempts)
		{
			logger.LogWarning("Reading for revision {RevisionId} exhausted its attempts", revisionId);
			return await FinishAsync(job, ReadingStatus.Failed, ExhaustedReason, time.GetUtcNow(), token);
		}
		// Back to Queued before the message is abandoned: a Running row would
		// block every later dequeue.
		job.Status = ReadingStatus.Queued;
		job.UpdatedAt = time.GetUtcNow();
		job.RowVersion++;
		try
		{
			await db.SaveChangesAsync(token);
		}
		catch (DbUpdateConcurrencyException)
		{
			db.ChangeTracker.Clear();
		}
		return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
	}

	private TimeSpan Backoff(long dequeueCount) =>
		TimeSpan.FromTicks(Math.Min(
			options.Value.VisibilityBackoff.Ticks * dequeueCount,
			options.Value.RedisplayAfter.Ticks));

	/// <summary>The framework layers may wrap what the gateway threw; look through them.</summary>
	private static bool Find<T>(Exception exception) where T : Exception
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (current is T)
				return true;
		}
		return false;
	}

	private sealed class AttemptState
	{
		public bool LeaseTaken { get; set; }
		public uint LeaseRowVersion { get; set; }
	}

	private enum StepKind
	{
		Done,
		Failed,
		Waiting,
		Transient,
	}

	private sealed record StepOutcome(StepKind Kind, string? Reason = null)
	{
		public static StepOutcome Finished { get; } = new(StepKind.Done);
		public static StepOutcome Waiting { get; } = new(StepKind.Waiting);
		public static StepOutcome Transient { get; } = new(StepKind.Transient);
		public static StepOutcome Fail(string reason) => new(StepKind.Failed, reason);
	}
}
