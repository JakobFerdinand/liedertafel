using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Assets;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Provenance;
using Archive.Backend.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Tests;

/// <summary>
/// The model stand-in of the reading tests: it sits where the provider would,
/// below the real gateway (budget, ledger, bounds), and answers the two kinds
/// of request from a script. It records every call.
/// </summary>
internal sealed class ScanModelStub : IChatClient
{
	public sealed record Call(bool IsTranscription, int Images, string Text, int? MaxOutputTokens);

	private readonly object gate = new();
	private readonly List<Call> calls = [];

	/// <summary>Answers of successive transcription requests; the last one repeats.</summary>
	public List<string> Transcriptions { get; } = [];

	/// <summary>JSON answer of a field request.</summary>
	public string FieldsJson { get; set; } = """{"felder":[]}""";

	/// <summary>Throws instead of answering the n-th (one-based) transcription request.</summary>
	public int? FailTranscriptionNumber { get; set; }

	public IReadOnlyList<Call> Calls { get { lock (gate) return [.. calls]; } }

	public IReadOnlyList<Call> TranscriptionCalls => Calls.Where(c => c.IsTranscription).ToList();

	public IReadOnlyList<Call> FieldCalls => Calls.Where(c => !c.IsTranscription).ToList();

	public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		var updates = new List<ChatResponseUpdate>();
		await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
			updates.Add(update);
		return updates.ToChatResponse();
	}

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
		ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await Task.Yield();
		var list = messages.ToList();
		var images = list.SelectMany(m => m.Contents).Count(c => c is DataContent);
		var text = string.Join('\n', list.Select(m => m.Text));
		int transcriptionNumber;
		lock (gate)
		{
			calls.Add(new Call(images > 0, images, text, options?.MaxOutputTokens));
			transcriptionNumber = calls.Count(c => c.IsTranscription);
		}
		string answer;
		if (images > 0)
		{
			if (FailTranscriptionNumber == transcriptionNumber)
				throw new InvalidOperationException("Test: Modell nicht erreichbar.");
			answer = Transcriptions.Count == 0 ? string.Empty
				: Transcriptions[Math.Min(transcriptionNumber - 1, Transcriptions.Count - 1)];
		}
		else
		{
			answer = FieldsJson;
		}
		yield return new ChatResponseUpdate(ChatRole.Assistant, answer);
		yield return new ChatResponseUpdate
		{
			Contents = [new UsageContent(new UsageDetails { InputTokenCount = 100 + images * 1500, OutputTokenCount = 50 })],
		};
	}

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}

internal sealed class FakeReadingQueue(bool accept = true) : IReadingQueue
{
	private readonly List<Guid> attempts = [];

	public bool Accept { get; set; } = accept;

	public IReadOnlyList<Guid> Attempts { get { lock (attempts) return [.. attempts]; } }

	public Task<bool> SendAsync(Guid revisionId, CancellationToken token)
	{
		lock (attempts) attempts.Add(revisionId);
		return Task.FromResult(Accept);
	}
}

/// <summary>A score with its catalogue chain, its current revision, extraction row and reading row.</summary>
internal sealed record SeededScore(
	Song Song, Arrangement Arrangement, MusicalVersion Version, ArchiveAsset Asset, FileRevision Revision,
	ExtractionJob Extraction, ReadingJob Reading);

/// <summary>
/// Hosts the AI reading worker over the EF in-memory database, the real
/// gateway (budget, ledger, bounds) and a scripted model, so the tests go
/// through the same path the job does and only the provider is replaced.
/// </summary>
internal sealed class ReadingTestHost
{
	public const string ModelKey = "stub-model";

	public ReadingTestHost(IChatClient? provider = null, decimal capEur = 15m, Action<ReadingOptions>? configure = null,
		string autoApply = "sicher", bool maintenance = false, string modelKey = ModelKey, AiModelPrice? price = null)
	{
		DbOptions = new DbContextOptionsBuilder<ArchiveDbContext>()
			.UseInMemoryDatabase($"reading-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
			.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
			.UseApplicationServiceProvider(new ServiceCollection()
				.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(o =>
					o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3)
				.BuildServiceProvider())
			.Options;
		Db = new ArchiveDbContext(DbOptions);
		Model = provider as ScanModelStub ?? new ScanModelStub();
		Provider = provider ?? Model;
		Storage = new FakeAssetStorage();
		Queue = new FakeReadingQueue();
		Options = new ReadingOptions();
		configure?.Invoke(Options);
		AiOptions = new AiOptions
		{
			MonthlyCapEur = capEur,
			Models = { [modelKey] = price ?? new AiModelPrice { InputPricePerMillionEur = 0.1056m, OutputPricePerMillionEur = 0.528m } },
		};
		var scopes = new ServiceCollection().AddScoped(_ => new ArchiveDbContext(DbOptions)).BuildServiceProvider();
		var time = TimeProvider.System;
		Ledger = new InMemoryAiLedgerStore(scopes.GetRequiredService<IServiceScopeFactory>());
		Budget = new AiBudgetLedger(Ledger, time, new StaticMonitor<AiOptions>(AiOptions), NullLogger<AiBudgetLedger>.Instance);
		Gateway = new AiGateway(Provider, new ServiceCollection().BuildServiceProvider(), Budget,
			new StaticMonitor<AiOptions>(AiOptions), new AiModels { Chat = modelKey });
		ScanModel = new ScanReadingModel(Gateway, Microsoft.Extensions.Options.Options.Create(Options), NullLoggerFactory.Instance);
		Renderer = new PdfiumPageRenderer();
		Configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Archive:MaintenanceMode"] = maintenance ? "true" : "false",
			})
			.Build();
		ProvenanceOptions = Microsoft.Extensions.Options.Options.Create(new ProvenanceOptions { AutoApplyConfidence = autoApply });
		Worker = NewWorker(Db);
	}

	public DbContextOptions<ArchiveDbContext> DbOptions { get; }
	public ArchiveDbContext Db { get; }
	public ScanModelStub Model { get; }
	public IChatClient Provider { get; }
	public FakeAssetStorage Storage { get; }
	public FakeReadingQueue Queue { get; }
	public ReadingOptions Options { get; }
	public AiOptions AiOptions { get; }
	public InMemoryAiLedgerStore Ledger { get; }
	public AiBudgetLedger Budget { get; }
	public AiGateway Gateway { get; }
	public ScanReadingModel ScanModel { get; }
	public IPdfPageRenderer Renderer { get; set; }
	public IConfiguration Configuration { get; }
	public IOptions<ProvenanceOptions> ProvenanceOptions { get; }
	public ReadingWorker Worker { get; private set; }

	public DateTimeOffset Deadline => DateTimeOffset.UtcNow + Options.TimeBudget;

	public ReadingWorker NewWorker(ArchiveDbContext db)
	{
		var writes = new CatalogueWriteService(db);
		var writer = new AutomatedFieldWriter(db, writes, ProvenanceOptions, TimeProvider.System);
		return new ReadingWorker(db, Storage, Renderer, ScanModel, new ScoreFieldPrefill(db, writer, ScanModel),
			Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new ExtractionOptions()),
			Configuration, TimeProvider.System, NullLogger<ReadingWorker>.Instance);
	}

	/// <summary>Runs the worker on a fresh context, like the pump does for every message.</summary>
	public async Task<MessageDisposition> RunAsync(Guid revisionId, long dequeueCount = 1)
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await NewWorker(db).HandleAsync(Message(revisionId, dequeueCount), Deadline, CancellationToken.None);
	}

	public static ExtractionMessage Message(Guid revisionId, long dequeueCount = 1, string? body = null) =>
		new(Guid.CreateVersion7().ToString(), $"pop-{Guid.CreateVersion7():N}",
			body ?? JsonSerializer.Serialize(
				new ExtractionEnvelope(AzureReadingQueue.ReadingType, revisionId, null, null),
				ExtractionEnvelope.SerializerOptions),
			dequeueCount);

	/// <summary>
	/// Seeds a song with an empty catalogue (only the required title), one
	/// arrangement and version, and a score whose current revision holds
	/// <paramref name="pdf"/>; the extraction row is what the extraction job
	/// would have left (NoText for a scan, Completed with text otherwise).
	/// </summary>
	public SeededScore SeedScore(byte[] pdf, string? embeddedText = null, Action<Song>? song = null, bool current = true)
	{
		var account = Guid.CreateVersion7();
		var now = DateTimeOffset.UtcNow;
		var songRow = new Song { Title = "Waldfahrt", CreatedByAccountId = account, UpdatedByAccountId = account, CreatedAt = now, UpdatedAt = now };
		song?.Invoke(songRow);
		var arrangement = new Arrangement { Song = songRow, SongId = songRow.Id, Label = "Chorsatz", CreatedByAccountId = account, CreatedAt = now };
		var version = new MusicalVersion { Arrangement = arrangement, ArrangementId = arrangement.Id, Label = "Original", CreatedByAccountId = account, CreatedAt = now };
		var asset = new ArchiveAsset
		{
			AssetType = AssetEndpoints.ScoreAssetType, MusicalVersion = version, MusicalVersionId = version.Id,
			CreatedByAccountId = account, CreatedAt = now,
		};
		var revision = new FileRevision
		{
			Asset = asset, AssetId = asset.Id, RevisionNumber = 1, BlobName = $"revisions/{Guid.CreateVersion7():N}.pdf",
			ContentType = AssetEndpoints.PdfContentType, SizeBytes = pdf.Length, OriginalFileName = "waldfahrt.pdf",
			CreatedByAccountId = account, CreatedAt = now,
		};
		var extraction = new ExtractionJob
		{
			RevisionId = revision.Id, AssetId = asset.Id, TriggeredByAccountId = account, CreatedAt = now, UpdatedAt = now, CompletedAt = now,
			Status = embeddedText is null ? ExtractionStatus.NoText : ExtractionStatus.Completed, Text = embeddedText,
		};
		var reading = ReadingService.CreateForRevision(revision, account, Options.MaxPagesPerRequest, now);
		reading.LastEnqueuedAt = now;
		Db.Songs.Add(songRow);
		Db.Arrangements.Add(arrangement);
		Db.MusicalVersions.Add(version);
		Db.Assets.Add(asset);
		Db.FileRevisions.Add(revision);
		if (current)
			asset.CurrentRevisionId = revision.Id;
		Db.ExtractionJobs.Add(extraction);
		Db.ReadingJobs.Add(reading);
		Db.SaveChanges();
		Storage.Store(revision.BlobName, pdf);
		return new SeededScore(songRow, arrangement, version, asset, revision, extraction, reading);
	}

	public async Task<ReadingJob> ReadingAsync(Guid revisionId)
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await db.ReadingJobs.AsNoTracking().SingleAsync(j => j.RevisionId == revisionId);
	}

	public async Task<ExtractionJob> ExtractionAsync(Guid revisionId)
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await db.ExtractionJobs.AsNoTracking().SingleAsync(j => j.RevisionId == revisionId);
	}

	public async Task<Song> SongAsync(Guid id)
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await db.Songs.AsNoTracking().SingleAsync(s => s.Id == id);
	}

	public async Task<FieldProvenance?> ProvenanceAsync(string entityType, Guid id, string field)
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await db.FieldProvenance.AsNoTracking()
			.FirstOrDefaultAsync(p => p.EntityType == entityType && p.EntityId == id && p.Field == field);
	}

	public async Task<List<Proposal>> ProposalsAsync()
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await db.Proposals.AsNoTracking().ToListAsync();
	}

	public async Task<List<AiUsageEntry>> LedgerAsync()
	{
		await using var db = new ArchiveDbContext(DbOptions);
		return await db.AiUsageEntries.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync();
	}

	private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
	{
		public T CurrentValue => value;
		public T Get(string? name) => value;
		public IDisposable? OnChange(Action<T, string?> listener) => null;
	}
}
