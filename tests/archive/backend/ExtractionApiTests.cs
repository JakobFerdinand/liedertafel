using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034 slice S1: PDF finalize writes the extraction row in the revision's
/// own save (outbox) and hands it to the queue afterwards; a send failure
/// keeps the request (row stays Queued with a null enqueue stamp); the batch
/// status query answers the auth matrix with request-order results and skips
/// unknown ids; the retry action walks the state matrix (Failed → Queued with
/// re-enqueue, missing row → create, Running → 409, terminal → idempotent
/// reply) and rejects non-PDF material.
/// </summary>
public sealed class ExtractionApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task ScorePdfFinalizeCreatesQueuedJobAndQueueRecord()
	{
		var queue = new FakeExtractionQueue();
		var sharedSaves = 0;
		var interceptor = new ExtractionSaveInterceptor((db, _) =>
		{
			// Count the writes, not unchanged entities still tracked during
			// the later enqueue-stamp save.
			if (db.ChangeTracker.Entries<FileRevision>().Any(e => e.State == EntityState.Added)
				&& db.ChangeTracker.Entries<ExtractionJob>().Any(e => e.State == EntityState.Added))
				sharedSaves++;
			return Task.CompletedTask;
		});
		await using var factory = new AuthApiFactory(queue: queue, saveChangesInterceptor: interceptor);
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");

		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);

		// Exactly one queue record carrying the new revision id.
		Assert.Equal([revisionId], queue.Attempts);
		Assert.Equal(1, sharedSaves);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var job = await db.ExtractionJobs.Include(j => j.Revision).SingleAsync();
			Assert.Equal(revisionId, job.RevisionId);
			Assert.Equal(revisionId, job.Revision.Id);
			Assert.Equal(assetId, job.AssetId);
			Assert.Equal(ExtractionStatus.Queued, job.Status);
			Assert.Null(job.Text);
			Assert.Null(job.FailureReason);
			Assert.Equal(0, job.AttemptCount);
			Assert.Null(job.CompletedAt);
			Assert.NotNull(job.LastEnqueuedAt);
			Assert.Equal(editorId, job.TriggeredByAccountId);
		}

		var (status, body, noStore) = await GetExtractionStatusAsync(client, editorSession, $"{revisionId}");
		Assert.Equal(HttpStatusCode.OK, status);
		Assert.True(noStore);
		var item = Assert.Single(body.GetProperty("results").EnumerateArray());
		Assert.Equal(revisionId, Guid.Parse(item.GetProperty("revisionId").GetString()!));
		Assert.Equal(assetId, Guid.Parse(item.GetProperty("assetId").GetString()!));
		Assert.Equal(1, item.GetProperty("revisionNumber").GetInt32());
		Assert.Equal("queued", item.GetProperty("status").GetString());
		Assert.True(item.GetProperty("text").ValueKind is JsonValueKind.Null);
		Assert.True(item.GetProperty("failureReason").ValueKind is JsonValueKind.Null);
		Assert.Equal(0, item.GetProperty("attemptCount").GetInt32());
		Assert.True(item.GetProperty("completedAt").ValueKind is JsonValueKind.Null);
		Assert.True(item.GetProperty("lastAttemptAt").ValueKind is JsonValueKind.Null);
		Assert.True(item.GetProperty("lastEnqueuedAt").ValueKind is JsonValueKind.String);
	}

	[Fact]
	public async Task FinalizeCommitFailureKeepsStagedUploadReplayable()
	{
		var failOnce = true;
		var interceptor = new ExtractionSaveInterceptor((db, _) =>
		{
			if (failOnce && db.ChangeTracker.Entries<FileRevision>().Any(e => e.State == EntityState.Added))
			{
				failOnce = false;
				throw new DbUpdateConcurrencyException("Simulated finalize conflict.");
			}
			return Task.CompletedTask;
		});
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue, saveChangesInterceptor: interceptor);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var session = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, session);
		var assetId = await CreateAssetAsync(client, session, versionId, "score");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, session, assetId);
		var stagedBlob = factory.Storage.Find(uploadUrl)!.BlobName;
		factory.Storage.Store(stagedBlob, ValidPdf(1024));

		using (var failed = await FinalizeAsync(client, session, sessionId))
			Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
		Assert.True(factory.Storage.Has(stagedBlob));
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(PendingUploadState.Pending, (await db.UploadSessions.SingleAsync()).State);
			Assert.Empty(await db.FileRevisions.ToListAsync());
			Assert.Empty(await db.ExtractionJobs.ToListAsync());
		}
		Assert.Empty(queue.Attempts);

		using var replay = await FinalizeAsync(client, session, sessionId);
		Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
		Assert.False(factory.Storage.Has(stagedBlob));
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Single(await db.ExtractionJobs.ToListAsync());
			Assert.Equal(PendingUploadState.Finalized, (await db.UploadSessions.SingleAsync()).State);
		}
	}

	[Fact]
	public async Task EnqueueStampSaveFailureDoesNotFailCommittedFinalizeOrResend()
	{
		var failOnce = true;
		var interceptor = new ExtractionSaveInterceptor((db, _) =>
		{
			if (failOnce && db.ChangeTracker.Entries<ExtractionJob>()
				.Any(e => e.State == EntityState.Modified && e.Entity.LastEnqueuedAt is not null))
			{
				failOnce = false;
				throw new DbUpdateException("Simulated stamp failure.", (Exception?)null);
			}
			return Task.CompletedTask;
		});
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue, saveChangesInterceptor: interceptor);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var session = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, session);
		var assetId = await CreateAssetAsync(client, session, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, session, assetId);
		Assert.False(failOnce);
		Assert.Single(queue.Attempts);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Single(await db.FileRevisions.ToListAsync());
			Assert.Null((await db.ExtractionJobs.SingleAsync()).LastEnqueuedAt);
		}
		var revisionId = revision.GetProperty("revisionId").GetGuid();
		using var retry = await PostJsonAsync(client, $"/api/revisions/{revisionId}/extraction/retry", new { }, session);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		Assert.Equal(2, queue.Attempts.Count);
		using (var scope = factory.Services.CreateScope())
			Assert.NotNull((await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().ExtractionJobs.SingleAsync()).LastEnqueuedAt);
	}

	[Fact]
	public async Task AudioFinalizeCreatesNoExtractionWork()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "audio");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			new byte[512], AssetEndpoints.Mp3ContentType);

		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.FileRevisions.CountAsync());
			Assert.Equal(0, await db.ExtractionJobs.CountAsync());
		}
		Assert.Empty(queue.Attempts);
	}

	[Fact]
	public async Task QueueSendFailureKeepsJobQueuedWithUnsetEnqueueStamp()
	{
		foreach (var (failingQueue, attemptsAfterFinalize) in new (FakeExtractionQueue Queue, int Attempts)[]
		{
			// A deterministic "not accepted" reply (no-op queue) is not retried;
			// an infrastructure exception gets the second quiet attempt.
			(new FakeExtractionQueue(accept: false), 1),
			(new FakeExtractionQueue(throws: true), 2),
			(new FakeExtractionQueue(failure: new IOException("Provider URL must not be logged.")), 2),
			(new FakeExtractionQueue(failure: new DbUpdateException("Provider failure.")), 2),
		})
		{
			await using var factory = new AuthApiFactory(queue: failingQueue);
			await SeedAsync(factory, Editor, ArchiveRoles.Editor);
			var editorSession = await SignInAsync(factory, Editor);
			using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
			var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
			var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
			var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
			var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);

			// The send failure never fails finalize: the row is the durable
			// work record with a null enqueue stamp for the dispatch sweep.
			var (status, body, _) = await GetExtractionStatusAsync(client, editorSession, $"{revisionId}");
			Assert.Equal(HttpStatusCode.OK, status);
			var item = Assert.Single(body.GetProperty("results").EnumerateArray());
			Assert.Equal("queued", item.GetProperty("status").GetString());
			Assert.True(item.GetProperty("lastEnqueuedAt").ValueKind is JsonValueKind.Null);
			using (var scope = factory.Services.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
				var job = await db.ExtractionJobs.SingleAsync();
				Assert.Equal(ExtractionStatus.Queued, job.Status);
				Assert.Null(job.LastEnqueuedAt);
			}

			// The bounded quiet enqueue gave up without an accepted send…
			Assert.Equal(attemptsAfterFinalize, failingQueue.Attempts.Count);
			// …and the retry action rediscovers the missing enqueue quietly.
			using var retry = await PostJsonAsync(client,
				$"/api/revisions/{revisionId}/extraction/retry", new { }, editorSession);
			Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
			var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("queued", retryBody.GetProperty("status").GetString());
			Assert.True(retryBody.GetProperty("lastEnqueuedAt").ValueKind is JsonValueKind.Null);
			Assert.Equal(attemptsAfterFinalize * 2, failingQueue.Attempts.Count);
		}
	}

	[Fact]
	public async Task StatusQueryEnforcesAuthOrderAndBounds()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetA = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetB = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, revisionA) = await UploadAndFinalizeAsync(factory, client, editorSession, assetA);
		var (_, _, revisionB) = await UploadAndFinalizeAsync(factory, client, editorSession, assetB);
		var idA = Guid.Parse(revisionA.GetProperty("revisionId").GetString()!);
		var idB = Guid.Parse(revisionB.GetProperty("revisionId").GetString()!);

		using (var anonymous = await GetExtractionStatusResponseAsync(client, null, $"{idA}"))
		{
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
			var problem = await anonymous.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("Anmeldung erforderlich.", problem.GetProperty("title").GetString());
		}
		using (var member = await GetExtractionStatusResponseAsync(client, memberSession, $"{idA}"))
		{
			Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
			var problem = await member.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("Keine Berechtigung für das Liedverzeichnis.", problem.GetProperty("title").GetString());
		}

		// Editor sees results in request order.
		var (_, ordered, _) = await GetExtractionStatusAsync(client, editorSession, $"{idB},{idA}");
		var orderedResults = ordered.GetProperty("results");
		Assert.Equal(2, orderedResults.GetArrayLength());
		Assert.Equal(idB, Guid.Parse(orderedResults[0].GetProperty("revisionId").GetString()!));
		Assert.Equal(idA, Guid.Parse(orderedResults[1].GetProperty("revisionId").GetString()!));

		// Unknown ids are skipped and duplicates collapse, order preserved.
		var (_, filtered, _) = await GetExtractionStatusAsync(client, editorSession,
			$"{idA},{Guid.CreateVersion7()},{idA},{idB}");
		var filteredResults = filtered.GetProperty("results");
		Assert.Equal(2, filteredResults.GetArrayLength());
		Assert.Equal(idA, Guid.Parse(filteredResults[0].GetProperty("revisionId").GetString()!));
		Assert.Equal(idB, Guid.Parse(filteredResults[1].GetProperty("revisionId").GetString()!));

		// More than 100 ids are rejected before any row is loaded.
		var tooMany = string.Join(",", Enumerable.Range(0, 101).Select(_ => Guid.CreateVersion7()));
		var (overflow, overflowBody, _) = await GetExtractionStatusAsync(client, editorSession, tooMany);
		Assert.Equal(HttpStatusCode.BadRequest, overflow);
		Assert.Equal(ExtractionEndpoints.TooManyIdsMessage, overflowBody.GetProperty("title").GetString());
	}

	[Fact]
	public async Task RetryResetsFailedJobAndReEnqueues()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);
		uint rowVersionBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var job = await db.ExtractionJobs.SingleAsync();
			job.Status = ExtractionStatus.Failed;
			job.FailureReason = "Die Auswertung ist fehlgeschlagen.";
			job.AttemptCount = 2;
			job.LastAttemptAt = job.CreatedAt;
			await db.SaveChangesAsync();
			rowVersionBefore = job.RowVersion;
		}

		queue.Accept = false;
		using var retry = await PostJsonAsync(client,
			$"/api/revisions/{revisionId}/extraction/retry", new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("queued", body.GetProperty("status").GetString());
		Assert.True(body.GetProperty("failureReason").ValueKind is JsonValueKind.Null);
		Assert.Equal(0, body.GetProperty("attemptCount").GetInt32());
		Assert.True(body.GetProperty("lastAttemptAt").ValueKind is JsonValueKind.Null);
		Assert.True(body.GetProperty("lastEnqueuedAt").ValueKind is JsonValueKind.Null);
		Assert.True((uint)body.GetProperty("rowVersion").GetInt64() > rowVersionBefore);
		Assert.Equal(2, queue.Attempts.Count);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var job = await db.ExtractionJobs.SingleAsync();
			Assert.Equal(ExtractionStatus.Queued, job.Status);
			Assert.Null(job.FailureReason);
			Assert.Null(job.LastAttemptAt);
			Assert.Equal(0, job.AttemptCount);
			Assert.Null(job.LastEnqueuedAt);
		}
		queue.Accept = true;
		using var rediscovered = await PostJsonAsync(client,
			$"/api/revisions/{revisionId}/extraction/retry", new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, rediscovered.StatusCode);
		var rediscoveredBody = await rediscovered.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(0, rediscoveredBody.GetProperty("attemptCount").GetInt32());
		Assert.True(rediscoveredBody.GetProperty("lastEnqueuedAt").ValueKind is JsonValueKind.String);
		Assert.Equal(3, queue.Attempts.Count);
		using (var scope = factory.Services.CreateScope())
			Assert.NotNull((await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().ExtractionJobs.SingleAsync()).LastEnqueuedAt);
	}

	[Fact]
	public async Task RetryCreatesMissingRowAndEnqueues()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);

		// A row deleted from the store stands in for revisions finalized
		// before ARC-034 existed.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.ExtractionJobs.Remove(await db.ExtractionJobs.SingleAsync());
			await db.SaveChangesAsync();
		}

		using var retry = await PostJsonAsync(client,
			$"/api/revisions/{revisionId}/extraction/retry", new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("queued", body.GetProperty("status").GetString());
		Assert.Equal(1, body.GetProperty("revisionNumber").GetInt32());
		Assert.True(body.GetProperty("lastEnqueuedAt").ValueKind is JsonValueKind.String);
		Assert.Equal(2, queue.Attempts.Count);
	}

	[Fact]
	public async Task RetryOnRunningJobConflicts()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var job = await db.ExtractionJobs.SingleAsync();
			job.Status = ExtractionStatus.Running;
			await db.SaveChangesAsync();
		}

		using var retry = await PostJsonAsync(client,
			$"/api/revisions/{revisionId}/extraction/retry", new { }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
		var problem = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ExtractionEndpoints.ExtractionRunningMessage, problem.GetProperty("title").GetString());
		Assert.Single(queue.Attempts);
	}

	[Theory]
	[InlineData(ExtractionStatus.Completed, HttpStatusCode.OK, "completed")]
	[InlineData(ExtractionStatus.NoText, HttpStatusCode.OK, "noText")]
	[InlineData(ExtractionStatus.Running, HttpStatusCode.Conflict, null)]
	[InlineData(ExtractionStatus.Queued, HttpStatusCode.OK, "queued")]
	[InlineData(ExtractionStatus.Failed, HttpStatusCode.OK, "queued")]
	public async Task RetryInsertRaceReloadsWinnerAndReappliesStateMatrix(
		ExtractionStatus winnerStatus, HttpStatusCode expected, string? expectedStatus)
	{
		AuthApiFactory? factoryReference = null;
		var raceNextInsert = false;
		var conflicts = 0;
		var interceptor = new ExtractionSaveInterceptor(async (db, token) =>
		{
			var inserted = db.ChangeTracker.Entries<ExtractionJob>()
				.SingleOrDefault(e => e.State == EntityState.Added);
			if (!raceNextInsert || inserted is null)
				return;
			raceNextInsert = false;
			conflicts++;
			// InMemory does not enforce SQL unique constraints. Persist the
			// other request's winner in a separate context, then throw the
			// same DbUpdateException a relational insert loser receives.
			using var scope = factoryReference!.Services.CreateScope();
			var winnerDb = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			winnerDb.ExtractionJobs.Add(new ExtractionJob
			{
				RevisionId = inserted.Entity.RevisionId,
				AssetId = inserted.Entity.AssetId,
				Status = winnerStatus,
				Text = winnerStatus == ExtractionStatus.Completed ? "Gespeicherter Text" : null,
				AttemptCount = 2,
				LastEnqueuedAt = winnerStatus == ExtractionStatus.Queued ? null : inserted.Entity.CreatedAt,
				CreatedAt = inserted.Entity.CreatedAt,
				UpdatedAt = inserted.Entity.UpdatedAt,
				TriggeredByAccountId = inserted.Entity.TriggeredByAccountId,
				RowVersion = 7,
			});
			await winnerDb.SaveChangesAsync(token);
			throw new DbUpdateException("simulated unique violation", (Exception?)null);
		});
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue, saveChangesInterceptor: interceptor);
		factoryReference = factory;
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var session = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, session);
		var assetId = await CreateAssetAsync(client, session, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, session, assetId);
		var revisionId = revision.GetProperty("revisionId").GetGuid();
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.ExtractionJobs.Remove(await db.ExtractionJobs.SingleAsync());
			await db.SaveChangesAsync();
		}
		raceNextInsert = true;
		using var retry = await PostJsonAsync(client, $"/api/revisions/{revisionId}/extraction/retry", new { }, session);
		Assert.Equal(expected, retry.StatusCode);
		Assert.Equal(1, conflicts);
		var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
		var enqueue = winnerStatus is ExtractionStatus.Queued or ExtractionStatus.Failed;
		Assert.Equal(enqueue ? 2 : 1, queue.Attempts.Count);
		if (expectedStatus is not null)
			Assert.Equal(expectedStatus, body.GetProperty("status").GetString());
		else
			Assert.Equal(ExtractionEndpoints.ExtractionRunningMessage, body.GetProperty("title").GetString());
		using (var scope = factory.Services.CreateScope())
		{
			var job = await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().ExtractionJobs.SingleAsync();
			Assert.Equal(winnerStatus == ExtractionStatus.Failed ? ExtractionStatus.Queued : winnerStatus, job.Status);
			Assert.Equal(winnerStatus == ExtractionStatus.Failed ? 0 : 2, job.AttemptCount);
			Assert.Equal(winnerStatus == ExtractionStatus.Failed ? 8u : 7u, job.RowVersion);
			Assert.Equal(winnerStatus == ExtractionStatus.Completed ? "Gespeicherter Text" : null, job.Text);
			if (enqueue)
				Assert.NotNull(job.LastEnqueuedAt);
		}
	}

	[Fact]
	public async Task RetryInsertFailureIsBoundedToTwoPasses()
	{
		var failInserts = false;
		var failedSaves = 0;
		var interceptor = new ExtractionSaveInterceptor((db, _) =>
		{
			if (failInserts && db.ChangeTracker.Entries<ExtractionJob>().Any(e => e.State == EntityState.Added))
			{
				failedSaves++;
				throw new DbUpdateException("Simulated insert failure.", (Exception?)null);
			}
			return Task.CompletedTask;
		});
		await using var factory = new AuthApiFactory(saveChangesInterceptor: interceptor);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var session = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, session);
		var assetId = await CreateAssetAsync(client, session, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, session, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.ExtractionJobs.Remove(await db.ExtractionJobs.SingleAsync());
			await db.SaveChangesAsync();
		}
		failInserts = true;
		using var retry = await PostJsonAsync(client,
			$"/api/revisions/{revision.GetProperty("revisionId").GetGuid()}/extraction/retry", new { }, session);
		Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
		Assert.Equal(2, failedSaves);
		var problem = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.ConcurrencyMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task RetryEnforcesAuthorizationAndNoStoreOnAntiforgeryRejection()
	{
		const string administrator = "administration@liedertafel.test";
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, administrator, ArchiveRoles.Administrator);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var administratorSession = await SignInAsync(factory, administrator);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var path = $"/api/revisions/{revision.GetProperty("revisionId").GetGuid()}/extraction/retry";

		var (cookie, token) = await GetCsrfAsync(client);
		using (var request = AuthedPost(path, new { }, cookie, token))
		using (var anonymous = await client.SendAsync(request))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		using (var member = await PostJsonAsync(client, path, new { }, memberSession))
			Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
		using (var admin = await PostJsonAsync(client, path, new { }, administratorSession))
			Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			Assert.False(await users.IsInRoleAsync((await users.FindByEmailAsync(administrator))!, ArchiveRoles.Editor));
		}

		foreach (var invalidToken in new string?[] { null, "invalid" })
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, path);
			request.Headers.Add("Cookie", editorSession);
			if (invalidToken is not null)
				request.Headers.Add("X-CSRF-TOKEN", invalidToken);
			request.Content = JsonContent.Create(new { });
			using var rejection = await client.SendAsync(request);
			Assert.Equal(HttpStatusCode.BadRequest, rejection.StatusCode);
			Assert.True(rejection.Headers.CacheControl?.NoStore);
		}
	}

	[Fact]
	public async Task QuietEnqueuePropagatesCancellation()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var queue = new FakeExtractionQueue(failure: new OperationCanceledException());
		await Assert.ThrowsAsync<OperationCanceledException>(() => ExtractionService.TryEnqueueAsync(
			db, queue, NullLogger.Instance, Guid.CreateVersion7(), TimeProvider.System, CancellationToken.None));
		Assert.Single(queue.Attempts);
	}

	[Fact]
	public async Task RetryOnTerminalStatesIsIdempotentWithoutNewWork()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetA = await CreateAssetAsync(client, editorSession, versionId, "score");
		var assetB = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (_, _, revisionA) = await UploadAndFinalizeAsync(factory, client, editorSession, assetA);
		var (_, _, revisionB) = await UploadAndFinalizeAsync(factory, client, editorSession, assetB);
		var idA = Guid.Parse(revisionA.GetProperty("revisionId").GetString()!);
		var idB = Guid.Parse(revisionB.GetProperty("revisionId").GetString()!);
		Assert.Equal(2, queue.Attempts.Count);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var jobs = await db.ExtractionJobs.ToListAsync();
			var jobA = jobs.Single(j => j.RevisionId == idA);
			jobA.Status = ExtractionStatus.Completed;
			jobA.Text = "Erster Vers \nNotentext…";
			jobA.CompletedAt = jobA.CreatedAt;
			var jobB = jobs.Single(j => j.RevisionId == idB);
			jobB.Status = ExtractionStatus.NoText;
			await db.SaveChangesAsync();
		}

		using (var retryA = await PostJsonAsync(client,
			$"/api/revisions/{idA}/extraction/retry", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, retryA.StatusCode);
			var body = await retryA.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("completed", body.GetProperty("status").GetString());
			Assert.Equal("Erster Vers \nNotentext…", body.GetProperty("text").GetString());
			Assert.Equal(0, body.GetProperty("attemptCount").GetInt32());
		}
		using (var retryB = await PostJsonAsync(client,
			$"/api/revisions/{idB}/extraction/retry", new { }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, retryB.StatusCode);
			var body = await retryB.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("noText", body.GetProperty("status").GetString());
			Assert.True(body.GetProperty("text").ValueKind is JsonValueKind.Null);
		}

		// Terminal replies create no new work and no queue send.
		Assert.Equal(2, queue.Attempts.Count);

		// The status query reports both stored results by revision.
		var (_, bodyStatus, _) = await GetExtractionStatusAsync(client, editorSession, $"{idA},{idB}");
		var results = bodyStatus.GetProperty("results");
		Assert.Equal(2, results.GetArrayLength());
		Assert.Equal("completed", results[0].GetProperty("status").GetString());
		Assert.Equal("Erster Vers \nNotentext…", results[0].GetProperty("text").GetString());
		Assert.Equal("noText", results[1].GetProperty("status").GetString());
	}

	[Fact]
	public async Task RetryOnNonPdfRevisionIsUnsupported()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "audio");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			new byte[512], AssetEndpoints.Mp3ContentType);
		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);

		using var retry = await PostJsonAsync(client,
			$"/api/revisions/{revisionId}/extraction/retry", new { }, editorSession);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
		var problem = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ExtractionEndpoints.ExtractionNotSupportedMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(0, await db.ExtractionJobs.CountAsync());
		}
		Assert.Empty(queue.Attempts);
	}

	[Fact]
	public async Task RetryOnUnknownRevisionIsNotFound()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var retry = await PostJsonAsync(client,
			$"/api/revisions/{Guid.CreateVersion7()}/extraction/retry", new { }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
		var problem = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ExtractionEndpoints.RevisionNotFoundMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task IdempotentFinalizeReentryDoesNotDuplicateExtractionWork()
	{
		var queue = new FakeExtractionQueue();
		await using var factory = new AuthApiFactory(queue: queue);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, _, revision) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);

		using var retry = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(revisionId, Guid.Parse(retryBody.GetProperty("revisionId").GetString()!));

		// One row, one queue record: the re-entry branch subscribes nothing.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.ExtractionJobs.CountAsync());
		}
		Assert.Equal([revisionId], queue.Attempts);

		var (status, body, _) = await GetExtractionStatusAsync(client, editorSession, $"{revisionId}");
		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Single(body.GetProperty("results").EnumerateArray());
	}

	[Fact]
	public void ExtractionEnvelopeSerializesWithCamelCaseKeys()
	{
		var revisionId = Guid.Parse("0198e7f2-9a31-7d5e-bc31-8f0c1a2b3d4e");
		var json = JsonSerializer.Serialize(
			new ExtractionEnvelope(ExtractionEnvelope.ExtractionType, revisionId, "00-trace-parent-01", "trace=state"),
			ExtractionEnvelope.SerializerOptions);
		var element = JsonDocument.Parse(json).RootElement;
		Assert.Equal("extraction", element.GetProperty("type").GetString());
		Assert.Equal(revisionId, Guid.Parse(element.GetProperty("revisionId").GetString()!));
		Assert.Equal("00-trace-parent-01", element.GetProperty("traceParent").GetString());
		Assert.Equal("trace=state", element.GetProperty("traceState").GetString());
		var roundTrip = JsonSerializer.Deserialize<ExtractionEnvelope>(json, ExtractionEnvelope.SerializerOptions)!;
		Assert.Equal(ExtractionEnvelope.ExtractionType, roundTrip.Type);
		Assert.Equal(revisionId, roundTrip.RevisionId);
	}

	private static async Task<(HttpStatusCode Status, JsonElement Body, bool NoStore)> GetExtractionStatusAsync(
		HttpClient client, string? session, string ids)
	{
		using var response = await GetExtractionStatusResponseAsync(client, session, ids);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (response.StatusCode, body, response.Headers.CacheControl?.NoStore == true);
	}

	private static async Task<HttpResponseMessage> GetExtractionStatusResponseAsync(
		HttpClient client, string? session, string ids)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, $"/api/revisions/extraction?ids={ids}");
		if (session is not null)
			request.Headers.Add("Cookie", session);
		return await client.SendAsync(request);
	}

	private static async Task<(Guid SongId, Guid VersionId)> CreateSongWithVersionAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, string title = "Notenlied")
	{
		var songId = await CreateSongAsync(factory, client, editorSession, title);
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		return (songId, versionId);
	}

	private static async Task<Guid> CreateAssetAsync(
		HttpClient client, string editorSession, Guid versionId, string assetType)
	{
		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("id").GetString()!);
	}

	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> CreateUploadSessionAsync(
		HttpClient client, string editorSession, Guid assetId)
	{
		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", new { }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (
			Guid.Parse(body.GetProperty("uploadSessionId").GetString()!),
			body.GetProperty("uploadUrl").GetString()!,
			body);
	}

	/// <summary>Creates the session, stores a valid PDF and finalizes.</summary>
	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Revision)> UploadAndFinalizeAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, Guid assetId, long size = 1024)
	{
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(size));
		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		return (sessionId, uploadUrl, revision);
	}

	private static async Task<HttpResponseMessage> FinalizeAsync(
		HttpClient client, string session, Guid sessionId)
		=> await PostJsonAsync(client, $"/api/upload-sessions/{sessionId}/finalize", new { }, session);

	private static byte[] ValidPdf(long size)
	{
		var bytes = new byte[size];
		"%PDF-1.7\n"u8.CopyTo(bytes);
		return bytes;
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
	}

	private static async Task<Guid> CreateSongAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, string title)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);
	}

	private static async Task<Guid> SeedAsync(AuthApiFactory factory, string email, string role)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roles.RoleExistsAsync(role))
			Assert.True((await roles.CreateAsync(new ArchiveRole(role))).Succeeded);
		var user = await users.FindByEmailAsync(email);
		if (user is null)
		{
			user = new ArchiveUser { UserName = email, Email = email, DisplayName = "Test", EmailConfirmed = true };
			Assert.True((await users.CreateAsync(user)).Succeeded);
		}
		if (!await users.IsInRoleAsync(user, role))
			Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
		return user.Id;
	}

	private static async Task<string> SignInAsync(AuthApiFactory factory, string email)
	{
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		return Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
	}

	private static async Task<(string Cookie, string Token)> GetCsrfAsync(HttpClient client, string? sessionCookie = null)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		if (sessionCookie is not null)
			tokenRequest.Headers.Add("Cookie", sessionCookie);
		using var response = await client.SendAsync(tokenRequest);
		response.EnsureSuccessStatusCode();
		var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (cookie, body.GetProperty("token").GetString()!);
	}

	private static HttpRequestMessage AuthedPost(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private static async Task<JsonElement> GetSongDetailAsync(HttpClient client, string session, Guid songId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("song");
	}
}

/// <summary>
/// In-memory queue seam (ARC-034): records attempted sends so tests can
/// assert the outbox enqueue behavior; <c>accept: false</c> reports the
/// no-op path (message not accepted) and <c>throws: true</c> simulates an
/// infrastructure outage.
/// </summary>
internal sealed class FakeExtractionQueue(bool accept = true, bool throws = false, Exception? failure = null) : IExtractionQueue
{
	private readonly object gate = new();
	private readonly List<Guid> attempts = [];
	public bool Accept { get; set; } = accept;

	/// <summary>Every SendAsync call in order, accepted or not.</summary>
	public IReadOnlyList<Guid> Attempts
	{
		get { lock (gate) return [.. attempts]; }
	}

	public Task<bool> SendAsync(Guid revisionId, CancellationToken token)
	{
		lock (gate) attempts.Add(revisionId);
		if (throws)
			throw new InvalidOperationException("Fake queue outage.");
		if (failure is not null)
			throw failure;
		return Task.FromResult(Accept);
	}
}

internal sealed class ExtractionSaveInterceptor(Func<DbContext, CancellationToken, Task> onSaving) : SaveChangesInterceptor
{
	public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
		DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
	{
		await onSaving(eventData.Context!, cancellationToken);
		return result;
	}
}
