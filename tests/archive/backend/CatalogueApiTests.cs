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
}
