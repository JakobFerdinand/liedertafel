using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-026 ordered programmes: editor draft saves with stable item IDs
/// (update in place, full ordered replacement, repeated songs as distinct
/// entries), draft invisibility for members, atomic publication stamps,
/// stale rowVersion conflicts, unavailable/private selections rejected at
/// the publish boundary, German validation problems and the untouched event
/// row (merely passing the event date never marks songs performed).
/// </summary>
public sealed class ProgrammeApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task EditorSavesProgrammeItemsWithStableIdsAndReorder()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Erstes Lied",
			versionLabel: "Grundtonart", musicalKey: "G-Dur");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied",
			versionLabel: "Notenausgabe");
		var transposedB = await CreateVersionAsync(client, editorSession, songB.ArrangementId,
			"Tiefe Tonart", "F-Dur");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Frühjahrskonzert" });

		// First save: song A twice (repeated song, distinct entries) and song
		// B in its transposed version; creation omits the rowVersion.
		var firstItems = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId, note = "Eröffnung" },
			new { songId = songB.SongId, musicalVersionId = transposedB, note = "Transposition" },
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
		};
		using var firstSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items = firstItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, firstSave.StatusCode);
		var first = await ProgrammeOfAsync(firstSave);
		Assert.Equal(1, first.GetProperty("working").GetProperty("number").GetInt32());
		Assert.True(first.GetProperty("published").ValueKind is JsonValueKind.Null);
		var firstEntries = first.GetProperty("working").GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(3, firstEntries.Count);
		Assert.Equal(new[] { 1, 2, 3 }, firstEntries.Select(i => i.GetProperty("position").GetInt32()).ToList());
		// The repeated song A carries two distinct stable item IDs.
		var entryA1 = firstEntries[0].GetProperty("id").GetString()!;
		var entryB = firstEntries[1].GetProperty("id").GetString()!;
		var entryA2 = firstEntries[2].GetProperty("id").GetString()!;
		Assert.NotEqual(entryA1, entryA2);
		Assert.Equal("Erstes Lied", firstEntries[0].GetProperty("songTitle").GetString());
		Assert.Equal("Transposition", firstEntries[1].GetProperty("note").GetString());
		var rowVersionAfterFirstSave = first.GetProperty("rowVersion").GetUInt32();

		// Second save: reorder, edit the note, remove the transposed entry
		// and add a fresh song B entry — surviving entries keep their IDs.
		var secondItems = new object[]
		{
			new { id = entryA2, songId = songA.SongId, musicalVersionId = songA.VersionId },
			new { id = entryA1, songId = songA.SongId, musicalVersionId = songA.VersionId, note = "Geänderte Notiz" },
			new { songId = songB.SongId, musicalVersionId = songB.VersionId },
		};
		using var secondSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items = secondItems, rowVersion = rowVersionAfterFirstSave }, editorSession);
		Assert.Equal(HttpStatusCode.OK, secondSave.StatusCode);
		var second = await ProgrammeOfAsync(secondSave);
		Assert.Equal(first.GetProperty("working").GetProperty("id").GetString(),
			second.GetProperty("working").GetProperty("id").GetString());
		var secondEntries = second.GetProperty("working").GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(3, secondEntries.Count);
		Assert.Equal(new[] { 1, 2, 3 }, secondEntries.Select(i => i.GetProperty("position").GetInt32()).ToList());
		var secondIds = secondEntries.Select(i => i.GetProperty("id").GetString()!).ToList();
		// Positions follow the array order; the removed entry is gone.
		Assert.Equal(entryA2, secondIds[0]);
		Assert.Equal(entryA1, secondIds[1]);
		Assert.DoesNotContain(entryB, secondIds);
		var addedEntry = secondIds.Single(id => id != entryA1 && id != entryA2);
		Assert.Equal("Geänderte Notiz", secondEntries[1].GetProperty("note").GetString());
		Assert.True(secondEntries[2].GetProperty("note").ValueKind is JsonValueKind.Null);
		Assert.Equal(rowVersionAfterFirstSave + 1, second.GetProperty("rowVersion").GetUInt32());

		// The revision items persist with their stable IDs and positions.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var items = await db.ProgrammeItems
				.Where(i => i.Revision!.Programme!.EventId == eventId)
				.OrderBy(i => i.Position).ToListAsync();
			Assert.Equal(3, items.Count);
			Assert.Equal(new[] { entryA2, entryA1, addedEntry }, items.Select(i => i.Id.ToString()).ToList());
			Assert.Equal(new[] { 1, 2, 3 }, items.Select(i => i.Position).ToList());
			Assert.Equal("Geänderte Notiz", items[1].Note);
			Assert.Null(items[2].Note);
			// The working embed's updatedAt tracks the programme aggregate,
			// not the revision's frozen creation instant.
			var programme = await db.Programmes.SingleAsync(p => p.EventId == eventId);
			Assert.Equal(programme.UpdatedAt,
				DateTimeOffset.Parse(second.GetProperty("working")
					.GetProperty("updatedAt").GetString()!));
		}
	}

	[Fact]
	public async Task AdjacentSwapOfSurvivingEntriesPersistsFinalOrder()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Erstes Lied");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Nachbartausch" });

		// Two entries, then the frontend's ↑/↓ shape: a pure adjacent swap of
		// the two surviving rows. On PostgreSQL this reorder is a dependency
		// cycle for the unique (RevisionId, Position) index when renumbered
		// in place; the two-phase save must answer 200 with the final order.
		var firstItems = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
			new { songId = songB.SongId, musicalVersionId = songB.VersionId },
		};
		using var firstSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items = firstItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, firstSave.StatusCode);
		var first = await ProgrammeOfAsync(firstSave);
		var firstEntries = first.GetProperty("working").GetProperty("items").EnumerateArray().ToList();
		var idA = firstEntries[0].GetProperty("id").GetString()!;
		var idB = firstEntries[1].GetProperty("id").GetString()!;
		var rowVersion = first.GetProperty("rowVersion").GetUInt32();

		var swapItems = new object[]
		{
			new { id = idB, songId = songB.SongId, musicalVersionId = songB.VersionId },
			new { id = idA, songId = songA.SongId, musicalVersionId = songA.VersionId },
		};
		using var swapSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items = swapItems, rowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.OK, swapSave.StatusCode);
		var swapped = await ProgrammeOfAsync(swapSave);
		Assert.Equal(rowVersion + 1, swapped.GetProperty("rowVersion").GetUInt32());
		var swapEntries = swapped.GetProperty("working").GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(2, swapEntries.Count);
		Assert.Equal(new[] { 1, 2 }, swapEntries.Select(i => i.GetProperty("position").GetInt32()).ToList());
		Assert.Equal(new[] { idB, idA }, swapEntries.Select(i => i.GetProperty("id").GetString()!).ToList());

		// The final contiguous order persists with the stable ids intact.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var items = await db.ProgrammeItems
				.Where(i => i.Revision!.Programme!.EventId == eventId)
				.OrderBy(i => i.Position).ToListAsync();
			Assert.Equal(2, items.Count);
			Assert.Equal(new[] { idB, idA }, items.Select(i => i.Id.ToString()).ToList());
			Assert.Equal(new[] { 1, 2 }, items.Select(i => i.Position).ToList());
		}
	}

	[Fact]
	public async Task DraftProgrammeStaysHiddenFromMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Lied im Entwurf");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Auftritt mit Entwurf" });
		await PublishEventAsync(client, editorSession, eventId);
		var items = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
		};

		// Members may not save drafts.
		using var memberSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items }, memberSession);
		Assert.Equal(HttpStatusCode.Forbidden, memberSave.StatusCode);
		var memberProblem = await memberSave.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(EventEndpoints.ForbiddenMessage, memberProblem.GetProperty("title").GetString());

		// Anonymous callers receive 401.
		var (anonymousCookie, anonymousToken) = await GetCsrfAsync(client);
		using var anonymousSave = AuthedPut($"/api/events/{eventId}/programme/items",
			new { items }, anonymousCookie, anonymousToken);
		using var anonymousResponse = await client.SendAsync(anonymousSave);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

		// The editor's draft exists; the editor detail shows the working
		// revision without a published one.
		using var editorSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items }, editorSession);
		Assert.Equal(HttpStatusCode.OK, editorSave.StatusCode);
		var editorDetail = await GetEventDetailAsync(client, editorSession, eventId);
		Assert.Equal(1, editorDetail.GetProperty("programme").GetProperty("working")
			.GetProperty("number").GetInt32());
		Assert.True(editorDetail.GetProperty("programme").GetProperty("published").ValueKind
			is JsonValueKind.Null);

		// The member detail carries no programme (indistinguishable from
		// having none at all before the first publication).
		var memberDetail = await GetEventDetailAsync(client, memberSession, eventId);
		Assert.True(memberDetail.GetProperty("programme").ValueKind is JsonValueKind.Null);

		// Anonymous callers never reach the detail at all.
		using var anonymousDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		using var anonymousDetailResponse = await client.SendAsync(anonymousDetail);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousDetailResponse.StatusCode);

		// The member programme list stays empty before the first publication.
		Assert.Empty((await GetProgrammeListAsync(client, memberSession)).EnumerateArray());
	}

	[Fact]
	public async Task PublishStampsRevisionAndMembersSeeOrderedProgramme()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Eröffnungslied",
			arrangementLabel: "SATB-Fassung", voiceConfiguration: "SATB",
			versionLabel: "Grundtonart", musicalKey: "G-Dur");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied",
			versionLabel: "Notenausgabe", musicalKey: "Es-Dur");
		var transposedB = await CreateVersionAsync(client, editorSession, songB.ArrangementId,
			"Tiefe Tonart", "C-Dur");
		var eventId = await CreateEventAsync(client, editorSession, new
		{
			kind = "concert",
			title = "Frühjahrskonzert 2099",
			venue = "Stadtpfarrkirche",
			dateYear = 2099,
			dateMonth = 5,
			dateDay = 12,
			startTime = "19:30",
		});
		await PublishEventAsync(client, editorSession, eventId);
		var items = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId, note = "Stehchoral" },
			new { songId = songB.SongId, musicalVersionId = transposedB },
			new { songId = songA.SongId, musicalVersionId = songA.VersionId, note = "Zugabe" },
		};
		using var saveResponse = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items }, editorSession);
		Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
		var saved = await ProgrammeOfAsync(saveResponse);
		var draftId = saved.GetProperty("working").GetProperty("id").GetString()!;

		// Publishing stamps the working revision with the acting editor.
		using var publishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = saved.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		var published = await ProgrammeOfAsync(publishResponse);
		Assert.True(published.GetProperty("working").ValueKind is JsonValueKind.Null);
		var publishedRevision = published.GetProperty("published");
		Assert.Equal(draftId, publishedRevision.GetProperty("id").GetString());
		Assert.Equal(1, publishedRevision.GetProperty("number").GetInt32());
		Assert.NotNull(publishedRevision.GetProperty("publishedAt").GetString());
		Assert.Equal(3, publishedRevision.GetProperty("items").EnumerateArray().ToList().Count);

		// Members see the ordered programme with the display fields and the
		// ids for their own deep link into the song page.
		var memberDetail = await GetEventDetailAsync(client, memberSession, eventId);
		var memberProgramme = memberDetail.GetProperty("programme");
		Assert.True(memberProgramme.GetProperty("working").ValueKind is JsonValueKind.Null);
		var memberPublished = memberProgramme.GetProperty("published");
		Assert.Equal(1, memberPublished.GetProperty("number").GetInt32());
		var memberItems = memberPublished.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(3, memberItems.Count);
		Assert.Equal(new[] { 1, 2, 3 }, memberItems.Select(i => i.GetProperty("position").GetInt32()).ToList());
		Assert.Equal("Eröffnungslied", memberItems[0].GetProperty("songTitle").GetString());
		Assert.Equal("SATB-Fassung", memberItems[0].GetProperty("arrangementLabel").GetString());
		Assert.Equal("SATB", memberItems[0].GetProperty("voiceConfiguration").GetString());
		Assert.Equal("Grundtonart", memberItems[0].GetProperty("musicalVersionLabel").GetString());
		Assert.Equal("G-Dur", memberItems[0].GetProperty("musicalKey").GetString());
		Assert.Equal("Stehchoral", memberItems[0].GetProperty("note").GetString());
		Assert.Equal(songA.SongId, Guid.Parse(memberItems[0].GetProperty("songId").GetString()!));
		Assert.Equal(songA.ArrangementId, Guid.Parse(memberItems[0].GetProperty("arrangementId").GetString()!));
		Assert.Equal(songA.VersionId, Guid.Parse(memberItems[0].GetProperty("musicalVersionId").GetString()!));
		// The transposed entry repeats song A as a distinct item.
		Assert.Equal(songA.SongId, Guid.Parse(memberItems[2].GetProperty("songId").GetString()!));
		Assert.NotEqual(memberItems[0].GetProperty("id").GetString(),
			memberItems[2].GetProperty("id").GetString());
		// Unknown values stay null instead of invented placeholders.
		Assert.Equal("Zweites Lied", memberItems[1].GetProperty("songTitle").GetString());
		Assert.Equal("Standardfassung", memberItems[1].GetProperty("arrangementLabel").GetString());
		Assert.True(memberItems[1].GetProperty("voiceConfiguration").ValueKind is JsonValueKind.Null);
		Assert.Equal("C-Dur", memberItems[1].GetProperty("musicalKey").GetString());
		Assert.True(memberItems[1].GetProperty("note").ValueKind is JsonValueKind.Null);

		// A second event with an unknown date and a published programme.
		var unknownDateEventId = await CreateEventAsync(client, editorSession,
			new { kind = "service", title = "Gottesdienst ohne Datum" });
		await PublishEventAsync(client, editorSession, unknownDateEventId);
		var unknownItems = new object[]
		{
			new { songId = songB.SongId, musicalVersionId = songB.VersionId },
		};
		using var unknownSaveResponse = await PutJsonAsync(client,
			$"/api/events/{unknownDateEventId}/programme/items", new { items = unknownItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, unknownSaveResponse.StatusCode);
		using var unknownPublishResponse = await PostJsonAsync(client,
			$"/api/events/{unknownDateEventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(unknownSaveResponse)).GetProperty("rowVersion").GetUInt32() },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, unknownPublishResponse.StatusCode);

		// Upcoming list: the dated programme soonest first, the unknown one last.
		var programmes = (await GetProgrammeListAsync(client, memberSession)).EnumerateArray().ToList();
		Assert.Equal(2, programmes.Count);
		Assert.Equal(eventId, Guid.Parse(programmes[0].GetProperty("eventId").GetString()!));
		Assert.Equal("Frühjahrskonzert 2099", programmes[0].GetProperty("eventTitle").GetString());
		Assert.Equal("concert", programmes[0].GetProperty("kind").GetString());
		Assert.Equal("12. Mai 2099", programmes[0].GetProperty("dateDisplay").GetString());
		Assert.Equal("day", programmes[0].GetProperty("datePrecision").GetString());
		Assert.False(programmes[0].GetProperty("dateApproximate").GetBoolean());
		Assert.Equal("Stadtpfarrkirche", programmes[0].GetProperty("venue").GetString());
		Assert.Equal("19:30", programmes[0].GetProperty("startTime").GetString());
		Assert.NotNull(programmes[0].GetProperty("publishedAt").GetString());
		Assert.Equal(3, programmes[0].GetProperty("itemCount").GetInt32());
		Assert.Equal(unknownDateEventId, Guid.Parse(programmes[1].GetProperty("eventId").GetString()!));
		Assert.Equal("Datum unbekannt", programmes[1].GetProperty("dateDisplay").GetString());
		Assert.Equal("unknown", programmes[1].GetProperty("datePrecision").GetString());
		Assert.Equal(1, programmes[1].GetProperty("itemCount").GetInt32());

		// A later draft never leaks to members: they keep seeing the
		// published revision while the editor works on a fresh one.
		var editorRowVersion = await EditorProgrammeRowVersionAsync(client, editorSession, eventId);
		var draftItems = new object[]
		{
			new { songId = songB.SongId, musicalVersionId = transposedB, note = "Neuer Entwurf" },
		};
		using var draftSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items",
			new { items = draftItems, rowVersion = editorRowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.OK, draftSaveResponse.StatusCode);
		var draft = await ProgrammeOfAsync(draftSaveResponse);
		Assert.Equal(2, draft.GetProperty("working").GetProperty("number").GetInt32());
		Assert.Single(draft.GetProperty("working").GetProperty("items").EnumerateArray());

		var memberDetailAfter = await GetEventDetailAsync(client, memberSession, eventId);
		var memberProgrammeAfter = memberDetailAfter.GetProperty("programme");
		Assert.Equal(1, memberProgrammeAfter.GetProperty("published").GetProperty("number").GetInt32());
		Assert.Equal(3, memberProgrammeAfter.GetProperty("published")
			.GetProperty("items").EnumerateArray().ToList().Count);
		var programmesAfter = (await GetProgrammeListAsync(client, memberSession)).EnumerateArray().ToList();
		Assert.Equal(3, programmesAfter[0].GetProperty("itemCount").GetInt32());

		// The publication stamp is persisted with the acting editor.
		var editorId = await UserIdAsync(factory, Editor);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var revision = await db.ProgrammeRevisions.SingleAsync(r => r.Id == Guid.Parse(draftId));
			Assert.NotNull(revision.PublishedAt);
			Assert.Equal(editorId, revision.PublishedByAccountId);
		}
	}

	[Fact]
	public async Task SecondPublishCreatesFreshDraftAndSupersedesFirstRevision()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Erstes Lied");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Sommerkonzert", dateYear = 2099, dateMonth = 7 });
		await PublishEventAsync(client, editorSession, eventId);
		var firstItems = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
			new { songId = songB.SongId, musicalVersionId = songB.VersionId },
		};
		using var firstSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items", new { items = firstItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, firstSaveResponse.StatusCode);
		var first = await ProgrammeOfAsync(firstSaveResponse);
		var firstRevisionId = first.GetProperty("working").GetProperty("id").GetString()!;
		var firstItemIds = first.GetProperty("working").GetProperty("items").EnumerateArray()
			.Select(i => i.GetProperty("id").GetString()!).ToList();
		using var firstPublishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = first.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, firstPublishResponse.StatusCode);
		DateTimeOffset firstPublishedAt;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			firstPublishedAt = (await db.ProgrammeRevisions
				.SingleAsync(r => r.Id == Guid.Parse(firstRevisionId))).PublishedAt!.Value;
		}

		// The next save starts a fresh, empty draft revision.
		var draftRowVersion = (await ProgrammeOfAsync(firstPublishResponse))
			.GetProperty("rowVersion").GetUInt32();
		using var draftSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items",
			new { items = Array.Empty<object>(), rowVersion = draftRowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.OK, draftSaveResponse.StatusCode);
		var draft = await ProgrammeOfAsync(draftSaveResponse);
		Assert.Equal(2, draft.GetProperty("working").GetProperty("number").GetInt32());
		Assert.NotEqual(firstRevisionId, draft.GetProperty("working").GetProperty("id").GetString());
		Assert.Empty(draft.GetProperty("working").GetProperty("items").EnumerateArray());

		// Items added to the fresh draft carry brand-new IDs.
		var secondItems = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId, note = "Neu gesetzt" },
		};
		using var secondSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items",
			new { items = secondItems, rowVersion = draft.GetProperty("rowVersion").GetUInt32() },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, secondSaveResponse.StatusCode);
		var second = await ProgrammeOfAsync(secondSaveResponse);
		var secondEntry = second.GetProperty("working").GetProperty("items").EnumerateArray().Single();
		var secondItemId = secondEntry.GetProperty("id").GetString()!;
		Assert.DoesNotContain(secondItemId, firstItemIds);

		// Publishing again supersedes revision 1 with revision 2.
		using var secondPublishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = second.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, secondPublishResponse.StatusCode);
		var republished = await ProgrammeOfAsync(secondPublishResponse);
		Assert.True(republished.GetProperty("working").ValueKind is JsonValueKind.Null);
		Assert.Equal(2, republished.GetProperty("published").GetProperty("number").GetInt32());

		// Members see the newest revision only.
		var memberDetail = await GetEventDetailAsync(client, memberSession, eventId);
		var memberProgramme = memberDetail.GetProperty("programme");
		Assert.Equal(2, memberProgramme.GetProperty("published").GetProperty("number").GetInt32());
		var memberItems = memberProgramme.GetProperty("published").GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(secondItemId, memberItems.Single().GetProperty("id").GetString());
		Assert.Equal("Neu gesetzt", memberItems.Single().GetProperty("note").GetString());
		var programmes = (await GetProgrammeListAsync(client, memberSession)).EnumerateArray().ToList();
		Assert.Equal(1, programmes.Single().GetProperty("itemCount").GetInt32());

		// Revision 1 stays frozen history: stamp and items untouched.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var firstRevision = await db.ProgrammeRevisions
				.Include(r => r.Items)
				.SingleAsync(r => r.Id == Guid.Parse(firstRevisionId));
			Assert.Equal(firstPublishedAt, firstRevision.PublishedAt);
			Assert.Equal(firstItemIds, firstRevision.Items.OrderBy(i => i.Position)
				.Select(i => i.Id.ToString()).ToList());
			var secondRevision = await db.ProgrammeRevisions.SingleAsync(r =>
				r.Id == Guid.Parse(republished.GetProperty("published").GetProperty("id").GetString()!));
			Assert.NotNull(secondRevision.PublishedAt);
			Assert.True(secondRevision.PublishedAt!.Value >= firstPublishedAt);
		}
	}

	[Fact]
	public async Task PublishedProgrammeOnDraftEventStaysHiddenUntilEventPublish()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Entwurfsauftrittslied");
		// Event publication is independent (ARC-024): the programme is
		// published while the event itself is still a draft.
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Noch unveröffentlichter Auftritt" });
		var items = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
		};
		using var saveResponse = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items }, editorSession);
		Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
		using var publishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(saveResponse))
				.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

		// Members stay blind: no programme list entry, and the draft event
		// detail answers its indistinguishable 404.
		Assert.Empty((await GetProgrammeListAsync(client, memberSession)).EnumerateArray());
		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.NotFound, memberDetailResponse.StatusCode);
		var memberProblem = await memberDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(EventEndpoints.NotFoundMessage, memberProblem.GetProperty("title").GetString());

		// Publishing the event lifts the veil: the programme appears in the
		// member list and rides the member event detail embed.
		await PublishEventAsync(client, editorSession, eventId);
		var programmes = (await GetProgrammeListAsync(client, memberSession)).EnumerateArray().ToList();
		Assert.Single(programmes);
		Assert.Equal(eventId, Guid.Parse(programmes[0].GetProperty("eventId").GetString()!));
		Assert.Equal(1, programmes[0].GetProperty("itemCount").GetInt32());
		var detail = await GetEventDetailAsync(client, memberSession, eventId);
		Assert.Equal(1, detail.GetProperty("programme").GetProperty("published")
			.GetProperty("number").GetInt32());
	}

	[Fact]
	public async Task StaleRowVersionConflictsReturn409OnSaveAndPublish()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Lied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Konzert" });
		var items = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
		};

		// Creation ignores the rowVersion (the client omits it).
		using var firstSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items", new { items }, editorSession);
		Assert.Equal(HttpStatusCode.OK, firstSaveResponse.StatusCode);
		var rowVersion = (await ProgrammeOfAsync(firstSaveResponse))
			.GetProperty("rowVersion").GetUInt32();

		// A stale save conflicts with the German concurrency title.
		using var staleSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items",
			new { items, rowVersion = rowVersion - 1 }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, staleSaveResponse.StatusCode);
		var staleProblem = await staleSaveResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ProgrammeEndpoints.ConcurrencyMessage, staleProblem.GetProperty("title").GetString());

		// The correctly versioned save succeeds and bumps the token.
		using var secondSaveResponse = await PutJsonAsync(client,
			$"/api/events/{eventId}/programme/items",
			new { items, rowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.OK, secondSaveResponse.StatusCode);
		var freshRowVersion = (await ProgrammeOfAsync(secondSaveResponse))
			.GetProperty("rowVersion").GetUInt32();

		// Publishing with a stale token conflicts as well.
		using var stalePublishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, stalePublishResponse.StatusCode);
		var stalePublishProblem = await stalePublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ProgrammeEndpoints.ConcurrencyMessage, stalePublishProblem.GetProperty("title").GetString());

		// The correctly versioned publish succeeds; another publish answers
		// that the programme is already published (no unpublish in ARC-026).
		using var publishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = freshRowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		var publishedRowVersion = (await ProgrammeOfAsync(publishResponse))
			.GetProperty("rowVersion").GetUInt32();
		using var secondPublishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = publishedRowVersion }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, secondPublishResponse.StatusCode);
		var secondPublishProblem = await secondPublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ProgrammeEndpoints.AlreadyPublishedMessage,
			secondPublishProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task PublishRejectsUnavailableAndPrivateSelections()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// A draft song may be drafted into the programme but not published.
		var draftSong = await CreateSongAsync(client, editorSession, "Entwurfslied", publish: false);
		var privateEventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Konzert mit Entwurfslied" });
		var privateItems = new object[]
		{
			new { songId = draftSong.SongId, musicalVersionId = draftSong.VersionId },
		};
		using var privateSaveResponse = await PutJsonAsync(client,
			$"/api/events/{privateEventId}/programme/items", new { items = privateItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, privateSaveResponse.StatusCode);
		using var privatePublishResponse = await PostJsonAsync(client,
			$"/api/events/{privateEventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(privateSaveResponse))
				.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, privatePublishResponse.StatusCode);
		var privateProblem = await privatePublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ProgrammeEndpoints.UnavailableVersionsMessage, privateProblem.GetProperty("title").GetString());
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var revision = await db.ProgrammeRevisions
				.SingleAsync(r => r.Programme!.EventId == privateEventId);
			Assert.Null(revision.PublishedAt);
		}

		// A version that disappears between draft and publish rejects too.
		var publishedSong = await CreateSongAsync(client, editorSession, "Veröffentlichtes Lied");
		var vanishedEventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Konzert mit verschwundener Fassung" });
		var vanishedItems = new object[]
		{
			new { songId = publishedSong.SongId, musicalVersionId = publishedSong.VersionId },
		};
		using var vanishedSaveResponse = await PutJsonAsync(client,
			$"/api/events/{vanishedEventId}/programme/items", new { items = vanishedItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, vanishedSaveResponse.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			db.MusicalVersions.Remove(await db.MusicalVersions
				.SingleAsync(v => v.Id == publishedSong.VersionId));
			await db.SaveChangesAsync();
		}
		using var vanishedPublishResponse = await PostJsonAsync(client,
			$"/api/events/{vanishedEventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(vanishedSaveResponse))
				.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, vanishedPublishResponse.StatusCode);
		var vanishedProblem = await vanishedPublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ProgrammeEndpoints.UnavailableVersionsMessage,
			vanishedProblem.GetProperty("title").GetString());

		// An empty programme cannot be published.
		var emptyEventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Konzert ohne Lieder" });
		using var emptySaveResponse = await PutJsonAsync(client,
			$"/api/events/{emptyEventId}/programme/items",
			new { items = Array.Empty<object>() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, emptySaveResponse.StatusCode);
		using var emptyPublishResponse = await PostJsonAsync(client,
			$"/api/events/{emptyEventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(emptySaveResponse))
				.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, emptyPublishResponse.StatusCode);
		var emptyProblem = await emptyPublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(ProgrammeEndpoints.EmptyProgrammeMessage, emptyProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ValidationErrorsReturnGermanProblems()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Erstes Lied");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Konzert" });

		async Task AssertRejected(object body, string expectedTitle)
		{
			using var saveResponse = await PutJsonAsync(client,
				$"/api/events/{eventId}/programme/items", body, editorSession);
			Assert.Equal(HttpStatusCode.BadRequest, saveResponse.StatusCode);
			var problem = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
		}

		await AssertRejected(new
		{
			items = new object[]
			{
				new { songId = songA.SongId, musicalVersionId = songA.VersionId, note = new string('x', 501) },
			},
		}, ProgrammeEndpoints.NoteTooLongMessage);
		await AssertRejected(new
		{
			items = new object[] { new { songId = Guid.NewGuid(), musicalVersionId = songA.VersionId } },
		}, ProgrammeEndpoints.MissingVersionMessage);
		await AssertRejected(new
		{
			items = new object[] { new { songId = songA.SongId, musicalVersionId = Guid.NewGuid() } },
		}, ProgrammeEndpoints.MissingVersionMessage);
		// The version's arrangement must belong to the requested song.
		await AssertRejected(new
		{
			items = new object[] { new { songId = songB.SongId, musicalVersionId = songA.VersionId } },
		}, ProgrammeEndpoints.MissingVersionMessage);
		// A null entry in the items array is malformed input, not a song.
		await AssertRejected(new
		{
			items = new object?[]
			{
				null,
				new { songId = songA.SongId, musicalVersionId = songA.VersionId },
			},
		}, ProgrammeEndpoints.InvalidItemMessage);

		// Nothing was written: failing entries among valid ones leave no
		// programme behind (all-or-nothing validation before any write).
		var detail = await GetEventDetailAsync(client, editorSession, eventId);
		Assert.True(detail.GetProperty("programme").ValueKind is JsonValueKind.Null);

		// A duplicated item id is rejected before any write: two entries
		// referencing the SAME id would silently route both into one row —
		// the later entry overwrites its twin's song and the full replacement
		// drops every other entry. The meaningful case repeats a KNOWN id, so
		// first save two real entries (no programme yet, rowVersion ignored).
		var seedItems = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
			new { songId = songB.SongId, musicalVersionId = songB.VersionId },
		};
		using var seedSave = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items = seedItems }, editorSession);
		Assert.Equal(HttpStatusCode.OK, seedSave.StatusCode);
		var seed = await ProgrammeOfAsync(seedSave);
		var seedEntries = seed.GetProperty("working").GetProperty("items").EnumerateArray().ToList();
		var twinId = seedEntries[0].GetProperty("id").GetString()!;
		await AssertRejected(new
		{
			items = new object[]
			{
				new { id = twinId, songId = songA.SongId, musicalVersionId = songA.VersionId },
				new { id = twinId, songId = songB.SongId, musicalVersionId = songB.VersionId },
			},
			rowVersion = seed.GetProperty("rowVersion").GetUInt32(),
		}, ProgrammeEndpoints.InvalidItemMessage);

		// The guard is broad (it fires on any duplicated id, existing or not)
		// and the rejection touched nothing: both entries keep their ids,
		// positions and song assignment from the last successful save.
		var detailAfter = await GetEventDetailAsync(client, editorSession, eventId);
		var itemsAfter = detailAfter.GetProperty("programme").GetProperty("working")
			.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(2, itemsAfter.Count);
		Assert.Equal(new[] { 1, 2 }, itemsAfter.Select(i => i.GetProperty("position").GetInt32()).ToList());
		Assert.Equal(twinId, itemsAfter[0].GetProperty("id").GetString());
		Assert.Equal(seedEntries[1].GetProperty("id").GetString(), itemsAfter[1].GetProperty("id").GetString());
		Assert.Equal(songA.SongId, Guid.Parse(itemsAfter[0].GetProperty("songId").GetString()!));
		Assert.Equal(songB.SongId, Guid.Parse(itemsAfter[1].GetProperty("songId").GetString()!));
	}

	[Fact]
	public async Task PublishingProgrammeLeavesEventRowAndSongsUnmarked()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Historisches Lied");
		var eventId = await CreateEventAsync(client, editorSession, new
		{
			kind = "concert",
			title = "Historisches Konzert",
			dateYear = 1950,
			dateMonth = 5,
			dateDay = 12,
		});
		await PublishEventAsync(client, editorSession, eventId);
		var detailBefore = await GetEventDetailAsync(client, editorSession, eventId);
		var publishedAtBefore = DateTimeOffset.Parse(detailBefore.GetProperty("publishedAt").GetString()!);
		var updatedAtBefore = DateTimeOffset.Parse(detailBefore.GetProperty("updatedAt").GetString()!);
		DateTimeOffset eventPublishedAtBefore;
		DateTimeOffset eventUpdatedAtBefore;
		long eventRowVersionBefore;
		long songRowVersionBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			eventPublishedAtBefore = persisted.PublishedAt!.Value;
			eventUpdatedAtBefore = persisted.UpdatedAt;
			eventRowVersionBefore = persisted.RowVersion;
			songRowVersionBefore = (await db.Songs.SingleAsync(s => s.Id == songA.SongId)).RowVersion;
		}

		var items = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
		};
		using var saveResponse = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items }, editorSession);
		Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
		using var publishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(saveResponse))
				.GetProperty("rowVersion").GetUInt32() }, editorSession);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

		// The event detail keeps its own publication state and carries no
		// performed/confirmed fields anywhere in the response.
		var detailAfter = await GetEventDetailAsync(client, memberSession, eventId);
		Assert.True(detailAfter.GetProperty("published").GetBoolean());
		Assert.Equal(publishedAtBefore,
			DateTimeOffset.Parse(detailAfter.GetProperty("publishedAt").GetString()!));
		Assert.Equal(updatedAtBefore,
			DateTimeOffset.Parse(detailAfter.GetProperty("updatedAt").GetString()!));
		Assert.Equal("12. Mai 1950", detailAfter.GetProperty("dateDisplay").GetString());
		Assert.Equal(1, detailAfter.GetProperty("programme").GetProperty("published")
			.GetProperty("number").GetInt32());
		AssertNoPerformedFields(detailAfter);

		// The event row and the song stay untouched by the programme publish.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.Equal(eventPublishedAtBefore, persisted.PublishedAt);
			Assert.Equal(eventUpdatedAtBefore, persisted.UpdatedAt);
			Assert.Equal(eventRowVersionBefore, persisted.RowVersion);
			Assert.Equal(songRowVersionBefore,
				(await db.Songs.SingleAsync(s => s.Id == songA.SongId)).RowVersion);
		}

		// The song detail carries no performed markers either.
		using var songRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songA.SongId}");
		songRequest.Headers.Add("Cookie", memberSession);
		using var songResponse = await client.SendAsync(songRequest);
		Assert.Equal(HttpStatusCode.OK, songResponse.StatusCode);
		AssertNoPerformedFields((await songResponse.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("song"));

		// The past exact date does not qualify as upcoming: the member list
		// only carries programmes while their date qualifies as upcoming.
		Assert.Empty((await GetProgrammeListAsync(client, memberSession)).EnumerateArray());
	}

	private static void AssertNoPerformedFields(JsonElement element)
	{
		if (element.ValueKind is JsonValueKind.Object)
		{
			foreach (var property in element.EnumerateObject())
			{
				Assert.False(property.Name.Contains("performed", StringComparison.OrdinalIgnoreCase)
					|| property.Name.Contains("confirmed", StringComparison.OrdinalIgnoreCase),
					$"Unexpected field {property.Name}.");
				AssertNoPerformedFields(property.Value);
			}
		}
		else if (element.ValueKind is JsonValueKind.Array)
		{
			foreach (var item in element.EnumerateArray())
				AssertNoPerformedFields(item);
		}
	}

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

	/// <summary>
	/// Creates a song with its POST /api/songs default chain (one arrangement
	/// carrying one musical version), optionally labels both and publishes
	/// the song, so programme items can reference the full chain.
	/// </summary>
	private static async Task<(Guid SongId, Guid ArrangementId, Guid VersionId)> CreateSongAsync(
		HttpClient client, string editorSession, string title,
		string? arrangementLabel = null, string? voiceConfiguration = null,
		string? versionLabel = null, string? musicalKey = null, bool publish = true)
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
		if (arrangementLabel is not null || voiceConfiguration is not null)
		{
			using var patch = AuthedPatch($"/api/arrangements/{arrangementId}",
				new { label = arrangementLabel, voiceConfiguration }, $"{cookie}; {editorSession}", token);
			using var patchResponse = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		}
		if (versionLabel is not null || musicalKey is not null)
		{
			using var patch = AuthedPatch($"/api/musical-versions/{versionId}",
				new { label = versionLabel, musicalKey }, $"{cookie}; {editorSession}", token);
			using var patchResponse = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		}
		if (publish)
		{
			using var publishSong = AuthedPost($"/api/songs/{songId}/publish", new { },
				$"{cookie}; {editorSession}", token);
			using var publishResponse = await client.SendAsync(publishSong);
			Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		}
		return (songId, arrangementId, versionId);
	}

	/// <summary>Adds a further labelled musical version to the arrangement.</summary>
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

	/// <summary>The editor's current programme rowVersion from the event detail.</summary>
	private static async Task<uint> EditorProgrammeRowVersionAsync(
		HttpClient client, string editorSession, Guid eventId)
	{
		var detail = await GetEventDetailAsync(client, editorSession, eventId);
		return detail.GetProperty("programme").GetProperty("rowVersion").GetUInt32();
	}

	private static async Task<JsonElement> GetProgrammeListAsync(HttpClient client, string session)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/programmes");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programmes");
	}

	private static async Task<JsonElement> ProgrammeOfAsync(HttpResponseMessage response)
		=> (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");

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

	private static async Task<HttpResponseMessage> PutJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPut(path, body, $"{cookie}; {session}", token));
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
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
}
