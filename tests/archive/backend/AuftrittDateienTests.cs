using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>#171: documents, photos and Aufnahmen of an Auftritt are Dateien; nothing writes asset or revision rows (the tables are gone).</summary>
public sealed class AuftrittDateienTests
{
	private const string PdfType = "application/pdf";

	[Fact]
	public async Task EditorUploadsDocumentAndPhotoAndMembersOpenThem()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var published = await host.CreateEventAsync(editor, "Adventkonzert");

		var pdf = await host.UploadEventDateiAsync(editor, published, "sonstiges", LiedTestHost.ValidPdf(512),
			"programm.pdf", PdfType, description: "Programmzettel");
		var photo = await host.UploadEventDateiAsync(editor, published, "sonstiges", LiedTestHost.Png(256),
			"foto.png", "image/png");

		var detail = await host.GetJsonAsync($"/api/events/{published}", member);
		var documents = detail.GetProperty("event").GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal(["programm.pdf", "foto.png"], documents.Select(d => d.GetProperty("fileName").GetString()).ToArray());
		Assert.Equal(["document", "photo"], documents.Select(d => d.GetProperty("art").GetString()).ToArray());
		Assert.Equal("Programmzettel", documents[0].GetProperty("description").GetString());

		var access = await host.GetJsonAsync($"/api/dateien/{pdf.GetProperty("id").GetString()}/access", member);
		Assert.Equal(PdfType, access.GetProperty("contentType").GetString());
		Assert.False(string.IsNullOrEmpty(access.GetProperty("viewUrl").GetString()));
		using var photoAccess = await host.GetAsync($"/api/dateien/{photo.GetProperty("id").GetString()}/access", member);
		Assert.Equal(HttpStatusCode.OK, photoAccess.StatusCode);

		using var memberUpload = await host.PostAsync($"/api/events/{published}/dateien/upload-session",
			new { kind = "sonstiges", contentType = PdfType }, member);
		Assert.Equal(HttpStatusCode.Forbidden, memberUpload.StatusCode);
	}

	[Fact]
	public async Task ReplacingADocumentSendsTheOldOneToTheTrash()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		var old = await host.UploadEventDateiAsync(editor, eventId, "sonstiges", LiedTestHost.ValidPdf(100), "alt.pdf", PdfType);
		var oldId = Guid.Parse(old.GetProperty("id").GetString()!);

		var fresh = await host.UploadEventDateiAsync(editor, eventId, "sonstiges", LiedTestHost.ValidPdf(200), "neu.pdf",
			PdfType, replacesDateiId: oldId);

		var documents = (await host.GetJsonAsync($"/api/events/{eventId}", editor))
			.GetProperty("event").GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal("neu.pdf", Assert.Single(documents).GetProperty("fileName").GetString());
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var trashed = await db.Dateien.SingleAsync(d => d.Id == oldId);
		Assert.NotNull(trashed.DeletedAt);
		Assert.Equal(Guid.Parse(fresh.GetProperty("id").GetString()!), trashed.ReplacedByDateiId);
	}

	[Fact]
	public async Task LiedKindsAndMismatchingFilesAreRefusedOnAnAuftritt()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		using var noten = await host.PostAsync($"/api/events/{eventId}/dateien/upload-session",
			new { kind = "noten", contentType = PdfType }, editor);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, noten.StatusCode);
		using var fake = await host.UploadEventDateiRawAsync(editor, eventId, "sonstiges",
			LiedTestHost.Mp3(100), "falsch.pdf", PdfType);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, fake.StatusCode);
	}

	[Fact]
	public async Task EditorUploadsConcertVideoAndMemberPlaysItAfterPublication()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var eventId = await host.CreateEventAsync(editor, "Adventkonzert");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Gesamtmitschnitt", "video");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);
		Assert.Equal("missing", recording.GetProperty("playback").GetProperty("state").GetString());

		using var attach = await host.AttachAsync(editor, eventId, recordingId, "original",
			LiedTestHost.Mp4(4096), "konzert.mp4", "video/mp4");
		Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
		var attached = (await attach.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		Assert.Equal("ready", attached.GetProperty("playback").GetProperty("state").GetString());
		Assert.Equal("original", attached.GetProperty("playback").GetProperty("source").GetString());
		var dateiId = attached.GetProperty("playback").GetProperty("dateiId").GetString();
		Assert.Equal("konzert.mp4", attached.GetProperty("editor").GetProperty("original")
			.GetProperty("file").GetProperty("fileName").GetString());

		Assert.Empty((await host.GetJsonAsync($"/api/events/{eventId}/recordings", member)).GetProperty("recordings").EnumerateArray());
		using var publish = await host.PatchAsync($"/api/recordings/{recordingId}", new { isPublished = true }, editor);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

		var access = await host.GetJsonAsync($"/api/recordings/{recordingId}/access", member);
		Assert.Equal(dateiId, access.GetProperty("dateiId").GetString());
		Assert.Equal("video/mp4", access.GetProperty("contentType").GetString());
		Assert.False(string.IsNullOrEmpty(access.GetProperty("viewUrl").GetString()));
		Assert.Equal(JsonValueKind.Null, access.GetProperty("downloadUrl").ValueKind);

		// The generic Datei access never serves Aufnahmen of an Auftritt.
		using var generic = await host.GetAsync($"/api/dateien/{dateiId}/access", member);
		Assert.Equal(HttpStatusCode.NotFound, generic.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
	}

	[Fact]
	public async Task UnplayableOriginalNeedsAValidatedPlaybackCopyAndReplacingTrashesTheOldFile()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Kamera", "video");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);

		using var original = await host.AttachAsync(editor, eventId, recordingId, "original",
			LiedTestHost.Mov(2048), "kamera.mov", "video/quicktime");
		Assert.Equal(HttpStatusCode.OK, original.StatusCode);
		var state = (await original.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		Assert.Equal("needsPlaybackCopy", state.GetProperty("playback").GetProperty("state").GetString());

		using var refused = await host.AttachAsync(editor, eventId, recordingId, "playback",
			LiedTestHost.Mov(512), "noch-ein.mov", "video/quicktime");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

		using var copy = await host.AttachAsync(editor, eventId, recordingId, "playback",
			LiedTestHost.Mp4(1024), "kamera-web.mp4", "video/mp4");
		Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
		var ready = (await copy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		Assert.Equal("playbackCopy", ready.GetProperty("playback").GetProperty("source").GetString());
		var firstCopy = ready.GetProperty("playback").GetProperty("dateiId").GetString();

		using var better = await host.AttachAsync(editor, eventId, recordingId, "playback",
			LiedTestHost.Mp4(2000, "mp42"), "kamera-web2.mp4", "video/mp4");
		Assert.Equal(HttpStatusCode.OK, better.StatusCode);
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var old = await db.Dateien.SingleAsync(d => d.Id == Guid.Parse(firstCopy!));
			Assert.NotNull(old.DeletedAt);
			Assert.NotNull(old.ReplacedByDateiId);
		}

		// Only the creating editor may change the files.
		var second = await host.SignInAsync(LiedTestHost.SecondEditor, ArchiveRoles.Editor);
		var foreign = await host.UploadEventDateiAsync(second, eventId, "aufnahme", LiedTestHost.Mp4(300), "x.mp4", "video/mp4");
		using var denied = await host.PutAsync($"/api/recordings/{recordingId}/dateien/playback",
			new { dateiId = foreign.GetProperty("id").GetString() }, second);
		Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
	}

	[Fact]
	public async Task ZeitmarkeFollowsTheAufnahmeDateiAndNeedsReviewWhenItIsReplaced()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var eventId = await host.CreateEventAsync(editor, "Konzert");
		var liedId = await host.CreateLiedIdAsync(editor, "Waldfahrt");
		using var programme = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = new object[] { new { liedId } } }, editor);
		Assert.Equal(HttpStatusCode.OK, programme.StatusCode);
		var programmeItemId = (await programme.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("programme").GetProperty("items")[0].GetProperty("id").GetString();
		var recording = await host.CreateRecordingAsync(editor, eventId, "Mitschnitt", "audio");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);
		using var first = await host.AttachAsync(editor, eventId, recordingId, "original", LiedTestHost.Mp3(2048), "a.mp3", "audio/mpeg");
		var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording")
			.GetProperty("playback").GetProperty("dateiId").GetString();
		using var publish = await host.PatchAsync($"/api/recordings/{recordingId}", new { isPublished = true }, editor);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

		using var stale = await host.PostAsync($"/api/recordings/{recordingId}/passages",
			new { programmeItemId, startSeconds = 5, endSeconds = 65, expectedPlaybackDateiId = Guid.NewGuid() }, editor);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		using var created = await host.PostAsync($"/api/recordings/{recordingId}/passages",
			new { programmeItemId, startSeconds = 5, endSeconds = 65, expectedPlaybackDateiId = firstId }, editor);
		Assert.Equal(HttpStatusCode.Created, created.StatusCode);

		var current = (await host.GetJsonAsync($"/api/recordings/{recordingId}/passages", member)).GetProperty("passages");
		var row = Assert.Single(current.EnumerateArray());
		Assert.Equal("current", row.GetProperty("timestampState").GetString());
		Assert.Equal(5, row.GetProperty("startSeconds").GetDouble());

		// A replaced file makes the mark untrustworthy: members lose the position.
		using var replaced = await host.AttachAsync(editor, eventId, recordingId, "original", LiedTestHost.Mp3(4096), "b.mp3", "audio/mpeg");
		Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
		var flagged = Assert.Single((await host.GetJsonAsync($"/api/recordings/{recordingId}/passages", member))
			.GetProperty("passages").EnumerateArray());
		Assert.Equal("needsReview", flagged.GetProperty("timestampState").GetString());
		Assert.Equal(JsonValueKind.Null, flagged.GetProperty("startSeconds").ValueKind);
		var editorList = await host.GetJsonAsync($"/api/recordings/{recordingId}/passages", editor);
		Assert.NotEqual(firstId, editorList.GetProperty("playbackDateiId").GetString());
	}
}
