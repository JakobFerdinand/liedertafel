using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.Extensions.Logging;

namespace Archive.Backend.Videos;

/// <summary>
/// Queues the MP4 playback-copy transcode for a finalized video Datei (#161).
/// The Queued row is added to the Datei's own save (outbox), so a committed
/// upload never loses its transcode request; the queue send follows the
/// commit, and a failed send leaves the row for the dispatch sweep.
/// </summary>
public sealed class VideoHook(IVideoQueue queue, TimeProvider time, ILoggerFactory loggers) : IDateiFinalizedHook
{
	public Task BeforeSaveAsync(Datei datei, PendingUploadContext upload, ArchiveDbContext db, CancellationToken token)
	{
		if (VideoService.IsVideoMaterial(datei.Kind, datei.ContentType))
		{
			var job = VideoService.CreateForDatei(datei, upload.Now);
			job.TriggeredByAccountId = upload.ActorAccountId;
			db.VideoJobs.Add(job);
		}
		return Task.CompletedTask;
	}

	public async Task AfterSaveAsync(Datei datei, ArchiveDbContext db, CancellationToken token)
	{
		if (VideoService.IsVideoMaterial(datei.Kind, datei.ContentType))
			await VideoService.TryEnqueueAsync(
				db, queue, loggers.CreateLogger("Archive.Videos"), datei.Id, time, token);
	}
}
