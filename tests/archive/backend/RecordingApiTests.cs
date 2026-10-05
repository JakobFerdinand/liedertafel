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
/// ARC-030 concert recordings at the HTTP seam: an editor creates a labelled
/// recording on an event, transfers the original through the shared upload
/// protocol, and members play it through renewable recording tickets once it
/// is published. An incompatible original is preserved and needs an
/// externally converted playback copy, which is validated before it counts;
/// downloads stay off until an editor enables them; recordings never create
/// performance occurrences.
/// </summary>
public sealed partial class RecordingApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";
	private const string SecondEditor = "zweitredaktion@liedertafel.test";

	private const string RecordingNotFound = "Aufnahme nicht gefunden.";

	[Fact]
	public async Task EditorPublishesPlayableOriginalAndMemberPlaysIt()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var editorSession = await SignInAsync(factory, Editor);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "concert", title = "Adventkonzert" });
		await PublishEventAsync(client, editorSession, eventId);

		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/recordings",
			new { label = "  Gesamtmitschnitt Video  ", kind = "video" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var created = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		var recordingId = Guid.Parse(created.GetProperty("id").GetString()!);
		Assert.Equal(eventId, Guid.Parse(created.GetProperty("eventId").GetString()!));
		Assert.Equal("Gesamtmitschnitt Video", created.GetProperty("label").GetString());
		Assert.Equal("video", created.GetProperty("kind").GetString());
		Assert.False(created.GetProperty("isPublished").GetBoolean());
		Assert.False(created.GetProperty("downloadEnabled").GetBoolean());
		Assert.True(created.GetProperty("durationSeconds").ValueKind is JsonValueKind.Null);
		Assert.Equal("missing", created.GetProperty("playback").GetProperty("state").GetString());
		var editorBlock = created.GetProperty("editor");
		Assert.True(editorBlock.GetProperty("canChangeFiles").GetBoolean());
		Assert.True(editorBlock.GetProperty("original").GetProperty("file").ValueKind is JsonValueKind.Null);
		Assert.True(editorBlock.GetProperty("playbackCopy").ValueKind is JsonValueKind.Null);
		var originalAssetId = Guid.Parse(editorBlock.GetProperty("original").GetProperty("assetId").GetString()!);

		// The original rides the unchanged upload protocol; a block-list
		// commit carries no content type, so the container is recognised
		// from its leading bytes.
		var revision = await UploadAsync(factory, client, editorSession, originalAssetId, Mp4(4096), "konzert.mp4");
		Assert.Equal("video/mp4", revision.GetProperty("contentType").GetString());

		// Unpublished: the editor sees it ready, the member sees nothing.
		var editorList = await ListAsync(client, editorSession, eventId);
		var ready = Assert.Single(editorList.EnumerateArray());
		Assert.Equal("ready", ready.GetProperty("playback").GetProperty("state").GetString());
		Assert.Equal("original", ready.GetProperty("playback").GetProperty("source").GetString());
		Assert.Equal("video/mp4", ready.GetProperty("playback").GetProperty("contentType").GetString());
		Assert.Equal(4096, ready.GetProperty("playback").GetProperty("sizeBytes").GetInt64());
		var originalFile = ready.GetProperty("editor").GetProperty("original").GetProperty("file");
		Assert.Equal("konzert.mp4", originalFile.GetProperty("fileName").GetString());
		Assert.True(originalFile.GetProperty("playable").GetBoolean());
		Assert.Empty((await ListAsync(client, memberSession, eventId)).EnumerateArray());
		using (var hidden = await GetAsync(client, $"/api/recordings/{recordingId}/access", memberSession))
		{
			Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
			Assert.Equal(RecordingNotFound, await TitleAsync(hidden));
		}

		// Publishing answers the complete recording, not a partial row.
		var version = ready.GetProperty("editor").GetProperty("version").GetUInt32();
		using var publish = await PatchJsonAsync(client, $"/api/recordings/{recordingId}",
			new { isPublished = true, expectedVersion = version }, editorSession);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		var published = (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		Assert.True(published.GetProperty("isPublished").GetBoolean());
		Assert.Equal("Gesamtmitschnitt Video", published.GetProperty("label").GetString());
		Assert.Equal("video", published.GetProperty("kind").GetString());
		Assert.Equal("ready", published.GetProperty("playback").GetProperty("state").GetString());
		Assert.Equal("konzert.mp4", published.GetProperty("editor").GetProperty("original")
			.GetProperty("file").GetProperty("fileName").GetString());

		// The member reads label, kind and playback state, but no editor data.
		var memberItem = Assert.Single((await ListAsync(client, memberSession, eventId)).EnumerateArray());
		Assert.Equal(recordingId, Guid.Parse(memberItem.GetProperty("id").GetString()!));
		Assert.Equal("Gesamtmitschnitt Video", memberItem.GetProperty("label").GetString());
		Assert.Equal("video", memberItem.GetProperty("kind").GetString());
		Assert.Equal("ready", memberItem.GetProperty("playback").GetProperty("state").GetString());
		Assert.True(memberItem.GetProperty("editor").ValueKind is JsonValueKind.Null);

		var ticketsBefore = factory.Storage.Tickets.Count;
		using var access = await GetAsync(client, $"/api/recordings/{recordingId}/access", memberSession);
		Assert.Equal(HttpStatusCode.OK, access.StatusCode);
		Assert.Equal("no-store", access.Headers.CacheControl?.ToString());
		var ticket = await access.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(recordingId, Guid.Parse(ticket.GetProperty("recordingId").GetString()!));
		Assert.Equal("video", ticket.GetProperty("kind").GetString());
		Assert.Equal("ready", ticket.GetProperty("playbackState").GetString());
		Assert.Equal("video/mp4", ticket.GetProperty("contentType").GetString());
		Assert.Equal(4096, ticket.GetProperty("sizeBytes").GetInt64());
		Assert.Equal(revision.GetProperty("revisionId").GetString(), ticket.GetProperty("revisionId").GetString());
		Assert.False(ticket.GetProperty("downloadEnabled").GetBoolean());
		Assert.True(ticket.GetProperty("downloadUrl").ValueKind is JsonValueKind.Null);
		Assert.True(ticket.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
		// Exactly one view ticket for the current file, with the playable
		// content type signed in, and no download ticket at all.
		var issued = Assert.Single(factory.Storage.Tickets.Skip(ticketsBefore));
		Assert.Equal(ticket.GetProperty("viewUrl").GetString(), issued.Url);
		Assert.False(issued.Download);
		Assert.Equal("video/mp4", issued.ContentType);
		Assert.Equal(TimeSpan.FromMinutes(15), issued.Lifetime);

		// Renewal is the same call again: a fresh ticket for the same file.
		using var renewed = await GetAsync(client, $"/api/recordings/{recordingId}/access", memberSession);
		var second = await renewed.Content.ReadFromJsonAsync<JsonElement>();
		Assert.NotEqual(ticket.GetProperty("viewUrl").GetString(), second.GetProperty("viewUrl").GetString());
		Assert.Equal(ticket.GetProperty("revisionId").GetString(), second.GetProperty("revisionId").GetString());
	}

	private static byte[] Mp4(int size, string brand = "isom")
	{
		var bytes = new byte[Math.Max(size, 16)];
		bytes[3] = 0x18;
		"ftyp"u8.CopyTo(bytes.AsSpan(4));
		System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(bytes.AsSpan(8));
		return bytes;
	}

	/// <summary>QuickTime container: preserved, but not played by browsers.</summary>
	private static byte[] Mov(int size) => Mp4(size, "qt  ");

	private static byte[] Mp3(int size)
	{
		var bytes = new byte[Math.Max(size, 16)];
		"ID3"u8.CopyTo(bytes);
		return bytes;
	}

	private static byte[] Wav(int size)
	{
		var bytes = new byte[Math.Max(size, 16)];
		"RIFF"u8.CopyTo(bytes);
		"WAVE"u8.CopyTo(bytes.AsSpan(8));
		return bytes;
	}

	private static byte[] Webm(int size)
	{
		var bytes = new byte[Math.Max(size, 64)];
		new byte[]
		{
			0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42, 0xF2, 0x81,
			0x04, 0x42, 0xF3, 0x81, 0x08, 0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D,
		}.CopyTo(bytes, 0);
		return bytes;
	}

	private static async Task<JsonElement> CreateRecordingAsync(
		HttpClient client, string session, Guid eventId, string label, string kind)
	{
		using var response = await PostJsonAsync(client, $"/api/events/{eventId}/recordings", new { label, kind }, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
	}

	private static Guid IdOf(JsonElement recording) => Guid.Parse(recording.GetProperty("id").GetString()!);

	private static Guid OriginalAssetOf(JsonElement recording) => Guid.Parse(recording.GetProperty("editor")
		.GetProperty("original").GetProperty("assetId").GetString()!);

	private static async Task<JsonElement> PatchRecordingAsync(
		HttpClient client, string session, Guid recordingId, object body)
	{
		using var response = await PatchJsonAsync(client, $"/api/recordings/{recordingId}", body, session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
	}

	private static async Task<Guid> AttachPlaybackAsync(HttpClient client, string session, Guid recordingId)
	{
		using var response = await PostJsonAsync(client, $"/api/recordings/{recordingId}/playback", new { }, session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var recording = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		return Guid.Parse(recording.GetProperty("editor").GetProperty("playbackCopy").GetProperty("assetId").GetString()!);
	}

	private static async Task<JsonElement> ListAsync(HttpClient client, string session, Guid eventId)
	{
		using var response = await GetAsync(client, $"/api/events/{eventId}/recordings", session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(eventId, Guid.Parse(body.GetProperty("eventId").GetString()!));
		return body.GetProperty("recordings").Clone();
	}

	private static async Task<JsonElement> AccessAsync(HttpClient client, string session, Guid recordingId)
	{
		using var response = await GetAsync(client, $"/api/recordings/{recordingId}/access", session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	/// <summary>Session, transfer without a stored content type, finalize.</summary>
	private static async Task<JsonElement> UploadAsync(
		AuthApiFactory factory, HttpClient client, string session, Guid assetId, byte[] content, string fileName)
	{
		using var finalize = await UploadRawAsync(factory, client, session, assetId, content, fileName);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		return await finalize.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static async Task<HttpResponseMessage> UploadRawAsync(
		AuthApiFactory factory, HttpClient client, string session, Guid assetId, byte[] content, string fileName)
	{
		using var start = await PostJsonAsync(client, $"/api/assets/{assetId}/upload-session",
			new { sizeBytes = content.LongLength, fileName }, session);
		Assert.Equal(HttpStatusCode.Created, start.StatusCode);
		var json = await start.Content.ReadFromJsonAsync<JsonElement>();
		var sessionId = Guid.Parse(json.GetProperty("uploadSessionId").GetString()!);
		factory.Storage.Store(factory.Storage.Find(json.GetProperty("uploadUrl").GetString()!)!.BlobName, content);
		return await PostJsonAsync(client, $"/api/upload-sessions/{sessionId}/finalize",
			new { sizeBytes = content.LongLength, fileName }, session);
	}

	private static async Task<string?> TitleAsync(HttpResponseMessage response)
	{
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
	}

	private static async Task<Guid> CreateEventAsync(HttpClient client, string editorSession, object body)
	{
		using var response = await PostJsonAsync(client, "/api/events", body, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(parsed.GetProperty("event").GetProperty("id").GetString()!);
	}

	private static async Task PublishEventAsync(HttpClient client, string editorSession, Guid eventId)
	{
		using var response = await PostJsonAsync(client, $"/api/events/{eventId}/publish", new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? session)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (session is not null)
			request.Headers.Add("Cookie", session);
		return await client.SendAsync(request);
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(Authed(HttpMethod.Post, path, body, $"{cookie}; {session}", token));
	}

	private static async Task<HttpResponseMessage> PatchJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(Authed(HttpMethod.Patch, path, body, $"{cookie}; {session}", token));
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
		using var request = Authed(HttpMethod.Post, "/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = Authed(HttpMethod.Post, "/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
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

	private static HttpRequestMessage Authed(HttpMethod method, string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(method, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}
}
