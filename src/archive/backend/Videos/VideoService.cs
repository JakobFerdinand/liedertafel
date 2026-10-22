using Archive.Backend.Lieder;

namespace Archive.Backend.Videos;

/// <summary>
/// Shared video-copy logic (#161) used by the finalize outbox hook and the
/// editor endpoints: the video-material rule, row creation, the bounded quiet
/// enqueue and the lease rule. The queue seam is <see cref="IVideoQueue"/>;
/// enqueue failures never propagate — the persisted row is the durable work
/// record that a later dispatch sweep rediscovers.
/// </summary>
public static partial class VideoService
{
	/// <summary>Quiet enqueue bound: two send attempts, then give up for this caller.</summary>
	private const int EnqueueAttempts = 2;

	/// <summary>
	/// Video material takes a playback copy: an Aufnahme whose leading bytes
	/// declared a video container at finalize. Übe-Dateien, Noten and photos
	/// never create transcode work.
	/// </summary>
	public static bool IsVideoMaterial(string kind, string contentType) =>
		kind == DateiKinds.Aufnahme
		&& contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}
