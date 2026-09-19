using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-013 member catalogue: editor creation with default labels, draft
/// visibility (members never see drafts), publication, unpublishing, PATCH
/// attribution, duplicate titles and validation.
/// </summary>
public sealed class CatalogueApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task MemberCannotCreateAndEditorCreatesWithDefaults()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (memberCookie, memberToken) = await GetCsrfAsync(client, memberSession);
		using var memberCreate = AuthedPost("/api/songs",
			new { title = "Stille Nacht" }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberResponse = await client.SendAsync(memberCreate);
		Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
		var memberProblem = await memberResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.ForbiddenMessage, memberProblem.GetProperty("title").GetString());

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs",
			new { title = "Stille Nacht", composer = "Franz Xaver Gruber", lyricist = "Joseph Mohr" },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var song = body.GetProperty("song");
		Assert.Equal("Stille Nacht", song.GetProperty("title").GetString());
		Assert.Equal("Franz Xaver Gruber", song.GetProperty("composer").GetString());
		Assert.Equal("Joseph Mohr", song.GetProperty("lyricist").GetString());
		Assert.False(song.GetProperty("published").GetBoolean());
		Assert.True(song.GetProperty("publishedAt").ValueKind is JsonValueKind.Null);
		var arrangements = song.GetProperty("arrangements").EnumerateArray().ToList();
		Assert.Single(arrangements);
		Assert.Equal(CatalogueEndpoints.DefaultLabel, arrangements[0].GetProperty("label").GetString());
		var versions = arrangements[0].GetProperty("musicalVersions").EnumerateArray().ToList();
		Assert.Single(versions);
		Assert.Equal(CatalogueEndpoints.DefaultLabel, versions[0].GetProperty("label").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var persisted = await db.Songs.Include(s => s.Arrangements).ThenInclude(a => a.MusicalVersions).SingleAsync();
		Assert.Equal(editorId, persisted.CreatedByAccountId);
		Assert.Equal(editorId, persisted.UpdatedByAccountId);
		var arrangement = Assert.Single(persisted.Arrangements);
		Assert.Equal(editorId, arrangement.CreatedByAccountId);
		var version = Assert.Single(arrangement.MusicalVersions);
		Assert.Equal(editorId, version.CreatedByAccountId);
	}

	[Fact]
	public async Task DraftStaysHiddenFromMembersAndVisibleToEditors()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Geheimes Lied");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var memberList = new HttpRequestMessage(HttpMethod.Get, "/api/songs");
		memberList.Headers.Add("Cookie", memberSession);
		using var memberListResponse = await client.SendAsync(memberList);
		Assert.Equal(HttpStatusCode.OK, memberListResponse.StatusCode);
		var memberListBody = await memberListResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Empty(memberListBody.GetProperty("songs").EnumerateArray());

		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.NotFound, memberDetailResponse.StatusCode);

		using var editorList = new HttpRequestMessage(HttpMethod.Get, "/api/songs");
		editorList.Headers.Add("Cookie", editorSession);
		using var editorListResponse = await client.SendAsync(editorList);
		Assert.Equal(HttpStatusCode.OK, editorListResponse.StatusCode);
		var editorListBody = await editorListResponse.Content.ReadFromJsonAsync<JsonElement>();
		var entry = Assert.Single(editorListBody.GetProperty("songs").EnumerateArray());
		Assert.Equal("Geheimes Lied", entry.GetProperty("title").GetString());
		Assert.False(entry.GetProperty("published").GetBoolean());

		using var editorDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		editorDetail.Headers.Add("Cookie", editorSession);
		using var editorDetailResponse = await client.SendAsync(editorDetail);
		Assert.Equal(HttpStatusCode.OK, editorDetailResponse.StatusCode);
	}

	[Fact]
	public async Task PublishMakesSongMemberVisibleAndRepublishIsNoOp()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Veröffentlichtes Lied");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var publishResponse = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		var publishBody = await publishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(publishBody.GetProperty("song").GetProperty("published").GetBoolean());

		using var memberList = new HttpRequestMessage(HttpMethod.Get, "/api/songs");
		memberList.Headers.Add("Cookie", memberSession);
		using var memberListResponse = await client.SendAsync(memberList);
		var memberListBody = await memberListResponse.Content.ReadFromJsonAsync<JsonElement>();
		var entry = Assert.Single(memberListBody.GetProperty("songs").EnumerateArray());
		Assert.Equal("Veröffentlichtes Lied", entry.GetProperty("title").GetString());
		Assert.True(entry.GetProperty("published").GetBoolean());

		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.OK, memberDetailResponse.StatusCode);
		var detailBody = await memberDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Veröffentlichtes Lied", detailBody.GetProperty("song").GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var persisted = await db.Songs.SingleAsync(s => s.Id == songId);
		Assert.Equal(editorId, persisted.PublishedByAccountId);
		Assert.NotNull(persisted.PublishedAt);
		var firstPublishedAt = persisted.PublishedAt;

		using var republish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var republishResponse = await client.SendAsync(republish);
		Assert.Equal(HttpStatusCode.OK, republishResponse.StatusCode);
		using (var scope2 = factory.Services.CreateScope())
		{
			var db2 = scope2.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var after = await db2.Songs.SingleAsync(s => s.Id == songId);
			Assert.Equal(firstPublishedAt, after.PublishedAt);
			Assert.Equal(editorId, after.PublishedByAccountId);
		}
	}

	[Fact]
	public async Task UnpublishHidesSongFromMembersAgain()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Zurückgezogenes Lied");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		_ = await client.SendAsync(publish);
		using var unpublish = AuthedPost($"/api/songs/{songId}/unpublish", new { }, $"{cookie}; {editorSession}", token);
		using var unpublishResponse = await client.SendAsync(unpublish);
		Assert.Equal(HttpStatusCode.OK, unpublishResponse.StatusCode);
		var unpublishBody = await unpublishResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(unpublishBody.GetProperty("song").GetProperty("published").GetBoolean());

		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.NotFound, memberDetailResponse.StatusCode);

		// Unpublishing a draft is a no-op success.
		using var unpublishAgain = AuthedPost($"/api/songs/{songId}/unpublish", new { }, $"{cookie}; {editorSession}", token);
		using var unpublishAgainResponse = await client.SendAsync(unpublishAgain);
		Assert.Equal(HttpStatusCode.OK, unpublishAgainResponse.StatusCode);
	}

	[Fact]
	public async Task PatchRecordsAttributionAndUpdatesTitle()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorId = await SeedAsync(factory, "zweitredaktion@liedertafel.test", ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Alter Titel");
		var secondSession = await SignInAsync(factory, "zweitredaktion@liedertafel.test");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, secondSession);
		using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/songs/{songId}");
		patch.Headers.Add("Cookie", $"{cookie}; {secondSession}");
		patch.Headers.Add("X-CSRF-TOKEN", token);
		patch.Content = JsonContent.Create(new { title = "Neuer Titel", composer = "Neuer Komponist" });
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var song = body.GetProperty("song");
		Assert.Equal("Neuer Titel", song.GetProperty("title").GetString());
		Assert.Equal("Neuer Komponist", song.GetProperty("composer").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var persisted = await db.Songs.SingleAsync(s => s.Id == songId);
		Assert.Equal(editorId, persisted.UpdatedByAccountId);
		Assert.Equal(DateTimeOffset.Parse(song.GetProperty("updatedAt").GetString()!), persisted.UpdatedAt);
		Assert.Equal("Neuer Titel", persisted.Title);
		Assert.Equal("Neuer Komponist", persisted.Composer);
	}

	[Fact]
	public async Task UnauthenticatedListIsRejected()
	{
		await using var factory = new AuthApiFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var response = await client.GetAsync("/api/songs");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Anmeldung erforderlich.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task DuplicateTitlesAreListedSeparately()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var firstId = await CreateSongAsync(factory, client: null, editorSession, "Doppelgänger");
		var secondId = await CreateSongAsync(factory, client: null, editorSession, "Doppelgänger");
		Assert.NotEqual(firstId, secondId);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/songs");
		request.Headers.Add("Cookie", editorSession);
		using var response = await client.SendAsync(request);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var titles = body.GetProperty("songs").EnumerateArray()
			.Select(s => s.GetProperty("title").GetString()).ToList();
		Assert.Equal(2, titles.Count(t => t == "Doppelgänger"));
	}

	[Fact]
	public async Task PublishAndDetailRequireEditorRole()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Nur Redaktion");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (memberCookie, memberToken) = await GetCsrfAsync(client, memberSession);

		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{memberCookie}; {memberSession}", memberToken);
		using var publishResponse = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.Forbidden, publishResponse.StatusCode);

		var (unauthCsrfCookie, unauthToken) = await GetCsrfAsync(client);
		using var unauthPublish = AuthedPost($"/api/songs/{songId}/publish", new { }, unauthCsrfCookie, unauthToken);
		using var unauthResponse = await client.SendAsync(unauthPublish);
		Assert.Equal(HttpStatusCode.Unauthorized, unauthResponse.StatusCode);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task EmptyTitleIsRejectedWithGermanProblem(string title)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Titel ist erforderlich.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task OverlongFieldsAreRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs",
			new { title = new string('x', 201) }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Titel ist zu lang.", problem.GetProperty("title").GetString());

		using var composer = AuthedPost("/api/songs",
			new { title = "Ok", composer = new string('x', 201) }, $"{cookie}; {editorSession}", token);
		using var composerResponse = await client.SendAsync(composer);
		Assert.Equal(HttpStatusCode.BadRequest, composerResponse.StatusCode);
		var composerProblem = await composerResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Komponist ist zu lang.", composerProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task UnknownSongIdAnswersNotFound()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{Guid.CreateVersion7()}");
		request.Headers.Add("Cookie", editorSession);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.NotFoundMessage, problem.GetProperty("title").GetString());

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/songs/{Guid.CreateVersion7()}");
		patch.Headers.Add("Cookie", $"{cookie}; {editorSession}");
		patch.Headers.Add("X-CSRF-TOKEN", token);
		patch.Content = JsonContent.Create(new { title = "Neu" });
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.NotFound, patchResponse.StatusCode);
	}

	[Fact]
	public async Task SecondArrangementCreationPersistsFields()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Lied mit Fassungen");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var firstArrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);

		using var create = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Männerchor-Bearbeitung", arranger = "Hans Schmid", voiceConfiguration = "TTBB" },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);

		var after = await GetSongDetailAsync(client, editorSession, songId);
		var arrangements = after.GetProperty("arrangements").EnumerateArray().ToList();
		Assert.Equal(2, arrangements.Count);
		var second = arrangements.Single(a =>
			a.GetProperty("label").GetString() == "Männerchor-Bearbeitung");
		var secondArrangementId = Guid.Parse(second.GetProperty("id").GetString()!);
		Assert.NotEqual(firstArrangementId, secondArrangementId);
		Assert.Equal("Hans Schmid", second.GetProperty("arranger").GetString());
		Assert.Equal("TTBB", second.GetProperty("voiceConfiguration").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(2, await db.Arrangements.CountAsync(a => a.SongId == songId));
		var persisted = await db.Arrangements.SingleAsync(a => a.Id == secondArrangementId);
		Assert.Equal(songId, persisted.SongId);
		Assert.Equal("Hans Schmid", persisted.Arranger);
		Assert.Equal("TTBB", persisted.VoiceConfiguration);
		Assert.Equal(editorId, persisted.CreatedByAccountId);
	}

	[Fact]
	public async Task VersionCreationPersistsFields()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Lied mit Tonarten");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var versionId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
				.GetProperty("id").GetString()!);

		using var create = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label = "Notenausgabe 1952", creator = "Archiv", musicalKey = "Es-Dur" },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);

		var after = await GetSongDetailAsync(client, editorSession, songId);
		var versions = after.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions").EnumerateArray().ToList();
		Assert.Equal(2, versions.Count);
		var second = versions.Single(v =>
			v.GetProperty("label").GetString() == "Notenausgabe 1952");
		var newVersionId = Guid.Parse(second.GetProperty("id").GetString()!);
		Assert.NotEqual(versionId, newVersionId);
		Assert.Equal("Archiv", second.GetProperty("creator").GetString());
		Assert.Equal("Es-Dur", second.GetProperty("musicalKey").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(2, await db.MusicalVersions.CountAsync(v => v.ArrangementId == arrangementId));
		var persisted = await db.MusicalVersions.SingleAsync(v => v.Id == newVersionId);
		Assert.Equal(arrangementId, persisted.ArrangementId);
		Assert.Equal("Archiv", persisted.Creator);
		Assert.Equal("Es-Dur", persisted.MusicalKey);
		Assert.Equal(editorId, persisted.CreatedByAccountId);
	}

	[Fact]
	public async Task PatchArrangementPersistsFieldsAndBumpsSongRowVersion()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Zu bearbeitendes Lied");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);

		long rowVersionBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			rowVersionBefore = (await db.Songs.SingleAsync(s => s.Id == songId)).RowVersion;
		}

		using var patch = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { label = "Umbenannte Fassung", arranger = "Neuer Arrangeur", voiceConfiguration = "SATB divisi" },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var updated = await GetSongDetailAsync(client, editorSession, songId);
		var arrangement = updated.GetProperty("arrangements").EnumerateArray()
			.Single(a => a.GetProperty("id").GetString() == arrangementId.ToString());
		Assert.Equal("Umbenannte Fassung", arrangement.GetProperty("label").GetString());
		Assert.Equal("Neuer Arrangeur", arrangement.GetProperty("arranger").GetString());
		Assert.Equal("SATB divisi", arrangement.GetProperty("voiceConfiguration").GetString());

		using var verifyScope = factory.Services.CreateScope();
		var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var song = await verifyDb.Songs.SingleAsync(s => s.Id == songId);
		Assert.Equal(editorId, song.UpdatedByAccountId);
		Assert.True(song.RowVersion > rowVersionBefore);
	}

	[Fact]
	public async Task PatchMusicalVersionPersistsFields()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Lied mit Tonartwechsel");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
				.GetProperty("id").GetString()!);

		using var patch = AuthedPatch($"/api/musical-versions/{versionId}",
			new { label = "Überarbeitete Ausgabe", creator = "Neuer Ersteller", musicalKey = "F-Dur" },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var updated = await GetSongDetailAsync(client, editorSession, songId);
		var version = updated.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0];
		Assert.Equal(versionId, Guid.Parse(version.GetProperty("id").GetString()!));
		Assert.Equal("Überarbeitete Ausgabe", version.GetProperty("label").GetString());
		Assert.Equal("Neuer Ersteller", version.GetProperty("creator").GetString());
		Assert.Equal("F-Dur", version.GetProperty("musicalKey").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var persisted = await db.MusicalVersions.SingleAsync(v => v.Id == versionId);
		Assert.Equal("Neuer Ersteller", persisted.Creator);
		Assert.Equal(editorId, db.Songs.Single(s => s.Id == songId).UpdatedByAccountId);
	}

	[Fact]
	public async Task IdenticalDefaultLabelsStayDistinctEntities()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Identitätsbeispiel");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var songLabel = detail.GetProperty("title").GetString();
		var arrangements = detail.GetProperty("arrangements").EnumerateArray().ToList();
		Assert.Single(arrangements);
		var arrangementId = Guid.Parse(arrangements[0].GetProperty("id").GetString()!);
		Assert.Equal(CatalogueEndpoints.DefaultLabel, arrangements[0].GetProperty("label").GetString());
		var versions = arrangements[0].GetProperty("musicalVersions").EnumerateArray().ToList();
		Assert.Single(versions);
		var versionId = Guid.Parse(versions[0].GetProperty("id").GetString()!);
		Assert.Equal(CatalogueEndpoints.DefaultLabel, versions[0].GetProperty("label").GetString());

		// Identical labels do not merge: three distinct, stable Guid identities.
		Assert.Equal("Identitätsbeispiel", songLabel);
		Assert.NotEqual(songId, arrangementId);
		Assert.NotEqual(songId, versionId);
		Assert.NotEqual(arrangementId, versionId);

		// Acceptance example: two arrangements and two versions with identical
		// labels stay separate rows.
		using var secondArrangement = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = CatalogueEndpoints.DefaultLabel }, $"{cookie}; {editorSession}", token);
		using var secondArrangementResponse = await client.SendAsync(secondArrangement);
		Assert.Equal(HttpStatusCode.Created, secondArrangementResponse.StatusCode);

		using var secondVersion = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label = CatalogueEndpoints.DefaultLabel }, $"{cookie}; {editorSession}", token);
		using var secondVersionResponse = await client.SendAsync(secondVersion);
		Assert.Equal(HttpStatusCode.Created, secondVersionResponse.StatusCode);

		var reloaded = await GetSongDetailAsync(client, editorSession, songId);
		var reloadedArrangements = reloaded.GetProperty("arrangements")
			.EnumerateArray().OrderBy(a => a.GetProperty("id").GetString()).ToList();
		Assert.Equal(2, reloadedArrangements.Count);
		var reloadedIds = reloadedArrangements
			.Select(a => Guid.Parse(a.GetProperty("id").GetString()!)).ToList();
		Assert.Equal(arrangementId, reloadedIds[0]);
		Assert.NotEqual(reloadedIds[0], reloadedIds[1]);
		Assert.All(reloadedArrangements,
			a => Assert.Equal(CatalogueEndpoints.DefaultLabel, a.GetProperty("label").GetString()));

		var reloadedVersions = reloadedArrangements[0].GetProperty("musicalVersions")
			.EnumerateArray().OrderBy(v => v.GetProperty("id").GetString()).ToList();
		Assert.Equal(2, reloadedVersions.Count);
		var reloadedVersionIds = reloadedVersions
			.Select(v => Guid.Parse(v.GetProperty("id").GetString()!)).ToList();
		Assert.Equal(versionId, reloadedVersionIds[0]);
		Assert.NotEqual(reloadedVersionIds[0], reloadedVersionIds[1]);
		Assert.All(reloadedVersions,
			v => Assert.Equal(CatalogueEndpoints.DefaultLabel, v.GetProperty("label").GetString()));
	}

	[Fact]
	public async Task ChildEndpointsValidateParentIdsAndKeepParents()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Elternprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var versionId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
				.GetProperty("id").GetString()!);

		using var patchVersion = AuthedPatch($"/api/musical-versions/{versionId}",
			new { label = "Verschobene Ausgabe" }, $"{cookie}; {editorSession}", token);
		using var patchVersionResponse = await client.SendAsync(patchVersion);
		Assert.Equal(HttpStatusCode.OK, patchVersionResponse.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.MusicalVersions.SingleAsync(v => v.Id == versionId);
			Assert.Equal(arrangementId, persisted.ArrangementId);
		}

		var unknownId = Guid.CreateVersion7();
		using var versionOnUnknown = AuthedPost($"/api/arrangements/{unknownId}/versions",
			new { label = "Verwaist" }, $"{cookie}; {editorSession}", token);
		using var versionOnUnknownResponse = await client.SendAsync(versionOnUnknown);
		Assert.Equal(HttpStatusCode.NotFound, versionOnUnknownResponse.StatusCode);
		var versionOnUnknownProblem = await versionOnUnknownResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.ArrangementNotFoundMessage, versionOnUnknownProblem.GetProperty("title").GetString());

		using var patchUnknownArrangement = AuthedPatch($"/api/arrangements/{unknownId}",
			new { label = "Neu" }, $"{cookie}; {editorSession}", token);
		using var patchUnknownArrangementResponse = await client.SendAsync(patchUnknownArrangement);
		Assert.Equal(HttpStatusCode.NotFound, patchUnknownArrangementResponse.StatusCode);
		var patchUnknownArrangementProblem = await patchUnknownArrangementResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.ArrangementNotFoundMessage, patchUnknownArrangementProblem.GetProperty("title").GetString());

		using var patchUnknownVersion = AuthedPatch($"/api/musical-versions/{unknownId}",
			new { label = "Neu" }, $"{cookie}; {editorSession}", token);
		using var patchUnknownVersionResponse = await client.SendAsync(patchUnknownVersion);
		Assert.Equal(HttpStatusCode.NotFound, patchUnknownVersionResponse.StatusCode);
		var patchUnknownVersionProblem = await patchUnknownVersionResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.ArrangementNotFoundMessage, patchUnknownVersionProblem.GetProperty("title").GetString());

		using var arrangementOnUnknownSong = AuthedPost($"/api/songs/{unknownId}/arrangements",
			new { label = "Verwaiste Fassung" }, $"{cookie}; {editorSession}", token);
		using var arrangementOnUnknownSongResponse = await client.SendAsync(arrangementOnUnknownSong);
		Assert.Equal(HttpStatusCode.NotFound, arrangementOnUnknownSongResponse.StatusCode);
		var arrangementOnUnknownSongProblem = await arrangementOnUnknownSongResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(CatalogueEndpoints.NotFoundMessage, arrangementOnUnknownSongProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ArrangementEndpointsRejectMembersAndAnonymousCalls()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Rollenprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (memberCookie, memberToken) = await GetCsrfAsync(client, memberSession);

		using var memberArrangement = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Verboten" }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberArrangementResponse = await client.SendAsync(memberArrangement);
		Assert.Equal(HttpStatusCode.Forbidden, memberArrangementResponse.StatusCode);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var versionId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
				.GetProperty("id").GetString()!);

		using var memberVersion = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label = "Verboten" }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberVersionResponse = await client.SendAsync(memberVersion);
		Assert.Equal(HttpStatusCode.Forbidden, memberVersionResponse.StatusCode);

		using var memberPatchArrangement = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { label = "Verboten" }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberPatchArrangementResponse = await client.SendAsync(memberPatchArrangement);
		Assert.Equal(HttpStatusCode.Forbidden, memberPatchArrangementResponse.StatusCode);

		using var memberPatchVersion = AuthedPatch($"/api/musical-versions/{versionId}",
			new { label = "Verboten" }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberPatchVersionResponse = await client.SendAsync(memberPatchVersion);
		Assert.Equal(HttpStatusCode.Forbidden, memberPatchVersionResponse.StatusCode);

		var (unauthCookie, unauthToken) = await GetCsrfAsync(client);
		using var unauthArrangement = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Anonym" }, unauthCookie, unauthToken);
		using var unauthResponse = await client.SendAsync(unauthArrangement);
		Assert.Equal(HttpStatusCode.Unauthorized, unauthResponse.StatusCode);
		var unauthProblem = await unauthResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Anmeldung erforderlich.", unauthProblem.GetProperty("title").GetString());

		// Members still read detail of a published song with all child fields.
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var visiblePatch = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { label = "Sichtbare Fassung", voiceConfiguration = "SATB" }, $"{cookie}; {editorSession}", token);
		using var visiblePatchResponse = await client.SendAsync(visiblePatch);
		Assert.Equal(HttpStatusCode.OK, visiblePatchResponse.StatusCode);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		_ = await client.SendAsync(publish);

		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.OK, memberDetailResponse.StatusCode);
		var memberDetailBody = await memberDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		var memberArrangements = memberDetailBody.GetProperty("song").GetProperty("arrangements")
			.EnumerateArray().ToList();
		Assert.Single(memberArrangements);
		var visible = memberArrangements.Single(a =>
			a.GetProperty("label").GetString() == "Sichtbare Fassung");
		Assert.Equal("SATB", visible.GetProperty("voiceConfiguration").GetString());
		var memberVersions = visible.GetProperty("musicalVersions").EnumerateArray().ToList();
		Assert.Single(memberVersions);
		Assert.Equal(versionId, Guid.Parse(memberVersions[0].GetProperty("id").GetString()!));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task EmptyArrangementLabelIsRejectedWithGermanProblem(string label)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Labelprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Das Label ist erforderlich.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ArrangementAndVersionOverlongFieldsAreRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Längenprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var versionId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
				.GetProperty("id").GetString()!);

		using var longLabel = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = new string('x', 201) }, $"{cookie}; {editorSession}", token);
		using var longLabelResponse = await client.SendAsync(longLabel);
		Assert.Equal(HttpStatusCode.BadRequest, longLabelResponse.StatusCode);
		var longLabelProblem = await longLabelResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Das Label ist zu lang.", longLabelProblem.GetProperty("title").GetString());

		using var longVoice = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Ok", voiceConfiguration = new string('x', 201) }, $"{cookie}; {editorSession}", token);
		using var longVoiceResponse = await client.SendAsync(longVoice);
		Assert.Equal(HttpStatusCode.BadRequest, longVoiceResponse.StatusCode);
		var longVoiceProblem = await longVoiceResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Die Stimmverteilung ist zu lang.", longVoiceProblem.GetProperty("title").GetString());

		using var longKey = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label = "Ok", musicalKey = new string('x', 201) }, $"{cookie}; {editorSession}", token);
		using var longKeyResponse = await client.SendAsync(longKey);
		Assert.Equal(HttpStatusCode.BadRequest, longKeyResponse.StatusCode);
		var longKeyProblem = await longKeyResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Die Tonart ist zu lang.", longKeyProblem.GetProperty("title").GetString());

		using var longCreator = AuthedPatch($"/api/musical-versions/{versionId}",
			new { creator = new string('x', 201) }, $"{cookie}; {editorSession}", token);
		using var longCreatorResponse = await client.SendAsync(longCreator);
		Assert.Equal(HttpStatusCode.BadRequest, longCreatorResponse.StatusCode);
		var longCreatorProblem = await longCreatorResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Ersteller ist zu lang.", longCreatorProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task DraftSongAllowsChildPatchesAndKeepsOptionalsNull()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Unveröffentlichtes Lied");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var versionId = Guid.Parse(
			detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
				.GetProperty("id").GetString()!);
		Assert.True(detail.GetProperty("arrangements")[0].GetProperty("voiceConfiguration")
			.ValueKind is JsonValueKind.Null);
		Assert.True(detail.GetProperty("arrangements")[0].GetProperty("musicalVersions")[0]
			.GetProperty("musicalKey").ValueKind is JsonValueKind.Null);

		// Child edits are allowed on unpublished drafts.
		using var patchArrangement = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { label = "Entwurfsfassung", voiceConfiguration = "SATB" }, $"{cookie}; {editorSession}", token);
		using var patchArrangementResponse = await client.SendAsync(patchArrangement);
		Assert.Equal(HttpStatusCode.OK, patchArrangementResponse.StatusCode);
		using var patchVersion = AuthedPatch($"/api/musical-versions/{versionId}",
			new { label = "Entwurfsausgabe", musicalKey = "C-Dur" }, $"{cookie}; {editorSession}", token);
		using var patchVersionResponse = await client.SendAsync(patchVersion);
		Assert.Equal(HttpStatusCode.OK, patchVersionResponse.StatusCode);

		var updated = await GetSongDetailAsync(client, editorSession, songId);
		Assert.False(updated.GetProperty("published").GetBoolean());
		var arrangements = updated.GetProperty("arrangements").EnumerateArray().ToList();
		Assert.Single(arrangements);
		Assert.Equal("Entwurfsfassung", arrangements[0].GetProperty("label").GetString());
		Assert.Equal("SATB", arrangements[0].GetProperty("voiceConfiguration").GetString());
		var versions = arrangements[0].GetProperty("musicalVersions").EnumerateArray().ToList();
		Assert.Single(versions);
		Assert.Equal(versionId, Guid.Parse(versions[0].GetProperty("id").GetString()!));
		Assert.Equal("Entwurfsausgabe", versions[0].GetProperty("label").GetString());
		Assert.Equal("C-Dur", versions[0].GetProperty("musicalKey").GetString());

		// Adding another version to the draft succeeds like on published songs.
		using var addVersion = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label = "Zweite Ausgabe" }, $"{cookie}; {editorSession}", token);
		using var addVersionResponse = await client.SendAsync(addVersion);
		Assert.Equal(HttpStatusCode.Created, addVersionResponse.StatusCode);
		var added = await GetSongDetailAsync(client, editorSession, songId);
		var addedVersions = added.GetProperty("arrangements").EnumerateArray().Single()
			.GetProperty("musicalVersions").EnumerateArray().ToList();
		Assert.Equal(2, addedVersions.Count);
	}

	private static async Task<Guid> CreateSongAsync(
		AuthApiFactory factory, HttpClient? client, string editorSession, string title)
	{
		client ??= factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
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
