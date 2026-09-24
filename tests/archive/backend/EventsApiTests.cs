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
/// ARC-024 historical events ("Auftritte"): editor creation with the explicit
/// partial date model, draft visibility (members never see drafts, drafts
/// answer an indistinguishable 404), publication stamps with idempotent
/// republish/unpublish, second-editor PATCH attribution, precision-aware
/// newest-first sorting with the years navigation summary, year/kind filters
/// and German validation problems.
/// </summary>
public sealed class EventsApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";
	private const string SecondEditor = "zweitredaktion@liedertafel.test";

	[Fact]
	public async Task MemberAndAnonymousCannotCreateEvents()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (memberCookie, memberToken) = await GetCsrfAsync(client, memberSession);
		using var memberCreate = AuthedPost("/api/events",
			new { kind = "concert", title = "Frühjahrskonzert" }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberResponse = await client.SendAsync(memberCreate);
		Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
		var memberProblem = await memberResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(EventEndpoints.ForbiddenMessage, memberProblem.GetProperty("title").GetString());

		var (anonymousCookie, anonymousToken) = await GetCsrfAsync(client);
		using var anonymousCreate = AuthedPost("/api/events",
			new { kind = "concert", title = "Frühjahrskonzert" }, anonymousCookie, anonymousToken);
		using var anonymousResponse = await client.SendAsync(anonymousCreate);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
		var anonymousProblem = await anonymousResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Anmeldung erforderlich.", anonymousProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task DraftStaysHiddenFromMembersAndVisibleToEditors()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var eventId = await CreateEventAsync(factory, client: null, editorSession,
			new { kind = "concert", title = "Probenabschluss" });
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var memberList = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		memberList.Headers.Add("Cookie", memberSession);
		using var memberListResponse = await client.SendAsync(memberList);
		Assert.Equal(HttpStatusCode.OK, memberListResponse.StatusCode);
		var memberListBody = await memberListResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(0, memberListBody.GetProperty("total").GetInt32());
		Assert.Empty(memberListBody.GetProperty("events").EnumerateArray());

		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.NotFound, memberDetailResponse.StatusCode);
		var memberDetailProblem = await memberDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(EventEndpoints.NotFoundMessage, memberDetailProblem.GetProperty("title").GetString());

		using var editorList = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		editorList.Headers.Add("Cookie", editorSession);
		using var editorListResponse = await client.SendAsync(editorList);
		var editorListBody = await editorListResponse.Content.ReadFromJsonAsync<JsonElement>();
		var entry = Assert.Single(editorListBody.GetProperty("events").EnumerateArray());
		Assert.Equal("Probenabschluss", entry.GetProperty("title").GetString());
		Assert.False(entry.GetProperty("published").GetBoolean());
		Assert.Equal("unknown", entry.GetProperty("datePrecision").GetString());
		Assert.Equal("Datum unbekannt", entry.GetProperty("dateDisplay").GetString());

		using var editorDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		editorDetail.Headers.Add("Cookie", editorSession);
		using var editorDetailResponse = await client.SendAsync(editorDetail);
		Assert.Equal(HttpStatusCode.OK, editorDetailResponse.StatusCode);
	}

	[Fact]
	public async Task PublishStampsAttributionAndUnpublishClearsIt()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var eventId = await CreateEventAsync(factory, client: null, editorSession,
			new
			{
				kind = "concert",
				title = "Veröffentlichter Auftritt",
				dateYear = 1950,
				dateMonth = 5,
				dateDay = 12,
				notes = "Für den Stehchoral sind die Noten mitzunehmen.",
				sourceNote = "Programmheft im Vereinsarchiv, Blatt 3.",
			});
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var publish = AuthedPost($"/api/events/{eventId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var publishResponse = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		var publishBody = await publishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(publishBody.GetProperty("event").GetProperty("published").GetBoolean());

		using var memberList = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		memberList.Headers.Add("Cookie", memberSession);
		using var memberListResponse = await client.SendAsync(memberList);
		var memberListBody = await memberListResponse.Content.ReadFromJsonAsync<JsonElement>();
		var entry = Assert.Single(memberListBody.GetProperty("events").EnumerateArray());
		Assert.Equal("Veröffentlichter Auftritt", entry.GetProperty("title").GetString());
		Assert.True(entry.GetProperty("published").GetBoolean());
		Assert.Equal("12. Mai 1950", entry.GetProperty("dateDisplay").GetString());

		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.OK, memberDetailResponse.StatusCode);
		var detailBody = await memberDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		var detailEvent = detailBody.GetProperty("event");
		Assert.Equal("Veröffentlichter Auftritt", detailEvent.GetProperty("title").GetString());
		Assert.Equal("12. Mai 1950", detailEvent.GetProperty("dateDisplay").GetString());
		Assert.Equal("Für den Stehchoral sind die Noten mitzunehmen.", detailEvent.GetProperty("notes").GetString());
		Assert.Equal("Programmheft im Vereinsarchiv, Blatt 3.", detailEvent.GetProperty("sourceNote").GetString());
		var createdAt = DateTimeOffset.Parse(detailEvent.GetProperty("createdAt").GetString()!);
		var updatedAt = DateTimeOffset.Parse(detailEvent.GetProperty("updatedAt").GetString()!);
		Assert.True(createdAt > DateTimeOffset.MinValue);
		Assert.True(updatedAt >= createdAt);

		DateTimeOffset firstPublishedAt;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.Equal(editorId, persisted.PublishedByAccountId);
			Assert.NotNull(persisted.PublishedAt);
			firstPublishedAt = persisted.PublishedAt!.Value;
			var rowVersionAfterPublish = persisted.RowVersion;

			// Republish keeps the first stamp and is a no-op (no RowVersion bump).
			using var republish = AuthedPost($"/api/events/{eventId}/publish", new { }, $"{cookie}; {editorSession}", token);
			using var republishResponse = await client.SendAsync(republish);
			Assert.Equal(HttpStatusCode.OK, republishResponse.StatusCode);
			var after = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.Equal(firstPublishedAt, after.PublishedAt);
			Assert.Equal(editorId, after.PublishedByAccountId);
			Assert.Equal(rowVersionAfterPublish, after.RowVersion);
		}

		using var unpublish = AuthedPost($"/api/events/{eventId}/unpublish", new { }, $"{cookie}; {editorSession}", token);
		using var unpublishResponse = await client.SendAsync(unpublish);
		Assert.Equal(HttpStatusCode.OK, unpublishResponse.StatusCode);
		var unpublishBody = await unpublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(unpublishBody.GetProperty("event").GetProperty("published").GetBoolean());
		Assert.True(unpublishBody.GetProperty("event").GetProperty("publishedAt").ValueKind is JsonValueKind.Null);

		using var memberDetailAfter = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		memberDetailAfter.Headers.Add("Cookie", memberSession);
		using var memberDetailAfterResponse = await client.SendAsync(memberDetailAfter);
		Assert.Equal(HttpStatusCode.NotFound, memberDetailAfterResponse.StatusCode);

		// The editor still sees the withdrawn draft.
		using var editorDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		editorDetail.Headers.Add("Cookie", editorSession);
		using var editorDetailResponse = await client.SendAsync(editorDetail);
		Assert.Equal(HttpStatusCode.OK, editorDetailResponse.StatusCode);
		var editorDetailBody = await editorDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(editorDetailBody.GetProperty("event").GetProperty("published").GetBoolean());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.True(persisted.PublishedAt is null);
			Assert.True(persisted.PublishedByAccountId is null);
		}

		// Unpublishing a draft is a no-op success.
		using var unpublishAgain = AuthedPost($"/api/events/{eventId}/unpublish", new { }, $"{cookie}; {editorSession}", token);
		using var unpublishAgainResponse = await client.SendAsync(unpublishAgain);
		Assert.Equal(HttpStatusCode.OK, unpublishAgainResponse.StatusCode);
	}

	[Fact]
	public async Task SecondEditorPatchAttributesAndKeepsPublicationStamps()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var secondEditorId = await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		var eventId = await CreateEventAsync(factory, client: null, editorSession,
			new { kind = "concert", title = "Alter Titel" });
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (publishCookie, publishToken) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/events/{eventId}/publish", new { }, $"{publishCookie}; {editorSession}", publishToken);
		_ = await client.SendAsync(publish);

		DateTimeOffset firstPublishedAt;
		DateTimeOffset updatedAtBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			firstPublishedAt = persisted.PublishedAt!.Value;
			updatedAtBefore = persisted.UpdatedAt;
			Assert.Equal(editorId, persisted.PublishedByAccountId);
		}

		var (cookie, token) = await GetCsrfAsync(client, secondSession);
		using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/events/{eventId}");
		patch.Headers.Add("Cookie", $"{cookie}; {secondSession}");
		patch.Headers.Add("X-CSRF-TOKEN", token);
		patch.Content = JsonContent.Create(new
		{
			title = "Neuer Titel",
			venue = "Stadtpfarrkirche",
			date = new { year = 1950, month = 5, day = 12, approximate = false },
		});
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var updated = body.GetProperty("event");
		Assert.Equal("Neuer Titel", updated.GetProperty("title").GetString());
		Assert.Equal("Stadtpfarrkirche", updated.GetProperty("venue").GetString());
		Assert.Equal("day", updated.GetProperty("datePrecision").GetString());
		Assert.Equal("12. Mai 1950", updated.GetProperty("dateDisplay").GetString());
		Assert.False(updated.GetProperty("dateApproximate").GetBoolean());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.Equal(secondEditorId, persisted.UpdatedByAccountId);
			Assert.True(persisted.UpdatedAt > updatedAtBefore);
			Assert.Equal(DateTimeOffset.Parse(updated.GetProperty("updatedAt").GetString()!), persisted.UpdatedAt);
			// The nested date replacement stored all four date fields.
			Assert.Equal(1950, persisted.DateYear);
			Assert.Equal(5, persisted.DateMonth);
			Assert.Equal(12, persisted.DateDay);
			Assert.False(persisted.DateApproximate);
			// A patch by another editor never overwrites the publication stamps.
			Assert.Equal(firstPublishedAt, persisted.PublishedAt);
			Assert.Equal(editorId, persisted.PublishedByAccountId);
		}
	}

	[Fact]
	public async Task ListSortsNewestFirstWithYearsSummaryAndFilters()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var dayId = await CreateEventAsync(factory, client, editorSession,
			new { kind = "concert", title = "Maikonzert", dateYear = 1950, dateMonth = 5, dateDay = 12 });
		var monthId = await CreateEventAsync(factory, client, editorSession,
			new { kind = "concert", title = "Mai-Auftritt", dateYear = 1950, dateMonth = 5, dateApproximate = true });
		var yearId = await CreateEventAsync(factory, client, editorSession,
			new { kind = "festival", title = "Sängerfest um 1950", dateYear = 1950, dateApproximate = true });
		var olderId = await CreateEventAsync(factory, client, editorSession,
			new { kind = "service", title = "Gottesdienst 1949", dateYear = 1949, dateMonth = 11, dateDay = 11 });
		var unknownId = await CreateEventAsync(factory, client, editorSession,
			new { kind = "wedding", title = "Hochzeit ohne Datum" });
		foreach (var id in new[] { dayId, monthId, yearId, olderId, unknownId })
			await PublishAsync(client, editorSession, id);

		var list = await GetEventListAsync(client, memberSession, "/api/events");
		Assert.Equal(5, list.GetProperty("total").GetInt32());
		var orderedIds = list.GetProperty("events").EnumerateArray()
			.Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToList();
		Assert.Equal(new[] { dayId, monthId, yearId, olderId, unknownId }, orderedIds);
		var events = list.GetProperty("events").EnumerateArray().ToList();
		Assert.Equal("12. Mai 1950", events[0].GetProperty("dateDisplay").GetString());
		Assert.Equal("day", events[0].GetProperty("datePrecision").GetString());
		Assert.False(events[0].GetProperty("dateApproximate").GetBoolean());
		Assert.Equal("um Mai 1950", events[1].GetProperty("dateDisplay").GetString());
		Assert.Equal("month", events[1].GetProperty("datePrecision").GetString());
		Assert.Equal("um 1950", events[2].GetProperty("dateDisplay").GetString());
		Assert.Equal("year", events[2].GetProperty("datePrecision").GetString());
		Assert.Equal("11. November 1949", events[3].GetProperty("dateDisplay").GetString());
		Assert.Equal("Datum unbekannt", events[4].GetProperty("dateDisplay").GetString());
		Assert.Equal("unknown", events[4].GetProperty("datePrecision").GetString());
		Assert.All(events, e => Assert.True(e.GetProperty("published").GetBoolean()));

		var years = list.GetProperty("years").EnumerateArray().ToList();
		Assert.Equal(3, years.Count);
		Assert.Equal(1950, years[0].GetProperty("year").GetInt32());
		Assert.Equal(3, years[0].GetProperty("count").GetInt32());
		Assert.Equal(1949, years[1].GetProperty("year").GetInt32());
		Assert.Equal(1, years[1].GetProperty("count").GetInt32());
		Assert.True(years[2].GetProperty("year").ValueKind is JsonValueKind.Null);
		Assert.Equal(1, years[2].GetProperty("count").GetInt32());

		// Year filter keeps the years summary stable (it ignores the filters).
		var yearFiltered = await GetEventListAsync(client, memberSession, "/api/events?year=1950");
		Assert.Equal(3, yearFiltered.GetProperty("total").GetInt32());
		Assert.Equal(new[] { dayId, monthId, yearId }, yearFiltered.GetProperty("events").EnumerateArray()
			.Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToList());
		var filteredYears = yearFiltered.GetProperty("years").EnumerateArray().ToList();
		Assert.Equal(3, filteredYears.Count);
		Assert.Equal(1, filteredYears[1].GetProperty("count").GetInt32());

		// Kind filter narrows by the closed kind set.
		var kindFiltered = await GetEventListAsync(client, memberSession, "/api/events?kind=concert");
		Assert.Equal(2, kindFiltered.GetProperty("total").GetInt32());
		Assert.Equal(new[] { dayId, monthId }, kindFiltered.GetProperty("events").EnumerateArray()
			.Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToList());

		// Unknown kinds are rejected with a German problem.
		using var unknownKind = new HttpRequestMessage(HttpMethod.Get, "/api/events?kind=gala");
		unknownKind.Headers.Add("Cookie", memberSession);
		using var unknownKindResponse = await client.SendAsync(unknownKind);
		Assert.Equal(HttpStatusCode.BadRequest, unknownKindResponse.StatusCode);
		var unknownKindProblem = await unknownKindResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(EventEndpoints.KindUnknownMessage, unknownKindProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ValidationErrorsReturnGermanProblems()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		async Task AssertRejected(object body, string expectedTitle)
		{
			using var create = AuthedPost("/api/events", body, $"{cookie}; {editorSession}", token);
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
		}

		await AssertRejected(new { kind = "concert", title = "" }, EventEndpoints.TitleRequiredMessage);
		await AssertRejected(new { kind = "concert", title = "   " }, EventEndpoints.TitleRequiredMessage);
		await AssertRejected(new { kind = "concert", title = new string('x', 201) }, EventEndpoints.TitleTooLongMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", dateMonth = 5 }, EventEndpoints.IncompleteDateMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", dateYear = 1950, dateDay = 12 }, EventEndpoints.IncompleteDateMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", dateYear = 1950, dateMonth = 2, dateDay = 30 }, EventEndpoints.InvalidDateMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", dateYear = 1750 }, EventEndpoints.YearOutOfRangeMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", dateYear = 2101 }, EventEndpoints.YearOutOfRangeMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", startTime = "7 Uhr" }, EventEndpoints.StartTimeInvalidMessage);
		await AssertRejected(new { kind = "gala", title = "Ok" }, EventEndpoints.KindUnknownMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", venue = new string('x', 201) }, EventEndpoints.VenueTooLongMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", notes = new string('x', 2001) }, EventEndpoints.NotesTooLongMessage);
		await AssertRejected(new { kind = "concert", title = "Ok", sourceNote = new string('x', 501) }, EventEndpoints.SourceNoteTooLongMessage);
	}

	[Fact]
	public async Task PatchClearsOptionalsAndReplacesDateBlock()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var eventId = await CreateEventAsync(factory, client: null, editorSession, new
		{
			kind = "concert",
			title = "Konzert mit Angaben",
			venue = "Alte Halle",
			dateYear = 1950,
			dateMonth = 5,
			dateDay = 12,
			startTime = "19:30",
			notes = "Einführung um 19 Uhr",
			sourceNote = "Programmheft im Archiv",
		});
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		// A valid startTime change applies before the clearing pass.
		using var timePatch = AuthedPatch($"/api/events/{eventId}",
			new { startTime = "18:15" }, $"{cookie}; {editorSession}", token);
		using var timePatchResponse = await client.SendAsync(timePatch);
		Assert.Equal(HttpStatusCode.OK, timePatchResponse.StatusCode);
		var timeBody = await timePatchResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("18:15", timeBody.GetProperty("event").GetProperty("startTime").GetString());

		// Present but empty strings clear the optional fields; the nested date
		// object replaces the whole date block with an unknown date.
		using var patch = AuthedPatch($"/api/events/{eventId}", new
		{
			venue = "",
			notes = "",
			sourceNote = "",
			startTime = "",
			date = new { approximate = true },
		}, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var updated = body.GetProperty("event");
		Assert.True(updated.GetProperty("venue").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("notes").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("sourceNote").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("startTime").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("dateYear").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("dateMonth").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("dateDay").ValueKind is JsonValueKind.Null);
		Assert.True(updated.GetProperty("dateApproximate").GetBoolean());
		Assert.Equal("unknown", updated.GetProperty("datePrecision").GetString());
		Assert.Equal("Datum unbekannt", updated.GetProperty("dateDisplay").GetString());

		// Approximate year precision renders with the "um " prefix.
		using var datePatch = AuthedPatch($"/api/events/{eventId}",
			new { date = new { year = 1951, approximate = true } }, $"{cookie}; {editorSession}", token);
		using var datePatchResponse = await client.SendAsync(datePatch);
		Assert.Equal(HttpStatusCode.OK, datePatchResponse.StatusCode);
		var dateBody = await datePatchResponse.Content.ReadFromJsonAsync<JsonElement>();
		var replaced = dateBody.GetProperty("event");
		Assert.Equal(1951, replaced.GetProperty("dateYear").GetInt32());
		Assert.True(replaced.GetProperty("dateApproximate").GetBoolean());
		Assert.Equal("year", replaced.GetProperty("datePrecision").GetString());
		Assert.Equal("um 1951", replaced.GetProperty("dateDisplay").GetString());
	}

	[Fact]
	public void EventDateDisplayCoversAllPrecisions()
	{
		Assert.Equal("12. Mai 1950", EventDate.Display(1950, 5, 12, approximate: false));
		Assert.Equal("ca. 12. Mai 1950", EventDate.Display(1950, 5, 12, approximate: true));
		Assert.Equal("Mai 1950", EventDate.Display(1950, 5, null, approximate: false));
		Assert.Equal("um Mai 1950", EventDate.Display(1950, 5, null, approximate: true));
		Assert.Equal("1950", EventDate.Display(1950, null, null, approximate: false));
		Assert.Equal("um 1950", EventDate.Display(1950, null, null, approximate: true));
		Assert.Equal("Datum unbekannt", EventDate.Display(null, null, null, approximate: false));
		Assert.Equal("Datum unbekannt", EventDate.Display(null, 5, 12, approximate: true));
	}

	private static async Task<Guid> CreateEventAsync(
		AuthApiFactory factory, HttpClient? client, string editorSession, object body)
	{
		client ??= factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/events", body, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(parsed.GetProperty("event").GetProperty("id").GetString()!);
	}

	private static async Task PublishAsync(HttpClient client, string editorSession, Guid eventId)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/events/{eventId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<JsonElement> GetEventListAsync(HttpClient client, string session, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<JsonElement>();
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

	private static HttpRequestMessage AuthedPatch(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Patch, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}
}
