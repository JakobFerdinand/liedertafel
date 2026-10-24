using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Archive.Backend.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Videos;

/// <summary>
/// #161 per-message transcode handling. The pump hands received messages
/// here; the worker parses the envelope, opens the correlated consumer span,
/// applies the idempotency matrix on the Datei-keyed row and runs the
/// deterministic ffmpeg transcode. It never throws except parent
/// cancellation: deterministic problems delete the message, infrastructure
/// problems take the bounded transient path (row back to Queued, message
/// abandoned with visibility backoff) or the terminal failed state once
/// attempts are exhausted.
/// The result is a real playback <see cref="Datei"/> of the same owner
/// (MP4, H.264 CRF 20, AAC 192 kbit/s, fast start, display size up to
/// 1080p-equivalent) that members play and editors keep the original
/// alongside; an Auftritt recording's empty playback slot receives it in the
/// same save. Already-playable originals up to 1080p end in
/// <see cref="VideoStatus.NotNeeded"/> — one object, no duplicate.
/// </summary>
public sealed class VideoWorker(
	ArchiveDbContext db,
	IAssetStorageAdapter storage,
	IVideoTranscoder transcoder,
	IOptions<VideoOptions> options,
	IConfiguration configuration,
	TimeProvider time,
	ILogger<VideoWorker> logger)
{
	/// <summary>The attempt budget is spent; the editor's retry action restarts the work.</summary>
	public const string ExhaustedReason = "Die Konvertierung ist nach mehreren Versuchen fehlgeschlagen.";

	/// <summary>Stored bytes the decoding tools cannot open at all.</summary>
	public const string UnreadableVideoReason = "Die Aufnahme konnte nicht gelesen werden.";

	/// <summary>Bytes ffmpeg declared unreadable mid-encode.</summary>
	public const string TranscodeFailedReason = "Die Video-Konvertierung ist fehlgeschlagen.";

	/// <summary>The same bytes without any video stream at all.</summary>
	public const string NoVideoStreamReason = "Die Aufnahme enthält keinen verwertbaren Video-Stream.";

	/// <summary>Too-large inputs never open the tooling.</summary>
	public const string TooLargeReason = "Die Aufnahme ist zu groß für die automatische Konvertierung.";

	/// <summary>The deterministic environment failure of a host without ffmpeg.</summary>
	public const string MissingToolReason =
		"Die Konvertierung benötigt ffmpeg, das auf dem Server nicht verfügbar ist. Nach dem Beheben der Einrichtung erneut versuchen.";

	/// <summary>The output of one transcode is bounded input, too: the streaming download stops at MaxInputBytes.</summary>
	private const string CopyContentType = "video/mp4";

	/// <summary>The file name suffix naming the purpose of the copy.</summary>
	public const string CopyNameSuffix = " - Abspielkopie";

	/// <summary>Handles one envelope within the caller's absolute deadline.</summary>
	/// <param name="message">The received transcode envelope and queue receipt.</param>
	/// <param name="deadline">Absolute per-message deadline, clamped by the batch's receive visibility;
	/// covers database work, the blob download and the encoding.</param>
	/// <param name="token">Parent cancellation, distinct from deadline expiry.</param>
	public async Task<MessageDisposition> HandleAsync(ExtractionMessage message, DateTimeOffset deadline, CancellationToken token)
	{
		ExtractionEnvelope? envelope = null;
		try
		{
			envelope = JsonSerializer.Deserialize<ExtractionEnvelope>(message.Body, ExtractionEnvelope.SerializerOptions);
		}
		catch (JsonException)
		{
			// Falls through to the poison disposition below.
		}
		if (envelope is null || envelope.Type != AzureVideoQueue.TranscodeType || envelope.DateiId == Guid.Empty)
		{
			logger.LogWarning("Transcode message discarded: not a valid transcode envelope");
			return MessageDisposition.Delete;
		}
		// Correlated processing: the sender's trace context arrives as a
		// remote parent, mirroring the extraction worker's pattern.
		ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out var parent);
		using (Extensions.Activities.StartActivity("archive.queue.process", ActivityKind.Consumer, parent))
		{
			return await ProcessAsync(envelope.DateiId, message, deadline, token);
		}
	}

	private async Task<MessageDisposition> ProcessAsync(Guid dateiId, ExtractionMessage message,
		DateTimeOffset deadline, CancellationToken token)
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
			return await ProcessAttemptAsync(dateiId, message, budget, attempt, token);
		}
		catch (OperationCanceledException) when (budget.IsCancellationRequested && !token.IsCancellationRequested)
		{
			logger.LogWarning("Transcode for Datei {DateiId} exceeded its time budget", dateiId);
			// Recovery must survive the expired processing token. Discard any
			// unsaved transition before checking the durable lease identity.
			db.ChangeTracker.Clear();
			if (!attempt.LeaseTaken)
				return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
			var job = await db.VideoJobs.FirstOrDefaultAsync(j => j.DateiId == dateiId, token);
			if (job is null)
				return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
			if (job.Status is VideoStatus.Completed or VideoStatus.NotNeeded or VideoStatus.Failed)
				return MessageDisposition.Delete;
			if (job.Status != VideoStatus.Running || job.RowVersion != attempt.LeaseRowVersion)
				return new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount));
			return await RetryAsync(job, message, token);
		}
	}

	private sealed class AttemptState
	{
		public bool LeaseTaken { get; set; }
		public uint LeaseRowVersion { get; set; }
	}

	/// <summary>
	/// Saves the row; on a lost optimistic-concurrency race the row is
	/// reloaded fresh: a terminal winner means the work is done (delete),
	/// anything else keeps the message for another pass (abandon).
	/// </summary>
	private async Task<MessageDisposition> SaveAndConcludeAsync(VideoJob job,
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
			var fresh = await db.VideoJobs.AsNoTracking()
				.FirstOrDefaultAsync(j => j.DateiId == job.DateiId, token);
			if (fresh is not null
				&& fresh.Status is VideoStatus.Completed or VideoStatus.NotNeeded or VideoStatus.Failed)
			{
				logger.LogInformation("Transcode for Datei {DateiId} reached a terminal state concurrently", job.DateiId);
				return MessageDisposition.Delete;
			}
			logger.LogInformation("Transcode for Datei {DateiId} raced another writer; message abandoned", job.DateiId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}
	}

	/// <summary>
	/// Linear visibility backoff per dequeue, capped at
	/// <see cref="VideoOptions.RedisplayAfter"/> so repeated failures cannot
	/// starve the queue beyond the configured window.
	/// </summary>
	private TimeSpan Backoff(long dequeueCount) =>
		TimeSpan.FromTicks(Math.Min(
			options.Value.VisibilityBackoff.Ticks * dequeueCount,
			options.Value.RedisplayAfter.Ticks));

	private async Task<MessageDisposition> RetryAsync(VideoJob job, ExtractionMessage message, CancellationToken token)
	{
		// Transient path: exhaustion (row budget or the message's own dequeue
		// count) is evaluated after the attempt bump. Terminal failure ends
		// the work; the editor's retry action restarts it fresh.
		if (job.AttemptCount >= options.Value.MaxAttempts || message.DequeueCount >= options.Value.MaxAttempts)
		{
			logger.LogWarning("Transcode for Datei {DateiId} exhausted its attempts", job.DateiId);
			job.Status = VideoStatus.Failed;
			job.FailureReason = ExhaustedReason;
			job.CompletedAt = null;
			job.UpdatedAt = time.GetUtcNow();
			job.RowVersion++;
			return await SaveAndConcludeAsync(job, MessageDisposition.Delete, token);
		}

		// The row must return to Queued before the message is abandoned — a
		// Running row would dead-lock every future dequeue on the abandon path.
		job.Status = VideoStatus.Queued;
		job.UpdatedAt = time.GetUtcNow();
		job.RowVersion++;
		return await SaveAndConcludeAsync(job,
			new MessageDisposition(Abandon: true, Visibility: Backoff(message.DequeueCount)), token);
	}

	private async Task<MessageDisposition> ProcessAttemptAsync(Guid dateiId, ExtractionMessage message,
		CancellationTokenSource budget, AttemptState attempt, CancellationToken parentToken)
	{
		var token = budget.Token;
		// 1. Maintenance pause: the row stays untouched and the message
		// redisplay after the backoff window; the pump stops the run itself.
		if (MaintenanceConfiguration.IsEnabled(configuration))
		{
			logger.LogInformation("Transcode paused by maintenance mode for Datei {DateiId}", dateiId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		// 2. The Datei is the ownership anchor of the copy; a Datei in the
		// trash is no longer worked on.
		var datei = await DateiQueries.Visible(db)
			.FirstOrDefaultAsync(d => d.Id == dateiId, token);
		if (datei is null)
		{
			logger.LogInformation("Transcode Datei {DateiId} vanished or is in the trash; message deleted", dateiId);
			return MessageDisposition.Delete;
		}

		// 3. Stale/foreign material: only video Aufnahmen take copies.
		if (!VideoService.IsVideoMaterial(datei.Kind, datei.ContentType))
		{
			logger.LogInformation("Transcode Datei {DateiId} is not video material; message deleted", dateiId);
			return MessageDisposition.Delete;
		}

		var now = time.GetUtcNow();

		// 4. Self-healing for Dateien finalized before #161: the row — not the
		// message — is the durable work record.
		var job = await db.VideoJobs
			.FirstOrDefaultAsync(j => j.DateiId == dateiId, token);
		if (job is null)
		{
			job = VideoService.CreateForDatei(datei, now);
			db.VideoJobs.Add(job);
			await db.SaveChangesAsync(token);
		}

		// 5. Idempotency on the Datei-keyed row: copies are never rewritten by
		// a duplicate, and rework runs only through the editor's explicit
		// retry action.
		switch (job.Status)
		{
			case VideoStatus.Completed or VideoStatus.NotNeeded:
				logger.LogDebug("Transcode for Datei {DateiId} is already terminal; duplicate deleted", dateiId);
				return MessageDisposition.Delete;
			case VideoStatus.Failed:
				logger.LogDebug("Transcode for Datei {DateiId} is failed; duplicate of the original send deleted", dateiId);
				return MessageDisposition.Delete;
			case VideoStatus.Running when !VideoService.HasStaleLease(job, now, options.Value):
				logger.LogInformation("Transcode for Datei {DateiId} is already running; message abandoned", dateiId);
				return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		// An interrupted final attempt has already spent the row's budget.
		// Conclude before re-taking the lease without claiming another attempt.
		if (job.AttemptCount >= options.Value.MaxAttempts)
		{
			logger.LogWarning("Transcode for Datei {DateiId} exhausted its attempts", dateiId);
			return await MarkFailedAsync(job, ExhaustedReason, MessageDisposition.Delete, token);
		}

		// 6. Oversize precheck: the tools never open an input the bounds
		// forbid; the failure is deterministic.
		if (datei.SizeBytes > options.Value.MaxInputBytes)
		{
			logger.LogInformation("Transcode for Datei {DateiId} refused: the recording is too large", dateiId);
			return await MarkFailedAsync(job, TooLargeReason, MessageDisposition.Delete, token);
		}

		// 7. Counted attempt transition; a concurrent writer may win the row —
		// the message is abandoned and the next dequeue re-decides.
		job.Status = VideoStatus.Running;
		job.AttemptCount++;
		job.LastAttemptAt = now;
		job.UpdatedAt = now;
		job.RowVersion++;
		try
		{
			await db.SaveChangesAsync(token);
			attempt.LeaseTaken = true;
			attempt.LeaseRowVersion = job.RowVersion;
		}
		catch (DbUpdateConcurrencyException)
		{
			// The loser must not poison the shared context's later saves.
			db.Entry(job).State = EntityState.Detached;
			logger.LogInformation("Transcode for Datei {DateiId} lost the running transition; message abandoned", dateiId);
			return new MessageDisposition(Abandon: true, Visibility: options.Value.VisibilityBackoff);
		}

		// 8. The deterministic work itself: probe the decoded size, decide,
		// encode, upload and attach in one save.
		try
		{
			var outcome = await TranscodeAsync(datei, budget, token);
			var completedAt = time.GetUtcNow();
			job.UpdatedAt = completedAt;
			job.RowVersion++;
			job.CompletedAt = completedAt;
			job.FailureReason = null;
			if (!outcome.NeedsCopy)
			{
				// The original itself plays: one object, no duplicate (§5).
				job.Status = VideoStatus.NotNeeded;
				logger.LogInformation("Transcode for Datei {DateiId} kept the original", dateiId);
				return await SaveAndConcludeAsync(job, MessageDisposition.Delete, token);
			}
			job.Status = VideoStatus.Completed;
			job.PlaybackDateiId = outcome.PlaybackDateiId;
			logger.LogInformation("Transcode for Datei {DateiId} completed its playback copy", dateiId);
			return await SaveAndConcludeAsync(job, MessageDisposition.Delete, token);
		}
		catch (VideoWorkerException failure)
		{
			// Deterministic: the stored bytes or the environment cannot change
			// on retry. Terminal, German reason on the Datei; the editor's
			// retry action restarts after a fix.
			logger.LogWarning("Transcode for Datei {DateiId} failed deterministically", dateiId);
			return await MarkFailedAsync(job, failure.FailureReason, MessageDisposition.Delete, token);
		}
		catch (Exception exception)
			when (exception is not OperationCanceledException and not VideoWorkerException)
		{
			// Infrastructure failure (storage/network): type only, never
			// provider details or URLs.
			logger.LogWarning("Transcode failed for Datei {DateiId} ({ExceptionType})", dateiId, exception.GetType().Name);
			// The row must return to Queued before the message is abandoned.
			db.ChangeTracker.Clear();
			var current = await db.VideoJobs.FirstOrDefaultAsync(j => j.DateiId == dateiId, token)
				?? throw new InvalidOperationException("Der Transcode-Auftrag ist verschwunden.");
			return await RetryAsync(current, message, token);
		}
	}

	/// <summary>One terminal German failure on the row, message deleted.</summary>
	private async Task<MessageDisposition> MarkFailedAsync(VideoJob job, string reason,
		MessageDisposition onSaved, CancellationToken token)
	{
		job.Status = VideoStatus.Failed;
		job.FailureReason = reason;
		job.CompletedAt = null;
		job.UpdatedAt = time.GetUtcNow();
		job.RowVersion++;
		return await SaveAndConcludeAsync(job, onSaved, token);
	}

	private sealed record TranscodeOutcome(bool NeedsCopy, Guid? PlaybackDateiId);

	/// <summary>
	/// Downloads the original bounded copy to a temporary file, probes and
	/// encodes it, uploads the playback copy and reports the new Datei's id;
	/// Nothing is saved yet — the caller writes row, Datei and recording
	/// slots in one save.
	/// </summary>
	private async Task<TranscodeOutcome> TranscodeAsync(Datei datei, CancellationTokenSource budget, CancellationToken token)
	{
		var tempDir = Path.GetTempPath();
		var inputPath = Path.Combine(tempDir, $"archive-transcode-{Guid.CreateVersion7():N}{Path.GetExtension(datei.OriginalFileName ?? ".bin")}");
		var outputPath = Path.Combine(tempDir, $"archive-transcode-{Guid.CreateVersion7():N}.mp4");
		try
		{
			using (var source = await storage.OpenReadAsync(datei.BlobName, token)
				?? throw new VideoWorkerException(UnreadableVideoReason))
			await using (var target = new FileStream(inputPath, FileMode.Create, FileAccess.Write,
				FileShare.None, 81920, FileOptions.Asynchronous))
			{
				var copied = await BoundedCopyAsync(source, target, options.Value.MaxInputBytes, budget.Token);
				budget.Token.ThrowIfCancellationRequested();
				// The same byte count the Datei row recorded must have arrived;
				// anything else is a storage rarity, not convertible material.
				if (copied != datei.SizeBytes)
					throw new VideoWorkerException(UnreadableVideoReason);
			}

			var probe = await transcoder.InspectAsync(inputPath, budget.Token)
				?? throw new VideoWorkerException(NoVideoStreamReason);
			var plan = VideoCopyPlan.Plan(datei.ContentType, probe);
			if (plan.Action == VideoAction.Fail)
				throw new VideoWorkerException(NoVideoStreamReason);
			if (!plan.NeedsCopy)
				return new TranscodeOutcome(NeedsCopy: false, null);

			await transcoder.TranscodeAsync(inputPath, outputPath, plan, budget.Token);
			await using (var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read,
				FileShare.None))
			{
				if (output.Length == 0)
					throw new VideoWorkerException(TranscodeFailedReason);
				var copyId = Guid.CreateVersion7();
				var blobName = $"dateien/{copyId}/{Guid.CreateVersion7():N}.mp4";
				await storage.UploadAsync(blobName, output, CopyContentType, budget.Token);
				var copy = new Datei
				{
					Id = copyId,
					LiedId = datei.LiedId,
					EventId = datei.EventId,
					Kind = datei.Kind,
					BlobName = blobName,
					ContentType = CopyContentType,
					SizeBytes = output.Length,
					OriginalFileName = CopyFileName(datei),
					CreatedByAccountId = datei.CreatedByAccountId,
					CreatedAt = time.GetUtcNow(),
					RowVersion = 1,
				};
				db.Dateien.Add(copy);
				// The copy heals any recording whose playback slot is empty;
				// an editor's own copy never gets overwritten.
				var recordings = await db.Recordings
					.Where(r => r.OriginalDateiId == datei.Id && r.PlaybackDateiId == null)
					.ToListAsync(budget.Token);
				foreach (var recording in recordings)
				{
					recording.PlaybackDateiId = copy.Id;
					recording.UpdatedAt = time.GetUtcNow();
					recording.UpdatedByAccountId = datei.CreatedByAccountId;
					recording.RowVersion++;
					// A measured duration describes the file members play.
					recording.DurationSeconds = null;
				}
				return new TranscodeOutcome(NeedsCopy: true, copyId);
			}
		}
		catch (Win32Exception exception) when (exception.NativeErrorCode == 2 /* ENOENT */)
		{
			throw new VideoWorkerException(MissingToolReason);
		}
		finally
		{
			DeleteTempBestEffort(inputPath);
			DeleteTempBestEffort(outputPath);
		}
	}

	/// <summary>Bounded streaming copy: stops with one byte beyond the bound.</summary>
	private static async Task<long> BoundedCopyAsync(Stream source, Stream target, long maxBytes, CancellationToken token)
	{
		var buffer = new byte[81920];
		long copied = 0;
		while (true)
		{
			var read = await source.ReadAsync(buffer, token);
			if (read == 0)
				return copied;
			copied += read;
			if (copied > maxBytes)
				throw new VideoWorkerException(TooLargeReason);
			await target.WriteAsync(buffer.AsMemory(0, read), token);
		}
	}

	/// <summary>"Konzert.mov" keeps its stem and gains the copy's suffix.</summary>
	private static string CopyFileName(Datei datei)
	{
		var name = datei.OriginalFileName ?? "Aufnahme";
		var dot = name.LastIndexOf('.');
		var stem = dot > 0 ? name[..dot] : name;
		return $"{stem}{CopyNameSuffix}.mp4";
	}

	private static void DeleteTempBestEffort(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
			// The OS cleans the temporary directory; the worker never fails for it.
		}
	}
}
