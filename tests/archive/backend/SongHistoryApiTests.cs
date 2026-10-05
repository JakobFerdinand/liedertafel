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
/// ARC-031 song history: a member reads where a song is documented, with
/// confirmed occurrences kept apart from unconfirmed programme mentions.
/// Totals count stable performance rows (never documents, retries or
/// recordings), preserve genuine repeats within one event and flag rows that
/// might describe the same performance twice instead of silently merging or
/// double counting them. Unpublished songs and events never leak into rows
/// or aggregates; members never receive source notes; ordering is total and
/// pages are bounded.
/// </summary>
public sealed class SongHistoryApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task MemberSeesRecordedHistoryWithSeparateHonestCounts()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Geschichtslied", "Grundtonart");
		var e1975 = await scenario.CreateEventAsync("Späterer Auftritt", 1975, 6, 1, published: true);
		var e1960 = await scenario.CreateEventAsync("Ungefähres Fest", 1960, null, null, approximate: true, published: true);
		var e1950 = await scenario.CreateEventAsync("Früherer Auftritt", 1950, 5, 12, published: true);
		var undated = await scenario.CreateEventAsync("Undatierter Auftritt", null, null, null, published: true);
		await scenario.RecordAsync(e1975, song.SongId, "mention", note: "Programm 1975");
		await scenario.RecordAsync(e1960, song.SongId, "mention", note: "Programm um 1960");
		var first = await scenario.RecordAsync(e1950, song.SongId, "confirmed", versionId: song.VersionId);
		// A genuine repeat within the same event: a second confirmed
		// occurrence with its own identity (distinct retry key).
		var repeat = await scenario.RecordAsync(e1950, song.SongId, "confirmed", key: "zugabe");
		var unknownDate = await scenario.RecordAsync(undated, song.SongId, "confirmed");

		var history = await scenario.HistoryAsync(song.SongId, scenario.MemberSession);
		var counts = history.GetProperty("counts");
		var confirmed = counts.GetProperty("confirmed");
		var unconfirmed = counts.GetProperty("unconfirmed");
		// Occurrences, not documents: two at the repeated event, one undated.
		Assert.Equal(3, confirmed.GetProperty("occurrences").GetInt32());
		Assert.Equal(2, confirmed.GetProperty("events").GetInt32());
		Assert.Equal(1, confirmed.GetProperty("uncertainDates").GetInt32());
		Assert.Equal(0, confirmed.GetProperty("possiblyDuplicate").GetInt32());
		Assert.Equal(2, unconfirmed.GetProperty("occurrences").GetInt32());
		Assert.Equal(2, unconfirmed.GetProperty("events").GetInt32());
		Assert.Equal(2, unconfirmed.GetProperty("onlyEvents").GetInt32());
		Assert.Equal(1, unconfirmed.GetProperty("uncertainDates").GetInt32());
		Assert.Equal(5, history.GetProperty("total").GetInt32());
		Assert.Equal(1, history.GetProperty("page").GetInt32());
		Assert.Equal(20, history.GetProperty("pageSize").GetInt32());
		Assert.Equal("Geschichtslied", history.GetProperty("song").GetProperty("title").GetString());

		// Newest known year first, within a year the day precision comes
		// first, unknown years last; rows of one event keep their position.
		var rows = history.GetProperty("performances").EnumerateArray().ToList();
		Assert.Equal(
			new[] { e1975, e1960, e1950, e1950, undated },
			rows.Select(r => Guid.Parse(r.GetProperty("eventId").GetString()!)).ToArray());
		Assert.Equal(new[] { Guid.Parse(first), Guid.Parse(repeat) },
			rows.Skip(2).Take(2).Select(r => Guid.Parse(r.GetProperty("id").GetString()!)).ToArray());
		Assert.Equal(unknownDate, rows[4].GetProperty("id").GetString());

		var latest = rows[0];
		Assert.Equal("Späterer Auftritt", latest.GetProperty("eventTitle").GetString());
		Assert.Equal("1. Juni 1975", latest.GetProperty("dateDisplay").GetString());
		Assert.Equal("day", latest.GetProperty("datePrecision").GetString());
		Assert.False(latest.GetProperty("dateUncertain").GetBoolean());
		Assert.Equal("mention", latest.GetProperty("evidenceStatus").GetString());
		Assert.Equal("um 1960", rows[1].GetProperty("dateDisplay").GetString());
		Assert.True(rows[1].GetProperty("dateUncertain").GetBoolean());
		Assert.Equal("Datum unbekannt", rows[4].GetProperty("dateDisplay").GetString());
		Assert.True(rows[4].GetProperty("dateUncertain").GetBoolean());

		// Known chain by label, unknown chain as explicit null.
		Assert.Equal("Grundtonart", rows[2].GetProperty("musicalVersion").GetProperty("label").GetString());
		Assert.Equal(song.ArrangementId.ToString(), rows[2].GetProperty("arrangement").GetProperty("id").GetString());
		Assert.True(rows[3].GetProperty("musicalVersion").ValueKind is JsonValueKind.Null);
		Assert.True(rows[3].GetProperty("arrangement").ValueKind is JsonValueKind.Null);

		// The genuine repeat is visible as such.
		Assert.Equal(1, rows[2].GetProperty("occurrence").GetProperty("index").GetInt32());
		Assert.Equal(2, rows[2].GetProperty("occurrence").GetProperty("of").GetInt32());
		Assert.Equal(2, rows[3].GetProperty("occurrence").GetProperty("index").GetInt32());
		Assert.Equal(1, rows[4].GetProperty("occurrence").GetProperty("of").GetInt32());
		// Two hand-entered rows are a genuine repeat, not a possible duplicate.
		Assert.All(rows, row => Assert.False(row.GetProperty("possiblyDuplicateAtEvent").GetBoolean()));

		// Members get neither source notes nor editor attribution.
		foreach (var row in rows)
		{
			Assert.False(row.TryGetProperty("sourceNote", out _));
			Assert.False(row.TryGetProperty("updatedByAccountId", out _));
			Assert.False(row.TryGetProperty("capturedAt", out _));
			Assert.Equal("record", row.GetProperty("origin").GetString());
		}

		// Editors additionally read the source context.
		var editorRows = (await scenario.HistoryAsync(song.SongId, scenario.EditorSession))
			.GetProperty("performances").EnumerateArray().ToList();
		Assert.Equal("Programm 1975", editorRows[0].GetProperty("sourceNote").GetString());
	}

	[Fact]
	public async Task OwnedAndHandEnteredRowsAreFlaggedNotMergedAndEvidenceChangesCountOnce()
	{
		await using var scenario = await Scenario.CreateAsync();
		var songS = await scenario.CreateSongAsync("Gesungenes Lied", "Grundtonart");
		var songT = await scenario.CreateSongAsync("Anderes Lied");
		var concert = await scenario.CreateEventAsync("Konzert 1980", 1980, 3, 1, published: true);
		await scenario.PublishProgrammeAsync(concert, [songS, songT, songS]);

		// Confirming the plan creates one owned occurrence per sung entry,
		// the repeated programme entry stays two distinct occurrences.
		await scenario.ConfirmAsync(concert, skip: []);
		// A hand-entered confirmed row and a mention for the same song at
		// the same event: not merged, but flagged and counted honestly.
		var handEntered = await scenario.RecordAsync(concert, songS.SongId, "confirmed", note: "Chronik");
		var mention = await scenario.RecordAsync(concert, songS.SongId, "mention", note: "Programmzettel");

		var history = await scenario.HistoryAsync(songS.SongId, scenario.MemberSession);
		var confirmed = history.GetProperty("counts").GetProperty("confirmed");
		var unconfirmed = history.GetProperty("counts").GetProperty("unconfirmed");
		Assert.Equal(3, confirmed.GetProperty("occurrences").GetInt32());
		Assert.Equal(1, confirmed.GetProperty("events").GetInt32());
		Assert.Equal(1, confirmed.GetProperty("possiblyDuplicate").GetInt32());
		Assert.Equal(1, unconfirmed.GetProperty("occurrences").GetInt32());
		Assert.Equal(1, unconfirmed.GetProperty("events").GetInt32());
		Assert.Equal(0, unconfirmed.GetProperty("onlyEvents").GetInt32());
		var rows = history.GetProperty("performances").EnumerateArray().ToList();
		Assert.Equal(4, rows.Count);
		var byId = rows.ToDictionary(r => r.GetProperty("id").GetString()!);
		Assert.Equal("record", byId[handEntered].GetProperty("origin").GetString());
		Assert.True(byId[handEntered].GetProperty("possiblyDuplicate").GetBoolean());
		Assert.True(byId[mention].GetProperty("alsoConfirmedAtEvent").GetBoolean());
		var owned = rows.Where(r => r.GetProperty("origin").GetString() == "programme").ToList();
		Assert.Equal(2, owned.Count);
		Assert.All(owned, r => Assert.False(r.GetProperty("possiblyDuplicate").GetBoolean()));
		Assert.All(owned, r => Assert.Equal("Grundtonart", r.GetProperty("musicalVersion").GetProperty("label").GetString()));
		// The three confirmed rows are numbered, the mention is not an occurrence.
		Assert.Equal(3, byId[handEntered].GetProperty("occurrence").GetProperty("of").GetInt32());
		Assert.True(byId[mention].GetProperty("occurrence").ValueKind is JsonValueKind.Null);
		// Every confirmed row of the event is marked, so "3 of 3" is never
		// presented as an asserted repeat; the mention is not.
		Assert.All(rows.Where(r => r.GetProperty("evidenceStatus").GetString() == "confirmed"),
			r => Assert.True(r.GetProperty("possiblyDuplicateAtEvent").GetBoolean()));
		Assert.False(byId[mention].GetProperty("possiblyDuplicateAtEvent").GetBoolean());

		// The member history leaks nothing of the plan.
		var memberText = history.GetRawText();
		Assert.DoesNotContain("programmeItemId", memberText);
		Assert.DoesNotContain("confirmationId", memberText);
		Assert.DoesNotContain("revision", memberText);

		// Downgrading one owned occurrence to a mention moves exactly one
		// occurrence between the totals, once.
		var ownedId = owned[0].GetProperty("id").GetString()!;
		await scenario.DowngradeAsync(concert, ownedId, "Nur im Programm belegt");
		var after = (await scenario.HistoryAsync(songS.SongId, scenario.MemberSession)).GetProperty("counts");
		Assert.Equal(2, after.GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(2, after.GetProperty("unconfirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(1, after.GetProperty("confirmed").GetProperty("possiblyDuplicate").GetInt32());
		// The downgraded row keeps its programme origin but never reads as
		// a confirmation: evidence says mention.
		var downgradedRow = (await scenario.HistoryAsync(songS.SongId, scenario.MemberSession))
			.GetProperty("performances").EnumerateArray()
			.Single(r => r.GetProperty("id").GetString() == ownedId);
		Assert.Equal("mention", downgradedRow.GetProperty("evidenceStatus").GetString());
		Assert.Equal("programme", downgradedRow.GetProperty("origin").GetString());
		Assert.False(downgradedRow.GetProperty("possiblyDuplicateAtEvent").GetBoolean());

		// The other song only has its own planned occurrence.
		var other = (await scenario.HistoryAsync(songT.SongId, scenario.MemberSession)).GetProperty("counts");
		Assert.Equal(1, other.GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(0, other.GetProperty("unconfirmed").GetProperty("occurrences").GetInt32());
	}

	[Fact]
	public async Task SkippedPlannedSongsCreateNoHistory()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Ausgefallenes Lied");
		var other = await scenario.CreateSongAsync("Gesungenes Lied");
		var concert = await scenario.CreateEventAsync("Konzert", 1990, 1, 1, published: true);
		await scenario.PublishProgrammeAsync(concert, [song, other]);
		await scenario.ConfirmAsync(concert, skip: [0]);

		var skipped = await scenario.HistoryAsync(song.SongId, scenario.MemberSession);
		Assert.Equal(0, skipped.GetProperty("total").GetInt32());
		Assert.Empty(skipped.GetProperty("performances").EnumerateArray());
		Assert.Equal(0, skipped.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		var sung = await scenario.HistoryAsync(other.SongId, scenario.MemberSession);
		Assert.Equal(1, sung.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task UnpublishedEventsAndSongsNeverReachMembersRowsOrCounts()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Sichtbares Lied");
		var visible = await scenario.CreateEventAsync("Veröffentlicht", 1970, 5, 1, published: true);
		var hidden = await scenario.CreateEventAsync("Entwurf", 1971, 5, 1);
		await scenario.RecordAsync(visible, song.SongId, "confirmed");
		await scenario.RecordAsync(hidden, song.SongId, "confirmed", note: "Geheimer Entwurf");
		await scenario.RecordAsync(hidden, song.SongId, "mention", note: "Auch geheim", key: "zweite");

		// Members: rows and aggregates ignore the draft event completely.
		var member = await scenario.HistoryAsync(song.SongId, scenario.MemberSession);
		Assert.Equal(1, member.GetProperty("total").GetInt32());
		Assert.Equal(1, member.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(0, member.GetProperty("counts").GetProperty("unconfirmed").GetProperty("occurrences").GetInt32());
		Assert.True(member.GetProperty("counts").GetProperty("draftEventOccurrences").ValueKind is JsonValueKind.Null);
		var memberText = member.GetRawText();
		Assert.DoesNotContain("Entwurf", memberText);
		Assert.DoesNotContain("Geheim", memberText);
		Assert.DoesNotContain(hidden.ToString(), memberText);

		// Editors see the draft rows flagged, but every aggregate stays the
		// member-visible one.
		var editor = await scenario.HistoryAsync(song.SongId, scenario.EditorSession);
		Assert.Equal(3, editor.GetProperty("total").GetInt32());
		Assert.Equal(1, editor.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(0, editor.GetProperty("counts").GetProperty("unconfirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(2, editor.GetProperty("counts").GetProperty("draftEventOccurrences").GetInt32());
		var editorRows = editor.GetProperty("performances").EnumerateArray().ToList();
		Assert.Equal(2, editorRows.Count(r => !r.GetProperty("eventPublished").GetBoolean()));

		// Publishing makes the rows count; withdrawing hides them again.
		await scenario.SetEventPublishedAsync(hidden, true);
		var published = await scenario.HistoryAsync(song.SongId, scenario.MemberSession);
		Assert.Equal(3, published.GetProperty("total").GetInt32());
		Assert.Equal(2, published.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		await scenario.SetEventPublishedAsync(hidden, false);
		Assert.Equal(1, (await scenario.HistoryAsync(song.SongId, scenario.MemberSession))
			.GetProperty("total").GetInt32());

		// A draft song answers an indistinguishable 404 for members and
		// stays readable for editors; unknown ids look the same.
		var draftSong = await scenario.CreateSongAsync("Entwurfslied", publish: false);
		await scenario.RecordAsync(visible, draftSong.SongId, "confirmed");
		using var draftForMember = await scenario.GetRawAsync($"/api/songs/{draftSong.SongId}/performances", scenario.MemberSession);
		Assert.Equal(HttpStatusCode.NotFound, draftForMember.StatusCode);
		using var unknown = await scenario.GetRawAsync($"/api/songs/{Guid.NewGuid()}/performances", scenario.MemberSession);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		Assert.Equal(
			(await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString(),
			(await draftForMember.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		Assert.Equal(1, (await scenario.HistoryAsync(draftSong.SongId, scenario.EditorSession))
			.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task AnonymousRevokedAndDowngradedCallersLoseAccessAndDrafts()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Geschütztes Lied");
		var hidden = await scenario.CreateEventAsync("Entwurf", 1971, 5, 1);
		await scenario.RecordAsync(hidden, song.SongId, "mention", note: "Nur Redaktion");
		var path = $"/api/songs/{song.SongId}/performances";

		using var anonymous = await scenario.GetRawAsync(path, null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

		// A second editor sees the draft and the note until downgraded; the
		// next request already reads the member view (fresh decision).
		const string second = "zweitredaktion@liedertafel.test";
		await SeedAsync(scenario.Factory, second, ArchiveRoles.Editor);
		var secondSession = await SignInAsync(scenario.Factory, second);
		var before = await scenario.HistoryAsync(song.SongId, secondSession);
		Assert.Equal(1, before.GetProperty("total").GetInt32());
		Assert.Equal("Nur Redaktion", before.GetProperty("performances")[0].GetProperty("sourceNote").GetString());
		using (var scope = scenario.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(second);
			Assert.True((await users.RemoveFromRoleAsync(user!, ArchiveRoles.Editor)).Succeeded);
		}
		await SeedAsync(scenario.Factory, second, ArchiveRoles.Member);
		var downgraded = await scenario.HistoryAsync(song.SongId, secondSession);
		Assert.Equal(0, downgraded.GetProperty("total").GetInt32());
		Assert.DoesNotContain("Nur Redaktion", downgraded.GetRawText());
		using (var scope = scenario.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(second);
			user!.EmailConfirmed = false;
			Assert.True((await users.UpdateAsync(user)).Succeeded);
		}
		using var revoked = await scenario.GetRawAsync(path, secondSession);
		Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
	}

	[Fact]
	public async Task HistoryPagesAreBoundedDeterministicAndFilterable()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Oft gesungen", "Erste Fassung");
		// A second arrangement with its own version, to read arrangement histories.
		var second = await scenario.AddArrangementAsync(song.SongId, "Zweiter Satz");
		// 23 events on distinct days: 17 confirmed (arrangement unknown,
		// first arrangement or second), 6 mentions.
		var eventIds = new List<Guid>();
		for (var day = 1; day <= 23; day++)
		{
			var id = await scenario.CreateEventAsync($"Auftritt {day}", 2000, 1, day, published: true);
			eventIds.Add(id);
			if (day <= 6)
				await scenario.RecordAsync(id, song.SongId, "mention", note: $"Quelle {day}");
			else if (day <= 12)
				await scenario.RecordAsync(id, song.SongId, "confirmed", versionId: song.VersionId);
			else if (day <= 15)
				await scenario.RecordAsync(id, song.SongId, "confirmed", versionId: second.VersionId);
			else
				await scenario.RecordAsync(id, song.SongId, "confirmed");
		}

		var first = await scenario.HistoryAsync(song.SongId, scenario.MemberSession);
		Assert.Equal(23, first.GetProperty("total").GetInt32());
		Assert.Equal(20, first.GetProperty("performances").GetArrayLength());
		Assert.Equal(17, first.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		Assert.Equal(6, first.GetProperty("counts").GetProperty("unconfirmed").GetProperty("occurrences").GetInt32());
		var pageTwo = await scenario.HistoryAsync(song.SongId, scenario.MemberSession, "?page=2");
		Assert.Equal(3, pageTwo.GetProperty("performances").GetArrayLength());
		Assert.Equal(2, pageTwo.GetProperty("page").GetInt32());
		// Newest day first across the page boundary, no row twice or missing.
		var ids = first.GetProperty("performances").EnumerateArray()
			.Concat(pageTwo.GetProperty("performances").EnumerateArray())
			.Select(r => Guid.Parse(r.GetProperty("eventId").GetString()!)).ToList();
		Assert.Equal(Enumerable.Reverse(eventIds).ToList(), ids);
		// Same request, same answer; nonsense pages clamp to the first page.
		Assert.Equal(first.GetRawText(), (await scenario.HistoryAsync(song.SongId, scenario.MemberSession)).GetRawText());
		Assert.Equal(1, (await scenario.HistoryAsync(song.SongId, scenario.MemberSession, "?page=0")).GetProperty("page").GetInt32());
		// A page past the end answers (and reports) the last page.
		var beyond = await scenario.HistoryAsync(song.SongId, scenario.MemberSession, "?page=9");
		Assert.Equal(2, beyond.GetProperty("page").GetInt32());
		Assert.Equal(3, beyond.GetProperty("performances").GetArrayLength());
		Assert.Equal(23, beyond.GetProperty("total").GetInt32());
		var absurd = await scenario.HistoryAsync(song.SongId, scenario.MemberSession, $"?page={int.MaxValue}");
		Assert.Equal(2, absurd.GetProperty("page").GetInt32());
		Assert.Equal(3, absurd.GetProperty("performances").GetArrayLength());
		// An empty selection still has a first page.
		var none = await scenario.HistoryAsync(song.SongId, scenario.MemberSession, "?evidence=mention&arrangementId=" + second.ArrangementId + "&page=4");
		Assert.Equal(1, none.GetProperty("page").GetInt32());
		Assert.Empty(none.GetProperty("performances").EnumerateArray());

		// Arrangement overview: counted per arrangement and unknown.
		var arrangements = first.GetProperty("arrangements").EnumerateArray().ToList();
		Assert.Equal(2, arrangements.Count);
		Assert.Equal(6, arrangements[0].GetProperty("confirmed").GetInt32());
		Assert.Equal(3, arrangements[1].GetProperty("confirmed").GetInt32());
		Assert.Equal("Zweiter Satz", arrangements[1].GetProperty("label").GetString());
		Assert.Equal(8, first.GetProperty("unknownArrangement").GetProperty("confirmed").GetInt32());
		Assert.Equal(6, first.GetProperty("unknownArrangement").GetProperty("unconfirmed").GetInt32());

		// Filters narrow the rows (total follows), the overview stays whole.
		var onlyMentions = await scenario.HistoryAsync(song.SongId, scenario.MemberSession, "?evidence=mention");
		Assert.Equal(6, onlyMentions.GetProperty("total").GetInt32());
		var secondOnly = await scenario.HistoryAsync(song.SongId, scenario.MemberSession,
			$"?arrangementId={second.ArrangementId}");
		Assert.Equal(3, secondOnly.GetProperty("total").GetInt32());
		Assert.All(secondOnly.GetProperty("performances").EnumerateArray(), r =>
			Assert.Equal("Zweiter Satz", r.GetProperty("arrangement").GetProperty("label").GetString()));
		Assert.Equal(17, secondOnly.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		var unknownOnly = await scenario.HistoryAsync(song.SongId, scenario.MemberSession,
			"?arrangementId=unknown&evidence=confirmed");
		Assert.Equal(8, unknownOnly.GetProperty("total").GetInt32());

		using var badEvidence = await scenario.GetRawAsync(
			$"/api/songs/{song.SongId}/performances?evidence=gesungen", scenario.MemberSession);
		Assert.Equal(HttpStatusCode.BadRequest, badEvidence.StatusCode);
		Assert.Equal(SongHistoryEndpoints.InvalidEvidenceFilterMessage,
			(await badEvidence.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		using var foreign = await scenario.GetRawAsync(
			$"/api/songs/{song.SongId}/performances?arrangementId={Guid.NewGuid()}", scenario.MemberSession);
		Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
		Assert.Equal(SongHistoryEndpoints.ArrangementNotFoundMessage,
			(await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
	}

	[Fact]
	public async Task SameYearOrderFollowsThePrecisionRankOfTheEventList()
	{
		await using var scenario = await Scenario.CreateAsync();
		var song = await scenario.CreateSongAsync("Jahreslied");
		var yearOnly = await scenario.CreateEventAsync("Nur Jahr", 1950, null, null, published: true);
		var december = await scenario.CreateEventAsync("Dezember", 1950, 12, null, published: true);
		var may = await scenario.CreateEventAsync("12. Mai", 1950, 5, 12, published: true);
		var january = await scenario.CreateEventAsync("3. Januar", 1950, 1, 3, published: true);
		foreach (var id in new[] { yearOnly, december, may, january })
			await scenario.RecordAsync(id, song.SongId, "confirmed");

		// Day precision first (newest day first), then month-only, then
		// year-only: exactly the order of the event list.
		var history = await scenario.HistoryAsync(song.SongId, scenario.MemberSession);
		var historyOrder = history.GetProperty("performances").EnumerateArray()
			.Select(r => Guid.Parse(r.GetProperty("eventId").GetString()!)).ToArray();
		Assert.Equal(new[] { may, january, december, yearOnly }, historyOrder);
		using var list = await scenario.GetRawAsync("/api/events?year=1950", scenario.MemberSession);
		var listOrder = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("events")
			.EnumerateArray().Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToArray();
		Assert.Equal(listOrder, historyOrder);
	}

	/// <summary>Reusable API scaffolding in the style of the other suites.</summary>
	private sealed class Scenario : IAsyncDisposable
	{
		public required AuthApiFactory Factory { get; init; }

		public required HttpClient Client { get; init; }

		public required string MemberSession { get; init; }

		public required string EditorSession { get; init; }

		public static async Task<Scenario> CreateAsync()
		{
			var factory = new AuthApiFactory();
			await SeedAsync(factory, Member, ArchiveRoles.Member);
			await SeedAsync(factory, Editor, ArchiveRoles.Editor);
			return new Scenario
			{
				Factory = factory,
				Client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }),
				MemberSession = await SignInAsync(factory, Member),
				EditorSession = await SignInAsync(factory, Editor),
			};
		}

		public async ValueTask DisposeAsync()
		{
			Client.Dispose();
			await Factory.DisposeAsync();
		}

		public async Task<(Guid SongId, Guid ArrangementId, Guid VersionId)> CreateSongAsync(
			string title, string? versionLabel = null, bool publish = true)
		{
			var (cookie, token) = await GetCsrfAsync(Client, EditorSession);
			using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {EditorSession}", token);
			using var createResponse = await Client.SendAsync(create);
			Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
			var song = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
			var songId = Guid.Parse(song.GetProperty("id").GetString()!);
			var arrangement = song.GetProperty("arrangements").EnumerateArray().Single();
			var arrangementId = Guid.Parse(arrangement.GetProperty("id").GetString()!);
			var version = arrangement.GetProperty("musicalVersions").EnumerateArray().Single();
			var versionId = Guid.Parse(version.GetProperty("id").GetString()!);
			if (versionLabel is not null)
			{
				using var patch = AuthedPatch($"/api/musical-versions/{versionId}",
					new { label = versionLabel }, $"{cookie}; {EditorSession}", token);
				using var patchResponse = await Client.SendAsync(patch);
				Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
			}
			if (publish)
			{
				using var publishSong = AuthedPost($"/api/songs/{songId}/publish", new { },
					$"{cookie}; {EditorSession}", token);
				using var publishResponse = await Client.SendAsync(publishSong);
				Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
			}
			return (songId, arrangementId, versionId);
		}

		public async Task<(Guid ArrangementId, Guid VersionId)> AddArrangementAsync(Guid songId, string label)
		{
			using var response = await PostAsync($"/api/songs/{songId}/arrangements", new { label }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, response.StatusCode);
			var song = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
			var arrangementId = Guid.Parse(song.GetProperty("arrangements").EnumerateArray()
				.Single(a => a.GetProperty("label").GetString() == label).GetProperty("id").GetString()!);
			using var version = await PostAsync($"/api/arrangements/{arrangementId}/versions",
				new { label = $"{label} (Fassung)" }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, version.StatusCode);
			var created = (await version.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song")
				.GetProperty("arrangements").EnumerateArray()
				.Single(a => Guid.Parse(a.GetProperty("id").GetString()!) == arrangementId)
				.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!;
			return (arrangementId, Guid.Parse(created));
		}

		public async Task<Guid> CreateEventAsync(string title, int? year, int? month, int? day,
			bool approximate = false, bool published = false)
		{
			using var create = await PostAsync("/api/events", new
			{
				kind = "concert",
				title,
				dateYear = year,
				dateMonth = month,
				dateDay = day,
				dateApproximate = approximate,
			}, EditorSession);
			Assert.Equal(HttpStatusCode.Created, create.StatusCode);
			var parsed = await create.Content.ReadFromJsonAsync<JsonElement>();
			var id = Guid.Parse(parsed.GetProperty("event").GetProperty("id").GetString()!);
			if (published)
				await SetEventPublishedAsync(id, true);
			return id;
		}

		public async Task SetEventPublishedAsync(Guid eventId, bool published)
		{
			using var response = await PostAsync(
				$"/api/events/{eventId}/{(published ? "publish" : "unpublish")}", new { }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		/// <summary>Records one occurrence and returns its stable id.</summary>
		public async Task<string> RecordAsync(Guid eventId, Guid songId, string status,
			string? note = null, Guid? versionId = null, string? key = null)
		{
			using var response = await PostAsync($"/api/events/{eventId}/performances", new
			{
				songId,
				evidenceStatus = status,
				sourceNote = note,
				musicalVersionId = versionId,
				idempotencyKey = key,
			}, EditorSession);
			Assert.Equal(HttpStatusCode.Created, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>())
				.GetProperty("performance").GetProperty("id").GetString()!;
		}

		/// <summary>Saves and publishes a programme made of the songs' default chains.</summary>
		public async Task PublishProgrammeAsync(Guid eventId,
			IEnumerable<(Guid SongId, Guid ArrangementId, Guid VersionId)> songs)
		{
			var items = songs.Select(s => (object)new { songId = s.SongId, musicalVersionId = s.VersionId }).ToArray();
			using var save = await SendJsonAsync(HttpMethod.Put, $"/api/events/{eventId}/programme/items",
				new { items }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, save.StatusCode);
			var saved = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");
			using var publish = await PostAsync($"/api/events/{eventId}/programme/publish",
				new { rowVersion = saved.GetProperty("rowVersion").GetUInt32() }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		}

		/// <summary>Confirms the published programme (planned entries sung except the skipped indices).</summary>
		public async Task ConfirmAsync(Guid eventId, int[] skip)
		{
			var path = $"/api/events/{eventId}/programme/confirmation";
			using var reviewResponse = await GetRawAsync(path, EditorSession);
			Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
			var review = (await reviewResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("review");
			var items = review.GetProperty("items").EnumerateArray().Select((item, index) => new
			{
				programmeItemId = Guid.Parse(item.GetProperty("programmeItemId").GetString()!),
				outcome = skip.Contains(index) ? "skipped" : "sung",
			}).ToArray();
			using var response = await SendJsonAsync(HttpMethod.Put, path, new
			{
				revisionId = Guid.Parse(review.GetProperty("revision").GetProperty("id").GetString()!),
				rowVersion = review.GetProperty("rowVersion").GetUInt32(),
				items,
				additions = Array.Empty<object>(),
			}, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		/// <summary>Turns a confirmed occurrence into an unconfirmed mention through the editor PATCH.</summary>
		public async Task DowngradeAsync(Guid eventId, string performanceId, string note)
		{
			using var detail = await GetRawAsync($"/api/events/{eventId}", EditorSession);
			var rowVersion = (await detail.Content.ReadFromJsonAsync<JsonElement>())
				.GetProperty("event").GetProperty("performances").EnumerateArray()
				.Single(p => p.GetProperty("id").GetString() == performanceId)
				.GetProperty("rowVersion").GetUInt32();
			using var response = await SendJsonAsync(HttpMethod.Patch, $"/api/performances/{performanceId}",
				new { rowVersion, evidenceStatus = "mention", sourceNote = note }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		public async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object body, string session)
		{
			var (cookie, token) = await GetCsrfAsync(Client, session);
			var request = new HttpRequestMessage(method, path);
			request.Headers.Add("Cookie", $"{cookie}; {session}");
			request.Headers.Add("X-CSRF-TOKEN", token);
			request.Content = JsonContent.Create(body);
			return await Client.SendAsync(request);
		}

		public async Task<JsonElement> HistoryAsync(Guid songId, string session, string query = "")
		{
			using var response = await GetRawAsync($"/api/songs/{songId}/performances{query}", session);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return await response.Content.ReadFromJsonAsync<JsonElement>();
		}

		public async Task<HttpResponseMessage> GetRawAsync(string path, string? session)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, path);
			if (session is not null)
				request.Headers.Add("Cookie", session);
			return await Client.SendAsync(request);
		}

		public async Task<HttpResponseMessage> PostAsync(string path, object body, string session)
		{
			var (cookie, token) = await GetCsrfAsync(Client, session);
			return await Client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
		}
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
