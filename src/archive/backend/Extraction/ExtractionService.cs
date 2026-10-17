using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Archive.Backend.Extraction;

/// <summary>
/// Shared extraction logic (ARC-034) used by the finalize outbox hook and the
/// editor endpoints: row creation, the bounded quiet enqueue and the
/// extractable-type rule. The queue seam is <see cref="IExtractionQueue"/>;
/// enqueue failures never propagate — the persisted row is the durable work
/// record that a later dispatch sweep rediscovers.
/// </summary>
public static class ExtractionService
{
	/// <summary>Quiet enqueue bound: two send attempts, then give up for this caller.</summary>
	private const int EnqueueAttempts = 2;

	/// <summary>
	/// Creates the Queued row for a finalized Datei (ARC-034); the caller
	/// adds it to the change tracker so it is written in the same
	/// <c>SaveChanges</c> as the Datei (outbox). Also the worker's self-healing
	/// path: the Datei itself supplies the triggering account.
	/// </summary>
	public static ExtractionJob CreateForDatei(Datei datei, DateTimeOffset now) => new()
	{
		DateiId = datei.Id,
		Status = ExtractionStatus.Queued,
		TriggeredByAccountId = datei.CreatedByAccountId,
		CreatedAt = now,
		UpdatedAt = now,
	};

	/// <summary>Shared lease rule for worker recovery and the editor's retry action.</summary>
	public static bool HasStaleLease(ExtractionJob job, DateTimeOffset now, ExtractionOptions options) =>
		job.LastAttemptAt is null || job.LastAttemptAt < now - options.StaleRunningAfter;

	/// <summary>
	/// Bounded quiet enqueue (ARC-034): up to two send attempts, catching the
	/// queue's failures, propagating only cancellation. On an accepted
	/// send the row's <see cref="ExtractionJob.LastEnqueuedAt"/> is stamped
	/// and saved; a rejected or failed send leaves it null so the dispatch
	/// sweeper rediscovers the row.
	/// </summary>
	public static async Task<bool> TryEnqueueAsync(ArchiveDbContext db, IExtractionQueue queue,
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
				var job = await db.ExtractionJobs.FirstOrDefaultAsync(j => j.DateiId == dateiId, token);
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
				logger.LogWarning("Extraction enqueue failed: {ExceptionType}", exception.GetType().Name);
				// An accepted message remains queued even when its stamp cannot
				// be saved. A later sweep may safely send an idempotent duplicate.
				if (accepted)
					return true;
			}
		}
		return false;
	}

	/// <summary>
	/// ARC-034 subscribes only to PDF Dateien of the kinds that carry text:
	/// Noten and sonstige Dateien. Audio, MIDI, recordings and photographs
	/// never create extraction work.
	/// </summary>
	public static bool IsExtractable(Datei datei) =>
		datei.Kind is DateiKinds.Noten or DateiKinds.Sonstiges
		&& string.Equals(datei.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
}
