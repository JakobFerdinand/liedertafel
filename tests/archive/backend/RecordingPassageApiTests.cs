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
/// ARC-032 passages in whole recordings at the HTTP seam: an editor marks
/// where a performance occurrence starts and ends inside a recording; the
/// passage links recording and performance ids without copying the
/// performance, so a second recording can document the same occurrence and no
/// history count moves. Members reach passages through the recording and the
/// song history only while the recording, its event and the song are
/// published; a passage whose timestamps were taken against another playback
/// file is flagged for editors and never offered to members as a jump.
/// </summary>
public sealed partial class RecordingPassageApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task EditorIndexesTwoSongsInOneVideoAndOneOfThemInASecondRecording()
	{
		await using var scenario = await Scenario.CreateAsync();
		var first = await scenario.CreateSongAsync("Erstes Lied", "Grundtonart");
		var second = await scenario.CreateSongAsync("Zweites Lied");
		var concert = await scenario.CreateEventAsync("Adventkonzert", 1988, 12, 4, published: true);
		var firstPerformance = await scenario.RecordAsync(concert, first.SongId, versionId: first.VersionId);
		var secondPerformance = await scenario.RecordAsync(concert, second.SongId);
		var video = await scenario.CreateRecordingAsync(concert, "Video Kamera 1", "video", duration: 5400);
		var audio = await scenario.CreateRecordingAsync(concert, "Tonmitschnitt", "audio", duration: 5300);

		var historyBefore = await scenario.HistoryAsync(first.SongId, scenario.MemberSession);
		var occurrencesBefore = await scenario.PerformanceCountAsync();

		// The write answer carries the complete display chain, not a bare row.
		var one = await scenario.AddPassageAsync(video, firstPerformance, 12.5, 301.25);
		Assert.Equal(firstPerformance, one.GetProperty("performanceId").GetString());
		Assert.Equal(video.ToString(), one.GetProperty("recordingId").GetString());
		Assert.Equal(first.SongId.ToString(), one.GetProperty("songId").GetString());
		Assert.Equal("Erstes Lied", one.GetProperty("songTitle").GetString());
		Assert.Equal("Grundtonart", one.GetProperty("musicalVersion").GetProperty("label").GetString());
		Assert.Equal(first.ArrangementId.ToString(), one.GetProperty("arrangement").GetProperty("id").GetString());
		Assert.Equal("confirmed", one.GetProperty("evidenceStatus").GetString());
		Assert.Equal(12.5, one.GetProperty("startSeconds").GetDouble());
		Assert.Equal(301.25, one.GetProperty("endSeconds").GetDouble());
		Assert.Equal("current", one.GetProperty("timestampState").GetString());
		Assert.True(one.GetProperty("editor").GetProperty("version").GetUInt32() > 0);
		var two = await scenario.AddPassageAsync(video, secondPerformance, 320, 700);
		Assert.Equal("Zweites Lied", two.GetProperty("songTitle").GetString());
		Assert.True(two.GetProperty("musicalVersion").ValueKind is JsonValueKind.Null);
		// The same occurrence documented by a second recording.
		var other = await scenario.AddPassageAsync(audio, firstPerformance, 5, 290);
		Assert.NotEqual(one.GetProperty("id").GetString(), other.GetProperty("id").GetString());

		// Unpublished recordings stay editor-only; publish both.
		using (var hidden = await scenario.GetAsync($"/api/recordings/{video}/passages", scenario.MemberSession))
		{
			Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
			Assert.Equal("Aufnahme nicht gefunden.", await TitleAsync(hidden));
		}
		await scenario.PublishRecordingAsync(video);
		await scenario.PublishRecordingAsync(audio);

		// The member reads both songs of the video in programme order and the
		// timestamps, but no editor data and no candidates.
		var memberList = await scenario.PassagesAsync(video, scenario.MemberSession);
		Assert.Equal(new[] { "Erstes Lied", "Zweites Lied" },
			memberList.GetProperty("passages").EnumerateArray().Select(p => p.GetProperty("songTitle").GetString()));
		var memberFirst = memberList.GetProperty("passages")[0];
		Assert.Equal(12.5, memberFirst.GetProperty("startSeconds").GetDouble());
		Assert.Equal(301.25, memberFirst.GetProperty("endSeconds").GetDouble());
		Assert.True(memberFirst.GetProperty("editor").ValueKind is JsonValueKind.Null);
		Assert.False(memberList.TryGetProperty("occurrences", out _));
		Assert.False(memberFirst.TryGetProperty("createdByAccountId", out _));

		// The song history lists the recordings per occurrence row.
		var history = await scenario.HistoryAsync(first.SongId, scenario.MemberSession);
		var row = Assert.Single(history.GetProperty("performances").EnumerateArray());
		var recordings = row.GetProperty("recordings").EnumerateArray().ToList();
		Assert.Equal(2, recordings.Count);
		var videoLink = recordings.Single(r => r.GetProperty("recordingId").GetString() == video.ToString());
		Assert.Equal("Video Kamera 1", videoLink.GetProperty("recordingLabel").GetString());
		Assert.Equal("video", videoLink.GetProperty("kind").GetString());
		Assert.Equal(one.GetProperty("id").GetString(), videoLink.GetProperty("passageId").GetString());
		Assert.Equal(12.5, videoLink.GetProperty("startSeconds").GetDouble());
		Assert.Equal(301.25, videoLink.GetProperty("endSeconds").GetDouble());
		Assert.Equal("current", videoLink.GetProperty("timestampState").GetString());
		var songTwoRow = Assert.Single((await scenario.HistoryAsync(second.SongId, scenario.MemberSession))
			.GetProperty("performances").EnumerateArray());
		Assert.Single(songTwoRow.GetProperty("recordings").EnumerateArray());

		// A second recording of the same occurrence adds no occurrence and no
		// count: the history and the performance table are unchanged.
		Assert.Equal(occurrencesBefore, await scenario.PerformanceCountAsync());
		var historyAfter = await scenario.HistoryAsync(first.SongId, scenario.MemberSession);
		Assert.Equal(historyBefore.GetProperty("counts").GetRawText(), historyAfter.GetProperty("counts").GetRawText());
		Assert.Equal(historyBefore.GetProperty("total").GetInt32(), historyAfter.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task WholeRecordingPlaysAndUnmarkedSongsStayUnmarked()
	{
		await using var scenario = await Scenario.CreateAsync();
		var marked = await scenario.CreateSongAsync("Markiertes Lied");
		var unmarked = await scenario.CreateSongAsync("Unmarkiertes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1990, 5, 1, published: true);
		var markedPerformance = await scenario.RecordAsync(concert, marked.SongId);
		await scenario.RecordAsync(concert, unmarked.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 4000);
		await scenario.PublishRecordingAsync(recording);

		// Before any marking the whole recording is listed and playable.
		var empty = await scenario.PassagesAsync(recording, scenario.MemberSession);
		Assert.Empty(empty.GetProperty("passages").EnumerateArray());
		var access = await scenario.AccessAsync(recording, scenario.MemberSession);
		Assert.Equal("ready", access.GetProperty("playbackState").GetString());
		Assert.False(string.IsNullOrEmpty(access.GetProperty("viewUrl").GetString()));

		await scenario.AddPassageAsync(recording, markedPerformance, 100, 400);

		// Marking one song leaves the whole recording and the other song's
		// history untouched: no recording is claimed, no invented link.
		access = await scenario.AccessAsync(recording, scenario.MemberSession);
		Assert.Equal("ready", access.GetProperty("playbackState").GetString());
		var markedHistory = await scenario.HistoryAsync(marked.SongId, scenario.MemberSession);
		Assert.Single(markedHistory.GetProperty("performances")[0].GetProperty("recordings").EnumerateArray());
		var unmarkedHistory = await scenario.HistoryAsync(unmarked.SongId, scenario.MemberSession);
		Assert.Empty(unmarkedHistory.GetProperty("performances")[0].GetProperty("recordings").EnumerateArray());
		var list = await scenario.PassagesAsync(recording, scenario.MemberSession);
		Assert.Single(list.GetProperty("passages").EnumerateArray());
	}

	[Fact]
	public async Task EditorSeesTheEventsOccurrencesInOrderAsTheMarkerList()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateSongAsync("Anfang");
		var b = await scenario.CreateSongAsync("Mitte");
		var concert = await scenario.CreateEventAsync("Konzert", 1991, 6, 2, published: true);
		await scenario.PublishProgrammeAsync(concert, [a, b]);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "audio", duration: 3000);

		// A published programme without confirmation has no occurrences to
		// mark yet: the list says so instead of inventing any.
		var before = await scenario.PassagesAsync(recording, scenario.EditorSession);
		Assert.Empty(before.GetProperty("occurrences").EnumerateArray());
		Assert.True(before.GetProperty("hasPublishedProgramme").GetBoolean());

		await scenario.ConfirmAsync(concert);
		var after = await scenario.PassagesAsync(recording, scenario.EditorSession);
		var occurrences = after.GetProperty("occurrences").EnumerateArray().ToList();
		Assert.Equal(new[] { "Anfang", "Mitte" }, occurrences.Select(o => o.GetProperty("songTitle").GetString()));
		Assert.All(occurrences, o => Assert.True(o.GetProperty("passageId").ValueKind is JsonValueKind.Null));
		Assert.Equal(new[] { 1, 2 }, occurrences.Select(o => o.GetProperty("position").GetInt32()));

		var firstId = occurrences[0].GetProperty("performanceId").GetString()!;
		var added = await scenario.AddPassageAsync(recording, firstId, 10, 200);
		var marked = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("occurrences")[0];
		Assert.Equal(added.GetProperty("id").GetString(), marked.GetProperty("passageId").GetString());
	}

	[Fact]
	public async Task BoundsEventOwnershipAndDuplicatesAreValidated()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Prüflied");
		var concert = await scenario.CreateEventAsync("Konzert", 1992, 3, 3, published: true);
		var elsewhere = await scenario.CreateEventAsync("Anderer Auftritt", 1993, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var foreign = await scenario.RecordAsync(elsewhere, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 1000);
		var path = $"/api/recordings/{recording}/passages";

		async Task AssertRefusedAsync(object body, HttpStatusCode status, string message)
		{
			using var response = await scenario.SendAsync(HttpMethod.Post, path, body, scenario.EditorSession);
			Assert.Equal(status, response.StatusCode);
			Assert.Equal(message, await TitleAsync(response));
		}

		await AssertRefusedAsync(new { performanceId = performance, startSeconds = -1, endSeconds = 50 },
			HttpStatusCode.BadRequest, "Der Anfang ist ungültig.");
		await AssertRefusedAsync(new { performanceId = performance, endSeconds = 50 },
			HttpStatusCode.BadRequest, "Der Anfang ist ungültig.");
		await AssertRefusedAsync(new { performanceId = performance, startSeconds = 10 },
			HttpStatusCode.BadRequest, "Das Ende ist ungültig.");
		await AssertRefusedAsync(new { performanceId = performance, startSeconds = 100, endSeconds = 100 },
			HttpStatusCode.BadRequest, "Das Ende muss nach dem Anfang liegen.");
		await AssertRefusedAsync(new { performanceId = performance, startSeconds = 100, endSeconds = 50 },
			HttpStatusCode.BadRequest, "Das Ende muss nach dem Anfang liegen.");
		await AssertRefusedAsync(new { performanceId = performance, startSeconds = 100, endSeconds = 1500 },
			HttpStatusCode.BadRequest, "Das Ende liegt hinter dem Ende der Aufnahme.");
		await AssertRefusedAsync(new { performanceId = performance, startSeconds = 100, endSeconds = 200000 },
			HttpStatusCode.BadRequest, "Das Ende liegt hinter dem Ende der Aufnahme.");
		await AssertRefusedAsync(new { startSeconds = 1, endSeconds = 50 },
			HttpStatusCode.NotFound, "Die Aufführung gehört nicht zu diesem Auftritt.");
		// The occurrence of another event is not this recording's to mark.
		await AssertRefusedAsync(new { performanceId = foreign, startSeconds = 1, endSeconds = 50 },
			HttpStatusCode.NotFound, "Die Aufführung gehört nicht zu diesem Auftritt.");
		await AssertRefusedAsync(new { performanceId = Guid.NewGuid(), startSeconds = 1, endSeconds = 50 },
			HttpStatusCode.NotFound, "Die Aufführung gehört nicht zu diesem Auftritt.");
		Assert.Empty((await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages").EnumerateArray());

		// A passage up to the very end of the recording is fine.
		var created = await scenario.AddPassageAsync(recording, performance, 900, 1000);
		// A lost retry of the identical request answers the stored passage.
		using (var retry = await scenario.SendAsync(HttpMethod.Post, path,
			new { performanceId = performance, startSeconds = 900, endSeconds = 1000 }, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
			Assert.Equal(created.GetProperty("id").GetString(),
				(await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage").GetProperty("id").GetString());
		}
		// A different statement for a marked occurrence is "not allowed in
		// this state" (edit the existing one), not a stale conflict.
		await AssertRefusedAsync(new { performanceId = performance, startSeconds = 10, endSeconds = 20 },
			HttpStatusCode.Conflict, "Für diese Aufführung gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.");
		Assert.Single((await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages").EnumerateArray());

		// A recording without a playable file cannot carry timestamps.
		var empty = await scenario.CreateRecordingAsync(concert, "Ohne Datei", "video", upload: false);
		using var noFile = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{empty}/passages",
			new { performanceId = performance, startSeconds = 1, endSeconds = 5 }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, noFile.StatusCode);
		Assert.Equal("Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.",
			await TitleAsync(noFile));

		// Unknown recording.
		using var unknown = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{Guid.NewGuid()}/passages",
			new { performanceId = performance, startSeconds = 1, endSeconds = 5 }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		Assert.Equal("Aufnahme nicht gefunden.", await TitleAsync(unknown));
	}

	[Fact]
	public async Task EditingMovesTimesWithAStaleVersionRefusedAndNoOpLeftAlone()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Wandellied");
		var other = await scenario.CreateSongAsync("Anderes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1994, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var otherPerformance = await scenario.RecordAsync(concert, other.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var otherRecording = await scenario.CreateRecordingAsync(concert, "Zweite", "video", duration: 2000);
		var passage = await scenario.AddPassageAsync(recording, performance, 100, 200);
		var passageId = passage.GetProperty("id").GetString();
		var version = passage.GetProperty("editor").GetProperty("version").GetUInt32();
		var path = $"/api/recordings/{recording}/passages/{passageId}";

		// A move answers the complete passage with display fields and a new version.
		using var moved = await scenario.SendAsync(HttpMethod.Patch, path,
			new { startSeconds = 110.5, endSeconds = 215, expectedVersion = version }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
		var movedPassage = (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage");
		Assert.Equal(110.5, movedPassage.GetProperty("startSeconds").GetDouble());
		Assert.Equal(215, movedPassage.GetProperty("endSeconds").GetDouble());
		Assert.Equal("Wandellied", movedPassage.GetProperty("songTitle").GetString());
		var newVersion = movedPassage.GetProperty("editor").GetProperty("version").GetUInt32();
		Assert.NotEqual(version, newVersion);

		// A change that changes nothing leaves the version alone.
		using var same = await scenario.SendAsync(HttpMethod.Patch, path,
			new { startSeconds = 110.5, endSeconds = 215, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, same.StatusCode);
		Assert.Equal(newVersion, (await same.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("passage").GetProperty("editor").GetProperty("version").GetUInt32());

		// A stale form answers the stale message; invalid bounds keep their own.
		using var stale = await scenario.SendAsync(HttpMethod.Patch, path,
			new { startSeconds = 50, expectedVersion = version }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal("Die Zeitmarke wurde zwischenzeitlich geändert.", await TitleAsync(stale));
		using var inverted = await scenario.SendAsync(HttpMethod.Patch, path,
			new { startSeconds = 500, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
		Assert.Equal("Das Ende muss nach dem Anfang liegen.", await TitleAsync(inverted));
		using var beyond = await scenario.SendAsync(HttpMethod.Patch, path,
			new { endSeconds = 2500, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal("Das Ende liegt hinter dem Ende der Aufnahme.", await TitleAsync(beyond));

		// The passage's ids belong to its stated recording: the same passage
		// under another recording, or another recording's id, is unknown.
		using var wrongRecording = await scenario.SendAsync(HttpMethod.Patch,
			$"/api/recordings/{otherRecording}/passages/{passageId}",
			new { startSeconds = 1, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, wrongRecording.StatusCode);
		Assert.Equal("Zeitmarke nicht gefunden.", await TitleAsync(wrongRecording));
		using var wrongDelete = await scenario.SendAsync(HttpMethod.Post,
			$"/api/recordings/{otherRecording}/passages/{passageId}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, wrongDelete.StatusCode);
		// A patch cannot move the passage to another occurrence.
		using var retarget = await scenario.SendAsync(HttpMethod.Patch, path,
			new { performanceId = otherPerformance, startSeconds = 111, expectedVersion = newVersion }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, retarget.StatusCode);
		Assert.Equal(performance, (await retarget.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("passage").GetProperty("performanceId").GetString());

		// Delete: stale refused, then removed; the occurrence itself stays.
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
		Assert.Equal(2, await scenario.PerformanceCountAsync());
		// Deleting again is a plain unknown passage.
		using var again = await scenario.SendAsync(HttpMethod.Post, $"{path}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
	}

	[Fact]
	public async Task AuthorizationHoldsOnEveryPath()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Geschütztes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1995, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var passage = await scenario.AddPassageAsync(recording, performance, 10, 100);
		var passageId = passage.GetProperty("id").GetString();
		var body = new { performanceId = performance, startSeconds = 1, endSeconds = 5 };
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
	public async Task UnpublishedRecordingsEventsAndSongsNeverReachMembers()
	{
		await using var scenario = await Scenario.CreateAsync();
		var visible = await scenario.CreateSongAsync("Sichtbares Lied");
		var draftSong = await scenario.CreateSongAsync("Entwurfslied", publish: false);
		var concert = await scenario.CreateEventAsync("Konzert", 1996, 3, 3, published: true);
		var visiblePerformance = await scenario.RecordAsync(concert, visible.SongId);
		var draftSongPerformance = await scenario.RecordAsync(concert, draftSong.SongId);
		var published = await scenario.CreateRecordingAsync(concert, "Veröffentlicht", "video", duration: 2000);
		var draft = await scenario.CreateRecordingAsync(concert, "Entwurf", "video", duration: 2000);
		await scenario.PublishRecordingAsync(published);
		await scenario.AddPassageAsync(published, visiblePerformance, 10, 100);
		await scenario.AddPassageAsync(published, draftSongPerformance, 200, 300);
		await scenario.AddPassageAsync(draft, visiblePerformance, 20, 120);

		// Members: only the published recording, only the published song.
		var memberList = await scenario.PassagesAsync(published, scenario.MemberSession);
		Assert.Equal(new[] { "Sichtbares Lied" },
			memberList.GetProperty("passages").EnumerateArray().Select(p => p.GetProperty("songTitle").GetString()));
		using (var draftList = await scenario.GetAsync($"/api/recordings/{draft}/passages", scenario.MemberSession))
			Assert.Equal(HttpStatusCode.NotFound, draftList.StatusCode);
		var memberRow = Assert.Single((await scenario.HistoryAsync(visible.SongId, scenario.MemberSession))
			.GetProperty("performances").EnumerateArray());
		var memberLinks = memberRow.GetProperty("recordings").EnumerateArray().ToList();
		Assert.Equal(new[] { "Veröffentlicht" }, memberLinks.Select(r => r.GetProperty("recordingLabel").GetString()));
		using (var draftHistory = await scenario.GetAsync($"/api/songs/{draftSong.SongId}/performances", scenario.MemberSession))
			Assert.Equal(HttpStatusCode.NotFound, draftHistory.StatusCode);

		// Editors see the draft recording and the draft song's passage, flagged.
		var editorLinks = (await scenario.HistoryAsync(visible.SongId, scenario.EditorSession))
			.GetProperty("performances")[0].GetProperty("recordings").EnumerateArray().ToList();
		Assert.Equal(2, editorLinks.Count);
		Assert.False(editorLinks.Single(r => r.GetProperty("recordingLabel").GetString() == "Entwurf")
			.GetProperty("isPublished").GetBoolean());
		Assert.Equal(2, (await scenario.PassagesAsync(published, scenario.EditorSession))
			.GetProperty("passages").GetArrayLength());

		// Withdrawing the recording or the event takes the passages away again.
		await scenario.PatchRecordingAsync(published, new { isPublished = false });
		using (var withdrawn = await scenario.GetAsync($"/api/recordings/{published}/passages", scenario.MemberSession))
			Assert.Equal(HttpStatusCode.NotFound, withdrawn.StatusCode);
		Assert.Empty((await scenario.HistoryAsync(visible.SongId, scenario.MemberSession))
			.GetProperty("performances")[0].GetProperty("recordings").EnumerateArray());
		await scenario.PatchRecordingAsync(published, new { isPublished = true });
		Assert.Single((await scenario.HistoryAsync(visible.SongId, scenario.MemberSession))
			.GetProperty("performances")[0].GetProperty("recordings").EnumerateArray());
		await scenario.SetEventPublishedAsync(concert, false);
		using (var hiddenEvent = await scenario.GetAsync($"/api/recordings/{published}/passages", scenario.MemberSession))
			Assert.Equal(HttpStatusCode.NotFound, hiddenEvent.StatusCode);
		var hiddenHistory = await scenario.HistoryAsync(visible.SongId, scenario.MemberSession);
		Assert.Equal(0, hiddenHistory.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task ChangedPlaybackFileFlagsPassagesForReviewUntilAnEditorConfirmsThem()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Wanderlied");
		var concert = await scenario.CreateEventAsync("Konzert", 1997, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var passage = await scenario.AddPassageAsync(recording, performance, 100, 300);
		var passageId = passage.GetProperty("id").GetString();
		var revisionBefore = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("playbackRevisionId").GetString();
		Assert.Equal((await scenario.AccessAsync(recording, scenario.MemberSession)).GetProperty("revisionId").GetString(),
			revisionBefore);

		// A converted copy replaces what members play: the timestamps were
		// taken against the old file.
		await scenario.ReplacePlaybackFileAsync(recording);
		var editorView = await scenario.PassagesAsync(recording, scenario.EditorSession);
		Assert.NotEqual(revisionBefore, editorView.GetProperty("playbackRevisionId").GetString());
		var flagged = editorView.GetProperty("passages")[0];
		Assert.Equal("needsReview", flagged.GetProperty("timestampState").GetString());
		Assert.Equal(100, flagged.GetProperty("startSeconds").GetDouble());
		Assert.Equal(revisionBefore, flagged.GetProperty("editor").GetProperty("playbackRevisionId").GetString());

		// Members get the song as part of the recording, but no position.
		var memberView = await scenario.PassagesAsync(recording, scenario.MemberSession);
		var memberPassage = memberView.GetProperty("passages")[0];
		Assert.Equal("Wanderlied", memberPassage.GetProperty("songTitle").GetString());
		Assert.Equal("needsReview", memberPassage.GetProperty("timestampState").GetString());
		Assert.True(memberPassage.GetProperty("startSeconds").ValueKind is JsonValueKind.Null);
		Assert.True(memberPassage.GetProperty("endSeconds").ValueKind is JsonValueKind.Null);
		var memberLink = (await scenario.HistoryAsync(song.SongId, scenario.MemberSession))
			.GetProperty("performances")[0].GetProperty("recordings")[0];
		Assert.Equal("needsReview", memberLink.GetProperty("timestampState").GetString());
		Assert.True(memberLink.GetProperty("startSeconds").ValueKind is JsonValueKind.Null);
		// The editor's history row names the state and keeps the values.
		var editorLink = (await scenario.HistoryAsync(song.SongId, scenario.EditorSession))
			.GetProperty("performances")[0].GetProperty("recordings")[0];
		Assert.Equal("needsReview", editorLink.GetProperty("timestampState").GetString());
		Assert.Equal(100, editorLink.GetProperty("startSeconds").GetDouble());

		// Editing against the new file is a new statement: it takes the new revision.
		var version = flagged.GetProperty("editor").GetProperty("version").GetUInt32();
		using var edited = await scenario.SendAsync(HttpMethod.Patch, $"/api/recordings/{recording}/passages/{passageId}",
			new { startSeconds = 90, expectedVersion = version }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
		var reanchored = (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage");
		Assert.Equal("current", reanchored.GetProperty("timestampState").GetString());
		Assert.Equal(editorView.GetProperty("playbackRevisionId").GetString(),
			reanchored.GetProperty("editor").GetProperty("playbackRevisionId").GetString());
		// ... while its end was not looked at: the edit confirmed the pair.
		Assert.Equal(300, reanchored.GetProperty("endSeconds").GetDouble());
	}

	[Fact]
	public async Task EditorConfirmsAllFlaggedPassagesInOneStep()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateSongAsync("Eins");
		var b = await scenario.CreateSongAsync("Zwei");
		var concert = await scenario.CreateEventAsync("Konzert", 1998, 3, 3, published: true);
		var pa = await scenario.RecordAsync(concert, a.SongId);
		var pb = await scenario.RecordAsync(concert, b.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		await scenario.AddPassageAsync(recording, pa, 10, 100);
		await scenario.AddPassageAsync(recording, pb, 200, 400);
		await scenario.ReplacePlaybackFileAsync(recording);

		using var review = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{recording}/passages/review",
			new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, review.StatusCode);
		var answer = await review.Content.ReadFromJsonAsync<JsonElement>();
		var currentRevision = answer.GetProperty("playbackRevisionId").GetString();
		var passages = answer.GetProperty("passages").EnumerateArray().ToList();
		Assert.Equal(2, passages.Count);
		Assert.All(passages, p =>
		{
			Assert.Equal("current", p.GetProperty("timestampState").GetString());
			Assert.Equal(currentRevision, p.GetProperty("editor").GetProperty("playbackRevisionId").GetString());
			Assert.False(string.IsNullOrEmpty(p.GetProperty("songTitle").GetString()));
		});
		// The values were confirmed as they are, and members get the jump again.
		Assert.Equal(new[] { 10d, 200d }, passages.Select(p => p.GetProperty("startSeconds").GetDouble()));
		var member = (await scenario.PassagesAsync(recording, scenario.MemberSession)).GetProperty("passages")[0];
		Assert.Equal("current", member.GetProperty("timestampState").GetString());
		Assert.Equal(10, member.GetProperty("startSeconds").GetDouble());

		// Confirming again changes nothing (versions stay).
		var versions = passages.Select(p => p.GetProperty("editor").GetProperty("version").GetUInt32()).ToList();
		using var again = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{recording}/passages/review",
			new { }, scenario.EditorSession);
		var repeated = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passages")
			.EnumerateArray().Select(p => p.GetProperty("editor").GetProperty("version").GetUInt32()).ToList();
		Assert.Equal(versions, repeated);
	}

	[Fact]
	public async Task PerformanceWithPassagesCannotBeDeletedOrSkippedBehindTheirBack()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateSongAsync("Gesungen");
		var b = await scenario.CreateSongAsync("Ausgelassen");
		var historic = await scenario.CreateEventAsync("Historischer Auftritt", 1950, 5, 5, published: true);
		var concert = await scenario.CreateEventAsync("Konzert", 1999, 3, 3, published: true);
		var handEntered = await scenario.RecordAsync(historic, a.SongId, note: "Chronik");
		await scenario.PublishProgrammeAsync(concert, [a, b]);
		await scenario.ConfirmAsync(concert);
		var historicRecording = await scenario.CreateRecordingAsync(historic, "Alter Mitschnitt", "audio", duration: 600);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var occurrences = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("occurrences").EnumerateArray().ToList();
		var ownedB = occurrences[1].GetProperty("performanceId").GetString()!;
		await scenario.AddPassageAsync(historicRecording, handEntered, 5, 100);
		var passage = await scenario.AddPassageAsync(recording, ownedB, 400, 800);

		// Deleting a hand-entered occurrence with a passage is refused with a
		// reason that names the recording; nothing is deleted.
		using var delete = await scenario.SendAsync(HttpMethod.Post, $"/api/performances/{handEntered}/delete",
			new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
		var deleteMessage = await TitleAsync(delete);
		Assert.StartsWith("Zeitmarken vorhanden:", deleteMessage);
		Assert.Contains("„Alter Mitschnitt“", deleteMessage);
		Assert.Contains("„Gesungen“", deleteMessage);
		Assert.Equal(3, await scenario.PerformanceCountAsync());

		// Skipping a planned entry whose occurrence has a passage is refused the
		// same way, and the confirmation stays as it was.
		var (review, rowVersion) = await scenario.ReviewAsync(concert);
		var items = review.GetProperty("items").EnumerateArray().Select((item, index) => new
		{
			programmeItemId = item.GetProperty("programmeItemId").GetString(),
			outcome = index == 1 ? "skipped" : "sung",
		}).ToArray();
		using var skip = await scenario.SendAsync(HttpMethod.Put, $"/api/events/{concert}/programme/confirmation", new
		{
			revisionId = review.GetProperty("revision").GetProperty("id").GetString(),
			rowVersion,
			items,
			additions = Array.Empty<object>(),
		}, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, skip.StatusCode);
		var skipMessage = await TitleAsync(skip);
		Assert.StartsWith("Zeitmarken vorhanden:", skipMessage);
		Assert.Contains("„Ausgelassen“", skipMessage);
		Assert.Contains("„Mitschnitt“", skipMessage);
		Assert.Equal(3, await scenario.PerformanceCountAsync());
		// The review tells the editor beforehand which entries carry passages.
		var (again, _) = await scenario.ReviewAsync(concert);
		Assert.Equal(0, again.GetProperty("items")[0].GetProperty("performance").GetProperty("passageCount").GetInt32());
		Assert.Equal(1, again.GetProperty("items")[1].GetProperty("performance").GetProperty("passageCount").GetInt32());

		// Removing the passages first lets both paths proceed.
		using var removed = await scenario.SendAsync(HttpMethod.Post,
			$"/api/recordings/{recording}/passages/{passage.GetProperty("id").GetString()}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
		using var skipNow = await scenario.SendAsync(HttpMethod.Put, $"/api/events/{concert}/programme/confirmation", new
		{
			revisionId = review.GetProperty("revision").GetProperty("id").GetString(),
			rowVersion,
			items,
			additions = Array.Empty<object>(),
		}, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, skipNow.StatusCode);
		var historicPassage = (await scenario.PassagesAsync(historicRecording, scenario.EditorSession))
			.GetProperty("passages")[0].GetProperty("id").GetString();
		using var unmark = await scenario.SendAsync(HttpMethod.Post,
			$"/api/recordings/{historicRecording}/passages/{historicPassage}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NoContent, unmark.StatusCode);
		using var deleteNow = await scenario.SendAsync(HttpMethod.Post, $"/api/performances/{handEntered}/delete",
			new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NoContent, deleteNow.StatusCode);
	}

	[Fact]
	public async Task CatalogueFiltersSongsWithMemberVisibleRecordedPassages()
	{
		await using var scenario = await Scenario.CreateAsync();
		var recorded = await scenario.CreateSongAsync("Aufgenommenes Lied");
		var unrecorded = await scenario.CreateSongAsync("Stilles Lied");
		var draftOnly = await scenario.CreateSongAsync("Nur im Entwurf");
		var hiddenEventSong = await scenario.CreateSongAsync("Verborgener Auftritt");
		var concert = await scenario.CreateEventAsync("Konzert", 2000, 3, 3, published: true);
		var secret = await scenario.CreateEventAsync("Unveröffentlicht", 2001, 3, 3, published: false);
		var recordedPerformance = await scenario.RecordAsync(concert, recorded.SongId);
		await scenario.RecordAsync(concert, unrecorded.SongId);
		var draftPerformance = await scenario.RecordAsync(concert, draftOnly.SongId);
		var secretPerformance = await scenario.RecordAsync(secret, hiddenEventSong.SongId);
		var published = await scenario.CreateRecordingAsync(concert, "Veröffentlicht", "video", duration: 2000);
		var draft = await scenario.CreateRecordingAsync(concert, "Entwurf", "video", duration: 2000);
		var secretRecording = await scenario.CreateRecordingAsync(secret, "Geheim", "video", duration: 2000);
		await scenario.PublishRecordingAsync(published);
		await scenario.PublishRecordingAsync(secretRecording);
		await scenario.AddPassageAsync(published, recordedPerformance, 10, 100);
		await scenario.AddPassageAsync(draft, draftPerformance, 10, 100);
		await scenario.AddPassageAsync(secretRecording, secretPerformance, 10, 100);

		// Members: only songs with a passage in a published recording of a
		// published event. The echo names the accepted material.
		var members = await scenario.ListSongsAsync(scenario.MemberSession, "material=recording");
		Assert.Equal(new[] { "Aufgenommenes Lied" },
			members.GetProperty("songs").EnumerateArray().Select(s => s.GetProperty("title").GetString()));
		Assert.Equal(1, members.GetProperty("total").GetInt32());
		Assert.Equal(new[] { "recording" },
			members.GetProperty("filters").GetProperty("materials").EnumerateArray().Select(m => m.GetString()));
		// Editors see what their own views show: draft recordings count too.
		var editors = await scenario.ListSongsAsync(scenario.EditorSession, "material=recording");
		Assert.Equal(new[] { "Aufgenommenes Lied", "Nur im Entwurf", "Verborgener Auftritt" }.OrderBy(x => x),
			editors.GetProperty("songs").EnumerateArray().Select(s => s.GetProperty("title").GetString()).OrderBy(x => x));
		// The filter combines with the other material types and the search.
		Assert.Equal(0, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording,score"))
			.GetProperty("total").GetInt32());
		Assert.Equal(1, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording&q=aufgenommen"))
			.GetProperty("total").GetInt32());
		Assert.Equal(0, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording&q=still"))
			.GetProperty("total").GetInt32());

		// Withdrawing the recording or the event removes the song again.
		await scenario.PatchRecordingAsync(published, new { isPublished = false });
		Assert.Equal(0, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording"))
			.GetProperty("total").GetInt32());
		await scenario.PatchRecordingAsync(published, new { isPublished = true });
		Assert.Equal(1, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording"))
			.GetProperty("total").GetInt32());
		// Deleting the passage removes it; no passage, no recorded song.
		var passageId = (await scenario.PassagesAsync(published, scenario.EditorSession))
			.GetProperty("passages")[0].GetProperty("id").GetString();
		using (var delete = await scenario.SendAsync(HttpMethod.Post,
			$"/api/recordings/{published}/passages/{passageId}/delete", new { }, scenario.EditorSession))
			Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
		Assert.Equal(0, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording"))
			.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task RecordedFilterHoldsOnTheSameArrangementAsTheOtherConditions()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Doppelfassung");
		var second = await scenario.AddArrangementAsync(song.SongId, "Männerchor");
		await scenario.SetVoiceAsync(song.ArrangementId, "SATB");
		await scenario.SetVoiceAsync(second.ArrangementId, "TTBB");
		var concert = await scenario.CreateEventAsync("Konzert", 2002, 3, 3, published: true);
		// Only the TTBB arrangement was recorded; one occurrence has no known arrangement.
		var performance = await scenario.RecordAsync(concert, song.SongId, versionId: second.VersionId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		await scenario.AddPassageAsync(recording, performance, 10, 100);

		var ttbb = await scenario.ListSongsAsync(scenario.MemberSession, "material=recording&voiceConfiguration=ttbb");
		Assert.Equal(1, ttbb.GetProperty("total").GetInt32());
		Assert.Equal(new[] { second.ArrangementId.ToString() },
			ttbb.GetProperty("songs")[0].GetProperty("matchedArrangements").EnumerateArray()
				.Select(a => a.GetProperty("id").GetString()));
		// The SATB arrangement satisfies the voice filter, but has no recording:
		// siblings never combine into a match.
		var satb = await scenario.ListSongsAsync(scenario.MemberSession, "material=recording&voiceConfiguration=satb");
		Assert.Equal(0, satb.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task OccurrenceWithoutKnownArrangementStillMakesTheSongRecorded()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Unbekannte Fassung");
		var concert = await scenario.CreateEventAsync("Konzert", 2003, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		await scenario.AddPassageAsync(recording, performance, 10, 100);

		Assert.Equal(1, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording"))
			.GetProperty("total").GetInt32());
		// An arrangement-level condition cannot be satisfied through an unknown chain.
		Assert.Equal(0, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording&voiceConfiguration=satb"))
			.GetProperty("total").GetInt32());
	}

	private static async Task<string?> TitleAsync(HttpResponseMessage response)
	{
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
	}
}
