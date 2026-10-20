using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// Zeitmarken in whole recordings at the HTTP seam: an editor marks where a
/// Programm entry starts and ends inside a recording; the passage links
/// recording and entry ids without copying the entry, so a second recording
/// can mark the same entry and no history count moves. Members reach passages through the recording and the
/// lied history only while the recording, its event and the lied are
/// published; a passage whose timestamps were taken against another playback
/// file is flagged for editors and never offered to members as a jump.
/// </summary>
public sealed partial class RecordingPassageApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task EditorIndexesTwoLiederInOneVideoAndOneOfThemInASecondRecording()
	{
		await using var scenario = await Scenario.CreateAsync();
		var first = await scenario.CreateLiedAsync("Erstes Lied");
		var second = await scenario.CreateLiedAsync("Zweites Lied");
		var concert = await scenario.CreateEventAsync("Adventkonzert", 1988, 12, 4);
		var firstEntry = await scenario.RecordAsync(concert, first);
		var secondEntry = await scenario.RecordAsync(concert, second);
		var video = await scenario.CreateRecordingAsync(concert, "Video Kamera 1", "video", duration: 5400);
		var audio = await scenario.CreateRecordingAsync(concert, "Tonmitschnitt", "audio", duration: 5300);

		var historyBefore = await scenario.HistoryAsync(first, scenario.MemberSession);
		var entriesBefore = await scenario.ProgrammeItemCountAsync();

		// The write answer carries the complete display chain, not a bare row.
		var one = await scenario.AddPassageAsync(video, firstEntry, 12.5, 301.25);
		Assert.Equal(firstEntry, one.GetProperty("programmeItemId").GetString());
		Assert.Equal(video.ToString(), one.GetProperty("recordingId").GetString());
		Assert.Equal(first.ToString(), one.GetProperty("liedId").GetString());
		Assert.Equal("Erstes Lied", one.GetProperty("liedTitle").GetString());
		Assert.False(one.TryGetProperty("evidenceStatus", out _));
		Assert.Equal(12.5, one.GetProperty("startSeconds").GetDouble());
		Assert.Equal(301.25, one.GetProperty("endSeconds").GetDouble());
		Assert.Equal("current", one.GetProperty("timestampState").GetString());
		Assert.True(one.GetProperty("editor").GetProperty("version").GetUInt32() > 0);
		var two = await scenario.AddPassageAsync(video, secondEntry, 320, 700);
		Assert.Equal("Zweites Lied", two.GetProperty("liedTitle").GetString());
		// The same entry documented by a second recording.
		var other = await scenario.AddPassageAsync(audio, firstEntry, 5, 290);
		Assert.NotEqual(one.GetProperty("id").GetString(), other.GetProperty("id").GetString());

		// Unpublished recordings stay editor-only; publish both.
		using (var hidden = await scenario.GetAsync($"/api/recordings/{video}/passages", scenario.MemberSession))
		{
			Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
			Assert.Equal("Aufnahme nicht gefunden.", await TitleAsync(hidden));
		}
		await scenario.PublishRecordingAsync(video);
		await scenario.PublishRecordingAsync(audio);

		// The member reads both Lieder of the video in programme order and the
		// timestamps, but no editor data and no candidates.
		var memberList = await scenario.PassagesAsync(video, scenario.MemberSession);
		Assert.Equal(new[] { "Erstes Lied", "Zweites Lied" },
			memberList.GetProperty("passages").EnumerateArray().Select(p => p.GetProperty("liedTitle").GetString()));
		var memberFirst = memberList.GetProperty("passages")[0];
		Assert.Equal(12.5, memberFirst.GetProperty("startSeconds").GetDouble());
		Assert.Equal(301.25, memberFirst.GetProperty("endSeconds").GetDouble());
		Assert.True(memberFirst.GetProperty("editor").ValueKind is JsonValueKind.Null);
		Assert.False(memberList.TryGetProperty("entries", out _));
		Assert.False(memberFirst.TryGetProperty("createdByAccountId", out _));

		// The Lied history lists the recordings per Auftritt row.
		var history = await scenario.HistoryAsync(first, scenario.MemberSession);
		var row = Assert.Single(history.GetProperty("auftritte").EnumerateArray());
		var recordings = row.GetProperty("recordings").EnumerateArray().ToList();
		Assert.Equal(2, recordings.Count);
		var videoLink = recordings.Single(r => r.GetProperty("recordingId").GetString() == video.ToString());
		Assert.Equal("Video Kamera 1", videoLink.GetProperty("recordingLabel").GetString());
		Assert.Equal("video", videoLink.GetProperty("kind").GetString());
		Assert.Equal(one.GetProperty("id").GetString(), videoLink.GetProperty("passageId").GetString());
		Assert.Equal(12.5, videoLink.GetProperty("startSeconds").GetDouble());
		Assert.Equal(301.25, videoLink.GetProperty("endSeconds").GetDouble());
		Assert.Equal("current", videoLink.GetProperty("timestampState").GetString());
		var liedTwoRow = Assert.Single((await scenario.HistoryAsync(second, scenario.MemberSession))
			.GetProperty("auftritte").EnumerateArray());
		Assert.Single(liedTwoRow.GetProperty("recordings").EnumerateArray());

		// A second recording of the same entry adds no entry and no
		// count: the history and the Programm are unchanged.
		Assert.Equal(entriesBefore, await scenario.ProgrammeItemCountAsync());
		var historyAfter = await scenario.HistoryAsync(first, scenario.MemberSession);
		Assert.Equal(historyBefore.GetProperty("counts").GetRawText(), historyAfter.GetProperty("counts").GetRawText());
		Assert.Equal(historyBefore.GetProperty("total").GetInt32(), historyAfter.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task WholeRecordingPlaysAndUnmarkedLiederStayUnmarked()
	{
		await using var scenario = await Scenario.CreateAsync();
		var marked = await scenario.CreateLiedAsync("Markiertes Lied");
		var unmarked = await scenario.CreateLiedAsync("Unmarkiertes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1990, 5, 1);
		var markedEntry = await scenario.RecordAsync(concert, marked);
		await scenario.RecordAsync(concert, unmarked);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 4000);
		await scenario.PublishRecordingAsync(recording);

		// Before any marking the whole recording is listed and playable.
		var empty = await scenario.PassagesAsync(recording, scenario.MemberSession);
		Assert.Empty(empty.GetProperty("passages").EnumerateArray());
		var access = await scenario.AccessAsync(recording, scenario.MemberSession);
		Assert.Equal("ready", access.GetProperty("playbackState").GetString());
		Assert.False(string.IsNullOrEmpty(access.GetProperty("viewUrl").GetString()));

		await scenario.AddPassageAsync(recording, markedEntry, 100, 400);

		// Marking one lied leaves the whole recording and the other lied's
		// history untouched: no recording is claimed, no invented link.
		access = await scenario.AccessAsync(recording, scenario.MemberSession);
		Assert.Equal("ready", access.GetProperty("playbackState").GetString());
		var markedHistory = await scenario.HistoryAsync(marked, scenario.MemberSession);
		Assert.Single(markedHistory.GetProperty("auftritte")[0].GetProperty("recordings").EnumerateArray());
		var unmarkedHistory = await scenario.HistoryAsync(unmarked, scenario.MemberSession);
		Assert.Empty(unmarkedHistory.GetProperty("auftritte")[0].GetProperty("recordings").EnumerateArray());
		var list = await scenario.PassagesAsync(recording, scenario.MemberSession);
		Assert.Single(list.GetProperty("passages").EnumerateArray());
	}

	[Fact]
	public async Task EditorSeesTheProgrammeEntriesInOrderAsTheMarkerList()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateLiedAsync("Anfang");
		var b = await scenario.CreateLiedAsync("Mitte");
		var concert = await scenario.CreateEventAsync("Konzert", 1991, 6, 2);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "audio", duration: 3000);

		// Without a Programm there is nothing to mark: the list says so.
		var before = await scenario.PassagesAsync(recording, scenario.EditorSession);
		Assert.Empty(before.GetProperty("entries").EnumerateArray());

		// Saving the Programm is enough, there is no confirmation step.
		await scenario.SaveProgrammeAsync(concert, [a, b]);
		var after = await scenario.PassagesAsync(recording, scenario.EditorSession);
		var entries = after.GetProperty("entries").EnumerateArray().ToList();
		Assert.Equal(new[] { "Anfang", "Mitte" }, entries.Select(o => o.GetProperty("liedTitle").GetString()));
		Assert.All(entries, o => Assert.True(o.GetProperty("passageId").ValueKind is JsonValueKind.Null));
		Assert.Equal(new[] { 1, 2 }, entries.Select(o => o.GetProperty("position").GetInt32()));

		var firstId = entries[0].GetProperty("programmeItemId").GetString()!;
		var added = await scenario.AddPassageAsync(recording, firstId, 10, 200);
		var marked = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("entries")[0];
		Assert.Equal(added.GetProperty("id").GetString(), marked.GetProperty("passageId").GetString());
	}

	[Fact]
	public async Task BoundsEventOwnershipAndDuplicatesAreValidated()
	{
		await using var scenario = await Scenario.CreateAsync();
		var lied = await scenario.CreateLiedAsync("Prüflied");
		var concert = await scenario.CreateEventAsync("Konzert", 1992, 3, 3);
		var elsewhere = await scenario.CreateEventAsync("Anderer Auftritt", 1993, 3, 3);
		var entry = await scenario.RecordAsync(concert, lied);
		var foreign = await scenario.RecordAsync(elsewhere, lied);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 1000);
		var path = $"/api/recordings/{recording}/passages";
		var rev = await scenario.PlaybackDateiAsync(recording);

		async Task AssertRefusedAsync(object body, HttpStatusCode status, string message)
		{
			using var response = await scenario.SendAsync(HttpMethod.Post, path, body, scenario.EditorSession);
			Assert.Equal(status, response.StatusCode);
			Assert.Equal(message, await TitleAsync(response));
		}

		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = -1, endSeconds = 50 },
			HttpStatusCode.BadRequest, "Der Anfang ist ungültig.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, endSeconds = 50 },
			HttpStatusCode.BadRequest, "Der Anfang ist ungültig.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 10 },
			HttpStatusCode.BadRequest, "Das Ende ist ungültig.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 100, endSeconds = 100 },
			HttpStatusCode.BadRequest, "Das Ende muss nach dem Anfang liegen.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 100, endSeconds = 50 },
			HttpStatusCode.BadRequest, "Das Ende muss nach dem Anfang liegen.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 100, endSeconds = 1500 },
			HttpStatusCode.BadRequest, "Das Ende liegt hinter dem Ende der Aufnahme.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 100, endSeconds = 200000 },
			HttpStatusCode.BadRequest, "Das Ende liegt hinter dem Ende der Aufnahme.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, startSeconds = 1, endSeconds = 50 },
			HttpStatusCode.NotFound, "Der Programmpunkt gehört nicht zum Programm dieses Auftritts.");
		// The Programm entry of another event is not this recording's to mark.
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = foreign, startSeconds = 1, endSeconds = 50 },
			HttpStatusCode.NotFound, "Der Programmpunkt gehört nicht zum Programm dieses Auftritts.");
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = Guid.NewGuid(), startSeconds = 1, endSeconds = 50 },
			HttpStatusCode.NotFound, "Der Programmpunkt gehört nicht zum Programm dieses Auftritts.");
		Assert.Empty((await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages").EnumerateArray());

		// A passage up to the very end of the recording is fine.
		var created = await scenario.AddPassageAsync(recording, entry, 900, 1000);
		// A lost retry of the identical request answers the stored passage.
		using (var retry = await scenario.SendAsync(HttpMethod.Post, path,
			new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 900, endSeconds = 1000 }, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
			Assert.Equal(created.GetProperty("id").GetString(),
				(await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage").GetProperty("id").GetString());
		}
		// A different statement for a marked entry is "not allowed in
		// this state" (edit the existing one), not a stale conflict.
		await AssertRefusedAsync(new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 10, endSeconds = 20 },
			HttpStatusCode.Conflict, "Für diesen Programmpunkt gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.");
		Assert.Single((await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages").EnumerateArray());

		// A recording without a playable file cannot carry timestamps.
		var empty = await scenario.CreateRecordingAsync(concert, "Ohne Datei", "video", upload: false);
		using var noFile = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{empty}/passages",
			new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 1, endSeconds = 5 }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, noFile.StatusCode);
		Assert.Equal("Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.",
			await TitleAsync(noFile));

		// Unknown recording.
		using var unknown = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{Guid.NewGuid()}/passages",
			new { expectedPlaybackDateiId = rev, programmeItemId = entry, startSeconds = 1, endSeconds = 5 }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		Assert.Equal("Aufnahme nicht gefunden.", await TitleAsync(unknown));
	}

	[Fact]
	public async Task EditingMovesTimesWithAStaleVersionRefusedAndNoOpLeftAlone()
	{
		await using var scenario = await Scenario.CreateAsync();
		var lied = await scenario.CreateLiedAsync("Wandellied");
		var other = await scenario.CreateLiedAsync("Anderes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1994, 3, 3);
		var entry = await scenario.RecordAsync(concert, lied);
		var otherEntry = await scenario.RecordAsync(concert, other);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var otherRecording = await scenario.CreateRecordingAsync(concert, "Zweite", "video", duration: 2000);
		var passage = await scenario.AddPassageAsync(recording, entry, 100, 200);
		var passageId = passage.GetProperty("id").GetString();
		var version = passage.GetProperty("editor").GetProperty("version").GetUInt32();
		var path = $"/api/recordings/{recording}/passages/{passageId}";
		var rev = await scenario.PlaybackDateiAsync(recording);

		// A move answers the complete passage with display fields and a new version.
		using var moved = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackDateiId = rev, startSeconds = 110.5, endSeconds = 215, expectedVersion = version }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
		var movedPassage = (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage");
		Assert.Equal(110.5, movedPassage.GetProperty("startSeconds").GetDouble());
		Assert.Equal(215, movedPassage.GetProperty("endSeconds").GetDouble());
		Assert.Equal("Wandellied", movedPassage.GetProperty("liedTitle").GetString());
		var newVersion = movedPassage.GetProperty("editor").GetProperty("version").GetUInt32();
		Assert.NotEqual(version, newVersion);

		// A change that changes nothing leaves the version alone.
		using var same = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackDateiId = rev, startSeconds = 110.5, endSeconds = 215, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, same.StatusCode);
		Assert.Equal(newVersion, (await same.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("passage").GetProperty("editor").GetProperty("version").GetUInt32());

		// A stale form answers the stale message; invalid bounds keep their own.
		using var stale = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackDateiId = rev, startSeconds = 50, expectedVersion = version }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal("Die Zeitmarke wurde zwischenzeitlich geändert.", await TitleAsync(stale));
		using var inverted = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackDateiId = rev, startSeconds = 500, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
		Assert.Equal("Das Ende muss nach dem Anfang liegen.", await TitleAsync(inverted));
		using var beyond = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackDateiId = rev, endSeconds = 2500, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal("Das Ende liegt hinter dem Ende der Aufnahme.", await TitleAsync(beyond));

		// The passage's ids belong to its stated recording: the same passage
		// under another recording, or another recording's id, is unknown.
		using var wrongRecording = await scenario.SendAsync(HttpMethod.Patch,
			$"/api/recordings/{otherRecording}/passages/{passageId}",
			new { expectedPlaybackDateiId = rev, startSeconds = 1, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, wrongRecording.StatusCode);
		Assert.Equal("Zeitmarke nicht gefunden.", await TitleAsync(wrongRecording));
		using var wrongDelete = await scenario.SendAsync(HttpMethod.Post,
			$"/api/recordings/{otherRecording}/passages/{passageId}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, wrongDelete.StatusCode);
		// A patch cannot move the passage to another entry.
		using var retarget = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackDateiId = rev, programmeItemId = otherEntry, startSeconds = 111, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, retarget.StatusCode);
		Assert.Equal(entry, (await retarget.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("passage").GetProperty("programmeItemId").GetString());

		// Delete: stale refused, then removed; the entry itself stays.
		var current = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages")[0].GetProperty("editor").GetProperty("version").GetUInt32();
		using var staleDelete = await scenario.SendAsync(HttpMethod.Post, $"{path}/delete",
			new { expectedVersion = version }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
		Assert.Equal("Die Zeitmarke wurde zwischenzeitlich geändert.", await TitleAsync(staleDelete));
		using var deleted = await scenario.SendAsync(HttpMethod.Post, $"{path}/delete",
			new { expectedVersion = current }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
		Assert.Empty((await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages").EnumerateArray());
		Assert.Equal(2, await scenario.ProgrammeItemCountAsync());
		// Deleting again is a plain unknown passage.
		using var again = await scenario.SendAsync(HttpMethod.Post, $"{path}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
	}

	[Fact]
	public async Task AuthorizationHoldsOnEveryPath()
	{
		await using var scenario = await Scenario.CreateAsync();
		var lied = await scenario.CreateLiedAsync("Geschütztes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1995, 3, 3);
		var entry = await scenario.RecordAsync(concert, lied);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var passage = await scenario.AddPassageAsync(recording, entry, 10, 100);
		var passageId = passage.GetProperty("id").GetString();
		var body = new { programmeItemId = entry, startSeconds = 1, endSeconds = 5 };
		var path = $"/api/recordings/{recording}/passages";

		// Anonymous.
		using (var anonymous = await scenario.GetAsync(path, null))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		using (var anonymousWrite = await scenario.SendAsync(HttpMethod.Post, path, body, null))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymousWrite.StatusCode);

		// Members read, but never write.
		using (var read = await scenario.GetAsync(path, scenario.MemberSession))
			Assert.Equal(HttpStatusCode.OK, read.StatusCode);
		foreach (var (method, route, payload) in new (HttpMethod, string, object)[]
		{
			(HttpMethod.Post, path, body),
			(HttpMethod.Patch, $"{path}/{passageId}", new { startSeconds = 2, expectedVersion = 1 }),
			(HttpMethod.Post, $"{path}/{passageId}/delete", new { }),
			(HttpMethod.Post, $"{path}/review", new { }),
		})
		{
			using var denied = await scenario.SendAsync(method, route, payload, scenario.MemberSession);
			Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
			Assert.Equal("Keine Berechtigung für die Zeitmarken.", await TitleAsync(denied));
		}

		// Mutations need the antiforgery token.
		using (var request = new HttpRequestMessage(HttpMethod.Post, path))
		{
			request.Headers.Add("Cookie", scenario.EditorSession);
			request.Content = JsonContent.Create(body);
			using var noToken = await scenario.Client.SendAsync(request);
			Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
		}

		// A downgraded editor loses write access at once, a revoked one the session.
		await scenario.SetRoleAsync(Editor, ArchiveRoles.Member);
		using (var downgraded = await scenario.SendAsync(HttpMethod.Post, path, body, scenario.EditorSession))
			Assert.Equal(HttpStatusCode.Forbidden, downgraded.StatusCode);
		await scenario.DeactivateAsync(Editor);
		using (var revoked = await scenario.SendAsync(HttpMethod.Post, path, body, scenario.EditorSession))
			Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
	}

	[Fact]
	public async Task UnpublishedRecordingsNeverReachMembers()
	{
		await using var scenario = await Scenario.CreateAsync();
		var visible = await scenario.CreateLiedAsync("Sichtbares Lied");
		var other = await scenario.CreateLiedAsync("Anderes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1996, 3, 3);
		var visibleEntry = await scenario.RecordAsync(concert, visible);
		var otherEntry = await scenario.RecordAsync(concert, other);
		var published = await scenario.CreateRecordingAsync(concert, "Veröffentlicht", "video", duration: 2000);
		var draft = await scenario.CreateRecordingAsync(concert, "Entwurf", "video", duration: 2000);
		await scenario.PublishRecordingAsync(published);
		await scenario.AddPassageAsync(published, visibleEntry, 10, 100);
		await scenario.AddPassageAsync(published, otherEntry, 200, 300);
		await scenario.AddPassageAsync(draft, visibleEntry, 20, 120);

		// Members: only the published recording.
		var memberList = await scenario.PassagesAsync(published, scenario.MemberSession);
		Assert.Equal(new[] { "Sichtbares Lied", "Anderes Lied" },
			memberList.GetProperty("passages").EnumerateArray().Select(p => p.GetProperty("liedTitle").GetString()));
		using (var draftList = await scenario.GetAsync($"/api/recordings/{draft}/passages", scenario.MemberSession))
			Assert.Equal(HttpStatusCode.NotFound, draftList.StatusCode);
		var memberRow = Assert.Single((await scenario.HistoryAsync(visible, scenario.MemberSession))
			.GetProperty("auftritte").EnumerateArray());
		var memberLinks = memberRow.GetProperty("recordings").EnumerateArray().ToList();
		Assert.Equal(new[] { "Veröffentlicht" }, memberLinks.Select(r => r.GetProperty("recordingLabel").GetString()));

		// Editors see the draft recording too, flagged.
		var editorLinks = (await scenario.HistoryAsync(visible, scenario.EditorSession))
			.GetProperty("auftritte")[0].GetProperty("recordings").EnumerateArray().ToList();
		Assert.Equal(2, editorLinks.Count);
		Assert.False(editorLinks.Single(r => r.GetProperty("recordingLabel").GetString() == "Entwurf")
			.GetProperty("isPublished").GetBoolean());
		Assert.Equal(2, (await scenario.PassagesAsync(published, scenario.EditorSession))
			.GetProperty("passages").GetArrayLength());

		// Withdrawing the recording takes its Zeitmarken away again.
		await scenario.PatchRecordingAsync(published, new { isPublished = false });
		using (var withdrawn = await scenario.GetAsync($"/api/recordings/{published}/passages", scenario.MemberSession))
			Assert.Equal(HttpStatusCode.NotFound, withdrawn.StatusCode);
		Assert.Empty((await scenario.HistoryAsync(visible, scenario.MemberSession))
			.GetProperty("auftritte")[0].GetProperty("recordings").EnumerateArray());
		await scenario.PatchRecordingAsync(published, new { isPublished = true });
		Assert.Single((await scenario.HistoryAsync(visible, scenario.MemberSession))
			.GetProperty("auftritte")[0].GetProperty("recordings").EnumerateArray());
	}

	[Fact]
	public async Task ChangedPlaybackFileFlagsPassagesForReviewUntilAnEditorConfirmsThem()
	{
		await using var scenario = await Scenario.CreateAsync();
		var lied = await scenario.CreateLiedAsync("Wanderlied");
		var concert = await scenario.CreateEventAsync("Konzert", 1997, 3, 3);
		var entry = await scenario.RecordAsync(concert, lied);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var passage = await scenario.AddPassageAsync(recording, entry, 100, 300);
		var passageId = passage.GetProperty("id").GetString();
		var revisionBefore = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("playbackDateiId").GetString();
		Assert.Equal((await scenario.AccessAsync(recording, scenario.MemberSession)).GetProperty("dateiId").GetString(),
			revisionBefore);

		// A converted copy replaces what members play: the timestamps were
		// taken against the old file.
		await scenario.ReplacePlaybackFileAsync(recording);
		var editorView = await scenario.PassagesAsync(recording, scenario.EditorSession);
		Assert.NotEqual(revisionBefore, editorView.GetProperty("playbackDateiId").GetString());
		var flagged = editorView.GetProperty("passages")[0];
		Assert.Equal("needsReview", flagged.GetProperty("timestampState").GetString());
		Assert.Equal(100, flagged.GetProperty("startSeconds").GetDouble());
		Assert.Equal(revisionBefore, flagged.GetProperty("editor").GetProperty("playbackDateiId").GetString());

		// Members get the lied as part of the recording, but no position.
		var memberView = await scenario.PassagesAsync(recording, scenario.MemberSession);
		var memberPassage = memberView.GetProperty("passages")[0];
		Assert.Equal("Wanderlied", memberPassage.GetProperty("liedTitle").GetString());
		Assert.Equal("needsReview", memberPassage.GetProperty("timestampState").GetString());
		Assert.True(memberPassage.GetProperty("startSeconds").ValueKind is JsonValueKind.Null);
		Assert.True(memberPassage.GetProperty("endSeconds").ValueKind is JsonValueKind.Null);
		var memberLink = (await scenario.HistoryAsync(lied, scenario.MemberSession))
			.GetProperty("auftritte")[0].GetProperty("recordings")[0];
		Assert.Equal("needsReview", memberLink.GetProperty("timestampState").GetString());
		Assert.True(memberLink.GetProperty("startSeconds").ValueKind is JsonValueKind.Null);
		// The editor's history row names the state and keeps the values.
		var editorLink = (await scenario.HistoryAsync(lied, scenario.EditorSession))
			.GetProperty("auftritte")[0].GetProperty("recordings")[0];
		Assert.Equal("needsReview", editorLink.GetProperty("timestampState").GetString());
		Assert.Equal(100, editorLink.GetProperty("startSeconds").GetDouble());

		// Editing against the new file is a new statement: it takes the new
		// revision, and both bounds are stated because neither was verified.
		var version = flagged.GetProperty("editor").GetProperty("version").GetUInt32();
		var current = editorView.GetProperty("playbackDateiId").GetString();
		using (var oneBound = await scenario.SendAsync(HttpMethod.Patch,
			$"/api/recordings/{recording}/passages/{passageId}",
			new { startSeconds = 90, expectedVersion = version, expectedPlaybackDateiId = current }, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.BadRequest, oneBound.StatusCode);
			Assert.Equal("Zum Prüfen einer Zeitmarke bitte Anfang und Ende angeben.", await TitleAsync(oneBound));
		}
		using var edited = await scenario.SendAsync(HttpMethod.Patch, $"/api/recordings/{recording}/passages/{passageId}",
			new { startSeconds = 90, endSeconds = 310, expectedVersion = version, expectedPlaybackDateiId = current },
			scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
		var reanchored = (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage");
		Assert.Equal("current", reanchored.GetProperty("timestampState").GetString());
		Assert.Equal(current, reanchored.GetProperty("editor").GetProperty("playbackDateiId").GetString());
		Assert.Equal(90, reanchored.GetProperty("startSeconds").GetDouble());
		Assert.Equal(310, reanchored.GetProperty("endSeconds").GetDouble());
	}

	[Fact]
	public async Task EditorConfirmsAllFlaggedPassagesInOneStep()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateLiedAsync("Eins");
		var b = await scenario.CreateLiedAsync("Zwei");
		var concert = await scenario.CreateEventAsync("Konzert", 1998, 3, 3);
		var pa = await scenario.RecordAsync(concert, a);
		var pb = await scenario.RecordAsync(concert, b);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		await scenario.AddPassageAsync(recording, pa, 10, 100);
		await scenario.AddPassageAsync(recording, pb, 200, 400);
		await scenario.ReplacePlaybackFileAsync(recording);

		var view = await scenario.PassagesAsync(recording, scenario.EditorSession);
		var currentRevisionId = view.GetProperty("playbackDateiId").GetString();
		var listed = view.GetProperty("passages").EnumerateArray()
			.Select(p => new { id = p.GetProperty("id").GetString(), expectedVersion = p.GetProperty("editor").GetProperty("version").GetUInt32() })
			.ToArray();
		using var review = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{recording}/passages/review",
			new { expectedPlaybackDateiId = currentRevisionId, passages = listed }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, review.StatusCode);
		var answer = await review.Content.ReadFromJsonAsync<JsonElement>();
		var currentRevision = answer.GetProperty("playbackDateiId").GetString();
		var passages = answer.GetProperty("passages").EnumerateArray().ToList();
		Assert.Equal(2, passages.Count);
		Assert.All(passages, p =>
		{
			Assert.Equal("current", p.GetProperty("timestampState").GetString());
			Assert.Equal(currentRevision, p.GetProperty("editor").GetProperty("playbackDateiId").GetString());
			Assert.False(string.IsNullOrEmpty(p.GetProperty("liedTitle").GetString()));
		});
		// The values were confirmed as they are, and members get the jump again.
		Assert.Equal(new[] { 10d, 200d }, passages.Select(p => p.GetProperty("startSeconds").GetDouble()));
		var member = (await scenario.PassagesAsync(recording, scenario.MemberSession)).GetProperty("passages")[0];
		Assert.Equal("current", member.GetProperty("timestampState").GetString());
		Assert.Equal(10, member.GetProperty("startSeconds").GetDouble());

		// Confirming again changes nothing (versions stay).
		var versions = passages.Select(p => p.GetProperty("editor").GetProperty("version").GetUInt32()).ToList();
		var confirmed = passages.Select(p => new { id = p.GetProperty("id").GetString(), expectedVersion = p.GetProperty("editor").GetProperty("version").GetUInt32() }).ToArray();
		using var again = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{recording}/passages/review",
			new { expectedPlaybackDateiId = currentRevisionId, passages = confirmed }, scenario.EditorSession);
		var repeated = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passages")
			.EnumerateArray().Select(p => p.GetProperty("editor").GetProperty("version").GetUInt32()).ToList();
		Assert.Equal(versions, repeated);
	}

	[Fact]
	public async Task ProgrammeEntryWithZeitmarkenCannotBeRemovedBehindTheirBack()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateLiedAsync("Gesungen");
		var b = await scenario.CreateLiedAsync("Ausgelassen");
		var concert = await scenario.CreateEventAsync("Konzert", 1999, 3, 3);
		var entryA = await scenario.RecordAsync(concert, a);
		var entryB = await scenario.RecordAsync(concert, b);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var passage = await scenario.AddPassageAsync(recording, entryB, 400, 800);

		async Task<HttpResponseMessage> SaveOnlyAAsync() => await scenario.SendAsync(HttpMethod.Put,
			$"/api/events/{concert}/programme/items", new
			{
				items = new object[] { new { id = entryA, liedId = a } },
				rowVersion = await ProgrammeRowVersionAsync(scenario, concert),
			}, scenario.EditorSession);

		// Dropping the marked entry is refused with a reason that names the
		// recording; the Programm stays as it was.
		using var refused = await SaveOnlyAAsync();
		Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
		var message = await TitleAsync(refused);
		Assert.StartsWith("Zeitmarken vorhanden:", message);
		Assert.Contains("„Ausgelassen“", message);
		Assert.Contains("„Mitschnitt“", message);
		Assert.Equal(2, await scenario.ProgrammeItemCountAsync());

		// Reordering keeps the entry and its mark.
		using var reorder = await scenario.SendAsync(HttpMethod.Put, $"/api/events/{concert}/programme/items", new
		{
			items = new object[] { new { id = entryB, liedId = b }, new { id = entryA, liedId = a } },
			rowVersion = await ProgrammeRowVersionAsync(scenario, concert),
		}, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
		Assert.Equal(passage.GetProperty("id").GetString(), (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("entries")[0].GetProperty("passageId").GetString());

		// Removing the mark first lets the removal proceed.
		using var removed = await scenario.SendAsync(HttpMethod.Post,
			$"/api/recordings/{recording}/passages/{passage.GetProperty("id").GetString()}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
		using var now = await SaveOnlyAAsync();
		Assert.Equal(HttpStatusCode.OK, now.StatusCode);
		Assert.Equal(1, await scenario.ProgrammeItemCountAsync());
	}

	private static async Task<uint> ProgrammeRowVersionAsync(Scenario scenario, Guid eventId)
	{
		using var detail = await scenario.GetAsync($"/api/events/{eventId}", scenario.EditorSession);
		return (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("event")
			.GetProperty("programme").GetProperty("rowVersion").GetUInt32();
	}

	private static async Task<string?> TitleAsync(HttpResponseMessage response)
	{
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
	}
}
