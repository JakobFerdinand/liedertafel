using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Videos;

/// <summary>
/// Playback copies follow their originals into the Papierkorb (#161): when a
/// video Datei is deleted or replaced, its MP4 copy is trashed in the same
/// save, so the recordings' slot reads as empty and the members' playback
/// falls back honestly. Restoring the original (a later slice) must remember
/// this; the copy stays trashed until a new job runs.
/// </summary>
public static class VideoTrash
{
	/// <summary>Trashes the live playback copy of one original Datei, if any.</summary>
	public static async Task TrashCopyOfAsync(ArchiveDbContext db, Datei? original,
		Guid accountId, DateTimeOffset now, CancellationToken token)
	{
		if (original is null)
			return;
		var job = await db.VideoJobs
			.Include(j => j.PlaybackDatei)
			.FirstOrDefaultAsync(j => j.DateiId == original.Id
				&& j.PlaybackDateiId != null, token);
		var copy = job?.PlaybackDatei;
		if (copy is null || copy.Id == original.Id || copy.DeletedAt is not null)
			return;
		DateiTrash.MoveToTrash(copy, accountId, now);
	}
}
