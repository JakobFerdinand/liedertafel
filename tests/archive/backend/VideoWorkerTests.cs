using System.Text;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Archive.Backend.Recordings;
using Archive.Backend.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Archive.Backend.Tests;

/// <summary>
/// #161: the finite transcode worker. An iPhone-style original becomes a real
/// playback Datei (MP4 MP4/M4A container facts; the bytes themselves come
/// from a substitute transcoder here) attached to the Auftritt recording's
/// empty playback slot in the same save, an already-playable up-to-1080p
/// original ends in the explicit NotNeeded state without a copy, corrupt
/// material fails deterministically in German, missing tools record the
/// German environment failure, duplicates never rework a finished row,
/// transient infrastructure failures requeue the row and abandon the message
/// with bounded visibility backoff until the attempt budget is exhausted,
/// maintenance pauses without touching rows, poison envelopes are deleted,
/// and the dispatcher sweeps exactly the un-enqueued/stale Queued rows.
/// </summary>
public sealed class VideoWorkerTests
{
	[Fact]
	public async Task AnIphoneOriginalBecomesAPlaybackCopyAndFillsTheSlot()	{
		var host = new WorkerHost();
		var (recording, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);

		var capability = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(capability.Abandon);
		Assert.Equal(VideoStatus.Completed, job.Status);
		Assert.Null(job.FailureReason);
		Assert.NotNull(job.CompletedAt);
		Assert.Equal(1, job.AttemptCount);
		// The playback copy is a real Datei of the same owner.
		var copy = await host.Db.Dateien.SingleAsync(d => d.Id == job.PlaybackDateiId);
		Assert.Equal(DateiKinds.Aufnahme, copy.Kind);
		Assert.Equal("video/mp4", copy.ContentType);
		Assert.Equal(original.EventId, copy.EventId);
		Assert.StartsWith("dateien/", copy.BlobName);
		Assert.Contains(" - Abspielkopie.mp4", copy.OriginalFileName);
		Assert.Equal("Konzert - Abspielkopie.mp4", copy.OriginalFileName);
		// The upload of the copy itself happened.
		Assert.True(host.Storage.Has(copy.BlobName));
		// And the recording's playback slot received it in the same save.
		Assert.Equal(copy.Id, recording.PlaybackDateiId);
		// A measured duration describes the file members play; the change clears it.
		Assert.Null(recording.DurationSeconds);
	}

	[Fact]
	public async Task AnAlreadyPlayableOriginalStaysOneObject()
	{
		var host = new WorkerHost();
		var (recording, original) = SeedVideoRecording(host, contentType: "video/mp4");
		var job = SeedJob(host, original);
		// Already playable: the same container, playable codecs, up to 1080p.
		host.Transcoder.ProbeResult = new("h264", "aac", 1920, 1080);
		// The slot already holds the playable original as its own answer.
		recording.PlaybackDateiId = original.Id;
		host.Db.SaveChanges();
		var before = (job.PlaybackDateiId, recording.PlaybackDateiId, recording.DurationSeconds);

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(VideoStatus.NotNeeded, job.Status);
		Assert.NotNull(job.CompletedAt);
		Assert.Empty(await host.Db.Dateien.Where(d => d.Id != original.Id).ToListAsync());
		Assert.Equal(before, (job.PlaybackDateiId, recording.PlaybackDateiId, recording.DurationSeconds));
		// The decision is itself a counted attempt; nothing was re-encoded.
		Assert.Equal(1, job.AttemptCount);
	}

	[Fact]
	public async Task CorruptMaterialFailsDeterministicallyWithGermanReason()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);
		host.Transcoder.Corrupt = true;

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(VideoStatus.Failed, job.Status);
		Assert.Equal(VideoWorker.UnreadableVideoReason, job.FailureReason);
		Assert.Empty(await host.Db.Dateien.Where(d => d.ContentType == "video/mp4").ToListAsync());
	}

	[Fact]
	public async Task AMissingFfmpegFailsTheEnvironmentReason()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);
		host.Transcoder.ToolMissing = true;

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(VideoStatus.Failed, job.Status);
		Assert.Equal(VideoWorker.MissingToolReason, job.FailureReason);
	}

	[Fact]
	public async Task DuplicateMessagesNeverReworkAFinishedRow()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);
		await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);
		var completed = (job.Status, job.AttemptCount, job.RowVersion, job.PlaybackDateiId, job.CompletedAt);

		var duplicate = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(duplicate.Abandon);
		Assert.Equal(completed, (job.Status, job.AttemptCount, job.RowVersion, job.PlaybackDateiId, job.CompletedAt));
	}

	[Fact]
	public async Task TransientStorageFailureRequeuesAndAbandonsWithBackoff()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);
		host.Storage.ThrowOnOpenRead = true;

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		// The worker cleared the tracker on the recovery path; the durable row
		// — not the detached test reference — carries the truth.
		job = await host.Db.VideoJobs.SingleAsync(j => j.DateiId == original.Id);

		Assert.True(disposition.Abandon);
		Assert.True(disposition.Visibility > TimeSpan.Zero);
		Assert.True(disposition.Visibility <= host.Options.RedisplayAfter);
		Assert.Equal(VideoStatus.Queued, job.Status);
		Assert.Equal(1, job.AttemptCount);
		Assert.NotNull(job.LastAttemptAt);
		Assert.Null(job.CompletedAt);

		var terminal = await host.Worker.HandleAsync(
			Message(original.Id, dequeueCount: host.Options.MaxAttempts), host.Deadline, CancellationToken.None);
		Assert.False(terminal.Abandon);
		job = await host.Db.VideoJobs.SingleAsync(j => j.DateiId == original.Id);
		Assert.Equal(VideoStatus.Failed, job.Status);
		Assert.Equal(VideoWorker.ExhaustedReason, job.FailureReason);
	}

	[Fact]
	public async Task MaintenancePausesWithoutTouchingRowOrBlob()
	{
		var host = new WorkerHost(maintenance: true);
		var (_, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.True(disposition.Abandon);
		Assert.Equal(VideoStatus.Queued, job.Status);
		Assert.Equal(0, job.AttemptCount);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task NonVideoMaterialJustDeletesItsMessages()
	{
		foreach (var (kind, contentType) in new[]
		{
			(DateiKinds.Uebe, "video/quicktime"),
			(DateiKinds.Aufnahme, "audio/mpeg"),
		})
		{
			var host = new WorkerHost();
			var (_, datei) = SeedVideoRecording(host, kind: kind, contentType: contentType);
			var job = SeedJob(host, datei);
			var disposition = await host.Worker.HandleAsync(Message(datei.Id), host.Deadline, CancellationToken.None);
			Assert.False(disposition.Abandon);
			// The row is a leftover of foreign material: nothing touched.
			Assert.Equal(VideoStatus.Queued, job.Status);
			Assert.Equal(0, host.Storage.OpenReadCalls);
		}
	}

	[Fact]
	public async Task DateiInTheTrashIsNotTranscoded()
	{
		var host = new WorkerHost();
		var (_, datei) = SeedVideoRecording(host, trashed: true);
		SeedJob(host, datei);

		var disposition = await host.Worker.HandleAsync(Message(datei.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(VideoStatus.Queued, (await host.Db.VideoJobs.SingleAsync()).Status);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task OversizeInputsFailWithoutOpeningTheTools()
	{
		var host = new WorkerHost();
		host.Options.MaxInputBytes = 1024;
		var (_, original) = SeedVideoRecording(host, sizeBytes: 4096);
		var job = SeedJob(host, original);

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(VideoStatus.Failed, job.Status);
		Assert.Equal(VideoWorker.TooLargeReason, job.FailureReason);
		Assert.Equal(0, job.AttemptCount);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task MissingJobRowIsSelfHealedAndProcessedToCompletion()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);

		var disposition = await host.Worker.HandleAsync(Message(original.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		var job = await host.Db.VideoJobs.SingleAsync();
		Assert.Equal(original.Id, job.DateiId);
		Assert.Equal(VideoStatus.Completed, job.Status);
	}

	[Fact]
	public async Task MissingDateiIsDeletedWithoutAnyRowWork()
	{
		var host = new WorkerHost();

		var disposition = await host.Worker.HandleAsync(
			Message(Guid.CreateVersion7()), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Empty(await host.Db.VideoJobs.ToListAsync());
	}

	[Fact]
	public async Task MalformedAndForeignEnvelopesAreDeletedAsPoison()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);
		var job = SeedJob(host, original);
		var before = (job.Status, job.AttemptCount, job.RowVersion);

		foreach (var body in new[]
		{
			"not json",
			JsonSerializer.Serialize(new { type = "extraction", dateiId = original.Id }),
			JsonSerializer.Serialize(new { type = AzureVideoQueue.TranscodeType, traceParent = "", traceState = "" },
				ExtractionEnvelope.SerializerOptions),
		})
		{
			var disposition = await host.Worker.HandleAsync(
				Message(original.Id, body: body), host.Deadline, CancellationToken.None);
			Assert.False(disposition.Abandon);
		}

		Assert.Equal(before, (job.Status, job.AttemptCount, job.RowVersion));
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public void LeaseAndBatchOptionsStayDefensivelyBounded()
	{
		var options = new VideoOptions();
		Assert.Equal("archive-videos", options.QueueName);
		Assert.Equal(1, options.BatchSize);
		Assert.Equal(1, options.VisibilityFitBatchSize);
		Assert.Equal(TimeSpan.FromHours(1), options.StaleRunningAfter);
		Assert.Equal(TimeSpan.FromHours(8), options.TimeBudget);
		Assert.Equal("ffmpeg", options.FfmpegPath);
		Assert.Equal("ffprobe", options.FfprobePath);
		options.BatchSize = 0;
		options.StaleRunningAfter = TimeSpan.Zero;
		options.FfmpegPath = "  ";
		Assert.Equal(1, options.BatchSize);
		Assert.Equal(TimeSpan.FromHours(1), options.StaleRunningAfter);
		Assert.Equal("ffmpeg", options.FfmpegPath);
	}

	[Fact]
	public async Task PumpDrainsUntilTheQueueIsEmptyAndAppliesDispositions()
	{
		var host = new WorkerHost();
		var (_, original) = SeedVideoRecording(host);
		SeedJob(host, original);
		var poison = Message(Guid.CreateVersion7(), body: "not json");
		host.Source.Seed(poison, Message(original.Id));

		var processed = await host.Pump.DrainAsync(CancellationToken.None);

		Assert.Equal(2, processed);
		Assert.Equal(0, host.Source.PendingCount);
	}

	[Fact]
	public async Task DispatcherSweepsExactlyTheUnstampedAndStaleQueuedRows()
	{
		var host = new WorkerHost();
		var now = DateTimeOffset.UtcNow;
		var unstamped = SeedJob(host, SeedVideoRecording(host).Original, createdAt: now.AddMinutes(-30));
		var stale = SeedJob(host, SeedVideoRecording(host).Original,
			createdAt: now.AddHours(-2), lastEnqueuedAt: now.AddHours(-1).AddMinutes(-1));
		var fresh = SeedJob(host, SeedVideoRecording(host).Original,
			createdAt: now.AddMinutes(-10), lastEnqueuedAt: now);
		var running = SeedJob(host, SeedVideoRecording(host).Original, createdAt: now.AddMinutes(-5));
		running.Status = VideoStatus.Running;
		host.Db.SaveChanges();
		var queue = new FakeVideoQueue();

		var dispatched = await VideoDispatcher.DispatchPendingAsync(
			host.Db, queue, Options.Create(host.Options), TimeProvider.System,
			NullLogger.Instance, CancellationToken.None);

		Assert.Equal(2, dispatched);
		// Swept in CreatedAt order: the two-hour-old stale row first.
		Assert.Equal([stale.DateiId, unstamped.DateiId], queue.Attempts);
		var jobs = await host.Db.VideoJobs.ToDictionaryAsync(j => j.DateiId);
		Assert.NotNull(jobs[unstamped.DateiId].LastEnqueuedAt);
		Assert.NotNull(jobs[stale.DateiId].LastEnqueuedAt);
		Assert.Equal(now, jobs[fresh.DateiId].LastEnqueuedAt);
		Assert.Null(jobs[running.DateiId].LastEnqueuedAt);
	}

	// ---- fixtures ----

	private static (Recording Recording, Datei Original) SeedVideoRecording(WorkerHost host,
		Guid? eventId = null, string kind = DateiKinds.Aufnahme, string contentType = "video/quicktime",
		long sizeBytes = 2048, bool trashed = false)
	{
		var account = Guid.CreateVersion7();
		var now = DateTimeOffset.UtcNow;
		var ev = new ChoirEvent
		{
			Title = "Auftritt Aufnahmen (#161)", Kind = EventKinds.Known[0],
			DateYear = 1962, DateDay = 3, CreatedByAccountId = account, UpdatedByAccountId = account,
			CreatedAt = now, UpdatedAt = now, RowVersion = 1,
		};
		var recording = new Recording
		{
			EventId = ev.Id, Event = ev, Label = "Konzertfilm", Kind = RecordingKinds.Video,
			DurationSeconds = 3450,
			CreatedAt = now, CreatedByAccountId = account, UpdatedAt = now, UpdatedByAccountId = account,
			RowVersion = 1,
		};
		var datei = new Datei
		{
			EventId = ev.Id,
			Event = ev,
			Kind = kind,
			BlobName = $"dateien/{Guid.CreateVersion7():N}/mov",
			ContentType = contentType,
			SizeBytes = sizeBytes,
			OriginalFileName = "Konzert.mov",
			CreatedByAccountId = account,
			CreatedAt = now,
			RowVersion = 1,
		};
		if (kind == DateiKinds.Aufnahme && contentType.StartsWith("video/", StringComparison.Ordinal))
			recording.OriginalDateiId = datei.Id;
		if (trashed)
			datei.DeletedAt = now;
		host.Db.Events.Add(ev);
		host.Db.Recordings.Add(recording);
		host.Db.Dateien.Add(datei);
		host.Db.SaveChanges();
		host.Storage.Store(datei.BlobName, new byte[sizeBytes]);
		return (recording, datei);
	}

	private static VideoJob SeedJob(WorkerHost host, Datei datei,
		DateTimeOffset? createdAt = null, DateTimeOffset? lastEnqueuedAt = null)
	{
		var job = new VideoJob
		{
			DateiId = datei.Id,
			Status = VideoStatus.Queued,
			TriggeredByAccountId = datei.CreatedByAccountId,
			CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
			UpdatedAt = createdAt ?? DateTimeOffset.UtcNow,
			LastEnqueuedAt = lastEnqueuedAt,
		};
		host.Db.VideoJobs.Add(job);
		host.Db.SaveChanges();
		return job;
	}

	private static ExtractionMessage Message(Guid dateiId, long dequeueCount = 1, string? body = null) =>
		new(Guid.CreateVersion7().ToString(), $"pop-{Guid.CreateVersion7():N}",
			body ?? JsonSerializer.Serialize(
				new ExtractionEnvelope(AzureVideoQueue.TranscodeType, dateiId, null, null),
				ExtractionEnvelope.SerializerOptions),
			dequeueCount);

	/// <summary>
	/// No-WebApplicationFactory test host: the worker is constructed directly
	/// against its own InMemory database, a fake storage adapter and transcoder,
	/// an in-memory configuration (maintenance flag) and the fake message source.
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

		public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken) =>
			inner.ListAsync(prefix, cancellationToken);

		public Task SetAccessTierAsync(string blobName, string tier, CancellationToken cancellationToken) =>
			inner.SetAccessTierAsync(blobName, tier, cancellationToken);

		public Task UploadAsync(string blobName, Stream content, string? contentType, CancellationToken cancellationToken) =>
			inner.UploadAsync(blobName, content, contentType, cancellationToken);

		public bool Has(string blobName) => inner.Has(blobName);
	}

	/// <summary>The transcode seam: probe/tool-missing controlled per test.</summary>
	private sealed class FakeTranscoder : IVideoTranscoder
	{
		public VideoProbe? ProbeResult { get; set; } = new("h264", "aac", 3840, 2160);
		public bool ToolMissing { get; set; }
		public bool Corrupt { get; set; }

		public Task<VideoProbe?> InspectAsync(string inputPath, CancellationToken token) => Corrupt
			? throw new VideoWorkerException(VideoWorker.UnreadableVideoReason)
			: ToolMissing
				? throw new VideoWorkerException(VideoWorker.MissingToolReason)
				: Task.FromResult(ProbeResult);

		public Task TranscodeAsync(string inputPath, string outputPath, VideoPlan plan, CancellationToken token) =>
			File.WriteAllBytesAsync(outputPath, [1, 2, 3, 4], token);
	}

	/// <summary>The video wrapper of the shared in-memory receive seam.</summary>
	internal sealed class FakeVideoSource(params ExtractionMessage[] initial) : IVideoMessageSource
	{
		private readonly FakeExtractionMessageSource inner = new(initial);

		public Task<IReadOnlyList<ExtractionMessage>> ReceiveBatchAsync(int maxMessages, CancellationToken token) =>
			inner.ReceiveBatchAsync(maxMessages, token);

		public Task DeleteAsync(string messageId, string popReceipt, CancellationToken token) =>
			inner.DeleteAsync(messageId, popReceipt, token);

		public Task AbandonAsync(string messageId, string popReceipt, TimeSpan visibility, CancellationToken token) =>
			inner.AbandonAsync(messageId, popReceipt, visibility, token);

		// The fake source members the video pump tests use.
		public void Seed(params ExtractionMessage[] messages) => inner.Seed(messages);

		public int PendingCount => inner.PendingCount;
	}

	/// <summary>Dispatch fake that fails exactly one Datei.</summary>
	private sealed class FakeVideoQueue : IVideoQueue
	{
		public List<Guid> Attempts { get; } = [];

		public Task<bool> SendAsync(Guid dateiId, CancellationToken token)
		{
			Attempts.Add(dateiId);
			return Task.FromResult(true);
		}
	}

	/// <summary>
	/// No-WebApplicationFactory test host: the worker is constructed directly
	/// against its own InMemory database, a fake storage adapter and
	/// transcoder, an in-memory configuration (maintenance flag) and the fake
	/// message source — the extraction worker test host's shape.
	/// </summary>
	private sealed class WorkerHost
	{
		public WorkerHost(bool maintenance = false, TimeProvider? time = null)
		{
			Time = time ?? TimeProvider.System;
			DbOptions = new DbContextOptionsBuilder<ArchiveDbContext>()
				.UseInMemoryDatabase($"video-worker-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
				.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
				.UseApplicationServiceProvider(new ServiceCollection()
					.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(o =>
						o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3)
					.BuildServiceProvider())
				.Options;
			Db = new ArchiveDbContext(DbOptions);
			Storage = new WrapperStorage();
			Transcoder = new FakeTranscoder();
			Source = new FakeVideoSource();			Options = new VideoOptions();
			Configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Archive:MaintenanceMode"] = maintenance ? "true" : "false",
				})
				.Build();
			Worker = new VideoWorker(Db, Storage, Transcoder, Microsoft.Extensions.Options.Options.Create(Options),
				Configuration, time ?? TimeProvider.System, NullLogger<VideoWorker>.Instance);
			// Match production: each factory-created scope owns a fresh context
			// and worker, disposed after its single message.
			var services = new ServiceCollection();
			services.AddScoped(_ => new ArchiveDbContext(DbOptions));
			services.AddScoped(sp => new VideoWorker(sp.GetRequiredService<ArchiveDbContext>(), Storage, Transcoder,
				Microsoft.Extensions.Options.Options.Create(Options), Configuration,
				time ?? TimeProvider.System, NullLogger<VideoWorker>.Instance));
			var provider = services.BuildServiceProvider();
			Pump = new VideoPump(Source, () => provider.CreateAsyncScope(), Microsoft.Extensions.Options.Options.Create(Options),
				Configuration, time ?? TimeProvider.System, NullLogger<VideoPump>.Instance);
		}

		public DbContextOptions<ArchiveDbContext> DbOptions { get; }

		public ArchiveDbContext Db { get; }

		public WrapperStorage Storage { get; }

		public FakeTranscoder Transcoder { get; }

		public FakeVideoSource Source { get; }

		public VideoOptions Options { get; }

		private TimeProvider Time { get; }
		public DateTimeOffset Deadline => Time.GetUtcNow() + Options.TimeBudget;

		private IConfiguration Configuration { get; }

		public VideoWorker Worker { get; }

		public VideoPump Pump { get; }
	}
}
