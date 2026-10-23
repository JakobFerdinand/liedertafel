using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Videos;

/// <summary>
/// Read helpers for the playback copies (#161): the copy of a live video
/// Aufnahme travels through the access ticket (members play the copy, the
/// download stays the original) and the copy Dateien themselves never appear
/// in the ordinary lists — the upload lists carry originals only.
/// </summary>
public static class VideoPlayback
{
	/// <summary>
	/// The live playback copy of one video Datei: its job is
	/// <see cref="VideoStatus.Completed"/> and the copy Datei itself is not
	/// trashed (a superseded or replaced copy counts as none).
	/// </summary>
	public static Task<VideoJob?> LiveCopyAsync(ArchiveDbContext db, Guid dateiId, CancellationToken token) =>
		db.VideoJobs.AsNoTracking()
			.Include(j => j.PlaybackDatei)
			.FirstOrDefaultAsync(j => j.DateiId == dateiId
				&& j.Status == VideoStatus.Completed
				&& j.PlaybackDatei!.DeletedAt == null, token);

	/// <summary>The copy of any of the given originals that is still live, keyed by original id.</summary>
	public static async Task<IReadOnlyDictionary<Guid, VideoJob>> LiveCopiesAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> dateiIds, CancellationToken token)
	{
		if (dateiIds.Count == 0)
			return new Dictionary<Guid, VideoJob>();
		var jobs = await db.VideoJobs.AsNoTracking()
			.Include(j => j.PlaybackDatei)
			.Where(j => dateiIds.Contains(j.DateiId)
				&& j.Status == VideoStatus.Completed
				&& j.PlaybackDatei!.DeletedAt == null)
			.ToListAsync(token);
		return jobs.ToDictionary(j => j.DateiId);
	}

	/// <summary>Excludes already-live playback copies from ordinary Datei listings of a Lied.</summary>
	public static async Task<List<Datei>> WithoutCopiesAsync(ArchiveDbContext db, List<Datei> dateien, CancellationToken token)
	{
		var videoIds = dateien
			.Where(d => VideoService.IsVideoMaterial(d.Kind, d.ContentType))
			.Select(d => d.Id).ToList();
		if (videoIds.Count == 0)
			return dateien;
		var copies = await db.VideoJobs.AsNoTracking()
			.Where(j => j.Status == VideoStatus.Completed
				&& j.PlaybackDateiId != null
				&& videoIds.Contains(j.DateiId))
			.ToListAsync(token);
		var copyIds = copies.Select(j => j.PlaybackDateiId!.Value).ToList();
		return dateien.Where(d => !copyIds.Contains(d.Id)).ToList();
	}
}
