using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>#171: the rules of concert recordings that survive the move to Dateien (publication, downloads, visibility, files).</summary>
public sealed class AuftrittAufnahmeRegelnTests
{
	[Fact]
	public async Task PublishingNeedsAFileAndDownloadsStayOffUntilEnabled()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Ton", "audio");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);

		using var early = await host.PatchAsync($"/api/recordings/{recordingId}", new { isPublished = true }, editor);
		Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

		using var attach = await host.AttachAsync(editor, eventId, recordingId, "original", LiedTestHost.Mp3(2048), "a.mp3", "audio/mpeg");
		Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
		using var publish = await host.PatchAsync($"/api/recordings/{recordingId}", new { isPublished = true }, editor);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

		var without = await host.GetJsonAsync($"/api/recordings/{recordingId}/access", member);
		Assert.Equal(JsonValueKind.Null, without.GetProperty("downloadUrl").ValueKind);
		using var enable = await host.PatchAsync($"/api/recordings/{recordingId}", new { downloadEnabled = true }, editor);
		Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
		var with = await host.GetJsonAsync($"/api/recordings/{recordingId}/access", member);
		Assert.False(string.IsNullOrEmpty(with.GetProperty("downloadUrl").GetString()));
	}

	[Fact]
	public async Task MembersNeverReachDraftAuftritteOrUnpublishedRecordingsAndMutationsNeedEditorAndCsrf()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var draft = await host.CreateEventAsync(editor, "Entwurf", publish: false);
		var recording = await host.CreateRecordingAsync(editor, draft, "Ton", "audio");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);
		using var attach = await host.AttachAsync(editor, draft, recordingId, "original", LiedTestHost.Mp3(512), "a.mp3", "audio/mpeg");
		Assert.Equal(HttpStatusCode.OK, attach.StatusCode);

		using var list = await host.GetAsync($"/api/events/{draft}/recordings", member);
		Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
		using var access = await host.GetAsync($"/api/recordings/{recordingId}/access", member);
		Assert.Equal(HttpStatusCode.NotFound, access.StatusCode);
		using var memberAttach = await host.PutAsync($"/api/recordings/{recordingId}/dateien/original",
			new { dateiId = Guid.NewGuid() }, member);
		Assert.Equal(HttpStatusCode.Forbidden, memberAttach.StatusCode);

		// Without the antiforgery token the attach is refused before anything else.
		using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/recordings/{recordingId}/dateien/original");
		request.Headers.Add("Cookie", editor);
		request.Content = JsonContent.Create(new { dateiId = Guid.NewGuid() });
		using var noToken = await host.Client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

		// A published Auftritt still hides an unpublished recording.
		using var publishEvent = await host.PostAsync($"/api/events/{draft}/publish", new { }, editor);
		Assert.Equal(HttpStatusCode.OK, publishEvent.StatusCode);
		Assert.Empty((await host.GetJsonAsync($"/api/events/{draft}/recordings", member))
			.GetProperty("recordings").EnumerateArray());
	}

	[Fact]
	public async Task EmptyFilesStillImagesAndForeignDateienAreRefused()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		var otherEvent = await host.CreateEventAsync(editor, "Anderes Konzert");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Video", "video");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);

		// An empty file is refused when the upload is finalized.
		using var empty = await host.UploadEventDateiRawAsync(editor, eventId, "aufnahme", [], "leer.mp4", "video/mp4");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);

		// A HEIC still image in an ISO container is no playback copy.
		using var heic = await host.AttachAsync(editor, eventId, recordingId, "playback", LiedTestHost.Mp4(512, "heic"), "bild.heic", "video/mp4");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, heic.StatusCode);

		// A Datei of another Auftritt cannot be attached.
		var foreign = await host.UploadEventDateiAsync(editor, otherEvent, "aufnahme", LiedTestHost.Mp4(256), "fremd.mp4", "video/mp4");
		using var foreignAttach = await host.PutAsync($"/api/recordings/{recordingId}/dateien/original",
			new { dateiId = foreign.GetProperty("id").GetString() }, editor);
		Assert.Equal(HttpStatusCode.NotFound, foreignAttach.StatusCode);

		var state = (await host.GetJsonAsync($"/api/events/{eventId}/recordings", editor))
			.GetProperty("recordings")[0];
		Assert.Equal("missing", state.GetProperty("playback").GetProperty("state").GetString());
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(1, await db.Dateien.CountAsync(d => d.EventId == eventId && d.DeletedAt != null));
	}

	[Fact]
	public async Task MeasuredDurationBelongsToTheFileMembersPlayAndATrashedFileEmptiesTheSlot()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Ton", "audio");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);
		using var first = await host.AttachAsync(editor, eventId, recordingId, "original", LiedTestHost.Mp3(1024), "a.mp3", "audio/mpeg");
		var firstDatei = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording")
			.GetProperty("playback").GetProperty("dateiId").GetString();
		using var measure = await host.PatchAsync($"/api/recordings/{recordingId}", new { durationSeconds = 120.5 }, editor);
		Assert.Equal(HttpStatusCode.OK, measure.StatusCode);

		using var second = await host.AttachAsync(editor, eventId, recordingId, "original", LiedTestHost.Mp3(2048), "b.mp3", "audio/mpeg");
		var replaced = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		Assert.Equal(JsonValueKind.Null, replaced.GetProperty("durationSeconds").ValueKind);
		Assert.NotEqual(firstDatei, replaced.GetProperty("playback").GetProperty("dateiId").GetString());

		// Deleting the current Datei through the trash endpoint leaves the slot empty.
		var current = replaced.GetProperty("playback").GetProperty("dateiId").GetString();
		uint rowVersion;
		using (var scope = host.Services.CreateScope())
			rowVersion = (await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Dateien
				.SingleAsync(d => d.Id == Guid.Parse(current!))).RowVersion;
		using var delete = await host.PostAsync($"/api/dateien/{current}/delete", new { rowVersion }, editor);
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
		var after = (await host.GetJsonAsync($"/api/events/{eventId}/recordings", editor))
			.GetProperty("recordings")[0];
		Assert.Equal("missing", after.GetProperty("playback").GetProperty("state").GetString());
		Assert.Equal(JsonValueKind.Null, after.GetProperty("editor").GetProperty("original").GetProperty("file").ValueKind);
	}
}
