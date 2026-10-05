using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-029 confirmation of the actual programme: an editor reviews a
/// specific published revision and confirms it unchanged in one action, or
/// marks planned entries skipped, corrects the sung version and adds an
/// encore. The outcome persists as ARC-028 occurrences (always confirmed)
/// linked to their planned programme item; the planned list stays frozen.
/// Repeating the confirmation updates the same occurrences (stable
/// identity, no double count), genuine repeats remain distinct entries,
/// stale published revisions and stale tokens are refused, and only
/// editors confirm.
/// </summary>
public sealed class ProgrammeConfirmationApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";
	private const string Zweitredaktion = "zweitredaktion@liedertafel.test";

	[Fact]
	public async Task ConfirmingUnchangedProgrammeLinksOneOccurrencePerPlannedEntry()
	{
		await using var scenario = await Scenario.CreateAsync();
		var programmeBefore = await scenario.EditorProgrammeAsync();

		// Before any confirmation the review is open, token 0.
		var review = await scenario.ReviewAsync();
		Assert.Equal(0u, review.GetProperty("rowVersion").GetUInt32());
		Assert.True(review.GetProperty("confirmation").ValueKind is JsonValueKind.Null);
		Assert.True(review.GetProperty("upToDate").GetBoolean());
		Assert.Equal(scenario.RevisionId, Guid.Parse(review.GetProperty("revision").GetProperty("id").GetString()!));
		var items = review.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(3, items.Count);
		Assert.All(items, item => Assert.Equal("open", item.GetProperty("outcome").GetString()));
		Assert.Equal(new[] { 1, 2, 3 }, items.Select(i => i.GetProperty("position").GetInt32()).ToArray());
		Assert.Empty(review.GetProperty("additions").EnumerateArray());

		// One action: every planned entry sung as planned.
		using var response = await scenario.ConfirmAsync(scenario.AllSung(review));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var confirmed = await ReviewOfAsync(response);
		var confirmedItems = confirmed.GetProperty("items").EnumerateArray().ToList();
		Assert.All(confirmedItems, item => Assert.Equal("sung", item.GetProperty("outcome").GetString()));
		Assert.Equal(1u, confirmed.GetProperty("rowVersion").GetUInt32());
		Assert.False(confirmed.GetProperty("confirmation").ValueKind is JsonValueKind.Null);

		// Each planned entry owns its own occurrence: the repeated song is
		// two distinct occurrences, all confirmed and linked to the item.
		var performanceIds = confirmedItems
			.Select(i => i.GetProperty("performance").GetProperty("id").GetString()!).ToList();
		Assert.Equal(3, performanceIds.Distinct().Count());
		Assert.All(confirmedItems, item =>
		{
			var performance = item.GetProperty("performance");
			Assert.Equal("confirmed", performance.GetProperty("evidenceStatus").GetString());
			Assert.Equal(item.GetProperty("programmeItemId").GetString(),
				performance.GetProperty("programmeItemId").GetString());
			Assert.Equal(item.GetProperty("musicalVersionId").GetString(),
				performance.GetProperty("musicalVersionId").GetString());
		});

		// The ARC-028 evidence list carries them (editor embed shows the link).
		var detail = await scenario.DetailAsync(scenario.EditorSession);
		var occurrences = detail.GetProperty("performances").EnumerateArray().ToList();
		Assert.Equal(3, occurrences.Count);
		Assert.All(occurrences, o => Assert.Equal("confirmed", o.GetProperty("evidenceStatus").GetString()));
		Assert.All(occurrences, o => Assert.False(o.GetProperty("programmeItemId").ValueKind is JsonValueKind.Null));

		// The planned list is untouched: same revision, items and token.
		var programmeAfter = await scenario.EditorProgrammeAsync();
		Assert.Equal(programmeBefore.GetProperty("rowVersion").GetUInt32(),
			programmeAfter.GetProperty("rowVersion").GetUInt32());
		Assert.Equal(programmeBefore.GetProperty("published").GetRawText(),
			programmeAfter.GetProperty("published").GetRawText());

		// Members distinguish plan and actual history.
		var memberDetail = await scenario.DetailAsync(scenario.MemberSession);
		var memberProgramme = memberDetail.GetProperty("programme");
		Assert.Equal(3, memberProgramme.GetProperty("published").GetProperty("items").GetArrayLength());
		var memberConfirmation = memberProgramme.GetProperty("confirmation");
		Assert.Equal(3, memberConfirmation.GetProperty("actual").GetArrayLength());
		Assert.Empty(memberConfirmation.GetProperty("skipped").EnumerateArray());
		Assert.True(memberConfirmation.GetProperty("upToDate").GetBoolean());
		Assert.False(memberConfirmation.TryGetProperty("rowVersion", out _));
	}

	[Fact]
	public async Task RepeatedConfirmationUpdatesSameOccurrencesAndNeverDoubles()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		var body = scenario.AllSung(review);
		using var first = await scenario.ConfirmAsync(body);
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		var firstReview = await ReviewOfAsync(first);
		var firstIds = PerformanceIds(firstReview);

		// A lost response is retried with the identical (now stale) token:
		// the same occurrences answer, nothing is written, no bump.
		using var retry = await scenario.ConfirmAsync(body);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var retryReview = await ReviewOfAsync(retry);
		Assert.Equal(firstIds, PerformanceIds(retryReview));
		Assert.Equal(firstReview.GetProperty("rowVersion").GetUInt32(),
			retryReview.GetProperty("rowVersion").GetUInt32());
		Assert.Equal(3, await scenario.CountPerformancesAsync());

		// A deliberate re-confirmation with the fresh token and one corrected
		// version updates that occurrence in place; the others stay byte-equal.
		var current = await scenario.ReviewAsync();
		var corrected = scenario.AllSung(current, correctFirstTo: scenario.TransposedA);
		using var update = await scenario.ConfirmAsync(corrected);
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);
		var updated = await ReviewOfAsync(update);
		Assert.Equal(firstIds, PerformanceIds(updated));
		Assert.Equal(scenario.TransposedA,
			Guid.Parse(updated.GetProperty("items")[0].GetProperty("performance")
				.GetProperty("musicalVersionId").GetString()!));
		Assert.Equal(2u, updated.GetProperty("rowVersion").GetUInt32());
		Assert.Equal(
			firstReview.GetProperty("items")[1].GetProperty("performance").GetRawText(),
			updated.GetProperty("items")[1].GetProperty("performance").GetRawText());
		Assert.Equal(3, await scenario.CountPerformancesAsync());
	}

	[Fact]
	public async Task SkippedEntryCorrectedVersionAndEncoreKeepThePlanIntact()
	{
		await using var scenario = await Scenario.CreateAsync();
		var programmeBefore = await scenario.EditorProgrammeAsync();
		var review = await scenario.ReviewAsync();
		var encoreKey = Guid.CreateVersion7();
		var body = scenario.Body(review,
			skip: [1],
			correctFirstTo: scenario.TransposedA,
			additions: [new { clientKey = encoreKey, songId = scenario.SongD.SongId, musicalVersionId = scenario.SongD.VersionId }]);
		using var response = await scenario.ConfirmAsync(body);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await ReviewOfAsync(response);
		var items = result.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal("sung", items[0].GetProperty("outcome").GetString());
		Assert.Equal("skipped", items[1].GetProperty("outcome").GetString());
		Assert.True(items[1].GetProperty("performance").ValueKind is JsonValueKind.Null);
		Assert.Equal("sung", items[2].GetProperty("outcome").GetString());
		// The planned version of the corrected entry stays visible next to
		// the sung one.
		Assert.Equal(scenario.SongA.VersionId,
			Guid.Parse(items[0].GetProperty("musicalVersionId").GetString()!));
		Assert.Equal(scenario.TransposedA,
			Guid.Parse(items[0].GetProperty("performance").GetProperty("musicalVersionId").GetString()!));
		var additions = result.GetProperty("additions").EnumerateArray().ToList();
		var encore = Assert.Single(additions);
		Assert.Equal(scenario.SongD.SongId, Guid.Parse(encore.GetProperty("songId").GetString()!));
		Assert.True(encore.GetProperty("programmeItemId").ValueKind is JsonValueKind.Null);
		Assert.Equal("Zugabe", encore.GetProperty("songTitle").GetString());
		var encoreId = encore.GetProperty("id").GetString()!;
		Assert.Equal(3, await scenario.CountPerformancesAsync());

		// Retrying the same encore never duplicates it.
		var fresh = await scenario.ReviewAsync();
		var again = scenario.Body(fresh,
			skip: [1],
			correctFirstTo: scenario.TransposedA,
			additions: [new { clientKey = encoreKey, songId = scenario.SongD.SongId, musicalVersionId = scenario.SongD.VersionId }]);
		using var retry = await scenario.ConfirmAsync(again);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var retried = await ReviewOfAsync(retry);
		Assert.Equal(encoreId, Assert.Single(retried.GetProperty("additions").EnumerateArray())
			.GetProperty("id").GetString());
		Assert.Equal(3, await scenario.CountPerformancesAsync());

		// The planned list is exactly as published.
		var programmeAfter = await scenario.EditorProgrammeAsync();
		Assert.Equal(programmeBefore.GetProperty("rowVersion").GetUInt32(),
			programmeAfter.GetProperty("rowVersion").GetUInt32());
		Assert.Equal(programmeBefore.GetProperty("published").GetRawText(),
			programmeAfter.GetProperty("published").GetRawText());

		// Members read the plan with the skip and the encore marked in the
		// actual history.
		var memberConfirmation = (await scenario.DetailAsync(scenario.MemberSession))
			.GetProperty("programme").GetProperty("confirmation");
		var actual = memberConfirmation.GetProperty("actual").EnumerateArray().ToList();
		Assert.Equal(3, actual.Count);
		Assert.Equal(new[] { false, false, true },
			actual.Select(a => a.GetProperty("added").GetBoolean()).ToArray());
		Assert.True(actual[0].GetProperty("differsFromPlan").GetBoolean());
		Assert.False(actual[1].GetProperty("differsFromPlan").GetBoolean());
		var skipped = Assert.Single(memberConfirmation.GetProperty("skipped").EnumerateArray());
		Assert.Equal("Zweites Lied", skipped.GetProperty("songTitle").GetString());

		// Un-skipping later restores an occurrence for that planned entry.
		var later = await scenario.ReviewAsync();
		using var restore = await scenario.ConfirmAsync(scenario.Body(later,
			correctFirstTo: scenario.TransposedA,
			additions: [new { clientKey = encoreKey, songId = scenario.SongD.SongId, musicalVersionId = scenario.SongD.VersionId }]));
		Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
		var restored = await ReviewOfAsync(restore);
		Assert.Equal("sung", restored.GetProperty("items")[1].GetProperty("outcome").GetString());
		Assert.Equal(4, await scenario.CountPerformancesAsync());
	}

	[Fact]
	public async Task GenuineRepeatedSongsStayDistinctOccurrences()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		// Song A is planned twice and sung a third time as an encore: three
		// distinct occurrences of the same song at one event.
		using var response = await scenario.ConfirmAsync(scenario.Body(review, additions:
		[
			new { clientKey = Guid.CreateVersion7(), songId = scenario.SongA.SongId, musicalVersionId = scenario.SongA.VersionId },
			new { clientKey = Guid.CreateVersion7(), songId = scenario.SongA.SongId, musicalVersionId = scenario.SongA.VersionId },
		]));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await ReviewOfAsync(response);
		Assert.Equal(2, result.GetProperty("additions").GetArrayLength());
		Assert.Equal(5, await scenario.CountPerformancesAsync());
		var ids = PerformanceIds(result)
			.Concat(result.GetProperty("additions").EnumerateArray().Select(a => a.GetProperty("id").GetString()!))
			.ToList();
		Assert.Equal(5, ids.Distinct().Count());

		// Only confirmed occurrences count: the song history lists song A
		// four times (two planned, two added).
		var history = await scenario.SongHistoryAsync(scenario.SongA.SongId);
		Assert.Equal(4, history.GetArrayLength());
		Assert.All(history.EnumerateArray(), row => Assert.Equal("confirmed", row.GetProperty("evidenceStatus").GetString()));
	}

	[Fact]
	public async Task SkippedSongsDoNotCountAsPerformed()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		using var response = await scenario.ConfirmAsync(scenario.Body(review, skip: [1]));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		// Song B was planned but skipped: it has no history at all.
		Assert.Equal(0, (await scenario.SongHistoryAsync(scenario.SongB.SongId)).GetArrayLength());
		Assert.Equal(2, (await scenario.SongHistoryAsync(scenario.SongA.SongId)).GetArrayLength());
	}

	[Fact]
	public async Task StaleRevisionIsRefusedPlanStaysAndOccurrencesCarryOver()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		using var firstConfirm = await scenario.ConfirmAsync(scenario.AllSung(review));
		Assert.Equal(HttpStatusCode.OK, firstConfirm.StatusCode);
		var confirmedIds = PerformanceIds(await ReviewOfAsync(firstConfirm));
		var stalePut = scenario.AllSung(review);

		// The plan is revised and republished: revision 2 supersedes 1.
		var secondRevisionId = await scenario.RepublishAsync(
			[(scenario.SongA, scenario.SongA.VersionId), (scenario.SongB, scenario.SongB.VersionId)]);
		Assert.NotEqual(scenario.RevisionId, secondRevisionId);

		// A submission against the superseded revision is refused with the
		// stale message and writes nothing.
		using var stale = await scenario.ConfirmAsync(stalePut);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal(ProgrammeConfirmationEndpoints.StaleRevisionMessage,
			(await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		Assert.Equal(3, await scenario.CountPerformancesAsync());

		// The older revision stays readable as a historical review.
		var old = await scenario.ReviewAsync(scenario.RevisionId);
		Assert.False(old.GetProperty("upToDate").GetBoolean());
		Assert.Equal(1, old.GetProperty("revision").GetProperty("number").GetInt32());
		Assert.All(old.GetProperty("items").EnumerateArray(),
			item => Assert.Equal("sung", item.GetProperty("outcome").GetString()));

		// The default review is the newest revision: open entries with
		// suggested carry-over of the earlier occurrences (same song, in
		// order), which the editor adopts explicitly.
		var newest = await scenario.ReviewAsync();
		Assert.Equal(secondRevisionId, Guid.Parse(newest.GetProperty("revision").GetProperty("id").GetString()!));
		var newestItems = newest.GetProperty("items").EnumerateArray().ToList();
		Assert.All(newestItems, item => Assert.Equal("open", item.GetProperty("outcome").GetString()));
		var suggestions = newestItems
			.Select(i => i.GetProperty("suggestedPerformanceId").GetString()).ToList();
		Assert.Equal(new[] { confirmedIds[0], confirmedIds[1] }, suggestions.ToArray());
		Assert.True(newest.GetProperty("confirmation").GetProperty("revisionId").GetString()
			!= secondRevisionId.ToString());

		var adopt = new
		{
			revisionId = secondRevisionId,
			rowVersion = newest.GetProperty("rowVersion").GetUInt32(),
			items = newestItems.Select((item, index) => new
			{
				programmeItemId = Guid.Parse(item.GetProperty("programmeItemId").GetString()!),
				outcome = "sung",
				performanceId = Guid.Parse(suggestions[index]!),
			}).ToArray(),
			additions = Array.Empty<object>(),
		};
		using var adopted = await scenario.ConfirmAsync(adopt);
		Assert.Equal(HttpStatusCode.OK, adopted.StatusCode);
		var adoptedReview = await ReviewOfAsync(adopted);
		Assert.Equal(new[] { confirmedIds[0], confirmedIds[1] }, PerformanceIds(adoptedReview).ToArray());
		// The third earlier occurrence is no longer part of the confirmed
		// actual list (its planned entry is gone in revision 2).
		Assert.Equal(2, await scenario.CountPerformancesAsync());
		Assert.True(adoptedReview.GetProperty("upToDate").GetBoolean());
	}

	[Fact]
	public async Task OnlyEditorsMayReviewAndConfirm()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		var body = scenario.AllSung(review);

		// Members: 403 for the read and the write.
		using var memberRead = await scenario.GetReviewRawAsync(scenario.MemberSession);
		Assert.Equal(HttpStatusCode.Forbidden, memberRead.StatusCode);
		using var memberWrite = await scenario.ConfirmAsync(body, scenario.MemberSession);
		Assert.Equal(HttpStatusCode.Forbidden, memberWrite.StatusCode);

		// Anonymous callers: 401 (read without session, write with only
		// the antiforgery pair).
		using var anonymousRead = await scenario.GetReviewRawAsync(null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
		var (anonymousCookie, anonymousToken) = await GetCsrfAsync(scenario.Client);
		using var anonymousWrite = AuthedPut(scenario.ConfirmationPath, body, anonymousCookie, anonymousToken);
		using var anonymousResponse = await scenario.Client.SendAsync(anonymousWrite);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

		// A write without the antiforgery token is refused before anything
		// else (the editor session alone is not enough).
		using var noToken = new HttpRequestMessage(HttpMethod.Put, scenario.ConfirmationPath);
		noToken.Headers.Add("Cookie", scenario.EditorSession);
		noToken.Content = JsonContent.Create(body);
		using var noTokenResponse = await scenario.Client.SendAsync(noToken);
		Assert.Equal(HttpStatusCode.BadRequest, noTokenResponse.StatusCode);

		// A downgraded editor loses write access, a revoked one all access.
		using (var scope = scenario.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(Zweitredaktion);
			Assert.True((await users.RemoveFromRoleAsync(user!, ArchiveRoles.Editor)).Succeeded);
		}
		await SeedAsync(scenario.Factory, Zweitredaktion, ArchiveRoles.Member);
		using var downgradedRead = await scenario.GetReviewRawAsync(scenario.SecondEditorSession);
		Assert.Equal(HttpStatusCode.Forbidden, downgradedRead.StatusCode);
		using var downgradedWrite = await scenario.ConfirmAsync(body, scenario.SecondEditorSession);
		Assert.Equal(HttpStatusCode.Forbidden, downgradedWrite.StatusCode);
		using (var scope = scenario.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(Zweitredaktion);
			user!.EmailConfirmed = false;
			Assert.True((await users.UpdateAsync(user)).Succeeded);
		}
		using var revokedRead = await scenario.GetReviewRawAsync(scenario.SecondEditorSession);
		Assert.Equal(HttpStatusCode.Unauthorized, revokedRead.StatusCode);

		// Nothing was written by any refused call.
		Assert.Equal(0, await scenario.CountPerformancesAsync());
	}

	[Fact]
	public async Task ForeignAndMalformedReferencesAreRejectedBeforeAnyWrite()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		var items = review.GetProperty("items").EnumerateArray().ToList();
		var itemIds = items.Select(i => Guid.Parse(i.GetProperty("programmeItemId").GetString()!)).ToList();

		// Another event with its own published programme and a confirmed
		// occurrence there.
		var other = await scenario.CreateOtherPublishedProgrammeAsync();
		using var otherConfirm = await scenario.ConfirmAsync(other.AllSungBody(), path: other.Path);
		Assert.Equal(HttpStatusCode.OK, otherConfirm.StatusCode);
		var foreignPerformance = Guid.Parse(PerformanceIds(await ReviewOfAsync(otherConfirm))[0]);

		object Plan(IEnumerable<object> entries, Guid? revisionId = null, object[]? additions = null) => new
		{
			revisionId = revisionId ?? scenario.RevisionId,
			rowVersion = 0,
			items = entries.ToArray(),
			additions = additions ?? [],
		};
		object Sung(Guid id, Guid? performanceId = null, Guid? version = null) => new
		{
			programmeItemId = id,
			outcome = "sung",
			performanceId,
			musicalVersionId = version,
		};

		// The revision belongs to another event's programme: 404.
		using var foreignRevision = await scenario.ConfirmAsync(
			Plan(itemIds.Select(id => Sung(id)), other.RevisionId));
		Assert.Equal(HttpStatusCode.NotFound, foreignRevision.StatusCode);
		// A working draft revision cannot be confirmed: only published ones.
		using (var saveDraft = await PutJsonAsync(scenario.Client,
			$"/api/events/{scenario.EventId}/programme/items",
			new { items = Array.Empty<object>(), rowVersion = (await scenario.EditorProgrammeAsync()).GetProperty("rowVersion").GetUInt32() },
			scenario.EditorSession))
			Assert.Equal(HttpStatusCode.OK, saveDraft.StatusCode);
		var draftId = Guid.Parse((await scenario.EditorProgrammeAsync())
			.GetProperty("working").GetProperty("id").GetString()!);
		using var draft = await scenario.ConfirmAsync(
			Plan(itemIds.Select(id => Sung(id)), draftId));
		Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
		// An unknown revision is the same 404.
		using var unknownRevision = await scenario.ConfirmAsync(
			Plan(itemIds.Select(id => Sung(id)), Guid.NewGuid()));
		Assert.Equal(HttpStatusCode.NotFound, unknownRevision.StatusCode);
		// An item of another programme is invalid (400).
		using var foreignItem = await scenario.ConfirmAsync(Plan(
			[Sung(itemIds[0]), Sung(itemIds[1]), Sung(other.ItemIds[0])]));
		Assert.Equal(HttpStatusCode.BadRequest, foreignItem.StatusCode);
		// An occurrence of another event cannot be adopted (400).
		using var foreignOccurrence = await scenario.ConfirmAsync(Plan(
			[Sung(itemIds[0], foreignPerformance), Sung(itemIds[1]), Sung(itemIds[2])]));
		Assert.Equal(HttpStatusCode.BadRequest, foreignOccurrence.StatusCode);
		// Incomplete review (a planned entry is missing) is invalid.
		using var incomplete = await scenario.ConfirmAsync(Plan([Sung(itemIds[0]), Sung(itemIds[1])]));
		Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
		// A duplicated planned entry is invalid.
		using var duplicate = await scenario.ConfirmAsync(Plan(
			[Sung(itemIds[0]), Sung(itemIds[0]), Sung(itemIds[2])]));
		Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
		// A sung version of another song does not belong to the entry.
		using var wrongVersion = await scenario.ConfirmAsync(Plan(
			[Sung(itemIds[0], version: scenario.SongB.VersionId), Sung(itemIds[1]), Sung(itemIds[2])]));
		Assert.Equal(HttpStatusCode.BadRequest, wrongVersion.StatusCode);
		Assert.Equal(ProgrammeConfirmationEndpoints.VersionMismatchMessage,
			(await wrongVersion.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		// An encore must name an existing song and a matching version.
		using var unknownSong = await scenario.ConfirmAsync(Plan(
			itemIds.Select(id => Sung(id)),
			additions: [new { clientKey = Guid.NewGuid(), songId = Guid.NewGuid() }]));
		Assert.Equal(HttpStatusCode.NotFound, unknownSong.StatusCode);
		using var mismatchedEncore = await scenario.ConfirmAsync(Plan(
			itemIds.Select(id => Sung(id)),
			additions: [new { clientKey = Guid.NewGuid(), songId = scenario.SongC.SongId, musicalVersionId = scenario.SongB.VersionId }]));
		Assert.Equal(HttpStatusCode.NotFound, mismatchedEncore.StatusCode);
		// A malformed body is a German 400.
		using var empty = await scenario.ConfirmAsync(new { });
		Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

		// None of the refusals wrote anything to this event.
		Assert.Equal(0, await scenario.CountPerformancesAsync(scenario.EventId));
	}

	[Fact]
	public async Task TwoEditorsCannotSilentlyOverwriteEachOthersConfirmation()
	{
		await using var scenario = await Scenario.CreateAsync();
		var reviewA = await scenario.ReviewAsync();
		var reviewB = await scenario.ReviewAsync(session: scenario.SecondEditorSession);

		// Editor A confirms all three as planned.
		using var confirmA = await scenario.ConfirmAsync(scenario.AllSung(reviewA));
		Assert.Equal(HttpStatusCode.OK, confirmA.StatusCode);
		var idsA = PerformanceIds(await ReviewOfAsync(confirmA));

		// Editor B loaded the open review and now submits a different
		// outcome (skip one) with the old token: refused, A's state stays.
		using var conflict = await scenario.ConfirmAsync(scenario.Body(reviewB, skip: [1]),
			scenario.SecondEditorSession);
		Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
		Assert.Equal(ProgrammeConfirmationEndpoints.ConcurrencyMessage,
			(await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		var afterConflict = await scenario.ReviewAsync();
		Assert.Equal(idsA, PerformanceIds(afterConflict));
		Assert.All(afterConflict.GetProperty("items").EnumerateArray(),
			item => Assert.Equal("sung", item.GetProperty("outcome").GetString()));

		// After reloading, B's redo is accepted and attributed to B.
		var fresh = await scenario.ReviewAsync(session: scenario.SecondEditorSession);
		using var redo = await scenario.ConfirmAsync(scenario.Body(fresh, skip: [1]),
			scenario.SecondEditorSession);
		Assert.Equal(HttpStatusCode.OK, redo.StatusCode);
		var editorId = await UserIdAsync(scenario.Factory, Editor);
		var secondEditorId = await UserIdAsync(scenario.Factory, Zweitredaktion);
		using var scope = scenario.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var confirmation = await db.ProgrammeConfirmations.SingleAsync();
		Assert.Equal(editorId, confirmation.CreatedByAccountId);
		Assert.Equal(secondEditorId, confirmation.UpdatedByAccountId);
		var performances = await db.Performances.Where(p => p.EventId == scenario.EventId).ToListAsync();
		Assert.Equal(2, performances.Count);
		Assert.All(performances, p => Assert.Equal(editorId, p.CreatedByAccountId));
		Assert.All(performances, p => Assert.Equal(PerformanceEvidenceStatus.Confirmed, p.EvidenceStatus));
		Assert.All(performances, p => Assert.Equal(confirmation.Id, p.ConfirmationId));
	}

	[Fact]
	public async Task ConfirmationOfFutureEventIsRefusedAndDateAloneConfirmsNothing()
	{
		await using var scenario = await Scenario.CreateAsync(dateYear: 2099);
		var review = await scenario.ReviewAsync();
		using var response = await scenario.ConfirmAsync(scenario.AllSung(review));
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(ProgrammeConfirmationEndpoints.FutureEventMessage,
			(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		Assert.Equal(0, await scenario.CountPerformancesAsync());

		// A past event with an untouched programme confirms nothing either:
		// merely having passed its date creates no occurrence.
		await using var past = await Scenario.CreateAsync(dateYear: 1950);
		Assert.Equal(0, await past.CountPerformancesAsync());
		Assert.True((await past.DetailAsync(past.MemberSession))
			.GetProperty("programme").GetProperty("confirmation").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task ConfirmedOccurrencesCannotBeDeletedBehindTheConfirmationsBack()
	{
		await using var scenario = await Scenario.CreateAsync();
		var review = await scenario.ReviewAsync();
		using var response = await scenario.ConfirmAsync(scenario.AllSung(review));
		var performanceId = PerformanceIds(await ReviewOfAsync(response))[0];

		using var delete = await PostJsonAsync(scenario.Client, $"/api/performances/{performanceId}/delete",
			new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
		Assert.Equal(PerformanceEndpoints.ConfirmedOccurrenceMessage,
			(await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		Assert.Equal(3, await scenario.CountPerformancesAsync());

		// A downgrade to an unconfirmed mention through the evidence API is
		// visible in the review and for members instead of silently counted.
		var detail = await scenario.DetailAsync(scenario.EditorSession);
		var row = detail.GetProperty("performances").EnumerateArray()
			.Single(o => o.GetProperty("id").GetString() == performanceId);
		using var patch = await PatchJsonAsync(scenario.Client, $"/api/performances/{performanceId}",
			new { rowVersion = row.GetProperty("rowVersion").GetUInt32(), evidenceStatus = "mention", sourceNote = "Unsicher" },
			scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
		var after = await scenario.ReviewAsync();
		Assert.Equal("unconfirmed", after.GetProperty("items")[0].GetProperty("outcome").GetString());
		var memberActual = (await scenario.DetailAsync(scenario.MemberSession))
			.GetProperty("programme").GetProperty("confirmation").GetProperty("actual")[0];
		Assert.Equal("mention", memberActual.GetProperty("evidenceStatus").GetString());
	}

	private static async Task<JsonElement> ReviewOfAsync(HttpResponseMessage response)
		=> (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("review");

	private static List<string> PerformanceIds(JsonElement review)
		=> review.GetProperty("items").EnumerateArray()
			.Where(i => i.GetProperty("performance").ValueKind is not JsonValueKind.Null)
			.Select(i => i.GetProperty("performance").GetProperty("id").GetString()!)
			.ToList();

	/// <summary>
	/// A published concert with a member, two editors and the catalogue the
	/// confirmation scenarios draw on: songs A (planned twice, with a
	/// transposed version), B, C and D ("Zugabe", sung only as an encore).
	/// The programme is [A, B, A], published as revision 1.
	/// </summary>
	private sealed class Scenario : IAsyncDisposable
	{
		public required AuthApiFactory Factory { get; init; }
		public required HttpClient Client { get; init; }
		public required string MemberSession { get; init; }
		public required string EditorSession { get; init; }
		public required string SecondEditorSession { get; init; }
		public required Guid EventId { get; init; }
		public required Guid RevisionId { get; init; }
		public required (Guid SongId, Guid ArrangementId, Guid VersionId) SongA { get; init; }
		public required (Guid SongId, Guid ArrangementId, Guid VersionId) SongB { get; init; }
		public required (Guid SongId, Guid ArrangementId, Guid VersionId) SongC { get; init; }
		public required (Guid SongId, Guid ArrangementId, Guid VersionId) SongD { get; init; }
		public required Guid TransposedA { get; init; }

		public string ConfirmationPath => $"/api/events/{EventId}/programme/confirmation";

		public static async Task<Scenario> CreateAsync(int dateYear = 1950)
		{
			var factory = new AuthApiFactory();
			await SeedAsync(factory, Member, ArchiveRoles.Member);
			await SeedAsync(factory, Editor, ArchiveRoles.Editor);
			await SeedAsync(factory, Zweitredaktion, ArchiveRoles.Editor);
			var memberSession = await SignInAsync(factory, Member);
			var editorSession = await SignInAsync(factory, Editor);
			var secondSession = await SignInAsync(factory, Zweitredaktion);
			var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
			var songA = await CreateSongAsync(client, editorSession, "Erstes Lied", versionLabel: "Grundtonart", musicalKey: "G-Dur");
			var songB = await CreateSongAsync(client, editorSession, "Zweites Lied");
			var songC = await CreateSongAsync(client, editorSession, "Drittes Lied");
			var songD = await CreateSongAsync(client, editorSession, "Zugabe");
			var transposed = await CreateVersionAsync(client, editorSession, songA.ArrangementId, "Tiefe Tonart", "F-Dur");
			var eventId = await CreateEventAsync(client, editorSession, new
			{
				kind = "concert",
				title = "Jahreskonzert",
				dateYear,
				dateMonth = 5,
				dateDay = 12,
			});
			await PublishEventAsync(client, editorSession, eventId);
			var revisionId = await SaveAndPublishProgrammeAsync(client, editorSession, eventId,
				[(songA, songA.VersionId), (songB, songB.VersionId), (songA, songA.VersionId)]);
			return new Scenario
			{
				Factory = factory,
				Client = client,
				MemberSession = memberSession,
				EditorSession = editorSession,
				SecondEditorSession = secondSession,
				EventId = eventId,
				RevisionId = revisionId,
				SongA = songA,
				SongB = songB,
				SongC = songC,
				SongD = songD,
				TransposedA = transposed,
			};
		}

		public async ValueTask DisposeAsync()
		{
			Client.Dispose();
			await Factory.DisposeAsync();
		}

		/// <summary>Publishes a new revision (the draft starts after the last publication).</summary>
		public Task<Guid> RepublishAsync(
			IEnumerable<((Guid SongId, Guid ArrangementId, Guid VersionId) Song, Guid VersionId)> entries)
			=> SaveAndPublishProgrammeAsync(Client, EditorSession, EventId, entries);

		public async Task<JsonElement> ReviewAsync(Guid? revisionId = null, string? session = null)
		{
			using var response = await GetReviewRawAsync(session ?? EditorSession, revisionId);
			response.EnsureSuccessStatusCode();
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("review");
		}

		public async Task<HttpResponseMessage> GetReviewRawAsync(string? session, Guid? revisionId = null)
		{
			var query = revisionId is { } id ? $"?revisionId={id}" : string.Empty;
			using var request = new HttpRequestMessage(HttpMethod.Get, ConfirmationPath + query);
			if (session is not null)
				request.Headers.Add("Cookie", session);
			return await Client.SendAsync(request);
		}

		public async Task<HttpResponseMessage> ConfirmAsync(object body, string? session = null, string? path = null)
			=> await PutJsonAsync(Client, path ?? ConfirmationPath, body, session ?? EditorSession);

		public async Task<JsonElement> DetailAsync(string session)
			=> await GetEventDetailAsync(Client, session, EventId);

		public async Task<JsonElement> EditorProgrammeAsync()
			=> (await DetailAsync(EditorSession)).GetProperty("programme");

		public async Task<JsonElement> SongHistoryAsync(Guid songId)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}/performances");
			request.Headers.Add("Cookie", EditorSession);
			using var response = await Client.SendAsync(request);
			response.EnsureSuccessStatusCode();
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("performances");
		}

		public async Task<int> CountPerformancesAsync(Guid? eventId = null)
		{
			using var scope = Factory.Services.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var id = eventId ?? EventId;
			return await db.Performances.CountAsync(p => p.EventId == id);
		}

		/// <summary>Body confirming every planned entry as planned.</summary>
		public object AllSung(JsonElement review, Guid? correctFirstTo = null)
			=> Body(review, correctFirstTo: correctFirstTo);

		/// <summary>
		/// Builds the PUT body from a fetched review: every planned entry
		/// sung unless its index is skipped; existing occurrences ride along
		/// with their ids so a confirmation keeps their identity.
		/// </summary>
		public object Body(JsonElement review, int[]? skip = null, Guid? correctFirstTo = null,
			object[]? additions = null)
		{
			var skipped = skip ?? [];
			var entries = review.GetProperty("items").EnumerateArray().Select((item, index) => new
			{
				programmeItemId = Guid.Parse(item.GetProperty("programmeItemId").GetString()!),
				outcome = skipped.Contains(index) ? "skipped" : "sung",
				musicalVersionId = index == 0 ? correctFirstTo : null,
			}).ToArray();
			return new
			{
				revisionId = Guid.Parse(review.GetProperty("revision").GetProperty("id").GetString()!),
				rowVersion = review.GetProperty("rowVersion").GetUInt32(),
				items = entries,
				additions = additions ?? [],
			};
		}

		public async Task<OtherProgramme> CreateOtherPublishedProgrammeAsync()
		{
			var eventId = await CreateEventAsync(Client, EditorSession, new
			{
				kind = "concert",
				title = "Anderer Auftritt",
				dateYear = 1960,
			});
			var revisionId = await SaveAndPublishProgrammeAsync(Client, EditorSession, eventId,
				[(SongC, SongC.VersionId)]);
			using var scope = Factory.Services.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var itemIds = await db.ProgrammeItems.Where(i => i.RevisionId == revisionId)
				.OrderBy(i => i.Position).Select(i => i.Id).ToListAsync();
			return new OtherProgramme(eventId, revisionId, itemIds);
		}

		public sealed record OtherProgramme(Guid EventId, Guid RevisionId, List<Guid> ItemIds)
		{
			public string Path => $"/api/events/{EventId}/programme/confirmation";

			public object AllSungBody() => new
			{
				revisionId = RevisionId,
				rowVersion = 0,
				items = ItemIds.Select(id => new { programmeItemId = id, outcome = "sung" }).ToArray(),
				additions = Array.Empty<object>(),
			};
		}
	}

	/// <summary>
	/// Saves the draft items and publishes the programme; returns the
	/// published revision id (a draft after a publication starts empty, so
	/// the items are always supplied without ids).
	/// </summary>
	private static async Task<Guid> SaveAndPublishProgrammeAsync(
		HttpClient client, string editorSession, Guid eventId,
		IEnumerable<((Guid SongId, Guid ArrangementId, Guid VersionId) Song, Guid VersionId)> entries)
	{
		var detail = await GetEventDetailAsync(client, editorSession, eventId);
		var programme = detail.GetProperty("programme");
		var items = entries.Select(e => (object)new { songId = e.Song.SongId, musicalVersionId = e.VersionId }).ToArray();
		object body = programme.ValueKind is JsonValueKind.Null
			? new { items }
			: new { items, rowVersion = programme.GetProperty("rowVersion").GetUInt32() };
		using var save = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items", body, editorSession);
		Assert.Equal(HttpStatusCode.OK, save.StatusCode);
		var saved = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");
		using var publish = await PostJsonAsync(client, $"/api/events/{eventId}/programme/publish",
			new { rowVersion = saved.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		var published = (await publish.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("programme").GetProperty("published");
		return Guid.Parse(published.GetProperty("id").GetString()!);
	}

	// Helpers, copied per-file in the style of the other API test suites.

	private static async Task<JsonElement> GetEventDetailAsync(HttpClient client, string session, Guid eventId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("event");
	}

	private static async Task<Guid> CreateEventAsync(HttpClient client, string editorSession, object body)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/events", body, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(parsed.GetProperty("event").GetProperty("id").GetString()!);
	}

	private static async Task PublishEventAsync(HttpClient client, string editorSession, Guid eventId)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/events/{eventId}/publish", new { },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<(Guid SongId, Guid ArrangementId, Guid VersionId)> CreateSongAsync(
		HttpClient client, string editorSession, string title,
		string? versionLabel = null, string? musicalKey = null)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var createResponse = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		var song = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		var songId = Guid.Parse(song.GetProperty("id").GetString()!);
		var arrangement = song.GetProperty("arrangements").EnumerateArray().Single();
		var arrangementId = Guid.Parse(arrangement.GetProperty("id").GetString()!);
		var version = arrangement.GetProperty("musicalVersions").EnumerateArray().Single();
		var versionId = Guid.Parse(version.GetProperty("id").GetString()!);
		if (versionLabel is not null || musicalKey is not null)
		{
			using var patch = AuthedPatch($"/api/musical-versions/{versionId}",
				new { label = versionLabel, musicalKey }, $"{cookie}; {editorSession}", token);
			using var patchResponse = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		}
		using var publishSong = AuthedPost($"/api/songs/{songId}/publish", new { },
			$"{cookie}; {editorSession}", token);
		using var publishResponse = await client.SendAsync(publishSong);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		return (songId, arrangementId, versionId);
	}

	private static async Task<Guid> CreateVersionAsync(
		HttpClient client, string editorSession, Guid arrangementId, string label, string? musicalKey = null)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label, musicalKey }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var song = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		var arrangement = song.GetProperty("arrangements").EnumerateArray()
			.Single(a => Guid.Parse(a.GetProperty("id").GetString()!) == arrangementId);
		var version = arrangement.GetProperty("musicalVersions").EnumerateArray()
			.Single(v => v.GetProperty("label").GetString() == label);
		return Guid.Parse(version.GetProperty("id").GetString()!);
	}

	private static async Task<HttpResponseMessage> PutJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		// A changed account re-issues the session cookie alongside the CSRF
		// cookie; send whatever the server handed out last.
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		tokenRequest.Headers.Add("Cookie", session);
		using var tokenResponse = await client.SendAsync(tokenRequest);
		var cookies = tokenResponse.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).ToList();
		var auth = cookies.LastOrDefault(c => c.StartsWith("archive.auth=", StringComparison.Ordinal)) ?? session;
		var csrf = cookies.Single(c => c.StartsWith("archive.csrf=", StringComparison.Ordinal));
		var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
		return await client.SendAsync(AuthedPut(path, body, $"{csrf}; {auth}", token));
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
	}

	private static async Task<HttpResponseMessage> PatchJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPatch(path, body, $"{cookie}; {session}", token));
	}

	private static HttpRequestMessage AuthedPut(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private static HttpRequestMessage AuthedPost(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private static HttpRequestMessage AuthedPatch(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Patch, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private static async Task<Guid> UserIdAsync(AuthApiFactory factory, string email)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		return (await users.FindByEmailAsync(email))!.Id;
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
}
