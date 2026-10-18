using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.Extensions.Logging;

namespace Archive.Backend.Extraction;

/// <summary>
/// Queues text extraction for a finalized PDF Datei (#169). The Queued row is
/// added to the Datei's own save (outbox), so a committed upload never loses
/// its extraction request; the queue send follows the commit, and a failed
/// send leaves the row for the dispatch sweep.
/// </summary>
public sealed class DateiExtractionHook(
	IExtractionQueue queue, TimeProvider time, ILoggerFactory loggers) : IDateiFinalizedHook
{
	public Task BeforeSaveAsync(Datei datei, PendingUploadContext upload, ArchiveDbContext db, CancellationToken token)
	{
		if (ExtractionService.IsExtractable(datei))
		{
			var job = ExtractionService.CreateForDatei(datei, upload.Now);
			job.TriggeredByAccountId = upload.ActorAccountId;
			db.ExtractionJobs.Add(job);
		}
		return Task.CompletedTask;
	}

	public async Task AfterSaveAsync(Datei datei, ArchiveDbContext db, CancellationToken token)
	{
		if (ExtractionService.IsExtractable(datei))
			await ExtractionService.TryEnqueueAsync(
				db, queue, loggers.CreateLogger("Archive.Extraction"), datei.Id, time, token);
	}
}
