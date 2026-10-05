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
/// ARC-030 rules around the playable flow: preserved incompatible originals
/// and validated playback copies, the download switch, independence of
/// several recordings from each other and from the performance history, and
/// authorization on every path.
/// </summary>
public sealed partial class RecordingApiTests
{
	private const string InvalidPlayback =
		"Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme.";

	[Fact]
	public async Task IncompatibleOriginalIsPreservedAndNeedsAValidatedPlaybackCopy()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Kamera 1", "video");
		var recordingId = IdOf(recording);
		var originalAssetId = OriginalAssetOf(recording);

		// A QuickTime original is accepted and kept, but it is not playable.
		var original = await UploadAsync(stage.Factory, stage.Client, stage.Editor, originalAssetId, Mov(8192), "kamera.mov");
		Assert.Equal("video/quicktime", original.GetProperty("contentType").GetString());
		var listed = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.Equal("needsPlaybackCopy", listed.GetProperty("playback").GetProperty("state").GetString());
		Assert.True(listed.GetProperty("playback").GetProperty("source").ValueKind is JsonValueKind.Null);
		Assert.True(listed.GetProperty("playback").GetProperty("contentType").ValueKind is JsonValueKind.Null);
		Assert.False(listed.GetProperty("editor").GetProperty("original").GetProperty("file")
			.GetProperty("playable").GetBoolean());

		// Published as it is, a member is told the truth and gets no stream.
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = true });
		var memberItem = Assert.Single((await ListAsync(stage.Client, stage.Member, stage.EventId)).EnumerateArray());
		Assert.Equal("needsPlaybackCopy", memberItem.GetProperty("playback").GetProperty("state").GetString());
		var ticketsBefore = stage.Factory.Storage.Tickets.Count;
		var unplayable = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.Equal("needsPlaybackCopy", unplayable.GetProperty("playbackState").GetString());
		Assert.True(unplayable.GetProperty("viewUrl").ValueKind is JsonValueKind.Null);
		Assert.True(unplayable.GetProperty("downloadUrl").ValueKind is JsonValueKind.Null);
		Assert.Equal(ticketsBefore, stage.Factory.Storage.Tickets.Count);

		// The playback slot opens once; asking again returns the same slot.
		var playbackAssetId = await AttachPlaybackAsync(stage.Client, stage.Editor, recordingId);
		Assert.Equal(playbackAssetId, await AttachPlaybackAsync(stage.Client, stage.Editor, recordingId));
		Assert.NotEqual(originalAssetId, playbackAssetId);

		// An unusable candidate is refused and changes nothing.
		using (var refused = await UploadRawAsync(stage.Factory, stage.Client, stage.Editor, playbackAssetId,
			Mov(2048), "noch-immer.mov"))
		{
			Assert.Equal((HttpStatusCode)422, refused.StatusCode);
			Assert.Equal(InvalidPlayback, await TitleAsync(refused));
		}
		var stillWaiting = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.Equal("needsPlaybackCopy", stillWaiting.GetProperty("playback").GetProperty("state").GetString());
		Assert.True(stillWaiting.GetProperty("editor").GetProperty("playbackCopy").GetProperty("file").ValueKind
			is JsonValueKind.Null);

		// A converted MP4 passes validation and becomes what members play.
		var copy = await UploadAsync(stage.Factory, stage.Client, stage.Editor, playbackAssetId, Mp4(1024), "kamera-web.mp4");
		Assert.Equal("video/mp4", copy.GetProperty("contentType").GetString());
		var played = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.Equal("ready", played.GetProperty("playbackState").GetString());
		Assert.Equal("playbackCopy", played.GetProperty("source").GetString());
		Assert.Equal(copy.GetProperty("revisionId").GetString(), played.GetProperty("revisionId").GetString());
		Assert.Equal(1024, played.GetProperty("sizeBytes").GetInt64());

		// Replacing the copy adds a file to the same slot; a later bad
		// candidate leaves the good one current. The original stays as is.
		var better = await UploadAsync(stage.Factory, stage.Client, stage.Editor, playbackAssetId, Webm(3000), "kamera-web.webm");
		Assert.Equal(2, better.GetProperty("revisionNumber").GetInt32());
		Assert.Equal("video/webm", better.GetProperty("contentType").GetString());
		using (var bad = await UploadRawAsync(stage.Factory, stage.Client, stage.Editor, playbackAssetId,
			"kein Video"u8.ToArray(), "text.mp4"))
			Assert.Equal((HttpStatusCode)422, bad.StatusCode);
		var final = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.Equal("playbackCopy", final.GetProperty("playback").GetProperty("source").GetString());
		Assert.Equal(better.GetProperty("revisionId").GetString(),
			final.GetProperty("playback").GetProperty("revisionId").GetString());
		var keptOriginal = final.GetProperty("editor").GetProperty("original").GetProperty("file");
		Assert.Equal(original.GetProperty("revisionId").GetString(), keptOriginal.GetProperty("revisionId").GetString());
		Assert.Equal("kamera.mov", keptOriginal.GetProperty("fileName").GetString());
		Assert.Equal(8192, keptOriginal.GetProperty("sizeBytes").GetInt64());
		using var scope = stage.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var originalBlob = (await db.FileRevisions.SingleAsync(r => r.AssetId == originalAssetId)).BlobName;
		Assert.True(stage.Factory.Storage.Has(originalBlob));
		Assert.Equal(2, await db.FileRevisions.CountAsync(r => r.AssetId == playbackAssetId));
	}

	[Fact]
	public async Task ContainersAreJudgedForTheKindOfTheRecording()
	{
		await using var stage = await Stage.CreateAsync();
		var audio = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Tonmitschnitt", "audio");
		var video = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Video", "video");

		// The same MP4 container is audio for an audio recording.
		var m4a = await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(audio), Mp4(500, "M4A "), "ton.m4a");
		Assert.Equal("audio/mp4", m4a.GetProperty("contentType").GetString());
		var mp3 = await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(audio), Mp3(600), "ton.mp3");
		Assert.Equal("audio/mpeg", mp3.GetProperty("contentType").GetString());
		var wav = await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(audio), Wav(700), "ton.wav");
		Assert.Equal("audio/wav", wav.GetProperty("contentType").GetString());
		Assert.Equal(3, wav.GetProperty("revisionNumber").GetInt32());

		// Sound alone is kept as a video's original but cannot stand in for
		// the picture, neither as original nor as playback copy.
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(video), Mp3(600), "nur-ton.mp3");
		var copySlot = await AttachPlaybackAsync(stage.Client, stage.Editor, IdOf(video));
		using (var soundOnly = await UploadRawAsync(stage.Factory, stage.Client, stage.Editor, copySlot, Mp3(600), "nur-ton.mp3"))
			Assert.Equal((HttpStatusCode)422, soundOnly.StatusCode);
		// An empty file is nothing to preserve.
		using (var empty = await UploadRawAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(video), [], "leer.mp4"))
		{
			Assert.Equal((HttpStatusCode)422, empty.StatusCode);
			Assert.Equal("Die Datei ist leer und kann nicht als Aufnahme gespeichert werden.", await TitleAsync(empty));
		}

		var states = (await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray()
			.ToDictionary(r => r.GetProperty("label").GetString()!, r => r.GetProperty("playback").GetProperty("state").GetString());
		Assert.Equal("ready", states["Tonmitschnitt"]);
		Assert.Equal("needsPlaybackCopy", states["Video"]);
	}

	[Fact]
	public async Task DownloadsStayOffUntilAnEditorEnablesThem()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		var recordingId = IdOf(recording);
		var originalAssetId = OriginalAssetOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, originalAssetId, Mp3(2048), "mitschnitt.mp3");
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = true });

		// The generic asset ticket would hand out a download link; members
		// never get one for a recording file, published or not.
		var ticketsBefore = stage.Factory.Storage.Tickets.Count;
		using (var generic = await GetAsync(stage.Client, $"/api/assets/{originalAssetId}/access", stage.Member))
		{
			Assert.Equal(HttpStatusCode.NotFound, generic.StatusCode);
			Assert.Equal("Material nicht gefunden.", await TitleAsync(generic));
		}
		Assert.Equal(ticketsBefore, stage.Factory.Storage.Tickets.Count);
		var off = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.False(off.GetProperty("downloadEnabled").GetBoolean());
		Assert.True(off.GetProperty("downloadUrl").ValueKind is JsonValueKind.Null);
		Assert.DoesNotContain(stage.Factory.Storage.Tickets.Skip(ticketsBefore), t => t.Download);

		// Enabling answers the whole recording and is enforced by the ticket.
		var enabled = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { downloadEnabled = true });
		Assert.True(enabled.GetProperty("downloadEnabled").GetBoolean());
		Assert.True(enabled.GetProperty("isPublished").GetBoolean());
		Assert.Equal("Mitschnitt", enabled.GetProperty("label").GetString());
		Assert.True(Assert.Single((await ListAsync(stage.Client, stage.Member, stage.EventId)).EnumerateArray())
			.GetProperty("downloadEnabled").GetBoolean());
		var on = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.True(on.GetProperty("downloadEnabled").GetBoolean());
		var download = stage.Factory.Storage.Find(on.GetProperty("downloadUrl").GetString()!);
		Assert.NotNull(download);
		Assert.True(download.Download);
		Assert.Equal(stage.Factory.Storage.Find(on.GetProperty("viewUrl").GetString()!)!.BlobName, download.BlobName);

		// Switching it off again stops new download tickets at once.
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { downloadEnabled = false });
		var offAgain = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.True(offAgain.GetProperty("downloadUrl").ValueKind is JsonValueKind.Null);
		Assert.False(offAgain.GetProperty("viewUrl").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task EnabledDownloadOfAnUnplayableRecordingHandsOutThePreservedOriginal()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Rohmaterial", "video");
		var original = await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mov(4000), "roh.mov");
		await PatchRecordingAsync(stage.Client, stage.Editor, IdOf(recording), new { isPublished = true, downloadEnabled = true });

		var access = await AccessAsync(stage.Client, stage.Member, IdOf(recording));
		Assert.True(access.GetProperty("viewUrl").ValueKind is JsonValueKind.Null);
		Assert.Equal(original.GetProperty("revisionId").GetString(), access.GetProperty("revisionId").GetString());
		Assert.Equal("video/quicktime", access.GetProperty("contentType").GetString());
		Assert.True(stage.Factory.Storage.Find(access.GetProperty("downloadUrl").GetString()!)!.Download);
	}

	[Fact]
	public async Task SeveralRecordingsStayIndependentAndCreateNoPerformance()
	{
		await using var stage = await Stage.CreateAsync();
		// One song with one confirmed occurrence at this event.
		using var songCreate = await PostJsonAsync(stage.Client, "/api/songs", new { title = "Stille Nacht" }, stage.Editor);
		var songId = Guid.Parse((await songCreate.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("song").GetProperty("id").GetString()!);
		using (var publishSong = await PostJsonAsync(stage.Client, $"/api/songs/{songId}/publish", new { }, stage.Editor))
			Assert.Equal(HttpStatusCode.OK, publishSong.StatusCode);
		using (var record = await PostJsonAsync(stage.Client, $"/api/events/{stage.EventId}/performances",
			new { songId, evidenceStatus = "confirmed" }, stage.Editor))
			Assert.Equal(HttpStatusCode.Created, record.StatusCode);
		var historyBefore = await HistoryAsync();
		var eventBefore = await EventAsync();

		var video = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Video Saal", "video");
		var audio = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Ton Mischpult", "audio");
		// The same label twice is allowed: identity is the id, not the label.
		var twin = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Ton Mischpult", "audio");
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(video), Mp4(4096), "saal.mp4");
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(audio), Mp3(2048), "pult.mp3");
		await PatchRecordingAsync(stage.Client, stage.Editor, IdOf(video), new { isPublished = true });
		await PatchRecordingAsync(stage.Client, stage.Editor, IdOf(audio),
			new { isPublished = true, downloadEnabled = true, label = "Ton vom Mischpult" });

		// Creation order, each with its own label, switch and state.
		var all = (await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray().ToList();
		Assert.Equal([IdOf(video), IdOf(audio), IdOf(twin)], all.Select(IdOf).ToArray());
		Assert.Equal(["Video Saal", "Ton vom Mischpult", "Ton Mischpult"],
			all.Select(r => r.GetProperty("label").GetString()!).ToArray());
		Assert.Equal([false, true, false], all.Select(r => r.GetProperty("downloadEnabled").GetBoolean()).ToArray());
		Assert.Equal(["ready", "ready", "missing"],
			all.Select(r => r.GetProperty("playback").GetProperty("state").GetString()!).ToArray());
		// Members see the two published ones; the empty third stays hidden.
		var visible = (await ListAsync(stage.Client, stage.Member, stage.EventId)).EnumerateArray().ToList();
		Assert.Equal([IdOf(video), IdOf(audio)], visible.Select(IdOf).ToArray());
		var videoAccess = await AccessAsync(stage.Client, stage.Member, IdOf(video));
		var audioAccess = await AccessAsync(stage.Client, stage.Member, IdOf(audio));
		Assert.Equal("video/mp4", videoAccess.GetProperty("contentType").GetString());
		Assert.Equal("audio/mpeg", audioAccess.GetProperty("contentType").GetString());
		Assert.NotEqual(
			stage.Factory.Storage.Find(videoAccess.GetProperty("viewUrl").GetString()!)!.BlobName,
			stage.Factory.Storage.Find(audioAccess.GetProperty("viewUrl").GetString()!)!.BlobName);

		// The history did not move, the event row was not touched and the
		// recording files do not appear among the event's documents.
		var historyAfter = await HistoryAsync();
		Assert.Equal(1, historyAfter.GetProperty("total").GetInt32());
		Assert.Equal(historyBefore.GetProperty("counts").GetRawText(), historyAfter.GetProperty("counts").GetRawText());
		var eventAfter = await EventAsync();
		Assert.Equal(eventBefore.GetProperty("updatedAt").GetString(), eventAfter.GetProperty("updatedAt").GetString());
		Assert.Empty(eventAfter.GetProperty("documents").EnumerateArray());
		using var scope = stage.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(1, await db.Performances.CountAsync());
		Assert.Equal(0, await db.Programmes.CountAsync());
		Assert.Equal(0, await db.ProgrammeConfirmations.CountAsync());

		async Task<JsonElement> HistoryAsync()
		{
			using var response = await GetAsync(stage.Client, $"/api/songs/{songId}/performances", stage.Member);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return await response.Content.ReadFromJsonAsync<JsonElement>();
		}

		async Task<JsonElement> EventAsync()
		{
			using var response = await GetAsync(stage.Client, $"/api/events/{stage.EventId}", stage.Editor);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("event");
		}
	}

	[Fact]
	public async Task MembersNeverReachDraftEventsOrUnpublishedRecordings()
	{
		await using var stage = await Stage.CreateAsync(publishEvent: false);
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		var recordingId = IdOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mp3(1024), "a.mp3");
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = true, downloadEnabled = true });
		var ticketsBefore = stage.Factory.Storage.Tickets.Count;

		// Draft event: the list answers exactly like an unknown event, the
		// ticket exactly like an unknown recording.
		using (var list = await GetAsync(stage.Client, $"/api/events/{stage.EventId}/recordings", stage.Member))
		using (var unknownList = await GetAsync(stage.Client, $"/api/events/{Guid.NewGuid()}/recordings", stage.Member))
		{
			Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
			Assert.Equal(HttpStatusCode.NotFound, unknownList.StatusCode);
			Assert.Equal(await TitleAsync(unknownList), await TitleAsync(list));
		}
		using (var access = await GetAsync(stage.Client, $"/api/recordings/{recordingId}/access", stage.Member))
		using (var unknownAccess = await GetAsync(stage.Client, $"/api/recordings/{Guid.NewGuid()}/access", stage.Member))
		{
			Assert.Equal(HttpStatusCode.NotFound, access.StatusCode);
			Assert.Equal(HttpStatusCode.NotFound, unknownAccess.StatusCode);
			Assert.Equal(RecordingNotFound, await TitleAsync(access));
			Assert.Equal(RecordingNotFound, await TitleAsync(unknownAccess));
		}
		Assert.Equal(ticketsBefore, stage.Factory.Storage.Tickets.Count);
		// The editor previews the draft.
		Assert.False((await AccessAsync(stage.Client, stage.Editor, recordingId)).GetProperty("viewUrl").ValueKind
			is JsonValueKind.Null);

		// Published event, recording withdrawn again: still nothing.
		await PublishEventAsync(stage.Client, stage.Editor, stage.EventId);
		Assert.Single((await ListAsync(stage.Client, stage.Member, stage.EventId)).EnumerateArray());
		var withdrawn = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = false });
		Assert.False(withdrawn.GetProperty("isPublished").GetBoolean());
		Assert.Empty((await ListAsync(stage.Client, stage.Member, stage.EventId)).EnumerateArray());
		using (var access = await GetAsync(stage.Client, $"/api/recordings/{recordingId}/access", stage.Member))
			Assert.Equal(HttpStatusCode.NotFound, access.StatusCode);

		// Anonymous callers get 401 everywhere, without a hint.
		using (var anonymousList = await GetAsync(stage.Client, $"/api/events/{stage.EventId}/recordings", null))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
		using (var anonymousAccess = await GetAsync(stage.Client, $"/api/recordings/{recordingId}/access", null))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymousAccess.StatusCode);
	}

	[Fact]
	public async Task RenewalStopsWhenMembershipEnds()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mp3(1024), "a.mp3");
		await PatchRecordingAsync(stage.Client, stage.Editor, IdOf(recording), new { isPublished = true });
		await AccessAsync(stage.Client, stage.Member, IdOf(recording));
		var ticketsBefore = stage.Factory.Storage.Tickets.Count;

		using (var scope = stage.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(Member);
			user!.EmailConfirmed = false;
			Assert.True((await users.UpdateAsync(user)).Succeeded);
		}
		using var renewal = await GetAsync(stage.Client, $"/api/recordings/{IdOf(recording)}/access", stage.Member);
		Assert.Equal(HttpStatusCode.Unauthorized, renewal.StatusCode);
		Assert.Equal(ticketsBefore, stage.Factory.Storage.Tickets.Count);
	}

	[Fact]
	public async Task OnlyEditorsChangeRecordingsAndEveryMutationNeedsTheCsrfToken()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		var recordingId = IdOf(recording);
		var createPath = $"/api/events/{stage.EventId}/recordings";
		var patchPath = $"/api/recordings/{recordingId}";
		var playbackPath = $"/api/recordings/{recordingId}/playback";

		// Member: 403 on every mutation, and nothing changes.
		using (var create = await PostJsonAsync(stage.Client, createPath, new { label = "X", kind = "audio" }, stage.Member))
			Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
		using (var patch = await PatchJsonAsync(stage.Client, patchPath, new { downloadEnabled = true }, stage.Member))
			Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
		using (var playback = await PostJsonAsync(stage.Client, playbackPath, new { }, stage.Member))
			Assert.Equal(HttpStatusCode.Forbidden, playback.StatusCode);

		// Editor without the antiforgery token: 400 on every mutation.
		foreach (var (method, path, body) in new (HttpMethod, string, object)[]
		{
			(HttpMethod.Post, createPath, new { label = "X", kind = "audio" }),
			(HttpMethod.Patch, patchPath, new { downloadEnabled = true }),
			(HttpMethod.Post, playbackPath, new { }),
		})
		{
			using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
			request.Headers.Add("Cookie", stage.Editor);
			using var response = await stage.Client.SendAsync(request);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			Assert.Equal("Ungültiger Sicherheitstoken.", await TitleAsync(response));
		}

		// Anonymous with a token: 401.
		var (cookie, token) = await GetCsrfAsync(stage.Client);
		using (var anonymous = await stage.Client.SendAsync(
			Authed(HttpMethod.Patch, patchPath, new { downloadEnabled = true }, cookie, token)))
			Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

		var unchanged = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.False(unchanged.GetProperty("downloadEnabled").GetBoolean());
		Assert.True(unchanged.GetProperty("editor").GetProperty("playbackCopy").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task DowngradedEditorGets403AndRevokedEditorGets401()
	{
		await using var stage = await Stage.CreateAsync();
		await SeedAsync(stage.Factory, SecondEditor, ArchiveRoles.Editor);
		var former = await SignInAsync(stage.Factory, SecondEditor);
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		var recordingId = IdOf(recording);
		await PatchRecordingAsync(stage.Client, former, recordingId, new { label = "Vorher" });

		using (var scope = stage.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(SecondEditor);
			Assert.True((await users.RemoveFromRoleAsync(user!, ArchiveRoles.Editor)).Succeeded);
		}
		await SeedAsync(stage.Factory, SecondEditor, ArchiveRoles.Member);
		await AssertMutationsAsync(HttpStatusCode.Forbidden);

		using (var scope = stage.Factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(SecondEditor);
			user!.EmailConfirmed = false;
			Assert.True((await users.UpdateAsync(user)).Succeeded);
		}
		await AssertMutationsAsync(HttpStatusCode.Unauthorized);

		var listed = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.Equal("Vorher", listed.GetProperty("label").GetString());

		async Task AssertMutationsAsync(HttpStatusCode expected)
		{
			// A changed account re-issues the session cookie alongside the
			// CSRF cookie; send whatever the server handed out last.
			using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
			tokenRequest.Headers.Add("Cookie", former);
			using var tokenResponse = await stage.Client.SendAsync(tokenRequest);
			var cookies = string.Join("; ", tokenResponse.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
			var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
			var session = cookies.Contains(former.Split('=')[0], StringComparison.Ordinal) ? cookies : $"{cookies}; {former}";
			using var create = await stage.Client.SendAsync(Authed(HttpMethod.Post,
				$"/api/events/{stage.EventId}/recordings", new { label = "Neu", kind = "audio" }, session, token));
			Assert.Equal(expected, create.StatusCode);
			using var patch = await stage.Client.SendAsync(Authed(HttpMethod.Patch,
				$"/api/recordings/{recordingId}", new { label = "Nachher" }, session, token));
			Assert.Equal(expected, patch.StatusCode);
			using var playback = await stage.Client.SendAsync(Authed(HttpMethod.Post,
				$"/api/recordings/{recordingId}/playback", new { }, session, token));
			Assert.Equal(expected, playback.StatusCode);
		}
	}

	[Fact]
	public async Task AnotherEditorMayPublishButNotChangeTheFiles()
	{
		await using var stage = await Stage.CreateAsync();
		await SeedAsync(stage.Factory, SecondEditor, ArchiveRoles.Editor);
		var second = await SignInAsync(stage.Factory, SecondEditor);
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Kamera", "video");
		var recordingId = IdOf(recording);
		var originalAssetId = OriginalAssetOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, originalAssetId, Mov(2048), "kamera.mov");
		var playbackAssetId = await AttachPlaybackAsync(stage.Client, stage.Editor, recordingId);

		// Labels, publication and the download switch are editorial work.
		var edited = await PatchRecordingAsync(stage.Client, second, recordingId,
			new { label = "Kamera Empore", isPublished = true, downloadEnabled = true });
		Assert.Equal("Kamera Empore", edited.GetProperty("label").GetString());
		Assert.False(edited.GetProperty("editor").GetProperty("canChangeFiles").GetBoolean());
		Assert.True(Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray())
			.GetProperty("editor").GetProperty("canChangeFiles").GetBoolean());

		// Files stay with the creating editor, on both slots.
		foreach (var assetId in new[] { originalAssetId, playbackAssetId })
		{
			using var session = await PostJsonAsync(stage.Client, $"/api/assets/{assetId}/upload-session",
				new { sizeBytes = 10, fileName = "fremd.mp4" }, second);
			Assert.Equal(HttpStatusCode.Forbidden, session.StatusCode);
		}
		await SeedAsync(stage.Factory, "dritte@liedertafel.test", ArchiveRoles.Editor);
		var fresh = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Ohne Kopie", "video");
		using (var foreignSlot = await PostJsonAsync(stage.Client, $"/api/recordings/{IdOf(fresh)}/playback", new { }, second))
		{
			Assert.Equal(HttpStatusCode.Forbidden, foreignSlot.StatusCode);
			Assert.Equal("Nur die anlegende Person kann die Dateien dieser Aufnahme ändern.", await TitleAsync(foreignSlot));
		}
		using var scope = stage.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Null((await db.Recordings.SingleAsync(r => r.Id == IdOf(fresh))).PlaybackAssetId);
	}

	[Fact]
	public async Task RecordingFilesCannotBeCreatedOrRetypedThroughTheGenericAssetEndpoints()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		var originalAssetId = OriginalAssetOf(recording);

		foreach (var assetType in new[] { "recording-original", "recording-playback" })
		{
			using var create = await PostJsonAsync(stage.Client, $"/api/events/{stage.EventId}/assets",
				new { assetType }, stage.Editor);
			Assert.Equal((HttpStatusCode)422, create.StatusCode);
		}
		using (var retype = await PatchJsonAsync(stage.Client, $"/api/assets/{originalAssetId}",
			new { assetType = "document", description = "umgewidmet" }, stage.Editor))
		{
			Assert.Equal(HttpStatusCode.Conflict, retype.StatusCode);
			Assert.Equal("Dateien einer Aufnahme werden über die Aufnahme verwaltet.", await TitleAsync(retype));
		}
		using var scope = stage.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var asset = await db.Assets.SingleAsync(a => a.Id == originalAssetId);
		Assert.Equal("recording-original", asset.AssetType);
		Assert.Null(asset.Description);
	}

	[Fact]
	public async Task InvalidInputStaleFormsAndUnpublishableStatesAreToldApart()
	{
		await using var stage = await Stage.CreateAsync();
		var createPath = $"/api/events/{stage.EventId}/recordings";
		foreach (var (body, title) in new (object, string)[]
		{
			(new { label = "   ", kind = "audio" }, "Die Bezeichnung ist erforderlich."),
			(new { label = new string('x', 201), kind = "audio" }, "Die Bezeichnung ist zu lang."),
			(new { label = "Mitschnitt", kind = "film" }, "Unbekannte Aufnahmeart."),
			(new { label = "Mitschnitt" }, "Unbekannte Aufnahmeart."),
		})
		{
			using var invalid = await PostJsonAsync(stage.Client, createPath, body, stage.Editor);
			Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
			Assert.Equal(title, await TitleAsync(invalid));
		}
		using (var unknownEvent = await PostJsonAsync(stage.Client, $"/api/events/{Guid.NewGuid()}/recordings",
			new { label = "Mitschnitt", kind = "audio" }, stage.Editor))
			Assert.Equal(HttpStatusCode.NotFound, unknownEvent.StatusCode);
		using (var unknown = await PatchJsonAsync(stage.Client, $"/api/recordings/{Guid.NewGuid()}",
			new { label = "Mitschnitt" }, stage.Editor))
		{
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
			Assert.Equal(RecordingNotFound, await TitleAsync(unknown));
		}

		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, new string('x', 200), "audio");
		var recordingId = IdOf(recording);
		var path = $"/api/recordings/{recordingId}";
		var version = recording.GetProperty("editor").GetProperty("version").GetUInt32();

		// Not allowed in this state: no file yet. Reloading would not help.
		using (var early = await PatchJsonAsync(stage.Client, path,
			new { isPublished = true, label = "Zu früh", expectedVersion = version }, stage.Editor))
		{
			Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
			Assert.Equal("Die Aufnahme kann erst veröffentlicht werden, wenn eine Datei hochgeladen ist.",
				await TitleAsync(early));
		}
		// Nothing of the refused request was applied.
		var untouched = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.Equal(new string('x', 200), untouched.GetProperty("label").GetString());
		Assert.Equal(version, untouched.GetProperty("editor").GetProperty("version").GetUInt32());

		// Stale state: someone else saved in between.
		var renamed = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId,
			new { label = "Mitschnitt", expectedVersion = version });
		Assert.True(renamed.GetProperty("editor").GetProperty("version").GetUInt32() > version);
		using (var stale = await PatchJsonAsync(stage.Client, path,
			new { label = "Veraltet", expectedVersion = version }, stage.Editor))
		{
			Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
			Assert.Equal("Die Aufnahme wurde zwischenzeitlich geändert.", await TitleAsync(stale));
		}
		foreach (var durationSeconds in new[] { 0d, -3d, 200_000d })
		{
			using var invalid = await PatchJsonAsync(stage.Client, path, new { durationSeconds }, stage.Editor);
			Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
			Assert.Equal("Die Dauer ist ungültig.", await TitleAsync(invalid));
		}
		Assert.Equal("Mitschnitt", Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId))
			.EnumerateArray()).GetProperty("label").GetString());
	}

	[Fact]
	public async Task MeasuredDurationBelongsToTheFileMembersPlay()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Kamera", "video");
		var recordingId = IdOf(recording);
		var originalAssetId = OriginalAssetOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, originalAssetId, Mp4(4096), "kamera.mp4");

		var measured = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { durationSeconds = 5412.5 });
		Assert.Equal(5412.5, measured.GetProperty("durationSeconds").GetDouble());
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = true });
		Assert.Equal(5412.5, Assert.Single((await ListAsync(stage.Client, stage.Member, stage.EventId)).EnumerateArray())
			.GetProperty("durationSeconds").GetDouble());
		Assert.Equal(5412.5, (await AccessAsync(stage.Client, stage.Member, recordingId))
			.GetProperty("durationSeconds").GetDouble());

		// A playback copy replaces what members play: the old length no
		// longer describes it.
		var copySlot = await AttachPlaybackAsync(stage.Client, stage.Editor, recordingId);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, copySlot, Mp4(1024), "kamera-web.mp4");
		var afterCopy = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.True(afterCopy.GetProperty("durationSeconds").ValueKind is JsonValueKind.Null);

		// While the copy plays, replacing the original leaves the length alone.
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { durationSeconds = 5400 });
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, originalAssetId, Mov(9000), "kamera-neu.mov");
		var afterOriginal = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.Equal(5400, afterOriginal.GetProperty("durationSeconds").GetDouble());
		Assert.Equal("playbackCopy", afterOriginal.GetProperty("playback").GetProperty("source").GetString());
	}

	/// <summary>One event with a signed-in editor and member.</summary>
	private sealed class Stage : IAsyncDisposable
	{
		public required AuthApiFactory Factory { get; init; }

		public required HttpClient Client { get; init; }

		public required string Editor { get; init; }

		public required string Member { get; init; }

		public required Guid EventId { get; init; }

		public static async Task<Stage> CreateAsync(bool publishEvent = true)
		{
			var factory = new AuthApiFactory();
			await SeedAsync(factory, RecordingApiTests.Editor, ArchiveRoles.Editor);
			await SeedAsync(factory, RecordingApiTests.Member, ArchiveRoles.Member);
			var editor = await SignInAsync(factory, RecordingApiTests.Editor);
			var member = await SignInAsync(factory, RecordingApiTests.Member);
			var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
			var eventId = await CreateEventAsync(client, editor, new { kind = "concert", title = "Adventkonzert", dateYear = 1988 });
			if (publishEvent)
				await PublishEventAsync(client, editor, eventId);
			return new Stage { Factory = factory, Client = client, Editor = editor, Member = member, EventId = eventId };
		}

		public async ValueTask DisposeAsync()
		{
			Client.Dispose();
			await Factory.DisposeAsync();
		}
	}
}
