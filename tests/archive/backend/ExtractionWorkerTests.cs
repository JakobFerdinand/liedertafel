using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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
	public async Task OverlengthTextFailsDeterministically()
	{
		var host = new WorkerHost();
		host.Options.MaxTextCharacters = 10;
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Mehr als zehn Zeichen"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(PdfTextExtractor.TooMuchTextReason, job.FailureReason);
		Assert.Null(job.Text);
	}

	[Fact]
	public async Task PageCapCompletesWithOnlyTheIncludedPages()
	{
		var host = new WorkerHost();
		host.Options.MaxPdfPages = 1;
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		var bytes = PdfWithCorruptSecondPage();
		// The document and included page are readable, but parsing the excluded
		// page really throws: collecting every page then truncating cannot pass.
		using (var document = PdfDocument.Open(bytes))
		{
			Assert.Equal(2, document.NumberOfPages);
			Assert.Contains("Erste Seite", document.GetPage(1).Text);
			Assert.ThrowsAny<Exception>(() => document.GetPage(2));
		}
		host.Storage.Store(revision.BlobName, bytes);

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Completed, job.Status);
		Assert.Contains("Erste Seite", job.Text);
		Assert.DoesNotContain("Zweite", job.Text);
		Assert.Null(job.FailureReason);
	}

	[Fact]
	public void NonSeekableFallbackRejectsOversizeWithoutUnboundedBuffering()
	{
		using var stream = new NonSeekablePdfStream(new byte[100_000]);
		var outcome = PdfTextExtractor.Extract(stream, new ExtractionOptions { MaxPdfBytes = 100 }, CancellationToken.None);

		Assert.Equal(PdfTextExtractor.TooLargeReason, outcome.FailureReason);
		Assert.Equal(101, stream.Position);
		Assert.True(stream.CanRead); // The caller retains ownership.
	}

	[Fact]
	public void NonSeekableFallbackParsesAnExactlyBoundedDocument()
	{
		var bytes = TextPdf("Begrenzter Text");
		using var stream = new NonSeekablePdfStream(bytes);
		var outcome = PdfTextExtractor.Extract(stream, new ExtractionOptions { MaxPdfBytes = bytes.Length }, CancellationToken.None);

		Assert.Contains("Begrenzter Text", outcome.Text);
	}

	[Fact]
	public void CancelledBudgetIsCheckedBeforeOpeningPdf()
	{
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();
		Assert.Throws<OperationCanceledException>(() => PdfTextExtractor.Extract(
			new ThrowingPdfStream(new InvalidOperationException("Must never parse")), new ExtractionOptions(), cancelled.Token));
	}

	[Fact]
	public void BudgetExpiryDuringSynchronousParsingNeverReturnsText()
	{
		using var budget = new CancellationTokenSource();
		using var stream = new CancellingPdfStream(TextPdf("Nie akzeptiert"), budget);
		Assert.Throws<OperationCanceledException>(() => PdfTextExtractor.Extract(stream, new ExtractionOptions(), budget.Token));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task BlobOpenIsInsideTheBudgetEvenWhenStorageIgnoresCancellation(bool ignoreCancellation)
	{
		var host = new WorkerHost();
		host.Options.TimeBudget = TimeSpan.FromMilliseconds(100);
		host.Storage.OpenDelay = TimeSpan.FromMilliseconds(300);
		host.Storage.IgnoreCancellation = ignoreCancellation;
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Nie akzeptiert"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.True(disposition.Abandon);
		job = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(1, job.AttemptCount);
		Assert.Null(job.Text);
		Assert.Null(job.CompletedAt);
	}

	[Theory]
	[InlineData(ExtractionStatus.Completed)]
	[InlineData(ExtractionStatus.NoText)]
	[InlineData(ExtractionStatus.Failed)]
	public async Task DeadlineExpiryNeverRewritesAConcurrentTerminalWinner(ExtractionStatus status)
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		await using var winnerDb = new ArchiveDbContext(host.DbOptions);
		ExtractionJob? winner = null;
		host.Storage.BeforeOpenRead = async () =>
		{
			winner = await winnerDb.ExtractionJobs.SingleAsync();
			Assert.Equal(ExtractionStatus.Running, winner.Status);
			winner.Status = status;
			winner.Text = status == ExtractionStatus.Completed ? "Text des Gewinners" : null;
			winner.FailureReason = status == ExtractionStatus.Failed ? PdfTextExtractor.CorruptReason : null;
			winner.CompletedAt = status == ExtractionStatus.Failed ? null : DateTimeOffset.UtcNow;
			winner.UpdatedAt = DateTimeOffset.UtcNow;
			winner.RowVersion++;
			await winnerDb.SaveChangesAsync();
		};
		host.Storage.OpenDelay = TimeSpan.FromMilliseconds(300);

		var disposition = await host.Worker.HandleAsync(Message(revision.Id),
			TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(100), CancellationToken.None);

		Assert.Equal(MessageDisposition.Delete, disposition);
		Assert.NotNull(winner);
		var saved = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(winner.Status, saved.Status);
		Assert.Equal(winner.Text, saved.Text);
		Assert.Equal(winner.FailureReason, saved.FailureReason);
		Assert.Equal(winner.CompletedAt, saved.CompletedAt);
		Assert.Equal(winner.UpdatedAt, saved.UpdatedAt);
		Assert.Equal(winner.AttemptCount, saved.AttemptCount);
		Assert.Equal(winner.RowVersion, saved.RowVersion);
		Assert.Equal(1, host.Storage.OpenReadCalls);
	}

	[Theory]
	[InlineData(ExtractionStatus.Running)]
	[InlineData(ExtractionStatus.Queued)]
	public async Task DeadlineExpiryNeverRewritesANewerLeaseOrReleasedRow(ExtractionStatus status)
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		await using var otherDb = new ArchiveDbContext(host.DbOptions);
		ExtractionJob? other = null;
		host.Storage.BeforeOpenRead = async () =>
		{
			other = await otherDb.ExtractionJobs.SingleAsync();
			Assert.Equal(ExtractionStatus.Running, other.Status);
			other.Status = status;
			other.AttemptCount = status == ExtractionStatus.Running ? other.AttemptCount + 1 : 0;
			other.LastAttemptAt = status == ExtractionStatus.Running ? DateTimeOffset.UtcNow : null;
			other.UpdatedAt = DateTimeOffset.UtcNow;
			other.RowVersion++;
			await otherDb.SaveChangesAsync();
		};
		host.Storage.OpenDelay = TimeSpan.FromMilliseconds(300);

		var disposition = await host.Worker.HandleAsync(Message(revision.Id, dequeueCount: 2),
			TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(100), CancellationToken.None);

		Assert.True(disposition.Abandon);
		Assert.Equal(host.Options.VisibilityBackoff * 2, disposition.Visibility);
		Assert.NotNull(other);
		var saved = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(other.Status, saved.Status);
		Assert.Equal(other.AttemptCount, saved.AttemptCount);
		Assert.Equal(other.LastAttemptAt, saved.LastAttemptAt);
		Assert.Equal(other.UpdatedAt, saved.UpdatedAt);
		Assert.Equal(other.RowVersion, saved.RowVersion);
		Assert.Equal(1, host.Storage.OpenReadCalls);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task DeadlineExpiryReleasesOnlyItsOwnCountedLease(bool exhaustRow, bool exhaustMessage)
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.AttemptCount = exhaustRow ? host.Options.MaxAttempts - 1 : 0;
		await host.Db.SaveChangesAsync();
		host.Storage.OpenDelay = TimeSpan.FromMilliseconds(300);

		var disposition = await host.Worker.HandleAsync(
			Message(revision.Id, dequeueCount: exhaustMessage ? host.Options.MaxAttempts : 1),
			TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(100), CancellationToken.None);

		var exhausted = exhaustRow || exhaustMessage;
		Assert.Equal(!exhausted, disposition.Abandon);
		var saved = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(exhausted ? ExtractionStatus.Failed : ExtractionStatus.Queued, saved.Status);
		Assert.Equal(exhaustRow ? host.Options.MaxAttempts : 1, saved.AttemptCount);
		Assert.Equal(exhausted ? ExtractionWorker.ExhaustedReason : null, saved.FailureReason);
		Assert.Equal(2u, saved.RowVersion);
		Assert.NotNull(saved.LastAttemptAt);
		Assert.Null(saved.Text);
		Assert.Null(saved.CompletedAt);
		Assert.Equal(1, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task DuplicateMessagesNeverReworkAFinishedRow()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Erster Text"));
		await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);
		var completed = (job.Status, job.AttemptCount, job.RowVersion, job.Text);

		var duplicate = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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
			Message(revision.Id, dequeueCount: host.Options.MaxAttempts), host.Deadline, CancellationToken.None);
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
			Message(revision.Id, dequeueCount: host.Options.MaxAttempts), host.Deadline, CancellationToken.None);

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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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
		job.LastAttemptAt = DateTimeOffset.UtcNow;
		host.Db.SaveChanges();
		var before = (job.RowVersion, job.UpdatedAt, job.LastAttemptAt);

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.True(disposition.Abandon);
		Assert.True(disposition.Visibility > TimeSpan.Zero);
		Assert.Equal(ExtractionStatus.Running, job.Status);
		Assert.Equal(0, job.AttemptCount);
		Assert.Equal(before, (job.RowVersion, job.UpdatedAt, job.LastAttemptAt));
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StaleRunningLeaseIsRetakenAndCompleted(bool missingTimestamp)
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Running;
		job.AttemptCount = 2;
		job.LastAttemptAt = missingTimestamp ? null : DateTimeOffset.UtcNow - host.Options.StaleRunningAfter - TimeSpan.FromMinutes(1);
		host.Db.SaveChanges();
		host.Storage.Store(revision.BlobName, TextPdf("Wieder aufgenommen"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Completed, job.Status);
		Assert.Equal(3, job.AttemptCount);
		Assert.Contains("Wieder aufgenommen", job.Text);
		Assert.NotNull(job.CompletedAt);
		Assert.True(job.LastAttemptAt > DateTimeOffset.UtcNow.AddMinutes(-1));
		Assert.Equal(2u, job.RowVersion);
	}

	[Fact]
	public async Task StaleRunningLeaseWithSpentBudgetFailsWithoutOpeningTheBlob()
	{
		var clock = new MutableTimeProvider();
		var host = new WorkerHost(time: clock);
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Running;
		job.AttemptCount = host.Options.MaxAttempts;
		job.LastAttemptAt = clock.Now - host.Options.StaleRunningAfter - TimeSpan.FromMinutes(1);
		host.Db.SaveChanges();
		var rowVersion = job.RowVersion;
		host.Storage.ThrowOnOpenRead = true;

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.Equal(MessageDisposition.Delete, disposition);
		host.Db.ChangeTracker.Clear();
		var saved = await host.Db.ExtractionJobs.SingleAsync();
		Assert.Equal(ExtractionStatus.Failed, saved.Status);
		Assert.Equal(ExtractionWorker.ExhaustedReason, saved.FailureReason);
		Assert.Equal(host.Options.MaxAttempts, saved.AttemptCount);
		Assert.Equal(clock.Now, saved.LastAttemptAt);
		Assert.Equal(clock.Now, saved.UpdatedAt);
		Assert.Equal(rowVersion + 1, saved.RowVersion);
		Assert.Null(saved.CompletedAt);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task StaleRunningLeaseCanCompleteItsFinalAllowedAttempt()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Running;
		job.AttemptCount = host.Options.MaxAttempts - 1;
		job.LastAttemptAt = DateTimeOffset.UtcNow - host.Options.StaleRunningAfter - TimeSpan.FromMinutes(1);
		host.Db.SaveChanges();
		host.Storage.Store(revision.BlobName, TextPdf("Letzter erlaubter Versuch"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.Equal(MessageDisposition.Delete, disposition);
		Assert.Equal(ExtractionStatus.Completed, job.Status);
		Assert.Equal(host.Options.MaxAttempts, job.AttemptCount);
		Assert.Contains("Letzter erlaubter Versuch", job.Text);
		Assert.Equal(1, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task RepeatedStaleRetakesSpendTheAttemptLadderAndFailTerminally()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.ThrowOnOpenRead = true;
		for (var attempt = 1; attempt <= host.Options.MaxAttempts; attempt++)
		{
			// Simulate each preceding process dying with a Running row.
			job.Status = ExtractionStatus.Running;
			job.LastAttemptAt = DateTimeOffset.UtcNow - host.Options.StaleRunningAfter - TimeSpan.FromMinutes(1);
			host.Db.SaveChanges();
			var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);
			Assert.Equal(attempt, job.AttemptCount);
			Assert.Equal(attempt < host.Options.MaxAttempts, disposition.Abandon);
		}
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(ExtractionWorker.ExhaustedReason, job.FailureReason);
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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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
			Message(Guid.CreateVersion7()), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Empty(await host.Db.ExtractionJobs.ToListAsync());
	}

	[Fact]
	public async Task MissingJobRowIsSelfHealedAndProcessedToCompletion()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		host.Storage.Store(revision.BlobName, TextPdf("Selbstheilung"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(ExtractionWorker.TooLargeReason, job.FailureReason);
		Assert.Equal(0, job.AttemptCount);
		Assert.NotNull(job.LastAttemptAt);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task StaleOversizeLeaseIsCountedBeforeItsDeterministicFailure()
	{
		var host = new WorkerHost();
		host.Options.MaxPdfBytes = 1024;
		var (_, revision) = SeedRevision(host, sizeBytes: 4096);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Running;
		job.AttemptCount = 2;
		host.Db.SaveChanges();

		var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

		Assert.False(disposition.Abandon);
		Assert.Equal(ExtractionStatus.Failed, job.Status);
		Assert.Equal(3, job.AttemptCount);
		Assert.Equal(PdfTextExtractor.TooLargeReason, job.FailureReason);
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

			var disposition = await host.Worker.HandleAsync(Message(revision.Id), host.Deadline, CancellationToken.None);

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
				Message(revision.Id, body: body), host.Deadline, CancellationToken.None);
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

		await host.Worker.HandleAsync(Message(first.Id), host.Deadline, CancellationToken.None);
		await host.Worker.HandleAsync(Message(second.Id), host.Deadline, CancellationToken.None);
		var lateDuplicate = await host.Worker.HandleAsync(Message(first.Id), host.Deadline, CancellationToken.None);

		Assert.False(lateDuplicate.Abandon);
		var jobs = await host.Db.ExtractionJobs.ToDictionaryAsync(j => j.RevisionId);
		Assert.Equal(ExtractionStatus.Completed, jobs[first.Id].Status);
		Assert.Contains("Erste Fassung", jobs[first.Id].Text);
		Assert.Equal(ExtractionStatus.Completed, jobs[second.Id].Status);
		Assert.Contains("Zweite Fassung", jobs[second.Id].Text);
	}

	[Fact]
	public async Task SlowBlobOpenExceedingAbsoluteDeadlineRequeuesCountedAttempt()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Nie akzeptiert"));
		host.Storage.OpenDelay = TimeSpan.FromMilliseconds(300);
		host.Storage.IgnoreCancellation = true;

		var disposition = await host.Worker.HandleAsync(Message(revision.Id),
			TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(100), CancellationToken.None);

		Assert.True(disposition.Abandon);
		job = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(1, job.AttemptCount);
		Assert.Null(job.Text);
		Assert.Null(job.CompletedAt);
		Assert.Equal(1, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task DeadlineAlsoCancelsTheRunningTransitionBeforeBlobOpen()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		var dbOptions = new DbContextOptionsBuilder<ArchiveDbContext>(host.DbOptions)
			.AddInterceptors(new DelayedRunningTransition())
			.Options;
		await using var db = new ArchiveDbContext(dbOptions);
		var worker = new ExtractionWorker(db, host.Storage, Options.Create(host.Options),
			new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<ExtractionWorker>.Instance);

		var disposition = await worker.HandleAsync(Message(revision.Id),
			TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(100), CancellationToken.None);

		Assert.True(disposition.Abandon);
		var job = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(0, job.AttemptCount);
		Assert.Null(job.CompletedAt);
		Assert.Equal(0, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task AlreadyExpiredDeadlineAbandonsWithoutOpeningBlobOrTakingLease()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Nie geoeffnet"));

		var disposition = await host.Worker.HandleAsync(Message(revision.Id),
			TimeProvider.System.GetUtcNow() - TimeSpan.FromMilliseconds(1), CancellationToken.None);

		Assert.True(disposition.Abandon);
		var job = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(0, job.AttemptCount);
		Assert.Null(job.CompletedAt);
		Assert.Equal(0, host.Storage.OpenReadCalls);
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
		Assert.All(host.Source.RequestedBatchSizes, size => Assert.InRange(size, 1, 4));
		Assert.Equal(4, host.Source.RequestedBatchSizes[0]);
	}

	[Fact]
	public async Task PumpClampsTheLastAttemptToRemainingReceiveVisibility()
	{
		var clock = new MutableTimeProvider();
		var host = new WorkerHost(time: clock);
		var (_, revision) = SeedRevision(host);
		SeedJob(host, revision);
		host.Storage.Store(revision.BlobName, TextPdf("Hallo Liedertafel"));
		host.Storage.OpenDelay = TimeSpan.FromSeconds(5);
		var remaining = TimeSpan.FromMilliseconds(100);
		host.Source.Seed(Message(Guid.NewGuid(), body: "poison"), Message(revision.Id));
		host.Source.AfterDelete = () => clock.Now += host.Options.ReceiveVisibility - remaining;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

		Assert.Equal(2, await host.Pump.DrainAsync(timeout.Token));

		var job = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(ExtractionStatus.Queued, job.Status);
		Assert.Equal(1, job.AttemptCount);
		Assert.Single(host.Source.Deleted);
		Assert.Single(host.Source.Abandoned);
		Assert.Equal(1, host.Storage.OpenReadCalls);
	}

	[Fact]
	public async Task PumpReleasesAnExhaustedBatchAndEndsTheRound()
	{
		var clock = new MutableTimeProvider();
		var host = new WorkerHost(time: clock);
		host.Options.DrainBudget = TimeSpan.FromHours(1);
		host.Source.Seed(Enumerable.Range(0, 5).Select(_ => Message(Guid.NewGuid(), body: "poison")).ToArray());
		host.Source.AfterDelete = () => clock.Now += host.Options.ReceiveVisibility;

		Assert.Equal(1, await host.Pump.DrainAsync(CancellationToken.None));
		Assert.Equal(3, host.Source.Abandoned.Count);
		Assert.All(host.Source.Abandoned, item => Assert.Equal(TimeSpan.Zero, item.Visibility));
		Assert.Single(host.Source.RequestedBatchSizes);
		Assert.Equal(1, host.Source.PendingCount);
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
	[Fact]
	public async Task PumpAbandonsTheRestOfABatchImmediatelyAtTheDeadline()
	{
		var clock = new MutableTimeProvider();
		var host = new WorkerHost(time: clock);
		host.Source.Seed(Message(Guid.NewGuid(), body: "poison"), Message(Guid.NewGuid(), body: "poison"), Message(Guid.NewGuid(), body: "poison"));
		host.Source.AfterDelete = () => clock.Now += host.Options.DrainBudget;

		var processed = await host.Pump.DrainAsync(CancellationToken.None);

		Assert.Equal(1, processed);
		Assert.Single(host.Source.Deleted);
		Assert.Equal(2, host.Source.Abandoned.Count);
		Assert.All(host.Source.Abandoned, item => Assert.Equal(TimeSpan.Zero, item.Visibility));
	}

	[Fact]
	public async Task PumpContinuesAfterAReceiptDeleteFailure()
	{
		var host = new WorkerHost();
		host.Source.Seed(Message(Guid.NewGuid(), body: "poison"), Message(Guid.NewGuid(), body: "poison"));
		host.Source.ThrowOnNextDelete = true;

		Assert.Equal(2, await host.Pump.DrainAsync(CancellationToken.None));
		Assert.Single(host.Source.Deleted);
	}

	[Fact]
	public async Task PumpContinuesReleasingRemainingReceiptsAfterAnAbandonFailure()
	{
		var clock = new MutableTimeProvider();
		var host = new WorkerHost(time: clock);
		host.Source.Seed(Message(Guid.NewGuid(), body: "poison"), Message(Guid.NewGuid(), body: "poison"), Message(Guid.NewGuid(), body: "poison"));
		host.Source.AfterDelete = () => clock.Now += host.Options.DrainBudget;
		host.Source.ThrowOnNextAbandon = true;

		Assert.Equal(1, await host.Pump.DrainAsync(CancellationToken.None));
		Assert.Single(host.Source.Abandoned);
	}

	[Fact]
	public async Task PumpUsesFreshMessageScopesSoAnEditorRetryIsVisible()
	{
		var host = new WorkerHost();
		var (_, revision) = SeedRevision(host);
		var job = SeedJob(host, revision);
		job.Status = ExtractionStatus.Failed;
		host.Db.SaveChanges();
		host.Storage.Store(revision.BlobName, TextPdf("Nach dem Retry"));
		host.Options.BatchSize = 1;
		host.Source.Seed(Message(revision.Id), Message(revision.Id));
		host.Source.AfterDelete = () =>
		{
			// An editor's independent context resets the failed row between
			// receives. Reusing the first worker context would hide this retry.
			using var db = new ArchiveDbContext(host.DbOptions);
			var current = db.ExtractionJobs.Single();
			current.Status = ExtractionStatus.Queued;
			current.RowVersion++;
			db.SaveChanges();
			host.Source.AfterDelete = null;
		};

		Assert.Equal(2, await host.Pump.DrainAsync(CancellationToken.None));
		var result = await host.Db.ExtractionJobs.AsNoTracking().SingleAsync();
		Assert.Equal(ExtractionStatus.Completed, result.Status);
		Assert.Contains("Nach dem Retry", result.Text);
		Assert.Equal(1, result.AttemptCount);
	}

	[Fact]
	public void LeaseAndBatchOptionsStayDefensivelyBounded()
	{
		var options = new ExtractionOptions();
		Assert.Equal(8, options.BatchSize);
		Assert.Equal(4, options.VisibilityFitBatchSize);
		Assert.Equal(TimeSpan.FromMinutes(15), options.StaleRunningAfter);
		options.StaleRunningAfter = TimeSpan.Zero;
		options.BatchSize = int.MaxValue;
		Assert.Equal(TimeSpan.FromMinutes(15), options.StaleRunningAfter);
		Assert.Equal(8, options.BatchSize);
		options.BatchSize = 0;
		Assert.Equal(8, options.BatchSize);
		options.BatchSize = 2;
		Assert.Equal(2, options.VisibilityFitBatchSize);
		options.ReceiveVisibility = TimeSpan.FromSeconds(1);
		Assert.Equal(1, options.VisibilityFitBatchSize);
	}

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

	private static byte[] TextPdf(params string[] pages)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		foreach (var text in pages)
			builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(50, 700), font);
		return builder.Build();
	}

	private static byte[] ScannedPdf()
	{
		var builder = new PdfDocumentBuilder();
		builder.AddPage(PageSize.A4);
		return builder.Build();
	}

	private static byte[] PdfWithCorruptSecondPage()
	{
		var bytes = TextPdf("Erste Seite", "Zweite Seite");
		var pdf = Encoding.Latin1.GetString(bytes);
		var streams = Regex.Matches(pdf, @"\bstream\r?\n");
		Assert.Equal(2, streams.Count);
		var second = streams[1];
		var dictionaryStart = pdf.LastIndexOf("<<", second.Index, StringComparison.Ordinal);
		Assert.Contains("/FlateDecode", pdf[dictionaryStart..second.Index]);
		var start = second.Index + second.Length;
		var end = pdf.IndexOf("endstream", start, StringComparison.Ordinal);
		Assert.True(end > start);
		// Keep object lengths and xref offsets intact while invalidating only
		// page 2's content decoder. Document opening/page 1 remain valid.
		var filterStart = pdf.IndexOf("/FlateDecode", dictionaryStart, StringComparison.Ordinal);
		Encoding.ASCII.GetBytes("/BadFilterXX").CopyTo(bytes, filterStart);
		return bytes;
	}

	private static byte[] CorruptPdf() => "%PDF-1.7\n%%EOF\n"u8.ToArray();

	private sealed class ThrowingPdfStream(Exception failure) : MemoryStream
	{
		public override int Read(byte[] buffer, int offset, int count) => throw failure;
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			Task.FromException<int>(failure);
	}

	private sealed class NonSeekablePdfStream(byte[] bytes) : MemoryStream(bytes)
	{
		public override bool CanSeek => false;
	}

	private sealed class CancellingPdfStream(byte[] bytes, CancellationTokenSource budget) : MemoryStream(bytes)
	{
		public override int Read(byte[] buffer, int offset, int count)
		{
			budget.Cancel();
			return base.Read(buffer, offset, count);
		}
	}

	private sealed class MutableTimeProvider : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
		public override DateTimeOffset GetUtcNow() => Now;
	}

	private sealed class DelayedRunningTransition : SaveChangesInterceptor
	{
		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			if (eventData.Context!.ChangeTracker.Entries<ExtractionJob>()
				.Any(entry => entry.Entity.Status == ExtractionStatus.Running))
				await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
			return result;
		}
	}

	/// <summary>
	/// No-WebApplicationFactory test host: the worker is constructed directly
	/// against its own InMemory database, a fake storage adapter, an in-memory
	/// configuration (maintenance flag) and the fake message source.
	/// </summary>
	private sealed class WorkerHost
	{
		public WorkerHost(bool maintenance = false, TimeProvider? time = null)
		{
			Time = time ?? TimeProvider.System;
			DbOptions = new DbContextOptionsBuilder<ArchiveDbContext>()
				.UseInMemoryDatabase($"extraction-worker-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
				.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
				// EF shares one cached model between in-memory contexts with the
				// same options, so every context must build the same model as the
				// application: Identity schema version 3 (passkey tables). Without
				// this, a WorkerHost that builds the model first breaks passkey
				// endpoints in unrelated test hosts.
				.UseApplicationServiceProvider(new ServiceCollection()
					.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(o =>
						o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3)
					.BuildServiceProvider())
				.Options;
			Db = new ArchiveDbContext(DbOptions);
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
				Configuration, time ?? TimeProvider.System, NullLogger<ExtractionWorker>.Instance);
			// Match production: each factory-created scope owns a fresh context
			// and worker, disposed after its single message.
			var services = new ServiceCollection();
			services.AddScoped(_ => new ArchiveDbContext(DbOptions));
			services.AddScoped(sp => new ExtractionWorker(sp.GetRequiredService<ArchiveDbContext>(), Storage,
				Microsoft.Extensions.Options.Options.Create(Options), Configuration,
				time ?? TimeProvider.System, NullLogger<ExtractionWorker>.Instance));
			var provider = services.BuildServiceProvider();
			Pump = new ExtractionPump(Source, () => provider.CreateAsyncScope(), Microsoft.Extensions.Options.Options.Create(Options),
				Configuration, time ?? TimeProvider.System, NullLogger<ExtractionPump>.Instance);
		}

		public DbContextOptions<ArchiveDbContext> DbOptions { get; }

		public ArchiveDbContext Db { get; }

		public WrapperStorage Storage { get; }

		public FakeExtractionMessageSource Source { get; }

		public ExtractionOptions Options { get; }

		private TimeProvider Time { get; }
		public DateTimeOffset Deadline => Time.GetUtcNow() + Options.TimeBudget;

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
		public TimeSpan OpenDelay { get; set; }
		public bool IgnoreCancellation { get; set; }
		public Func<Task>? BeforeOpenRead { get; set; }

		public async Task<Stream?> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
		{
			OpenReadCalls++;
			if (BeforeOpenRead is not null)
				await BeforeOpenRead();
			if (ThrowOnOpenRead)
				throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
			if (OpenDelay > TimeSpan.Zero)
				await Task.Delay(OpenDelay, IgnoreCancellation ? CancellationToken.None : cancellationToken);
			return await inner.OpenReadAsync(blobName, IgnoreCancellation ? CancellationToken.None : cancellationToken);
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
	public List<int> RequestedBatchSizes { get; } = [];
	public Action? AfterDelete { get; set; }
	public bool ThrowOnNextDelete { get; set; }
	public bool ThrowOnNextAbandon { get; set; }
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
			RequestedBatchSizes.Add(maxMessages);
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
		if (ThrowOnNextDelete)
		{
			ThrowOnNextDelete = false;
			throw new InvalidOperationException("Expired receipt");
		}
		lock (gate) deleted.Add(Require(messageId));
		AfterDelete?.Invoke();
		return Task.CompletedTask;
	}

	public Task AbandonAsync(string messageId, string popReceipt, TimeSpan visibility, CancellationToken token)
	{
		if (ThrowOnNextAbandon)
		{
			ThrowOnNextAbandon = false;
			throw new InvalidOperationException("Expired receipt");
		}
		lock (gate) abandoned.Add((Require(messageId), visibility));
		return Task.CompletedTask;
	}

	private ExtractionMessage Require(string messageId) =>
		received.FirstOrDefault(m => m.MessageId == messageId)
			?? throw new InvalidOperationException("Delete/abandon before receive.");
}
