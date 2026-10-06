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

public sealed partial class RecordingPassageApiTests
{
	/// <summary>
	/// Reusable API scaffolding in the style of the other suites: songs,
	/// events, occurrences, recordings with a real (fake-stored) MP4 file.
	/// </summary>
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
			using var create = await SendAsync(HttpMethod.Post, "/api/songs", new { title }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, create.StatusCode);
			var song = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
			var songId = Guid.Parse(song.GetProperty("id").GetString()!);
			var arrangement = song.GetProperty("arrangements").EnumerateArray().Single();
			var arrangementId = Guid.Parse(arrangement.GetProperty("id").GetString()!);
			var versionId = Guid.Parse(arrangement.GetProperty("musicalVersions").EnumerateArray().Single()
				.GetProperty("id").GetString()!);
			if (versionLabel is not null)
			{
				using var patch = await SendAsync(HttpMethod.Patch, $"/api/musical-versions/{versionId}",
					new { label = versionLabel }, EditorSession);
				Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
			}
			if (publish)
			{
				using var published = await SendAsync(HttpMethod.Post, $"/api/songs/{songId}/publish", new { }, EditorSession);
				Assert.Equal(HttpStatusCode.OK, published.StatusCode);
			}
			return (songId, arrangementId, versionId);
		}

		public async Task<(Guid ArrangementId, Guid VersionId)> AddArrangementAsync(Guid songId, string label)
		{
			using var response = await SendAsync(HttpMethod.Post, $"/api/songs/{songId}/arrangements", new { label }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, response.StatusCode);
			var song = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
			var arrangementId = Guid.Parse(song.GetProperty("arrangements").EnumerateArray()
				.Single(a => a.GetProperty("label").GetString() == label).GetProperty("id").GetString()!);
			using var version = await SendAsync(HttpMethod.Post, $"/api/arrangements/{arrangementId}/versions",
				new { label = $"{label} (Fassung)" }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, version.StatusCode);
			var created = (await version.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song")
				.GetProperty("arrangements").EnumerateArray()
				.Single(a => Guid.Parse(a.GetProperty("id").GetString()!) == arrangementId)
				.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!;
			return (arrangementId, Guid.Parse(created));
		}

		public async Task SetVoiceAsync(Guid arrangementId, string voice)
		{
			using var response = await SendAsync(HttpMethod.Patch, $"/api/arrangements/{arrangementId}",
				new { voiceConfiguration = voice }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		public async Task<Guid> CreateEventAsync(string title, int? year, int? month, int? day, bool published = false)
		{
			using var create = await SendAsync(HttpMethod.Post, "/api/events",
				new { kind = "concert", title, dateYear = year, dateMonth = month, dateDay = day }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, create.StatusCode);
			var id = Guid.Parse((await create.Content.ReadFromJsonAsync<JsonElement>())
				.GetProperty("event").GetProperty("id").GetString()!);
			if (published)
				await SetEventPublishedAsync(id, true);
			return id;
		}

		public async Task SetEventPublishedAsync(Guid eventId, bool published)
		{
			using var response = await SendAsync(HttpMethod.Post,
				$"/api/events/{eventId}/{(published ? "publish" : "unpublish")}", new { }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		/// <summary>Records one confirmed (or mention) occurrence and returns its stable id.</summary>
		public async Task<string> RecordAsync(Guid eventId, Guid songId, Guid? versionId = null, string? note = null)
		{
			using var response = await SendAsync(HttpMethod.Post, $"/api/events/{eventId}/performances", new
			{
				songId,
				evidenceStatus = "confirmed",
				sourceNote = note,
				musicalVersionId = versionId,
				idempotencyKey = Guid.NewGuid().ToString("N"),
			}, EditorSession);
			Assert.Equal(HttpStatusCode.Created, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>())
				.GetProperty("performance").GetProperty("id").GetString()!;
		}

		public async Task PublishProgrammeAsync(Guid eventId,
			IEnumerable<(Guid SongId, Guid ArrangementId, Guid VersionId)> songs)
		{
			var items = songs.Select(s => (object)new { songId = s.SongId, musicalVersionId = s.VersionId }).ToArray();
			using var save = await SendAsync(HttpMethod.Put, $"/api/events/{eventId}/programme/items", new { items }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, save.StatusCode);
			var saved = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");
			using var publish = await SendAsync(HttpMethod.Post, $"/api/events/{eventId}/programme/publish",
				new { rowVersion = saved.GetProperty("rowVersion").GetUInt32() }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		}

		public async Task<(JsonElement Review, uint RowVersion)> ReviewAsync(Guid eventId)
		{
			using var response = await GetAsync($"/api/events/{eventId}/programme/confirmation", EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			var review = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("review").Clone();
			return (review, review.GetProperty("rowVersion").GetUInt32());
		}

		/// <summary>Confirms the published programme with every planned entry sung.</summary>
		public async Task ConfirmAsync(Guid eventId)
		{
			var (review, rowVersion) = await ReviewAsync(eventId);
			var items = review.GetProperty("items").EnumerateArray().Select(item => new
			{
				programmeItemId = item.GetProperty("programmeItemId").GetString(),
				outcome = "sung",
			}).ToArray();
			using var response = await SendAsync(HttpMethod.Put, $"/api/events/{eventId}/programme/confirmation", new
			{
				revisionId = review.GetProperty("revision").GetProperty("id").GetString(),
				rowVersion,
				items,
				additions = Array.Empty<object>(),
			}, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		/// <summary>Creates a recording; optionally with a playable MP4 original and a measured duration.</summary>
		public async Task<Guid> CreateRecordingAsync(
			Guid eventId, string label, string kind, double? duration = null, bool upload = true)
		{
			using var create = await SendAsync(HttpMethod.Post, $"/api/events/{eventId}/recordings", new { label, kind }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, create.StatusCode);
			var recording = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
			var id = Guid.Parse(recording.GetProperty("id").GetString()!);
			if (!upload)
				return id;
			var assetId = Guid.Parse(recording.GetProperty("editor").GetProperty("original").GetProperty("assetId").GetString()!);
			await UploadAsync(assetId, kind == "audio" ? Mp4(4096, "M4A ") : Mp4(4096), kind == "audio" ? "ton.m4a" : "bild.mp4");
			if (duration is { } seconds)
				await PatchRecordingAsync(id, new { durationSeconds = seconds });
			return id;
		}

		public async Task ReplacePlaybackFileAsync(Guid recordingId)
		{
			using var open = await SendAsync(HttpMethod.Post, $"/api/recordings/{recordingId}/playback", new { }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, open.StatusCode);
			var recording = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
			var assetId = Guid.Parse(recording.GetProperty("editor").GetProperty("playbackCopy").GetProperty("assetId").GetString()!);
			var kind = recording.GetProperty("kind").GetString();
			await UploadAsync(assetId, kind == "audio" ? Mp4(2048, "M4A ") : Mp4(2048), "kopie.mp4");
		}

		public async Task PublishRecordingAsync(Guid recordingId) => await PatchRecordingAsync(recordingId, new { isPublished = true });

		public async Task PatchRecordingAsync(Guid recordingId, object body)
		{
			using var response = await SendAsync(HttpMethod.Patch, $"/api/recordings/{recordingId}", body, EditorSession);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		private async Task UploadAsync(Guid assetId, byte[] content, string fileName)
		{
			using var start = await SendAsync(HttpMethod.Post, $"/api/assets/{assetId}/upload-session",
				new { sizeBytes = content.LongLength, fileName }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, start.StatusCode);
			var json = await start.Content.ReadFromJsonAsync<JsonElement>();
			var sessionId = Guid.Parse(json.GetProperty("uploadSessionId").GetString()!);
			Factory.Storage.Store(Factory.Storage.Find(json.GetProperty("uploadUrl").GetString()!)!.BlobName, content);
			using var finalize = await SendAsync(HttpMethod.Post, $"/api/upload-sessions/{sessionId}/finalize",
				new { sizeBytes = content.LongLength, fileName }, EditorSession);
			Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		}

		private static byte[] Mp4(int size, string brand = "isom")
		{
			var bytes = new byte[Math.Max(size, 16)];
			bytes[3] = 0x18;
			"ftyp"u8.CopyTo(bytes.AsSpan(4));
			System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(bytes.AsSpan(8));
			return bytes;
		}

		public async Task<JsonElement> AddPassageAsync(Guid recordingId, string performanceId, double start, double end)
		{
			using var response = await SendAsync(HttpMethod.Post, $"/api/recordings/{recordingId}/passages",
				new { performanceId, startSeconds = start, endSeconds = end }, EditorSession);
			Assert.Equal(HttpStatusCode.Created, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passage").Clone();
		}

		public async Task<JsonElement> PassagesAsync(Guid recordingId, string session)
		{
			using var response = await GetAsync($"/api/recordings/{recordingId}/passages", session);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
		}

		public async Task<JsonElement> AccessAsync(Guid recordingId, string session)
		{
			using var response = await GetAsync($"/api/recordings/{recordingId}/access", session);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
		}

		public async Task<JsonElement> HistoryAsync(Guid songId, string session)
		{
			using var response = await GetAsync($"/api/songs/{songId}/performances", session);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
		}

		public async Task<JsonElement> ListSongsAsync(string session, string query)
		{
			using var response = await GetAsync($"/api/songs?{query}", session);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
		}

		public async Task<int> PerformanceCountAsync()
		{
			using var scope = Factory.Services.CreateScope();
			return await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Performances.CountAsync();
		}

		public async Task SetRoleAsync(string email, string role)
		{
			using var scope = Factory.Services.CreateScope();
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(email);
			foreach (var current in await users.GetRolesAsync(user!))
				Assert.True((await users.RemoveFromRoleAsync(user!, current)).Succeeded);
			await SeedAsync(Factory, email, role);
		}

		public async Task DeactivateAsync(string email)
		{
			using var scope = Factory.Services.CreateScope();
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(email);
			user!.EmailConfirmed = false;
			Assert.True((await users.UpdateAsync(user)).Succeeded);
		}

		public async Task<HttpResponseMessage> GetAsync(string path, string? session)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, path);
			if (session is not null)
				request.Headers.Add("Cookie", session);
			return await Client.SendAsync(request);
		}

		/// <summary>
		/// Sends a mutation with a fresh antiforgery token. A changed account
		/// re-issues the session cookie next to the CSRF cookie, so whatever
		/// the server handed out last is sent.
		/// </summary>
		public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, string? session)
		{
			using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
			if (session is not null)
				tokenRequest.Headers.Add("Cookie", session);
			using var tokenResponse = await Client.SendAsync(tokenRequest);
			tokenResponse.EnsureSuccessStatusCode();
			var cookies = string.Join("; ", tokenResponse.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
			var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
			if (session is not null && !cookies.Contains(session.Split('=')[0], StringComparison.Ordinal))
				cookies = $"{cookies}; {session}";
			var request = new HttpRequestMessage(method, path);
			request.Headers.Add("Cookie", cookies);
			request.Headers.Add("X-CSRF-TOKEN", token);
			request.Content = JsonContent.Create(body);
			return await Client.SendAsync(request);
		}
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
		using var request = Post("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = Post("/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		return Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
	}

	private static async Task<(string Cookie, string Token)> GetCsrfAsync(HttpClient client)
	{
		using var response = await client.GetAsync("/api/antiforgery");
		response.EnsureSuccessStatusCode();
		var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (cookie, body.GetProperty("token").GetString()!);
	}

	private static HttpRequestMessage Post(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}
}
