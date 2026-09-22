using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-020 catalogue search: tokenized AND matching over the member-visible
/// set with German folding, deterministic rank/Id pagination, draft
/// protection, arrangement context and the alternate-titles/lyrics PATCH
/// contract.
/// </summary>
public sealed class SearchApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task SearchFindsTitleComposerLyricistLyricsAndAlternateTitleWithMatchedIn()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var titleSong = await CreatePublishedSongAsync(factory, editorSession, "Müller vom Berg",
			composer: "Hans Steinbach", lyricist: "Karl Reimann", lyrics: "Tief im Böhmerwald",
			alternateTitles: ["Das Berglied"]);
		await PublishAsync(factory, editorSession, titleSong);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Title variant (umlaut-folded) hits the title.
		var (titleBody, _) = await SearchAsync(client, memberSession, "mueller");
		Assert.Equal(1, titleBody.GetProperty("total").GetInt32());
		Assert.Equal("Müller vom Berg", FirstSong(titleBody).GetProperty("title").GetString());
		Assert.Equal(["title"], FirstSong(titleBody).GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());

		// Same song via alternate title, composer, lyricist and lyrics.
		var (alternateBody, _) = await SearchAsync(client, memberSession, "Berglied");
		Assert.Equal("Müller vom Berg", FirstSong(alternateBody).GetProperty("title").GetString());
		Assert.Equal(["alternateTitles"], FirstSong(alternateBody).GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());

		var (composerBody, _) = await SearchAsync(client, memberSession, "reimann");
		Assert.Equal("Müller vom Berg", FirstSong(composerBody).GetProperty("title").GetString());
		Assert.Equal(["lyricist"], FirstSong(composerBody).GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());

		var (composerSongBody, _) = await SearchAsync(client, memberSession, "Hans Steinbach");
		Assert.Equal("Müller vom Berg", FirstSong(composerSongBody).GetProperty("title").GetString());
		Assert.Equal(["composer"], FirstSong(composerSongBody).GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());

		var (lyricsBody, _) = await SearchAsync(client, memberSession, "böhmerwald");
		Assert.Equal("Müller vom Berg", FirstSong(lyricsBody).GetProperty("title").GetString());
		Assert.Equal(["lyrics"], FirstSong(lyricsBody).GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());

		// Response contract fields around a query.
		Assert.Equal("mueller", titleBody.GetProperty("query").GetString());
		Assert.Equal(1, titleBody.GetProperty("page").GetInt32());
		Assert.Equal(20, titleBody.GetProperty("pageSize").GetInt32());
		Assert.Equal(1, titleBody.GetProperty("total").GetInt32());
		Assert.True(FirstSong(titleBody).GetProperty("lyricsSnippet").ValueKind is JsonValueKind.Null);
	}

	[Theory]
	[InlineData("mueller")]
	[InlineData("müller")]
	[InlineData("muller")]
	public async Task UmlautEquivalenceAcrossFoldings(string query)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreatePublishedSongAsync(factory, editorSession, "Müllerlied");
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (body, response) = await SearchAsync(client, memberSession, query);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(1, body.GetProperty("total").GetInt32());
		Assert.Equal(songId, Guid.Parse(FirstSong(body).GetProperty("id").GetString()!));
	}

	[Fact]
	public async Task DraftsNeverLeakIntoMemberSearch()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		// Draft with distinctive alternate title and lyrics only known there.
		await CreateSongAsync(factory, client: null, editorSession, "Geheimes Müllerlied",
			lyrics: "strenggeheimertext", alternateTitles: ["strenggeheimerTitel"]);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		foreach (var query in new[] { "Müllerlied", "strenggeheimertext", "strenggeheimerTitel" })
		{
			var (body, _) = await SearchAsync(client, memberSession, query);
			Assert.Equal(0, body.GetProperty("total").GetInt32());
			Assert.Empty(body.GetProperty("songs").EnumerateArray());
		}

		// Editors keep the draft view in search results.
		var (editorBody, _) = await SearchAsync(client, editorSession, "Müllerlied");
		Assert.Equal(1, editorBody.GetProperty("total").GetInt32());
		Assert.Equal("Geheimes Müllerlied", FirstSong(editorBody).GetProperty("title").GetString());
		Assert.False(FirstSong(editorBody).GetProperty("published").GetBoolean());
	}

	[Fact]
	public async Task PaginationIsDeterministicAndClamped()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var created = new List<Guid>();
		for (var index = 0; index < 21; index++)
		{
			var songId = await CreatePublishedSongAsync(factory, editorSession, $"Seitenlied {index:00}");
			created.Add(songId);
		}
		var memberSession = await SignInAsync(factory, Member);

		var (page1, response1) = await SearchAsync(client, memberSession, "Seitenlied");
		Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
		Assert.Equal(21, page1.GetProperty("total").GetInt32());
		Assert.Equal(1, page1.GetProperty("page").GetInt32());
		var page1Songs = page1.GetProperty("songs").EnumerateArray().ToList();
		Assert.Equal(20, page1Songs.Count);

		var (page2, _) = await SearchAsync(client, memberSession, "Seitenlied", 2);
		Assert.Equal(21, page2.GetProperty("total").GetInt32());
		var page2Songs = page2.GetProperty("songs").EnumerateArray().ToList();
		Assert.Single(page2Songs);

		// Rank is equal for all; order is by Id, page split is the stable Id
		// order and pages do not overlap.
		var page1Ids = page1Songs.Select(s => Guid.Parse(s.GetProperty("id").GetString()!)).ToList();
		var page2Ids = page2Songs.Select(s => Guid.Parse(s.GetProperty("id").GetString()!)).ToList();
		Assert.Equal(page1Ids.OrderBy(id => id).ToList(), page1Ids);
		Assert.Equal(page2Ids.OrderBy(id => id).ToList(), page2Ids);
		Assert.Empty(page1Ids.Intersect(page2Ids));
		Assert.Equal(21, page1Ids.Union(page2Ids).Count());

		// Page beyond the end is empty with the correct total.
		var (page9, _) = await SearchAsync(client, memberSession, "Seitenlied", 9);
		Assert.Empty(page9.GetProperty("songs").EnumerateArray());
		Assert.Equal(21, page9.GetProperty("total").GetInt32());

		// page=0 and negative clamp to page 1.
		var (page0, _) = await SearchAsync(client, memberSession, "Seitenlied", 0);
		Assert.Equal(1, page0.GetProperty("page").GetInt32());
		Assert.Equal(20, page0.GetProperty("songs").EnumerateArray().Count());
		var (pageNegative, _) = await SearchAsync(client, memberSession, "Seitenlied", -3);
		Assert.Equal(1, pageNegative.GetProperty("page").GetInt32());
		Assert.Equal(20, pageNegative.GetProperty("songs").EnumerateArray().Count());
	}

	[Fact]
	public async Task RankOrdersExactAndTitleMatchesBeforeOtherFields()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var elsewhereSong = await CreatePublishedSongAsync(factory, editorSession, "Abendstille",
			lyrics: "Nebellied überm Tal");
		var titleSong = await CreatePublishedSongAsync(factory, editorSession, "Nebellied");
		var memberSession = await SignInAsync(factory, Member);

		var (body, _) = await SearchAsync(client, memberSession, "Nebellied");
		Assert.Equal(2, body.GetProperty("total").GetInt32());
		var songs = body.GetProperty("songs").EnumerateArray().ToList();
		// Rank 1 (all tokens in title) before rank 2 (match elsewhere); each
		// group by Id.
		Assert.Equal("Nebellied", songs[0].GetProperty("title").GetString());
		Assert.Equal("Abendstille", songs[1].GetProperty("title").GetString());
		Assert.Equal(["title"], songs[0].GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());
		Assert.Equal(["lyrics"], songs[1].GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()).ToList());
	}

	[Fact]
	public async Task MultiTokenQueryRequiresAllTokens()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		await CreatePublishedSongAsync(factory, editorSession, "Mondnacht am See",
			composer: "Anna Weber");
		await CreatePublishedSongAsync(factory, editorSession, "Morgenlied",
			composer: "Bernhard Anderl");
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Both tokens must occur somewhere (across fields): title + composer.
		var (both, _) = await SearchAsync(client, memberSession, "Mondnacht Weber");
		Assert.Equal(1, both.GetProperty("total").GetInt32());
		Assert.Equal("Mondnacht am See", FirstSong(both).GetProperty("title").GetString());

		// Only one token present → no hit.
		var (onlyOne, _) = await SearchAsync(client, memberSession, "Mondnacht Anderl");
		Assert.Equal(0, onlyOne.GetProperty("total").GetInt32());
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task EmptyOrWhitespaceQueryBehavesLikeNoQuery(string query)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		await CreatePublishedSongAsync(factory, editorSession, "OhneSuche");
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (emptyBody, _) = await SearchAsync(client, memberSession, query);
		Assert.True(emptyBody.GetProperty("query").ValueKind is JsonValueKind.Null);
		Assert.Equal(1, emptyBody.GetProperty("total").GetInt32());
		var song = Assert.Single(emptyBody.GetProperty("songs").EnumerateArray());
		Assert.Equal("OhneSuche", song.GetProperty("title").GetString());
		Assert.Empty(song.GetProperty("matchedIn").EnumerateArray());

		// Identical shape to a request without q entirely.
		var (plainBody, _) = await SearchAsync(client, memberSession, null);
		Assert.Equal(emptyBody.GetProperty("total").GetInt32(), plainBody.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task SearchResultsIncludeArrangementContextAndMatchArrangementFields()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreatePublishedSongAsync(factory, editorSession, "Waldlied");
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Männerchor-Bearbeitung", arranger = "Sepp Brandmeier" },
			$"{cookie}; {editorSession}", token);
		using var createResponse = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		var memberSession = await SignInAsync(factory, Member);

		var (byArranger, _) = await SearchAsync(client, memberSession, "Brandmeier");
		Assert.Equal(1, byArranger.GetProperty("total").GetInt32());
		var arrangerSong = FirstSong(byArranger);
		Assert.Equal(songId, Guid.Parse(arrangerSong.GetProperty("id").GetString()!));
		Assert.Contains("arrangements", arrangerSong.GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()));
		var arrangements = arrangerSong.GetProperty("arrangements").EnumerateArray().ToList();
		Assert.Equal(2, arrangements.Count);
		// Ordered by id: the creation arrangement first, then the added one.
		Assert.Equal("Standardfassung", arrangements[0].GetProperty("label").GetString());
		Assert.Equal("Männerchor-Bearbeitung", arrangements[1].GetProperty("label").GetString());
		Assert.Equal("Sepp Brandmeier", arrangements[1].GetProperty("arranger").GetString());

		var (byLabel, _) = await SearchAsync(client, memberSession, "Männerchor");
		Assert.Equal(1, byLabel.GetProperty("total").GetInt32());
		Assert.Contains("arrangements", FirstSong(byLabel).GetProperty("matchedIn").EnumerateArray()
			.Select(e => e.GetString()));
	}

	[Fact]
	public async Task PatchLyricsAndAlternateTitlesRoundTrip()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Rundreise");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { lyrics = "Erste Zeile des Liedes", alternateTitles = new[] { "Erster anderer Titel", "Zweiter anderer Titel" } },
			$"{cookie}; {editorSession}", token);
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		var patched = (await patchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal("Erste Zeile des Liedes", patched.GetProperty("lyrics").GetString());
		var patchedTitles = patched.GetProperty("alternateTitles").EnumerateArray()
			.Select(t => t.GetString()).ToList();
		Assert.Equal(["Erster anderer Titel", "Zweiter anderer Titel"], patchedTitles);

		// Re-read detail: entries persist in list order.
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		Assert.Equal("Erste Zeile des Liedes", detail.GetProperty("lyrics").GetString());
		var detailTitles = detail.GetProperty("alternateTitles").EnumerateArray()
			.Select(t => t.GetString()).ToList();
		Assert.Equal(["Erster anderer Titel", "Zweiter anderer Titel"], detailTitles);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Songs.Include(s => s.AlternateTitles)
				.SingleAsync(s => s.Id == songId);
			Assert.Equal(2, persisted.AlternateTitles.Count);
			Assert.All(persisted.AlternateTitles, t => Assert.NotEqual(Guid.Empty, t.CreatedByAccountId));
		}

		// null alternateTitles leaves the list unchanged; null lyrics leaves it.
		using var nullPatch = AuthedPatch($"/api/songs/{songId}",
			new { title = "Rundreise", alternateTitles = (string[]?)null, lyrics = (string?)null },
			$"{cookie}; {editorSession}", token);
		using var nullPatchResponse = await client.SendAsync(nullPatch);
		Assert.Equal(HttpStatusCode.OK, nullPatchResponse.StatusCode);
		var afterNull = (await nullPatchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal(2, afterNull.GetProperty("alternateTitles").EnumerateArray().Count());
		Assert.Equal("Erste Zeile des Liedes", afterNull.GetProperty("lyrics").GetString());

		// Empty array clears the list.
		using var clearPatch = AuthedPatch($"/api/songs/{songId}",
			new { alternateTitles = Array.Empty<string>() }, $"{cookie}; {editorSession}", token);
		using var clearPatchResponse = await client.SendAsync(clearPatch);
		Assert.Equal(HttpStatusCode.OK, clearPatchResponse.StatusCode);
		var afterClear = (await clearPatchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Empty(afterClear.GetProperty("alternateTitles").EnumerateArray());
	}

	[Theory]
	[InlineData("   ")]
	[InlineData("")]
	public async Task EmptyAlternateTitleIsRejected(string value)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Leerprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { alternateTitles = new[] { value } }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.AlternateTitleEmptyMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task OverlongAlternateTitleAndLyricsAreRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Längenprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var longTitle = AuthedPatch($"/api/songs/{songId}",
			new { alternateTitles = new[] { new string('x', 201) } }, $"{cookie}; {editorSession}", token);
		using var longTitleResponse = await client.SendAsync(longTitle);
		Assert.Equal(HttpStatusCode.BadRequest, longTitleResponse.StatusCode);
		var longTitleProblem = await longTitleResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.AlternateTitleTooLongMessage, longTitleProblem.GetProperty("title").GetString());

		using var longLyrics = AuthedPatch($"/api/songs/{songId}",
			new { lyrics = new string('x', 5001) }, $"{cookie}; {editorSession}", token);
		using var longLyricsResponse = await client.SendAsync(longLyrics);
		Assert.Equal(HttpStatusCode.BadRequest, longLyricsResponse.StatusCode);
		var longLyricsProblem = await longLyricsResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.LyricsTooLongMessage, longLyricsProblem.GetProperty("title").GetString());

		// Lyrics allow up to 5000 characters — beyond the usual 200 field limit.
		using var okayLyrics = AuthedPatch($"/api/songs/{songId}",
			new { lyrics = new string('x', 5000) }, $"{cookie}; {editorSession}", token);
		Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(okayLyrics)).StatusCode);
	}

	[Fact]
	public async Task MoreThanTenAlternateTitlesAreRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Zählprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var eleven = Enumerable.Range(1, 11).Select(i => $"Titel {i}").ToArray();
		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { alternateTitles = eleven }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.AlternateTitleTooManyMessage, problem.GetProperty("title").GetString());
	}

	private static async Task<(JsonElement Body, HttpResponseMessage Response)> SearchAsync(
		HttpClient client, string session, string? query, int? page = null)
	{
		var parameters = new List<string>();
		if (query is not null)
			parameters.Add($"q={Uri.EscapeDataString(query)}");
		if (page is not null)
			parameters.Add($"page={page}");
		var url = "/api/songs" + (parameters.Count > 0 ? $"?{string.Join('&', parameters)}" : string.Empty);
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Add("Cookie", session);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (body, response);
	}

	private static JsonElement FirstSong(JsonElement body)
		=> body.GetProperty("songs").EnumerateArray().First();

	private static async Task<Guid> CreatePublishedSongAsync(AuthApiFactory factory, string editorSession,
		string title, string? composer = null, string? lyricist = null, string? lyrics = null,
		string[]? alternateTitles = null)
	{
		var songId = await CreateSongAsync(factory, client: null, editorSession, title,
			composer: composer, lyricist: lyricist, lyrics: lyrics, alternateTitles: alternateTitles);
		await PublishAsync(factory, editorSession, songId);
		return songId;
	}

	private static async Task PublishAsync(AuthApiFactory factory, string editorSession, Guid songId)
	{
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<Guid> CreateSongAsync(
		AuthApiFactory factory, HttpClient? client, string editorSession, string title,
		string? composer = null, string? lyricist = null, string? lyrics = null, string[]? alternateTitles = null)
	{
		client ??= factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title, composer, lyricist }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var songId = Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);
		if (lyrics is not null || alternateTitles is not null)
		{
			// POST takes core data only; lyrics and alternate titles ride on PATCH.
			using var patch = AuthedPatch($"/api/songs/{songId}",
				new { lyrics, alternateTitles }, $"{cookie}; {editorSession}", token);
			using var patchResponse = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		}
		return songId;
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
