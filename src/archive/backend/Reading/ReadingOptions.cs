namespace Archive.Backend.Reading;

/// <summary>
/// Bounds of the "AI reading" job (ARC-034-1, <c>Archive:Reading</c>): its own
/// queue, size and timeout, separate from the model-free extraction job.
/// Invalid values are sanitized in the setters so a corrupt configuration can
/// never lift a bound.
/// </summary>
public sealed class ReadingOptions
{
	public const string SectionName = "Archive:Reading";

	public const string DefaultQueueName = "archive-reading";

	/// <summary>
	/// Whether AI reading is switched on. Off by default: the hosted queue,
	/// job, identity and schedule must exist before it is turned on, otherwise
	/// every new score would wait for a job that never runs. While off, the
	/// extraction job hands nothing over, no reading row is created, the
	/// editor actions answer 409 and the status payload hides the reading.
	/// Development turns it on (appsettings.Development.json).
	/// </summary>
	public bool Enabled { get; set; }

	public string QueueName
	{
		get => queueName;
		set => queueName = string.IsNullOrWhiteSpace(value) ? DefaultQueueName : value.Trim();
	}

	private string queueName = DefaultQueueName;

	/// <summary>
	/// Hosted queue account endpoint (Entra token mode). Null falls back to
	/// <c>Archive:Extraction:QueueServiceUri</c>, the same storage account.
	/// </summary>
	public string? QueueServiceUri
	{
		get => queueServiceUri;
		set => queueServiceUri = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private string? queueServiceUri;

	/// <summary>Largest PDF the reading job renders; the rendered pages are bounded separately.</summary>
	public long MaxPdfBytes
	{
		get => maxPdfBytes;
		set => maxPdfBytes = value > 0 ? value : 33_554_432;
	}

	private long maxPdfBytes = 33_554_432;

	/// <summary>Pages one reading request transcribes; longer scans are "teilweise gelesen".</summary>
	public int MaxPagesPerRequest
	{
		get => maxPagesPerRequest;
		set => maxPagesPerRequest = value > 0 ? value : 20;
	}

	private int maxPagesPerRequest = 20;

	/// <summary>Images in one model request.</summary>
	public int ImagesPerCall
	{
		get => imagesPerCall;
		set => imagesPerCall = value > 0 ? Math.Min(value, 10) : 10;
	}

	private int imagesPerCall = 10;

	/// <summary>Longest side of a rendered page in pixels.</summary>
	public int MaxImageLongSide
	{
		get => maxImageLongSide;
		set => maxImageLongSide = value > 0 ? Math.Min(value, 2048) : 1600;
	}

	private int maxImageLongSide = 1600;

	public int JpegQuality
	{
		get => jpegQuality;
		set => jpegQuality = value is > 0 and <= 100 ? value : 80;
	}

	private int jpegQuality = 80;

	/// <summary>
	/// Conservative token estimate per page image, given to the budget as the
	/// call's non-text input: 512-pixel tiles of a 1600-pixel page cost
	/// about 2 100 tokens.
	/// </summary>
	public int EstimatedTokensPerImage
	{
		get => estimatedTokensPerImage;
		set => estimatedTokensPerImage = value > 0 ? value : 2500;
	}

	private int estimatedTokensPerImage = 2500;

	/// <summary>Output bound of a transcription request (one to ten pages).</summary>
	public int TranscriptionMaxOutputTokens
	{
		get => transcriptionMaxOutputTokens;
		set => transcriptionMaxOutputTokens = value > 0 ? value : 5000;
	}

	private int transcriptionMaxOutputTokens = 5000;

	/// <summary>Output bound of the field request.</summary>
	public int FieldsMaxOutputTokens
	{
		get => fieldsMaxOutputTokens;
		set => fieldsMaxOutputTokens = value > 0 ? value : 3000;
	}

	private int fieldsMaxOutputTokens = 3000;

	/// <summary>The field request reads at most this much of the text.</summary>
	public int MaxFieldTextCharacters
	{
		get => maxFieldTextCharacters;
		set => maxFieldTextCharacters = value > 0 ? value : 24_000;
	}

	private int maxFieldTextCharacters = 24_000;

	public int MaxAttempts
	{
		get => maxAttempts;
		set => maxAttempts = value > 0 ? value : 4;
	}

	private int maxAttempts = 4;

	/// <summary>Per-message time budget; a model request with ten images takes up to about a minute.</summary>
	public TimeSpan TimeBudget
	{
		get => timeBudget;
		set => timeBudget = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(8);
	}

	private TimeSpan timeBudget = TimeSpan.FromMinutes(8);

	public TimeSpan StaleRunningAfter
	{
		get => staleRunningAfter;
		set => staleRunningAfter = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(20);
	}

	private TimeSpan staleRunningAfter = TimeSpan.FromMinutes(20);

	public TimeSpan RedisplayAfter
	{
		get => redisplayAfter;
		set => redisplayAfter = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(15);
	}

	private TimeSpan redisplayAfter = TimeSpan.FromMinutes(15);

	public TimeSpan VisibilityBackoff
	{
		get => visibilityBackoff;
		set => visibilityBackoff = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(1);
	}

	private TimeSpan visibilityBackoff = TimeSpan.FromMinutes(1);

	public TimeSpan ReceiveVisibility
	{
		get => receiveVisibility;
		set => receiveVisibility = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(20);
	}

	private TimeSpan receiveVisibility = TimeSpan.FromMinutes(20);

	/// <summary>One message per receive round keeps the visibility window comfortable for a slow model call.</summary>
	public int BatchSize
	{
		get => batchSize;
		set => batchSize = value > 0 ? Math.Min(value, 4) : 2;
	}

	private int batchSize = 2;

	public int MaxMessagesPerRun
	{
		get => maxMessagesPerRun;
		set => maxMessagesPerRun = value > 0 ? value : 50;
	}

	private int maxMessagesPerRun = 50;

	public TimeSpan DrainBudget
	{
		get => drainBudget;
		set => drainBudget = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(30);
	}

	private TimeSpan drainBudget = TimeSpan.FromMinutes(30);

	/// <summary>Bound for the dispatch sweep per finite run.</summary>
	public int MaxDispatchPerRun
	{
		get => maxDispatchPerRun;
		set => maxDispatchPerRun = value > 0 ? value : 50;
	}

	private int maxDispatchPerRun = 50;

	/// <summary>Receive batch honouring the visibility window: each message must fit its time budget.</summary>
	public int VisibilityFitBatchSize => (int)Math.Max(1, Math.Min(BatchSize,
		(ReceiveVisibility - TimeSpan.FromSeconds(30)).Ticks / TimeBudget.Ticks));
}
