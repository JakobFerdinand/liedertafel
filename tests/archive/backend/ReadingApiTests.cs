using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Provenance;
using Archive.Backend.Reading;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034-1 editor surface: the reading state rides on the extraction batch
/// query, "erneut versuchen" and "Rest lesen" are editor-only mutations with
/// antiforgery, members and anonymous callers are refused, and the state
/// machine answers honestly (idempotent where nothing new starts, 409 for a
/// running or finished-and-complete reading, 422 for anything but a score).
/// The asset's voice label joins the provenance: a human edit locks it and the
/// editor's song detail lists it for the "KI" badge.
/// </summary>
public sealed class ReadingApiTests
{
	private const string Editor = "redaktion@liedertafel.test";
	private const string Member = "mitglied@liedertafel.test";

	[Fact]
	public async Task TheBatchStatusCarriesTheReadingStateAndTheTextSource()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, score) = await SetupAsync(factory, reading: row =>
		{
			row.Status = ReadingStatus.Completed;
			row.PagesTotal = 35;
			row.PagesRead = 20;
			row.PageLimit = 20;
			row.FieldsApplied = 4;
			row.FieldsProposed = 1;
		}, extractionSource: ExtractionTextSource.Reading);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/revisions/extraction?ids={score}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());

		Assert.Equal("reading", item.GetProperty("textSource").GetString());
		Assert.True(item.GetProperty("readingEnabled").GetBoolean());
		var reading = item.GetProperty("reading");
		Assert.Equal("completed", reading.GetProperty("status").GetString());
		Assert.Equal((35, 20, true, 4, 1), (reading.GetProperty("pagesTotal").GetInt32(), reading.GetProperty("pagesRead").GetInt32(),
			reading.GetProperty("partial").GetBoolean(), reading.GetProperty("fieldsApplied").GetInt32(), reading.GetProperty("fieldsProposed").GetInt32()));
		Assert.True(reading.GetProperty("failureReason").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task ARevisionWithoutAReadingRowReportsNullReading()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, score) = await SetupAsync(factory, reading: null);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/revisions/extraction?ids={score}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());

		Assert.True(item.GetProperty("reading").ValueKind is JsonValueKind.Null);
		Assert.Equal("embedded", item.GetProperty("textSource").GetString());
	}

	[Theory]
	[InlineData("retry")]
	[InlineData("continue")]
	public async Task MembersAnonymousAndMissingTokensAreRefused(string action)
	{
		await using var factory = new AuthApiFactory();
		var (client, editorSession, score) = await SetupAsync(factory, reading: row => row.Status = ReadingStatus.Failed);
		await SeedAccountAsync(factory, Member, ArchiveRoles.Member);
		var memberSession = await SignInAsync(factory, Member);

		using (var anonymous = await PostAsync(client, $"/api/revisions/{score}/reading/{action}", null))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		using (var member = await PostAsync(client, $"/api/revisions/{score}/reading/{action}", memberSession))
			Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
		// An editor without the antiforgery token is refused before anything changes.
		using (var noToken = new HttpRequestMessage(HttpMethod.Post, $"/api/revisions/{score}/reading/{action}"))
		{
			noToken.Headers.Add("Cookie", editorSession);
			noToken.Content = JsonContent.Create(new { });
			using var response = await client.SendAsync(noToken);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		}
		Assert.Equal(ReadingStatus.Failed, (await ReadingRowAsync(factory, score)).Status);
	}

	[Fact]
	public async Task RetryRequeuesAFailedReadingWithFreshAttemptsAndSendsItToTheQueue()
	{
		var queue = new FakeReadingQueue();
		await using var factory = new AuthApiFactory(readingQueue: queue);
		var (client, session, score) = await SetupAsync(factory, reading: row =>
		{
			row.Status = ReadingStatus.Failed;
			row.FailureReason = ReadingWorker.ExhaustedReason;
			row.AttemptCount = 4;
			row.PagesRead = 10;
			row.PagesTotal = 30;
			row.PageLimit = 20;
		});

		using var response = await PostAsync(client, $"/api/revisions/{score}/reading/retry", session);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var reading = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reading");
		Assert.Equal("queued", reading.GetProperty("status").GetString());
		var row = await ReadingRowAsync(factory, score);
		Assert.Equal((ReadingStatus.Queued, 0, null, 20, 10), (row.Status, row.AttemptCount, row.FailureReason, row.PageLimit, row.PagesRead));
		Assert.Equal([score], queue.Attempts);
		Assert.NotNull(row.LastEnqueuedAt);
	}

	[Fact]
	public async Task RetryCreatesTheRowForARevisionExtractedBeforeTheReadingExisted()
	{
		var queue = new FakeReadingQueue();
		await using var factory = new AuthApiFactory(readingQueue: queue);
		var (client, session, score) = await SetupAsync(factory, reading: null, extractionNoText: true);

		using var response = await PostAsync(client, $"/api/revisions/{score}/reading/retry", session);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var row = await ReadingRowAsync(factory, score);
		Assert.Equal((ReadingStatus.Queued, 20), (row.Status, row.PageLimit));
		Assert.Equal([score], queue.Attempts);
	}

	[Fact]
	public async Task ContinueAsksForTheNextPagesOfAPartialReading()
	{
		var queue = new FakeReadingQueue();
		await using var factory = new AuthApiFactory(readingQueue: queue);
		var (client, session, score) = await SetupAsync(factory, reading: row =>
		{
			row.Status = ReadingStatus.Completed;
			row.PagesRead = 20;
			row.PagesTotal = 35;
			row.PageLimit = 20;
			row.FieldsDone = true;
			row.AttemptCount = 1;
		});

		using var response = await PostAsync(client, $"/api/revisions/{score}/reading/continue", session);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var row = await ReadingRowAsync(factory, score);
		Assert.Equal((ReadingStatus.Queued, 40, false, 0), (row.Status, row.PageLimit, row.FieldsDone, row.AttemptCount));
		Assert.Equal([score], queue.Attempts);
	}

	[Fact]
	public async Task ContinueRefusesWhenEverythingWasRead()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, score) = await SetupAsync(factory, reading: row =>
		{
			row.Status = ReadingStatus.Completed;
			row.PagesRead = 12;
			row.PagesTotal = 12;
		});

		using var response = await PostAsync(client, $"/api/revisions/{score}/reading/continue", session);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(ReadingEndpoints.NothingMoreToReadMessage, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		Assert.Equal(ReadingStatus.Completed, (await ReadingRowAsync(factory, score)).Status);
	}

	[Fact]
	public async Task ARunningReadingAnswers409AndAWaitingOneIsLeftAlone()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, running) = await SetupAsync(factory, reading: row =>
		{
			row.Status = ReadingStatus.Running;
			row.LastAttemptAt = DateTimeOffset.UtcNow;
		});

		using var conflict = await PostAsync(client, $"/api/revisions/{running}/reading/retry", session);
		Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
		Assert.Equal(ReadingEndpoints.ReadingRunningMessage, (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

		await using var other = new AuthApiFactory(readingQueue: new FakeReadingQueue());
		var (otherClient, otherSession, waiting) = await SetupAsync(other, reading: row => row.Status = ReadingStatus.WaitingForBudget);
		using var idempotent = await PostAsync(otherClient, $"/api/revisions/{waiting}/reading/retry", otherSession);
		Assert.Equal(HttpStatusCode.OK, idempotent.StatusCode);
		Assert.Equal("waitingForBudget",
			(await idempotent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reading").GetProperty("status").GetString());
		Assert.Equal(ReadingStatus.WaitingForBudget, (await ReadingRowAsync(other, waiting)).Status);
	}

	[Fact]
	public async Task OnlyFinishedExtractionsOfScoresCanBeRead()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, unfinished) = await SetupAsync(factory, reading: null, extractionStatus: ExtractionStatus.Running);
		using (var response = await PostAsync(client, $"/api/revisions/{unfinished}/reading/retry", session))
		{
			Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
			Assert.Equal(ReadingEndpoints.ExtractionNotFinishedMessage, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		}
		var (_, _, document) = await SetupAsync(factory, reading: null, assetType: AssetEndpoints.DocumentAssetType);
		using (var response = await PostAsync(client, $"/api/revisions/{document}/reading/retry", session))
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		using (var response = await PostAsync(client, $"/api/revisions/{Guid.NewGuid()}/reading/retry", session))
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task AHumanEditOfTheVoiceLabelLocksItAndAnAiWrittenOneShowsInTheEditorsProvenance()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, _) = await SetupAsync(factory, reading: null);
		Guid assetId, songId;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.Include(a => a.MusicalVersion!).ThenInclude(v => v.Arrangement).SingleAsync();
			assetId = asset.Id;
			songId = asset.MusicalVersion!.Arrangement.SongId;
			// The reading job wrote the voice label earlier.
			asset.VoiceLabel = "Tenor 1";
			db.FieldProvenance.Add(new FieldProvenance
			{
				EntityType = "asset", EntityId = assetId, Field = "voice_label",
				Source = ProvenanceSource.Ai, Confidence = ProvenanceConfidence.Sicher,
				Model = "gpt-6-luna", PromptVersion = ScanPrompts.FieldsVersion, ChangedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
		}

		// The editor's song detail lists the asset field's provenance (for the "KI" badge).
		using (var detail = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}"))
		{
			detail.Headers.Add("Cookie", session);
			using var response = await client.SendAsync(detail);
			var entry = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song").GetProperty("provenance")
				.EnumerateArray().Single(e => e.GetProperty("entityType").GetString() == "asset");
			Assert.Equal((assetId, "voice_label", "Ai"),
				(entry.GetProperty("entityId").GetGuid(), entry.GetProperty("field").GetString(), entry.GetProperty("source").GetString()));
		}

		// A human edit of the label is a human write: it locks the field.
		using (var patch = await SendAsync(client, session, HttpMethod.Patch, $"/api/assets/{assetId}", new { voiceLabel = "Bass II" }))
			Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
		using var check = factory.Services.CreateScope();
		var verify = check.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal("Bass II", (await verify.Assets.AsNoTracking().SingleAsync()).VoiceLabel);
		var row = await verify.FieldProvenance.AsNoTracking().SingleAsync(p => p.EntityType == "asset" && p.Field == "voice_label");
		Assert.Equal((ProvenanceSource.Human, true, "Tenor 1"), (row.Source, row.Locked, row.PreviousValue));
	}

	[Fact]
	public async Task WhileReadingIsNotEnabledNothingOfItIsShownAndTheActionsAreRefused()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?> { ["Archive:Reading:Enabled"] = "false" });
		// A row that exists anyway (created while it was on) stays hidden.
		var (client, session, score) = await SetupAsync(factory, reading: row => row.Status = ReadingStatus.Queued, extractionNoText: true);

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/revisions/extraction?ids={score}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());
		Assert.False(item.GetProperty("readingEnabled").GetBoolean());
		Assert.True(item.GetProperty("reading").ValueKind is JsonValueKind.Null);

		foreach (var action in new[] { "retry", "continue" })
		{
			using var refused = await PostAsync(client, $"/api/revisions/{score}/reading/{action}", session);
			Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
			Assert.Equal("Das Lesen durch die KI ist noch nicht eingerichtet.",
				(await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		}
		Assert.Equal(ReadingStatus.Queued, (await ReadingRowAsync(factory, score)).Status);
	}

	[Fact]
	public async Task WhenTheStoredTextIsFullRestLesenIsNotOfferedAndRefused()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?> { ["Archive:Extraction:MaxTextCharacters"] = "20" });
		var (client, session, score) = await SetupAsync(factory, reading: row =>
		{
			row.Status = ReadingStatus.Completed;
			row.PagesRead = 20;
			row.PagesTotal = 35;
			row.PageLimit = 20;
		}, extractionText: new string('x', 20));

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/revisions/extraction?ids={score}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());
		Assert.False(item.GetProperty("reading").GetProperty("partial").GetBoolean());

		using var refused = await PostAsync(client, $"/api/revisions/{score}/reading/continue", session);
		Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
		Assert.Equal(ReadingStatus.Completed, (await ReadingRowAsync(factory, score)).Status);
	}

	[Theory]
	[InlineData("retry")]
	[InlineData("continue")]
	public async Task ARevokedEditorIsUnauthorizedAndADowngradedOneForbidden(string action)
	{
		await using var factory = new AuthApiFactory();
		var (client, _, score) = await SetupAsync(factory, reading: row => row.Status = ReadingStatus.Failed);
		const string second = "zweite@liedertafel.test";
		await SeedAccountAsync(factory, second, ArchiveRoles.Editor);
		await SeedAccountAsync(factory, "mitglied-vorab@liedertafel.test", ArchiveRoles.Member); // makes the role exist
		var session = await SignInAsync(factory, second);
		var (csrfCookie, token) = await TokenAsync(client, session);

		using (var scope = factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = (await users.FindByEmailAsync(second))!;
			Assert.True((await users.RemoveFromRoleAsync(user, ArchiveRoles.Editor)).Succeeded);
			Assert.True((await users.AddToRoleAsync(user, ArchiveRoles.Member)).Succeeded);
		}
		using (var downgraded = await SendWithAsync(client, session, csrfCookie, token, HttpMethod.Post, $"/api/revisions/{score}/reading/{action}", new { }))
			Assert.Equal(HttpStatusCode.Forbidden, downgraded.StatusCode);

		using (var scope = factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = (await users.FindByEmailAsync(second))!;
			// Locked out: still a valid cookie, but no longer an active account.
			Assert.True((await users.SetLockoutEnabledAsync(user, true)).Succeeded);
			Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddDays(1))).Succeeded);
		}
		// A revoked cookie is no identity any more: the token is the anonymous one.
		var (anonymousCookie, anonymousToken) = await TokenAsync(client, null);
		using (var revoked = await SendWithAsync(client, session, anonymousCookie, anonymousToken, HttpMethod.Post, $"/api/revisions/{score}/reading/{action}", new { }))
			Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
		Assert.Equal(ReadingStatus.Failed, (await ReadingRowAsync(factory, score)).Status);
	}

	[Fact]
	public async Task ARaceOnTheNewRowAnswersFromWhatTheWinnerStoredAndAnyOtherSaveFailureSurfaces()
	{
		var queue = new FakeReadingQueue();
		AuthApiFactory? factory = null;
		var raced = new ReadingRaceInterceptor(async () =>
		{
			// The competing request wins: its row (still to be sent) is stored first.
			using var scope = factory!.Services.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var revision = await db.FileRevisions.SingleAsync();
			db.ReadingJobs.Add(ReadingService.CreateForRevision(revision, revision.CreatedByAccountId, 20, DateTimeOffset.UtcNow));
			await db.SaveChangesAsync();
		}, constraint: "PK_reading_jobs");
		factory = new AuthApiFactory(readingQueue: queue, saveChangesInterceptor: raced);
		await using var _ = factory!;
		var (client, session, score) = await SetupAsync(factory, reading: null, extractionNoText: true);

		using var response = await PostAsync(client, $"/api/revisions/{score}/reading/retry", session);

		Assert.True(raced.Fired);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("queued", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reading").GetProperty("status").GetString());
		Assert.Equal([score], queue.Attempts);

		// Any other constraint is not "a concurrent insert": it must not be dressed up as one.
		var other = new ReadingRaceInterceptor(() => Task.CompletedTask, constraint: "FK_reading_jobs_file_revisions_RevisionId");
		await using var second = new AuthApiFactory(readingQueue: new FakeReadingQueue(), saveChangesInterceptor: other);
		var (otherClient, otherSession, otherScore) = await SetupAsync(second, reading: null, extractionNoText: true);
		HttpStatusCode? status = null;
		try
		{
			using var failed = await PostAsync(otherClient, $"/api/revisions/{otherScore}/reading/retry", otherSession);
			status = failed.StatusCode;
		}
		catch (DbUpdateException)
		{
			// The test server rethrows an unhandled exception to the caller.
		}
		Assert.True(other.Fired);
		Assert.True(status is null or HttpStatusCode.InternalServerError, $"Status was {status}.");
	}

	private sealed class ReadingRaceInterceptor(Func<Task> beforeThrow, string constraint)
		: Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
	{
		public bool Fired { get; private set; }

		public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
			Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
			Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			if (!Fired && eventData.Context!.ChangeTracker.Entries<ReadingJob>().Any(e => e.State == EntityState.Added))
			{
				Fired = true;
				await beforeThrow();
				throw new DbUpdateException("Test: duplicate key.", new Npgsql.PostgresException(
					"duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation, constraintName: constraint));
			}
			return await base.SavingChangesAsync(eventData, result, cancellationToken);
		}
	}

	[Fact]
	public async Task OnlyTheCreatingEditorMayRevertOrAcceptChangesOfAMaterialsFields()
	{
		await using var factory = new AuthApiFactory();
		var (client, ownerSession, _) = await SetupAsync(factory, reading: null);
		const string other = "andere@liedertafel.test";
		await SeedAccountAsync(factory, other, ArchiveRoles.Editor);
		var otherSession = await SignInAsync(factory, other);
		Guid assetId;
		Guid proposalId;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.SingleAsync();
			assetId = asset.Id;
			asset.VoiceLabel = "Tenor 1";
			db.FieldProvenance.Add(new FieldProvenance
			{
				EntityType = "asset", EntityId = asset.Id, Field = "voice_label", Source = ProvenanceSource.Ai,
				Confidence = ProvenanceConfidence.Sicher, PreviousValue = "Bass", ChangedAt = DateTimeOffset.UtcNow,
			});
			var proposal = new Proposal
			{
				Kind = ProposalKind.FieldSuggestion, TargetEntityType = "asset", TargetEntityId = asset.Id,
				Payload = JsonSerializer.Serialize(new { field = "description", value = "Chorsatz" }),
				Reason = "Test", Source = ProvenanceSource.Ai, Confidence = ProvenanceConfidence.Unsicher,
				TargetRowVersion = asset.RowVersion, Status = ProposalStatus.Open, CreatedAt = DateTimeOffset.UtcNow,
			};
			db.Proposals.Add(proposal);
			await db.SaveChangesAsync();
			proposalId = proposal.Id;
		}
		var revert = new { entityType = "asset", entityId = assetId, field = "voice_label" };

		using (var denied = await SendAsync(client, otherSession, HttpMethod.Post, "/api/provenance/revert", revert))
		{
			Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
			Assert.Equal(AssetEndpoints.AssetOwnerMessage, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		}
		using (var denied = await SendAsync(client, otherSession, HttpMethod.Post, $"/api/proposals/{proposalId}/accept", new { }))
			Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.AsNoTracking().SingleAsync();
			Assert.Equal(("Tenor 1", null), (asset.VoiceLabel, asset.Description));
			Assert.Equal(ProposalStatus.Open, (await db.Proposals.AsNoTracking().SingleAsync()).Status);
		}

		// The owner may do both (the accept first: a revert moves the row version).
		using (var allowed = await SendAsync(client, ownerSession, HttpMethod.Post, $"/api/proposals/{proposalId}/accept", new { }))
			Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
		using (var allowed = await SendAsync(client, ownerSession, HttpMethod.Post, "/api/provenance/revert", revert))
			Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
	}

	[Fact]
	public async Task APatchThatChangesNothingKeepsTheRowVersionAndLeavesProposalsFresh()
	{
		await using var factory = new AuthApiFactory();
		var (client, session, _) = await SetupAsync(factory, reading: null);
		Guid assetId;
		uint before;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.SingleAsync();
			asset.VoiceLabel = "Tenor 1";
			asset.Description = "Chorsatz";
			db.Proposals.Add(new Proposal
			{
				Kind = ProposalKind.FieldSuggestion, TargetEntityType = "asset", TargetEntityId = asset.Id,
				Payload = JsonSerializer.Serialize(new { field = "description", value = "Anders" }),
				Reason = "Test", Source = ProvenanceSource.Ai, Confidence = ProvenanceConfidence.Unsicher,
				TargetRowVersion = asset.RowVersion, Status = ProposalStatus.Open, CreatedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
			assetId = asset.Id;
			before = asset.RowVersion;
		}

		using (var same = await SendAsync(client, session, HttpMethod.Patch, $"/api/assets/{assetId}",
			new { voiceLabel = "Tenor 1", description = "Chorsatz", assetType = "score" }))
			Assert.Equal(HttpStatusCode.OK, same.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(before, (await db.Assets.AsNoTracking().SingleAsync()).RowVersion);
		}
		using (var list = new HttpRequestMessage(HttpMethod.Get, "/api/proposals"))
		{
			list.Headers.Add("Cookie", session);
			using var response = await client.SendAsync(list);
			var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("proposals").EnumerateArray());
			Assert.False(item.GetProperty("isStale").GetBoolean());
		}

		// A real change still moves it.
		using (var changed = await SendAsync(client, session, HttpMethod.Patch, $"/api/assets/{assetId}", new { voiceLabel = "Bass II" }))
			Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
		using var check = factory.Services.CreateScope();
		Assert.True((await check.ServiceProvider.GetRequiredService<ArchiveDbContext>().Assets.AsNoTracking().SingleAsync()).RowVersion > before);
	}

	// ---- helpers ---------------------------------------------------------

	private static async Task<(HttpClient Client, string Session, Guid RevisionId)> SetupAsync(
		AuthApiFactory factory, Action<ReadingJob>? reading, ExtractionTextSource extractionSource = ExtractionTextSource.Embedded,
		bool extractionNoText = false, ExtractionStatus? extractionStatus = null,
		string assetType = AssetEndpoints.ScoreAssetType, string? extractionText = null)
	{
		var accountId = await SeedAccountAsync(factory, Editor, ArchiveRoles.Editor);
		var session = await SignInAsync(factory, Editor);
		var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var song = new Song { Title = "Waldfahrt", CreatedByAccountId = accountId, UpdatedByAccountId = accountId, CreatedAt = now, UpdatedAt = now };
		var arrangement = new Arrangement { Song = song, Label = "Chorsatz", CreatedByAccountId = accountId, CreatedAt = now };
		var version = new MusicalVersion { Arrangement = arrangement, Label = "Original", CreatedByAccountId = accountId, CreatedAt = now };
		var asset = new ArchiveAsset { AssetType = assetType, MusicalVersion = version, CreatedByAccountId = accountId, CreatedAt = now };
		var revision = new FileRevision
		{
			Asset = asset, RevisionNumber = 1, BlobName = $"revisions/{Guid.CreateVersion7():N}.pdf",
			ContentType = AssetEndpoints.PdfContentType, SizeBytes = 100, CreatedByAccountId = accountId, CreatedAt = now,
		};
		asset.CurrentRevisionId = revision.Id;
		db.AddRange(song, arrangement, version, asset, revision);
		db.ExtractionJobs.Add(new ExtractionJob
		{
			RevisionId = revision.Id, AssetId = asset.Id, TriggeredByAccountId = accountId, CreatedAt = now, UpdatedAt = now,
			Status = extractionStatus ?? (extractionNoText ? ExtractionStatus.NoText : ExtractionStatus.Completed),
			Text = extractionNoText ? null : extractionText ?? "Text", TextSource = extractionSource, CompletedAt = now,
		});
		if (reading is not null)
		{
			var row = ReadingService.CreateForRevision(revision, accountId, 20, now);
			reading(row);
			db.ReadingJobs.Add(row);
		}
		await db.SaveChangesAsync();
		return (client, session, revision.Id);
	}

	private static async Task<ReadingJob> ReadingRowAsync(AuthApiFactory factory, Guid revisionId)
	{
		using var scope = factory.Services.CreateScope();
		return await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().ReadingJobs.AsNoTracking()
			.SingleAsync(j => j.RevisionId == revisionId);
	}

	private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? session)
		=> SendAsync(client, session, HttpMethod.Post, path, new { });

	private static async Task<(string CsrfCookie, string Token)> TokenAsync(HttpClient client, string? session)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		// The token is bound to the signed-in identity.
		if (session is not null)
			tokenRequest.Headers.Add("Cookie", session);
		using var tokenResponse = await client.SendAsync(tokenRequest);
		var csrfCookie = Assert.Single(tokenResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
		return (csrfCookie, token);
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? session, HttpMethod method, string path, object body)
	{
		var (csrfCookie, token) = await TokenAsync(client, session);
		return await SendWithAsync(client, session, csrfCookie, token, method, path, body);
	}

	private static async Task<HttpResponseMessage> SendWithAsync(
		HttpClient client, string? session, string csrfCookie, string token, HttpMethod method, string path, object body)
	{
		var request = new HttpRequestMessage(method, path);
		request.Headers.Add("Cookie", session is null ? csrfCookie : $"{csrfCookie}; {session}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return await client.SendAsync(request);
	}

	private static async Task<Guid> SeedAccountAsync(AuthApiFactory factory, string email, string role)
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
		var (cookie, token) = await CsrfAsync(client);
		using var request = Post("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await CsrfAsync(client);
		using var verify = Post("/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		return Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
	}

	private static async Task<(string Cookie, string Token)> CsrfAsync(HttpClient client)
	{
		using var response = await client.GetAsync("/api/antiforgery");
		response.EnsureSuccessStatusCode();
		var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
		return (cookie, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
	}

	private static HttpRequestMessage Post(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}
}
