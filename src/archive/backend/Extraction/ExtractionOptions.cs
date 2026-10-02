namespace Archive.Backend.Extraction;

/// <summary>
/// Bounded extraction configuration (ARC-034, <c>Archive:Extraction</c>).
/// Invalid values are sanitized defensively in the setters: a partial or
/// corrupt configuration can never disable the bounds or lose the queue
/// name, so extraction keeps working with its default limits.
/// </summary>
public sealed class ExtractionOptions
{
	public const string SectionName = "Archive:Extraction";

	/// <summary>Target queue for extraction messages; storage init creates it (S3).</summary>
	public string QueueName
	{
		get => queueName;
		set => queueName = string.IsNullOrWhiteSpace(value) ? "archive-extraction" : value.Trim();
	}

	private string queueName = "archive-extraction";

	/// <summary>
	/// Hosted queue account endpoint (Entra token mode, mirroring the blob
	/// storage ARC-049 pattern). Null selects the <c>archive-queues</c>
	/// connection string (Azurite/local); both unset degrades to the no-op
	/// queue until a later dispatch sweep picks the rows up.
	/// </summary>
	public string? QueueServiceUri
	{
		get => queueServiceUri;
		set => queueServiceUri = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private string? queueServiceUri;

	/// <summary>Upper bound for persisted text per revision.</summary>
	public int MaxTextCharacters
	{
		get => maxTextCharacters;
		set => maxTextCharacters = value > 0 ? value : 100_000;
	}

	private int maxTextCharacters = 100_000;

	/// <summary>64 MiB: larger documents fail with an honest German reason instead of unbounded reads.</summary>
	public long MaxPdfBytes
	{
		get => maxPdfBytes;
		set => maxPdfBytes = value > 0 ? value : 67_108_864;
	}

	private long maxPdfBytes = 67_108_864;

	public int MaxPdfPages
	{
		get => maxPdfPages;
		set => maxPdfPages = value > 0 ? value : 500;
	}

	private int maxPdfPages = 500;

	/// <summary>Bounded retry: a message is abandoned with visibility backoff while attempts remain.</summary>
	public int MaxAttempts
	{
		get => maxAttempts;
		set => maxAttempts = value > 0 ? value : 5;
	}

	private int maxAttempts = 5;

	/// <summary>Per-message time budget for one extraction run.</summary>
	public TimeSpan TimeBudget
	{
		get => timeBudget;
		set => timeBudget = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(2);
	}

	private TimeSpan timeBudget = TimeSpan.FromMinutes(2);

	/// <summary>Abandoned messages redisplay after this window while attempts remain.</summary>
	public TimeSpan RedisplayAfter
	{
		get => redisplayAfter;
		set => redisplayAfter = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(10);
	}

	private TimeSpan redisplayAfter = TimeSpan.FromMinutes(10);

	/// <summary>Bound for the dispatch sweeper per finite run.</summary>
	public int MaxDispatchPerRun
	{
		get => maxDispatchPerRun;
		set => maxDispatchPerRun = value > 0 ? value : 200;
	}

	private int maxDispatchPerRun = 200;

	/// <summary>Visibility backoff between the worker's receive rounds.</summary>
	public TimeSpan VisibilityBackoff
	{
		get => visibilityBackoff;
		set => visibilityBackoff = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(30);
	}

	private TimeSpan visibilityBackoff = TimeSpan.FromSeconds(30);
}
