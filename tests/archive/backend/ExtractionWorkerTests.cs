using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034 slice S2: the finite extraction worker. Text-bearing PDFs complete
/// with bounded embedded text, scanned PDFs end in the explicit NoText
/// terminal state, corrupt documents fail deterministically in German,
/// duplicates never rework a finished row, transient infrastructure failures
/// requeue the row and abandon the message with bounded visibility backoff
/// until the attempt budget is exhausted, maintenance pauses without touching
/// rows, poison envelopes are deleted, and the dispatcher sweeps exactly the
/// un-enqueued/stale Queued rows.
/// </summary>
public sealed class ExtractionWorkerTests
{
	[Fact]
	public async Task TextPdfCompletesWithExtractedTextAndDeletesMessage()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Hallo Liedertafel"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Completed, job.Status);
		Assert.Contains("Hallo Liedertafel", job.Text);
		Assert.Null(job.FailureReason);
		Assert.NotNull(job.CompletedAt);
		Assert.NotNull(job.LastAttemptAt);
		Assert.Equal(1, job.AttemptCount);
		Assert.True(job.RowVersion > 0);
	}

	[Fact]
	public async Task ScannedPdfEndsInNoTextTerminalState()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		// A valid PDF page without any text stands in for a scanned document.
		host.Storage.Store(revision.BlobName, ScannedPdf());

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.NoText, job.Status);
		Assert.Null(job.Text);
		Assert.Null(job.FailureReason);
		Assert.NotNull(job.CompletedAt);
		Assert.Equal(1, job.AttemptCount);
	}

	[Fact]
	public async Task CorruptPdfFailsDeterministicallyWithGermanReason()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, CorruptPdf());

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(PdfTextExtractor.CorruptReason, job.FailureReason);
		Assert.Null(job.Text);
		Assert.Null(job.CompletedAt);
		Assert.Equal(1, job.AttemptCount);
	}

	[Fact]
	public void EncryptedPdfExceptionMapsToTheEncryptedReason()
	{
		// PdfPig's writer cannot produce encrypted documents, so the mapping
		// is covered by a stream surfacing exactly that exception type during
		// parsing — the same clause a password-protected document takes.
		var outcome = PdfTextExtractor.Extract(
			new ThrowingPdfStream(new PdfDocumentEncryptedException("encrypted")),
			new ExtractionOptions(), CancellationToken.None);

		Assert.Null(outcome.Text);
		Assert.False(outcome.NoText);
		Assert.Equal(PdfTextExtractor.EncryptedReason, outcome.FailureReason);
	}

	[Fact]
	public void PageAndTextBoundsTruncateInsteadOfFailing()
	{
		var textPdf = TextPdf(string.Join(" ", Enumerable.Repeat("Wort", 200)));
		var options = new ExtractionOptions { MaxTextCharacters = 10, MaxPdfPages = 1 };

		var outcome = PdfTextExtractor.Extract(new MemoryStream(textPdf), options, CancellationToken.None);

		Assert.NotNull(outcome.Text);
		Assert.Equal(10, outcome.Text.Length);
	}

	[Fact]
	public async Task DuplicateMessagesNeverReworkAFinishedRow()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Erster Text"));
		await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);
		var completed = (job.Status, job.AttemptCount, job.RowVersion, job.Text);

		var duplicate = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(duplicate.Abandon);
		Assert.Equal(completed, (job.Status, job.AttemptCount, job.RowVersion, job.Text));
		Assert.Equal(ExtractionStatus.Completed, job.Status);
	}

	[Fact]
	public async Task TransientStorageFailureRequeuesAndAbandonsWithBackoff()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.ThrowOnOpenRead = true;

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		// The row returns to Queued so the next dequeue proceeds, the attempt
		// is counted, and the message redisplay after the bounded backoff.
		Assert.True(disposition.Abandon);
		Assert.True(disposition.Visibility > TimeSpan.Zero);
		Assert.True(disposition.Visibility <= host.Options.RedisplayAfter);
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(1, job.AttemptCount);
		Assert.NotNull(job.LastAttemptAt);
		Assert.Null(job.CompletedAt);

		// The message's own dequeue count ends the retry loop: exhaustion is
		// evaluated after the attempt bump and goes terminal Failed.
		var terminal = await host.Worker.HandleAsync(
			Message(revision.Id, dequeueCount: host.Options.MaxAttempts), CancellationToken.None);
		Assert.False(terminal.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(ExtractionWorker.ExhaustedReason, job.FailureReason);
		Assert.Equal(2, job.AttemptCount);
		Assert.Null(job.CompletedAt);
	}

	[Fact]
	public async Task DequeueCountExhaustionGoesTerminalFailed()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.ThrowOnOpenRead = true;

		var disposition = await host.Worker.HandleAsync(
			Message(revision.Id, dequeueCount: host.Options.MaxAttempts), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(ExtractionWorker.ExhaustedReason, job.FailureReason);
		Assert.Equal(1, job.AttemptCount);
	}

	[Fact]
	public async Task RowAttemptExhaustionGoesTerminalFailed()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.AttemptCount = host.Options.MaxAttempts - 1;
		host.Db.SaveChanges();
		host.Storage.ThrowOnOpenRead = true;

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(ExtractionWorker.ExhaustedReason, job.FailureReason);
		Assert.Equal(host.Options.MaxAttempts, job.AttemptCount);
	}

	[Fact]
	public async Task RunningRowIsAbandonedForTheHoldingWorker()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Running;
		host.Db.SaveChanges();

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.True(disposition.Abandon);
		Assert.True(disposition.Visibility > TimeSpan.Zero);
		Assert.Equal(ExtractionStatus.Running, job.Status);
		Assert.Equal(0, job.AttemptCount);
	}

	[Fact]
	public async Task FailedRowIsNeverResurrectedByADuplicateSend()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Failed;
		job.FailureReason = PdfTextExtractor.CorruptReason;
		host.Db.SaveChanges();

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(PdfTextExtractor.CorruptReason, job.FailureReason);
		Assert.Equal(0, job.AttemptCount);
	}

	[Fact]
	public async Task MaintenancePausesWithoutTouchingRowOrBlob()
	{
		var host = new WorkerHost(maintenance: true);
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Hallo Liedertafel"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.True(disposition.Abandon);
		Assert.True(disposition.Visibility > TimeSpan.Zero);
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(0, job.AttemptCount);
		Assert.Null(job.LastAttemptAt);
		Assert.Null(job.CompletedAt);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task MissingRevisionIsDeletedWithoutAnyRowWork()
	{
		var host = new WorkerHost();

		var disposition = await host.Worker.HandleAsync(
			Message(Guid.CreateVersion7()), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Empty(await host.Db.ExtractionJobs.ToListAsync());
	}

	[Fact]
	public async Task MissingJobRowIsSelfHealedAndProcessedToCompletion()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		host.Storage.Store(revision.BlobName, TextPdf("Selbstheilung"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		var job = await host.Db.ExtractionJobs.SingleAsync();
		Assert.Equal(revision.Id, job.RevisionId);
		Assert.Equal(revision.AssetId, job.AssetId);
		Assert.Equal(revision.CreatedByAccountId, job.TriggeredByAccountId);
		Assert.Equal(ExtractionStatus.Completed, job.Status);
		Assert.Contains("Selbstheilung", job.Text);
	}

	[Fact]
	public async Task OversizeRevisionFailsWithoutOpeningTheBlob()
	{
		var host = new WorkerHost();
		host.Options.MaxPdfBytes = 1024;
		var (_, revision) = SeedRevision(host, sizeBytes: 4096);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Nie gelesen"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(ExtractionWorker.TooLargeReason, job.FailureReason);
		Assert.Equal(0, job.AttemptCount);
		Assert.NotNull(job.LastAttemptAt);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task NonPdfOrForeignRevisionsAreDeleted()
	{
		foreach (var (assetType, contentType) in new[]
		{
			(AssetEndpoints.AudioAssetType, AssetEndpoints.Mp3ContentType),
			(AssetEndpoints.ScoreAssetType, "application/octet-stream"),
		})
		{
			var host = new WorkerHost();
			var (_, revision) = SeedRevision(host, assetType: assetType, contentType: contentType);
			host.Storage.Store(revision.BlobName, TextPdf("Nie gelesen"));

			var disposition = await host.Worker.HandleAsync(Message(revision.Id), CancellationToken.None);

			Assert.False(disposition.Abandon);
			Assert.Empty(await host.Db.ExtractionJobs.ToListAsync());
			Assert.Equal(0, host.Storage.OpenReadCalls);
		}
	}

	[Fact]
	public async Task MalformedAndForeignEnvelopesAreDeletedAsPoison()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		var before = (job.Status, job.AttemptCount, job.RowVersion);

		foreach (var body in new[]
		{
			"not json",
			JsonSerializer.Serialize(new { type = "import", revisionId = revision.Id }),
			JsonSerializer.Serialize(new { type = ExtractionEnvelope.ExtractionType, traceParent = "", traceState = "" },
				ExtractionEnvelope.SerializerOptions),
		})
		{
			var disposition = await host.Worker.HandleAsync(
				Message(revision.Id, body: body), CancellationToken.None);
			Assert.False(disposition.Abandon);
		}

		Assert.Equal(before, (job.Status, job.AttemptCount, job.RowVersion));
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task VersionedRevisionsKeepTheirOwnExtractionResults()
	{
		var host = new WorkerHost();
		var asset = new ArchiveAsset
		{
			AssetType = AssetEndpoints.ScoreAssetType,
			CreatedByAccountId = Guid.CreateVersion7(),
			CreatedAt = DateTimeOffset.UtcNow,
		};
		host.Db.Assets.Add(asset);
		var first = NewRevision(asset, number: 1);
		var second = NewRevision(asset, number: 2);
		host.Db.FileRevisions.AddRange(first, second);
		host.Db.SaveChanges();
		SeedJob(host, first);
		SeedJob(host, second);
		host.Storage.Store(first.BlobName, TextPdf("Erste Fassung"));
		host.Storage.Store(second.BlobName, TextPdf("Zweite Fassung"));

		await host.Worker.HandleAsync(Message(first.Id), CancellationToken.None);
		await host.Worker.HandleAsync(Message(second.Id), CancellationToken.None);
		var lateDuplicate = await host.Worker.HandleAsync(Message(first.Id), CancellationToken.None);

		Assert.False(lateDuplicate.Abandon);
		var jobs = await host.Db.ExtractionJobs.ToDictionaryAsync(j => j.RevisionId);
		Assert.Equal(ExtractionStatus.Completed, jobs[first.Id].Status);
		Assert.Contains("Erste Fassung", jobs[first.Id].Text);
		Assert.Equal(ExtractionStatus.Completed, jobs[second.Id].Status);
		Assert.Contains("Zweite Fassung", jobs[second.Id].Text);
	}

	[Fact]
	public async Task PumpDrainsUntilTheQueueIsEmptyAndAppliesDispositions()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Hallo Liedertafel"));
		// A duplicate (deleted) and a poison envelope (deleted) ride along.
		var poison = Message(Guid.CreateVersion7(), body: "not json");
		host.Source.Seed(poison, Message(revision.Id), Message(revision.Id));

		var processed = await host.Pump.DrainAsync(CancellationToken.None);

		Assert.Equal(3, processed);
		Assert.Equal(0, host.Source.PendingCount);
		Assert.Equal(3, host.Source.Deleted.Count);
		Assert.Empty(host.Source.Abandoned);
	}

	[Fact]
	public async Task PumpStopsAtTheMessageBudget()
	{
		var host = new WorkerHost();
		host.Options.MaxMessagesPerRun = 2;
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Hallo Liedertafel"));
		host.Source.Seed(
			Message(revision.Id), Message(revision.Id), Message(revision.Id),
			Message(revision.Id), Message(revision.Id));

		var processed = await host.Pump.DrainAsync(CancellationToken.None);

		Assert.Equal(2, processed);
		Assert.Equal(3, host.Source.PendingCount);
		Assert.Equal(2, host.Source.Deleted.Count);
	}

	[Fact]
	public async Task DispatcherSweepsExactlyTheUnstampedAndStaleQueuedRows()
	{
		var host = new WorkerHost();
		var now = DateTimeOffset.UtcNow;
		var unstamped = SeedJob(host, SeedRevision(host).Revision, createdAt: now.AddMinutes(-30));
		var stale = SeedJob(host, SeedRevision(host).Revision,
			createdAt: now.AddMinutes(-20), lastEnqueuedAt: now.AddMinutes(-15));
		var fresh = SeedJob(host, SeedRevision(host).Revision,
			createdAt: now.AddMinutes(-10), lastEnqueuedAt: now);
		var running = SeedJob(host, SeedRevision(host).Revision, createdAt: now.AddMinutes(-5));
		running.Status = ExtractionStatus.Running;
		host.Db.SaveChanges();
		var queue = new FakeExtractionQueue();

		var dispatched = await ExtractionDispatcher.DispatchPendingAsync(
			host.Db, queue, Options.Create(host.Options), TimeProvider.System,
			NullLogger.Instance, CancellationToken.None);

		Assert.Equal(2, dispatched);
		Assert.Equal([unstamped.RevisionId, stale.RevisionId], queue.Attempts);
		var jobs = await host.Db.ExtractionJobs.ToDictionaryAsync(j => j.RevisionId);
		Assert.NotNull(jobs[unstamped.RevisionId].LastEnqueuedAt);
		Assert.NotNull(jobs[stale.RevisionId].LastEnqueuedAt);
		Assert.Equal(now, jobs[fresh.RevisionId].LastEnqueuedAt);
		Assert.Null(jobs[running.RevisionId].LastEnqueuedAt);
	}

	[Fact]
	public async Task DispatcherRespectsThePerRunBound()
	{
		var host = new WorkerHost();
		host.Options.MaxDispatchPerRun = 1;
		var oldest = SeedJob(host, SeedRevision(host).Revision, createdAt: DateTimeOffset.UtcNow.AddMinutes(-20));
		var newer = SeedJob(host, SeedRevision(host).Revision, createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
		var queue = new FakeExtractionQueue();

		var dispatched = await ExtractionDispatcher.DispatchPendingAsync(
			host.Db, queue, Options.Create(host.Options), TimeProvider.System,
			NullLogger.Instance, CancellationToken.None);

		Assert.Equal(1, dispatched);
		Assert.Equal([oldest.RevisionId], queue.Attempts);
		Assert.Null((await host.Db.ExtractionJobs.AsNoTracking().SingleAsync(j => j.RevisionId == newer.RevisionId)).LastEnqueuedAt);
	}

	[Fact]
	public async Task DispatcherContinuesAfterAFailingRow()
	{
		var host = new WorkerHost();
		var broken = SeedJob(host, SeedRevision(host).Revision, createdAt: DateTimeOffset.UtcNow.AddMinutes(-20));
		var healthy = SeedJob(host, SeedRevision(host).Revision, createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
		var queue = new SelectivelyFailingQueue(broken.RevisionId);

		var dispatched = await ExtractionDispatcher.DispatchPendingAsync(
			host.Db, queue, Options.Create(host.Options), TimeProvider.System,
			NullLogger.Instance, CancellationToken.None);

		Assert.Equal(1, dispatched);
		Assert.Equal([broken.RevisionId, healthy.RevisionId], queue.Attempts);
		Assert.Null((await host.Db.ExtractionJobs.AsNoTracking().SingleAsync(j => j.RevisionId == broken.RevisionId)).LastEnqueuedAt);
		Assert.NotNull((await host.Db.ExtractionJobs.AsNoTracking().SingleAsync(j => j.RevisionId == healthy.RevisionId)).LastEnqueuedAt);
	}

	// ---- fixtures ----

	private static (ArchiveAsset Asset, FileRevision Revision) SeedRevision(WorkerHost host,
		long sizeBytes = 2048, string assetType = AssetEndpoints.ScoreAssetType,
		string contentType = AssetEndpoints.PdfContentType)
	{
		var asset = new ArchiveAsset
		{
			AssetType = assetType,
			CreatedByAccountId = Guid.CreateVersion7(),
			CreatedAt = DateTimeOffset.UtcNow,
		};
		var revision = NewRevision(asset, number: 1, sizeBytes, contentType);
		host.Db.Assets.Add(asset);
		host.Db.FileRevisions.Add(revision);
		host.Db.SaveChanges();
		return (asset, revision);
	}

	private static FileRevision NewRevision(ArchiveAsset asset, int number,
		long sizeBytes = 2048, string contentType = AssetEndpoints.PdfContentType) =>
		new()
		{
			Asset = asset,
			AssetId = asset.Id,
			RevisionNumber = number,
			BlobName = $"revisions/{Guid.CreateVersion7():N}.pdf",
			ContentType = contentType,
			SizeBytes = sizeBytes,
			CreatedByAccountId = asset.CreatedByAccountId,
			CreatedAt = asset.CreatedAt,
		};

	private static ExtractionJob SeedJob(WorkerHost host, FileRevision revision,
		DateTimeOffset? createdAt = null, DateTimeOffset? lastEnqueuedAt = null)
	{
		var job = new ExtractionJob
		{
			RevisionId = revision.Id,
			AssetId = revision.AssetId,
			Status = ExtractionStatus.Queued,
			TriggeredByAccountId = revision.CreatedByAccountId,
			CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
			UpdatedAt = createdAt ?? DateTimeOffset.UtcNow,
			LastEnqueuedAt = lastEnqueuedAt,
		};
		host.Db.ExtractionJobs.Add(job);
		host.Db.SaveChanges();
		return job;
	}

	private static ExtractionMessage Message(Guid revisionId, long dequeueCount = 1, string? body = null) =>
		new(Guid.CreateVersion7().ToString(), $"pop-{Guid.CreateVersion7():N}",
			body ?? JsonSerializer.Serialize(
				new ExtractionEnvelope(ExtractionEnvelope.ExtractionType, revisionId, null, null),
				ExtractionEnvelope.SerializerOptions),
			dequeueCount);

	private static byte[] TextPdf(string text)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(50, 700), font);
		return builder.Build();
	}

	private static byte[] ScannedPdf()
	{
		var builder = new PdfDocumentBuilder();
		builder.AddPage(PageSize.A4);
		return builder.Build();
	}

	private static byte[] CorruptPdf() => "%PDF-1.7\n%%EOF\n"u8.ToArray();

	private sealed class ThrowingPdfStream(Exception failure) : MemoryStream
	{
		public override int Read(byte[] buffer, int offset, int count) => throw failure;
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			Task.FromException<int>(failure);
	}

	/// <summary>
	/// No-WebApplicationFactory test host: the worker is constructed directly
	/// against its own InMemory database, a fake storage adapter, an in-memory
	/// configuration (maintenance flag) and the fake message source.
	/// </summary>
	private sealed class WorkerHost
	{
		public WorkerHost(bool maintenance = false)
		{
			Db = new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
				.UseInMemoryDatabase($"extraction-worker-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
				.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
				.Options);
			Storage = new WrapperStorage();
			Source = new FakeExtractionMessageSource();
			Options = new ExtractionOptions();
			Configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Archive:MaintenanceMode"] = maintenance ? "true" : "false",
				})
				.Build();
			Worker = new ExtractionWorker(Db, Storage, Microsoft.Extensions.Options.Options.Create(Options),
				Configuration, TimeProvider.System, NullLogger<ExtractionWorker>.Instance);
			Pump = new ExtractionPump(Source, Worker, Microsoft.Extensions.Options.Options.Create(Options),
				Configuration, TimeProvider.System, NullLogger<ExtractionPump>.Instance);
		}

		public ArchiveDbContext Db { get; }

		public WrapperStorage Storage { get; }

		public FakeExtractionMessageSource Source { get; }

		public ExtractionOptions Options { get; }

		private IConfiguration Configuration { get; }

		public ExtractionWorker Worker { get; }

		public ExtractionPump Pump { get; }
	}

	/// <summary>
	/// FakeAssetStorage with an ARC-034 open-read seam for the transient and
	/// blob-never-opened assertions; every other member delegates to a real
	/// in-memory adapter.
	/// </summary>
	private sealed class WrapperStorage : IAssetStorageAdapter
	{
		private readonly FakeAssetStorage inner = new();

		public int OpenReadCalls { get; private set; }

		public bool ThrowOnOpenRead { get; set; }

		public Task<Stream?> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
		{
			OpenReadCalls++;
			if (ThrowOnOpenRead)
				throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
			return inner.OpenReadAsync(blobName, cancellationToken);
		}

		public void Store(string blobName, byte[] content) => inner.Store(blobName, content);

		public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken) =>
			inner.CreateUploadTicketAsync(blobName, lifetime, cancellationToken);

		public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, string? contentType = null, CancellationToken cancellationToken = default) =>
			inner.CreateReadTicketAsync(blobName, lifetime, asDownload, contentType, cancellationToken);

		public Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken) =>
			inner.ProbeAsync(blobName, cancellationToken);

		public Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken) =>
			inner.ReadHeaderAsync(blobName, length, cancellationToken);

		public Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken) =>
			inner.PromoteAsync(sourceBlobName, targetBlobName, cancellationToken);

		public Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
			inner.DeleteAsync(blobName, cancellationToken);
	}

	/// <summary>Dispatch fake that fails exactly one revision, like a row-specific outage.</summary>
	private sealed class SelectivelyFailingQueue(Guid failingRevisionId) : IExtractionQueue
	{
		public List<Guid> Attempts { get; } = [];

		public Task<bool> SendAsync(Guid revisionId, CancellationToken token)
		{
			Attempts.Add(revisionId);
			if (revisionId == failingRevisionId)
				throw new InvalidOperationException("Fake queue outage.");
			return Task.FromResult(true);
		}
	}
}

/// <summary>
/// In-memory receive seam (ARC-034 S2): messages live in a pending queue;
/// receives pop from it, and delete/abandon calls are recorded per message so
/// the pump tests can assert the applied dispositions.
/// </summary>
internal sealed class FakeExtractionMessageSource(params ExtractionMessage[] initial) : IExtractionMessageSource
{
	private readonly object gate = new();
	private readonly Queue<ExtractionMessage> pending = new(initial);
	private readonly List<ExtractionMessage> received = [];
	private readonly List<ExtractionMessage> deleted = [];
	private readonly List<(ExtractionMessage Message, TimeSpan Visibility)> abandoned = [];

	public void Seed(params ExtractionMessage[] messages)
	{
		lock (gate)
			foreach (var message in messages)
				pending.Enqueue(message);
	}

	public IReadOnlyList<ExtractionMessage> Deleted
	{
		get { lock (gate) return [.. deleted]; }
	}

	public IReadOnlyList<(ExtractionMessage Message, TimeSpan Visibility)> Abandoned
	{
		get { lock (gate) return [.. abandoned]; }
	}

	public int PendingCount
	{
		get { lock (gate) return pending.Count; }
	}

	public Task<IReadOnlyList<ExtractionMessage>> ReceiveBatchAsync(int maxMessages, CancellationToken token)
	{
		var batch = new List<ExtractionMessage>();
		lock (gate)
		{
			while (batch.Count < maxMessages && pending.Count > 0)
			{
				var message = pending.Dequeue();
				received.Add(message);
				batch.Add(message);
			}
		}
		return Task.FromResult<IReadOnlyList<ExtractionMessage>>(batch);
	}

	public Task DeleteAsync(string messageId, string popReceipt, CancellationToken token)
	{
		lock (gate) deleted.Add(Require(messageId));
		return Task.CompletedTask;
	}

	public Task AbandonAsync(string messageId, string popReceipt, TimeSpan visibility, CancellationToken token)
	{
		lock (gate) abandoned.Add((Require(messageId), visibility));
		return Task.CompletedTask;
	}

	private ExtractionMessage Require(string messageId) =>
		received.FirstOrDefault(m => m.MessageId == messageId)
			?? throw new InvalidOperationException("Delete/abandon before receive.");
}
