using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Recordings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-032 review follow-up: writes carry the playback revision the editor
/// looked at, the bulk review acts only on the passages the client names, and
/// the state transitions of the playable file (replaced, restored, lost) are
/// read honestly.
/// </summary>
public sealed partial class RecordingPassageApiTests
{
	private const string StalePlayback = "Die Datei der Aufnahme wurde zwischenzeitlich ersetzt.";

	[Fact]
	public async Task OutdatedPlaybackRevisionIsRefusedOnEveryWriteAndWritesNothing()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateSongAsync("Alt");
		var b = await scenario.CreateSongAsync("Neu");
		var c = await scenario.CreateSongAsync("Dritt");
		var concert = await scenario.CreateEventAsync("Konzert", 2004, 3, 3, published: true);
		var pa = await scenario.RecordAsync(concert, a.SongId);
		var pb = await scenario.RecordAsync(concert, b.SongId);
		var pc = await scenario.RecordAsync(concert, c.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var first = await scenario.AddPassageAsync(recording, pa, 10, 100);
		var second = await scenario.AddPassageAsync(recording, pb, 200, 300);
		var oldRevision = await scenario.PlaybackRevisionAsync(recording);
		await scenario.ReplacePlaybackFileAsync(recording);
		var newRevision = await scenario.PlaybackRevisionAsync(recording);
		Assert.NotEqual(oldRevision, newRevision);
		var path = $"/api/recordings/{recording}/passages";
		var firstId = first.GetProperty("id").GetString();
		var secondId = second.GetProperty("id").GetString();
		var firstVersion = first.GetProperty("editor").GetProperty("version").GetUInt32();
		var secondVersion = second.GetProperty("editor").GetProperty("version").GetUInt32();

		// A form built against the old file cannot create, change or confirm.
		using (var create = await scenario.SendAsync(HttpMethod.Post, path,
			new { expectedPlaybackRevisionId = oldRevision, performanceId = pc, startSeconds = 1, endSeconds = 5 },
			scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
			Assert.Equal(StalePlayback, await TitleAsync(create));
		}
		using (var missing = await scenario.SendAsync(HttpMethod.Post, path,
			new { performanceId = pc, startSeconds = 1, endSeconds = 5 }, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
			Assert.Equal("Die Dateiversion der Aufnahme fehlt.", await TitleAsync(missing));
		}
		using (var patch = await scenario.SendAsync(HttpMethod.Patch, $"{path}/{firstId}",
			new { expectedPlaybackRevisionId = oldRevision, startSeconds = 11, endSeconds = 101, expectedVersion = firstVersion },
			scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
			Assert.Equal(StalePlayback, await TitleAsync(patch));
		}
		using (var patchMissing = await scenario.SendAsync(HttpMethod.Patch, $"{path}/{firstId}",
			new { startSeconds = 11, endSeconds = 101, expectedVersion = firstVersion }, scenario.EditorSession))
			Assert.Equal(HttpStatusCode.BadRequest, patchMissing.StatusCode);
		using (var review = await scenario.SendAsync(HttpMethod.Post, $"{path}/review",
			new
			{
				expectedPlaybackRevisionId = oldRevision,
				passages = new[] { new { id = firstId, expectedVersion = firstVersion } },
			}, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, review.StatusCode);
			Assert.Equal(StalePlayback, await TitleAsync(review));
		}
		// Nothing was written: both passages still await review, with their old versions.
		var unchanged = (await scenario.PassagesAsync(recording, scenario.EditorSession)).GetProperty("passages")
			.EnumerateArray().ToList();
		Assert.Equal(2, unchanged.Count);
		Assert.All(unchanged, p => Assert.Equal("needsReview", p.GetProperty("timestampState").GetString()));
		Assert.Equal(new[] { firstVersion, secondVersion },
			unchanged.Select(p => p.GetProperty("editor").GetProperty("version").GetUInt32()));

		// The review needs a list; an empty one is as good as none.
		foreach (var body in new object[]
		{
			new { expectedPlaybackRevisionId = newRevision },
			new { expectedPlaybackRevisionId = newRevision, passages = Array.Empty<object>() },
		})
		{
			using var empty = await scenario.SendAsync(HttpMethod.Post, $"{path}/review", body, scenario.EditorSession);
			Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
			Assert.Equal("Es wurden keine Zeitmarken zum Bestätigen angegeben.", await TitleAsync(empty));
		}
		using (var noRevision = await scenario.SendAsync(HttpMethod.Post, $"{path}/review",
			new { passages = new[] { new { id = firstId, expectedVersion = firstVersion } } }, scenario.EditorSession))
			Assert.Equal(HttpStatusCode.BadRequest, noRevision.StatusCode);
		// An id of another recording (or an unknown one) is not this recording's passage.
		using (var foreign = await scenario.SendAsync(HttpMethod.Post, $"{path}/review",
			new
			{
				expectedPlaybackRevisionId = newRevision,
				passages = new[] { new { id = Guid.NewGuid().ToString(), expectedVersion = 1u } },
			}, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
			Assert.Equal("Zeitmarke nicht gefunden.", await TitleAsync(foreign));
		}

		// A concurrent edit moved the second passage's token: the whole review
		// is refused and not even the first passage is confirmed.
		using (var concurrent = await scenario.SendAsync(HttpMethod.Post, $"{path}/review",
			new
			{
				expectedPlaybackRevisionId = newRevision,
				passages = new[]
				{
					new { id = firstId, expectedVersion = firstVersion },
					new { id = secondId, expectedVersion = secondVersion + 7 },
				},
			}, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);
			Assert.Equal("Die Zeitmarke wurde zwischenzeitlich geändert.", await TitleAsync(concurrent));
		}
		Assert.All((await scenario.PassagesAsync(recording, scenario.EditorSession)).GetProperty("passages").EnumerateArray(),
			p => Assert.Equal("needsReview", p.GetProperty("timestampState").GetString()));

		// Only the passages the client names are confirmed.
		using (var one = await scenario.SendAsync(HttpMethod.Post, $"{path}/review",
			new
			{
				expectedPlaybackRevisionId = newRevision,
				passages = new[] { new { id = firstId, expectedVersion = firstVersion } },
			}, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.OK, one.StatusCode);
			var after = (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passages").EnumerateArray().ToList();
			Assert.Equal(new[] { "current", "needsReview" }, after.Select(p => p.GetProperty("timestampState").GetString()));
		}
	}

	[Fact]
	public async Task EditorWithTheCurrentRevisionStillGetsAStalePassageRefusedOnItsOwnToken()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Eigenes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 2005, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var passage = await scenario.AddPassageAsync(recording, performance, 10, 100);
		var revision = await scenario.PlaybackRevisionAsync(recording);
		var version = passage.GetProperty("editor").GetProperty("version").GetUInt32();
		var path = $"/api/recordings/{recording}/passages/{passage.GetProperty("id").GetString()}";
		using var moved = await scenario.SendAsync(HttpMethod.Patch, path,
			new { expectedPlaybackRevisionId = revision, startSeconds = 20, endSeconds = 110, expectedVersion = version },
			scenario.EditorSession);
		Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
		// A review that lists the old token for a current passage is a stale
		// form, not a no-op.
		using var review = await scenario.SendAsync(HttpMethod.Post, $"/api/recordings/{recording}/passages/review",
			new
			{
				expectedPlaybackRevisionId = revision,
				passages = new[] { new { id = passage.GetProperty("id").GetString(), expectedVersion = version } },
			}, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, review.StatusCode);
		Assert.Equal("Die Zeitmarke wurde zwischenzeitlich geändert.", await TitleAsync(review));
	}

	[Fact]
	public async Task AntiforgeryAndAnonymousCallersAreRefusedOnPatchDeleteAndReview()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Wachlied");
		var concert = await scenario.CreateEventAsync("Konzert", 2006, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var passage = await scenario.AddPassageAsync(recording, performance, 10, 100);
		var path = $"/api/recordings/{recording}/passages";
		var passageId = passage.GetProperty("id").GetString();
		var revision = await scenario.PlaybackRevisionAsync(recording);
		var calls = new (HttpMethod, string, object)[]
		{
			(HttpMethod.Patch, $"{path}/{passageId}",
				new { expectedPlaybackRevisionId = revision, startSeconds = 20, endSeconds = 50 }),
			(HttpMethod.Post, $"{path}/{passageId}/delete", new { }),
			(HttpMethod.Post, $"{path}/review",
				new { expectedPlaybackRevisionId = revision, passages = new[] { new { id = passageId, expectedVersion = 1u } } }),
		};
		foreach (var (method, route, payload) in calls)
		{
			using var anonymous = await scenario.SendAsync(method, route, payload, null);
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
			using var request = new HttpRequestMessage(method, route);
			request.Headers.Add("Cookie", scenario.EditorSession);
			request.Content = JsonContent.Create(payload);
			using var noToken = await scenario.Client.SendAsync(request);
			Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
			Assert.Equal("Ungültiger Sicherheitstoken.", await TitleAsync(noToken));
		}
		// Nothing was changed by any of them.
		var kept = Assert.Single((await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("passages").EnumerateArray());
		Assert.Equal(10, kept.GetProperty("startSeconds").GetDouble());
	}

	[Fact]
	public async Task RestoringTheEarlierFileMakesTheMarksCurrentAgain()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Rückkehrlied");
		var concert = await scenario.CreateEventAsync("Konzert", 2007, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var (copyAsset, firstCopy) = await scenario.ReplacePlaybackFileAsync(recording);
		await scenario.AddPassageAsync(recording, performance, 100, 300);
		Assert.Equal("current", Assert.Single((await scenario.PassagesAsync(recording, scenario.MemberSession))
			.GetProperty("passages").EnumerateArray()).GetProperty("timestampState").GetString());

		var (_, secondCopy) = await scenario.ReplacePlaybackFileAsync(recording);
		Assert.NotEqual(firstCopy, secondCopy);
		var flagged = Assert.Single((await scenario.PassagesAsync(recording, scenario.MemberSession))
			.GetProperty("passages").EnumerateArray());
		Assert.Equal("needsReview", flagged.GetProperty("timestampState").GetString());
		Assert.True(flagged.GetProperty("startSeconds").ValueKind is JsonValueKind.Null);

		// Making the earlier file current again needs no bookkeeping: the
		// timestamps were taken against exactly that file.
		await scenario.RestoreRevisionAsync(copyAsset, firstCopy);
		var restored = Assert.Single((await scenario.PassagesAsync(recording, scenario.MemberSession))
			.GetProperty("passages").EnumerateArray());
		Assert.Equal("current", restored.GetProperty("timestampState").GetString());
		Assert.Equal(100, restored.GetProperty("startSeconds").GetDouble());
		Assert.Equal(1, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording"))
			.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task RecordingThatLosesItsPlayableFileReadsEverythingAsInNeedOfReview()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Verlorenes Lied");
		var other = await scenario.CreateSongAsync("Anderes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 2008, 3, 3, published: true);
		var performance = await scenario.RecordAsync(concert, song.SongId);
		var otherPerformance = await scenario.RecordAsync(concert, other.SongId);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		await scenario.PublishRecordingAsync(recording);
		var passage = await scenario.AddPassageAsync(recording, performance, 10, 100);
		var revision = await scenario.PlaybackRevisionAsync(recording);
		var passageId = passage.GetProperty("id").GetString();
		var version = passage.GetProperty("editor").GetProperty("version").GetUInt32();

		// The new original is preserved but browsers cannot play it.
		await scenario.ReplaceOriginalWithUnplayableAsync(recording);
		var editorView = await scenario.PassagesAsync(recording, scenario.EditorSession);
		Assert.True(editorView.GetProperty("playbackRevisionId").ValueKind is JsonValueKind.Null);
		Assert.Equal("needsReview", editorView.GetProperty("passages")[0].GetProperty("timestampState").GetString());
		var member = (await scenario.PassagesAsync(recording, scenario.MemberSession)).GetProperty("passages")[0];
		Assert.Equal("needsReview", member.GetProperty("timestampState").GetString());
		Assert.True(member.GetProperty("startSeconds").ValueKind is JsonValueKind.Null);
		Assert.Equal(0, (await scenario.ListSongsAsync(scenario.MemberSession, "material=recording"))
			.GetProperty("total").GetInt32());

		// No write can be anchored to a file that does not play.
		var path = $"/api/recordings/{recording}/passages";
		foreach (var (method, route, payload) in new (HttpMethod, string, object)[]
		{
			(HttpMethod.Post, path,
				new { expectedPlaybackRevisionId = revision, performanceId = otherPerformance, startSeconds = 1, endSeconds = 5 }),
			(HttpMethod.Patch, $"{path}/{passageId}",
				new { expectedPlaybackRevisionId = revision, startSeconds = 11, endSeconds = 101, expectedVersion = version }),
			(HttpMethod.Post, $"{path}/review",
				new { expectedPlaybackRevisionId = revision, passages = new[] { new { id = passageId, expectedVersion = version } } }),
		})
		{
			using var refused = await scenario.SendAsync(method, route, payload, scenario.EditorSession);
			Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
			Assert.Equal("Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.",
				await TitleAsync(refused));
		}
		// Removing a mark stays possible.
		using var delete = await scenario.SendAsync(HttpMethod.Post, $"{path}/{passageId}/delete", new { }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
	}

	[Fact]
	public async Task StaleConfirmationFormGetsTheStaleAnswerNotThePassagesBlock()
	{
		await using var scenario = await Scenario.CreateAsync();
		var a = await scenario.CreateSongAsync("Gesungen");
		var b = await scenario.CreateSongAsync("Ausgelassen");
		var concert = await scenario.CreateEventAsync("Konzert", 2009, 3, 3, published: true);
		await scenario.PublishProgrammeAsync(concert, [a, b]);
		await scenario.ConfirmAsync(concert);
		var recording = await scenario.CreateRecordingAsync(concert, "Mitschnitt", "video", duration: 2000);
		var occurrences = (await scenario.PassagesAsync(recording, scenario.EditorSession))
			.GetProperty("occurrences").EnumerateArray().ToList();
		var markedId = occurrences[1].GetProperty("performanceId").GetString()!;
		await scenario.AddPassageAsync(recording, markedId, 10, 100);

		var (review, rowVersion) = await scenario.ReviewAsync(concert);
		var items = review.GetProperty("items").EnumerateArray().Select((item, index) => new
		{
			programmeItemId = item.GetProperty("programmeItemId").GetString(),
			outcome = index == 1 ? "skipped" : "sung",
		}).ToArray();
		var revisionId = review.GetProperty("revision").GetProperty("id").GetString();
		var path = $"/api/events/{concert}/programme/confirmation";

		// An outdated confirmation token: the editor is told to reload, not to
		// delete passages from a form that no longer describes the archive.
		using (var outdated = await scenario.SendAsync(HttpMethod.Put, path,
			new { revisionId, rowVersion = rowVersion + 7, items, additions = Array.Empty<object>() }, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, outdated.StatusCode);
			Assert.Equal("Die Bestätigung wurde zwischenzeitlich geändert.", await TitleAsync(outdated));
		}
		// An occurrence the form saw in another state.
		using (var unseen = await scenario.SendAsync(HttpMethod.Put, path,
			new
			{
				revisionId,
				rowVersion,
				items,
				additions = Array.Empty<object>(),
				knownOccurrences = new[] { new { performanceId = markedId, rowVersion = 99u } },
			}, scenario.EditorSession))
		{
			Assert.Equal(HttpStatusCode.Conflict, unseen.StatusCode);
			Assert.Equal("Die Bestätigung wurde zwischenzeitlich geändert.", await TitleAsync(unseen));
		}
		// An up-to-date form that would drop the marked occurrence is blocked.
		using var blocked = await scenario.SendAsync(HttpMethod.Put, path,
			new { revisionId, rowVersion, items, additions = Array.Empty<object>() }, scenario.EditorSession);
		Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
		Assert.StartsWith("Zeitmarken vorhanden:", await TitleAsync(blocked));
	}

	[Fact]
	public void ConstraintMappingsLookAtTheViolatedConstraint()
	{
		static DbUpdateException Violation(string sqlState, string? constraint) => new("save",
			new PostgresException("fehler", "ERROR", "ERROR", sqlState, constraintName: constraint));

		var passageToPerformance = Violation(PostgresErrorCodes.ForeignKeyViolation,
			RecordingPassages.PerformanceForeignKey);
		var otherForeignKey = Violation(PostgresErrorCodes.ForeignKeyViolation, "FK_something_else");
		var duplicate = Violation(PostgresErrorCodes.UniqueViolation, RecordingPassages.UniquePerRecording);
		var otherUnique = Violation(PostgresErrorCodes.UniqueViolation, "IX_other");

		Assert.True(RecordingPassages.IsPerformanceForeignKeyViolation(passageToPerformance));
		Assert.False(RecordingPassages.IsPerformanceForeignKeyViolation(otherForeignKey));
		Assert.False(RecordingPassages.IsPerformanceForeignKeyViolation(duplicate));
		Assert.False(RecordingPassages.IsPerformanceForeignKeyViolation(new DbUpdateException("save")));
		Assert.True(RecordingPassages.IsDuplicateViolation(duplicate));
		Assert.False(RecordingPassages.IsDuplicateViolation(otherUnique));
		Assert.False(RecordingPassages.IsDuplicateViolation(passageToPerformance));
		// The names are the ones the migration created.
		Assert.Equal("FK_recording_passages_performances_PerformanceId_EventId", RecordingPassages.PerformanceForeignKey);
		Assert.Equal("IX_recording_passages_RecordingId_PerformanceId", RecordingPassages.UniquePerRecording);
	}
}
