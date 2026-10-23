namespace Archive.Backend.Videos;

/// <summary>
/// Bounded transcode configuration (#161, <c>Archive:Video</c>). Invalid
/// values are sanitized defensively in the setters so a partial or corrupt
/// configuration can never disable the bounds or lose the queue name — the
/// same posture as the extraction options.
/// </summary>
public sealed class VideoOptions
{
	public const string SectionName = "Archive:Video";

	/// <summary>Target queue for transcode messages; storage init creates it.</summary>
	public string QueueName
	{
		get => queueName;
		set => queueName = string.IsNullOrWhiteSpace(value) ? "archive-videos" : value.Trim();
	}

	private string queueName = "archive-videos";

	/// <summary>
	/// Hosted queue account endpoint (Entra token mode, mirroring the blob
	/// storage ARC-049 pattern). Null selects the <c>archive-queues</c>
	/// connection string and falls back to the extraction queue's service URI,
	/// like the reading queue does — all queues share the assets account.
	/// </summary>
	public string? QueueServiceUri
	{
		get => queueServiceUri;
		set => queueServiceUri = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private string? queueServiceUri;

	/// <summary>ffmpeg executable; default resolves from the PATH. A worker without it fails the Datei deterministically in German.</summary>
	public string FfmpegPath
	{
		get => string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
		set => ffmpegPath = value;
	}

	private string? ffmpegPath;

	/// <summary>ffprobe executable; default resolves from the PATH.</summary>
	public string FfprobePath
	{
		get => string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath.Trim();
		set => ffprobePath = value;
	}

	private string? ffprobePath;

	/// <summary>Uploads are already capped at MaxUploadBytes; the same 10 GiB bound applies to the transcode input.</summary>
	public long MaxInputBytes
	{
		get => maxInputBytes;
		set => maxInputBytes = value > 0 ? value : 10 * 1024L * 1024 * 1024;
	}

	private long maxInputBytes = 10 * 1024L * 1024 * 1024;

	/// <summary>Bounded retry: a message is abandoned with visibility backoff while attempts remain.</summary>
	public int MaxAttempts
	{
		get => maxAttempts;
		set => maxAttempts = value > 0 ? value : 3;
	}

	private int maxAttempts = 3;

	/// <summary>Interrupted Running attempts can be re-taken after this lease window.</summary>
	public TimeSpan StaleRunningAfter
	{
		get => staleRunningAfter;
		set => staleRunningAfter = value > TimeSpan.Zero ? value : TimeSpan.FromHours(1);
	}

	private TimeSpan staleRunningAfter = TimeSpan.FromHours(1);

	/// <summary>
	/// Per-message time budget for one transcode. A two-hour concert encodes
	/// for hours on a single vCPU, so this is deliberately far above the
	/// extraction budget; the receive visibility below must cover it.
	/// </summary>
	public TimeSpan TimeBudget
	{
		get => timeBudget;
		set => timeBudget = value > TimeSpan.Zero ? value : TimeSpan.FromHours(8);
	}

	private TimeSpan timeBudget = TimeSpan.FromHours(8);

	/// <summary>Abandoned messages redisplay after this window while attempts remain.</summary>
	public TimeSpan RedisplayAfter
	{
		get => redisplayAfter;
		set => redisplayAfter = value > TimeSpan.Zero ? value : TimeSpan.FromHours(1);
	}

	private TimeSpan redisplayAfter = TimeSpan.FromHours(1);

	/// <summary>Bound for the dispatch sweeper per finite run.</summary>
	public int MaxDispatchPerRun
	{
		get => maxDispatchPerRun;
		set => maxDispatchPerRun = value > 0 ? value : 100;
	}

	private int maxDispatchPerRun = 100;

	/// <summary>Visibility backoff between the worker's receive rounds.</summary>
	public TimeSpan VisibilityBackoff
	{
		get => visibilityBackoff;
		set => visibilityBackoff = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(1);
	}

	private TimeSpan visibilityBackoff = TimeSpan.FromMinutes(1);

	/// <summary>Azure Queue Storage accepts at most this many messages per receive round.</summary>
	public const int MaxReceiveBatch = 32;

	/// <summary>One receive round asks for one message: a multi-hour transcode must not share a visibility window.</summary>
	public int BatchSize
	{
		get => batchSize;
		set => batchSize = value > 0 ? Math.Min(value, MaxReceiveBatch) : 1;
	}

	private int batchSize = 1;

	/// <summary>Time reserved for receipt release after serial processing in one batch.</summary>
	public static readonly TimeSpan ReleaseHeadroom = TimeSpan.FromSeconds(30);

	/// <summary>The batch cap, bounded by serial transcodes fitting the visibility window.</summary>
	public int VisibilityFitBatchSize => (int)Math.Max(1, Math.Min(BatchSize,
		(ReceiveVisibility - ReleaseHeadroom).Ticks / TimeBudget.Ticks));

	/// <summary>
	/// Visibility window of the received message; it must exceed one
	/// transcode, and Azure's cap keeps it legal.
	/// </summary>
	public TimeSpan ReceiveVisibility
	{
		get => receiveVisibility;
		set => receiveVisibility = value > TimeSpan.Zero ? value : TimeSpan.FromHours(8);
	}

	private TimeSpan receiveVisibility = TimeSpan.FromHours(8);

	/// <summary>
	/// Message budget of one finite worker run. The queue scale rule starts a
	/// run per message anyway (queueLength 1), so a small budget keeps the
	/// platform's bounded execution window honest.
	/// </summary>
	public int MaxMessagesPerRun
	{
		get => maxMessagesPerRun;
		set => maxMessagesPerRun = value > 0 ? value : 10;
	}

	private int maxMessagesPerRun = 10;

	/// <summary>Wall-clock budget for one finite drain run; the pump stops when it elapses.</summary>
	public TimeSpan DrainBudget
	{
		get => drainBudget;
		set => drainBudget = value > TimeSpan.Zero ? value : TimeSpan.FromHours(10);
	}

	private TimeSpan drainBudget = TimeSpan.FromHours(10);
}

/// <summary>The worker's deterministic outcomes for one Datei (dispositions follow from it).</summary>
public enum VideoAction
{
	/// <summary>The original already plays in browsers: no copied object is created.</summary>
	Keep = 0,

	/// <summary>An MP4 H.264/AAC playback copy is needed.</summary>
	Copy = 1,

	/// <summary>The stored bytes are unreadable or no video stream exists.</summary>
	Fail = 2,
}
