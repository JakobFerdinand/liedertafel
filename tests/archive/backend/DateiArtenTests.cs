using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>#167: Übe-Dateien, Aufnahmen and Sonstiges of a Lied through the Datei upload sessions.</summary>
public sealed class DateiArtenTests
{
	private static readonly byte[] Mp3 = Pad([0x49, 0x44, 0x33, 0x04, 0x00], 600);

	private static readonly byte[] Midi = Pad("MThd"u8.ToArray(), 200);

	private static readonly byte[] QuickTimeVideo = Pad([0, 0, 0, 0x14, .. "ftypqt  "u8], 600);
	private static readonly byte[] Mp4Video = Pad([0, 0, 0, 0x18, .. "ftypisom"u8], 600);

	private static byte[] Pad(byte[] start, int size)
	{
		var bytes = new byte[size];
		start.CopyTo(bytes, 0);
		return bytes;
	}

	private static async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(
		LiedTestHost host, string session, Guid liedId, object request, byte[] content, string storedContentType)
	{
		using var open = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session", request, session);
		if (open.StatusCode != HttpStatusCode.Created)
			return (open.StatusCode, await open.Content.ReadFromJsonAsync<JsonElement>());
		var json = await open.Content.ReadFromJsonAsync<JsonElement>();
		var sessionId = json.GetProperty("uploadSessionId").GetString();
		var blob = host.Factory.Storage.Find(json.GetProperty("uploadUrl").GetString()!)!.BlobName;
		host.Factory.Storage.Store(blob, content, storedContentType);
		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, session);
		return (finalize.StatusCode, await finalize.Content.ReadFromJsonAsync<JsonElement>());
	}

	[Fact]
	public async Task EditorAddsMp3ForTenor1AndMidiForAlleAndMemberGetsAccess()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = (await host.CreateLiedAsync(editor, new { title = "Zu Mining", voiceConfiguration = "TTBB" }))
			.GetProperty("id").GetGuid();

		var (mp3Status, mp3) = await UploadAsync(host, editor, liedId,
			new { kind = "uebe", stimme = "Tenor 1", fileName = "tenor1.mp3", contentType = "audio/mpeg" }, Mp3, "audio/mpeg");
		var (midiStatus, midi) = await UploadAsync(host, editor, liedId,
			new { kind = "uebe", stimme = "Alle", fileName = "alle.mid", contentType = "audio/midi" }, Midi, "audio/midi");

		Assert.Equal(HttpStatusCode.OK, mp3Status);
		Assert.Equal(HttpStatusCode.OK, midiStatus);
		Assert.Equal("uebe", mp3.GetProperty("datei").GetProperty("kind").GetString());
		Assert.Equal("Tenor 1", mp3.GetProperty("datei").GetProperty("stimme").GetString());
		Assert.Equal("audio/midi", midi.GetProperty("datei").GetProperty("contentType").GetString());

		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		var kinds = lied.GetProperty("dateien").EnumerateArray()
			.Select(d => (d.GetProperty("kind").GetString(), d.GetProperty("stimme").GetString())).ToArray();
		Assert.Equal([("uebe", "Alle"), ("uebe", "Tenor 1")], kinds);
		var access = await host.GetJsonAsync(
			$"/api/dateien/{mp3.GetProperty("datei").GetProperty("id").GetString()}/access", member);
		Assert.Equal("audio/mpeg", access.GetProperty("contentType").GetString());
		Assert.False(string.IsNullOrEmpty(access.GetProperty("viewUrl").GetString()));
	}

	[Fact]
	public async Task FreeTextStimmeIsTrimmedAndSaved()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Freies Lied");

		var (status, body) = await UploadAsync(host, editor, liedId,
			new { kind = "uebe", stimme = "  Solo Bariton  ", contentType = "audio/mpeg" }, Mp3, "audio/mpeg");

		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Equal("Solo Bariton", body.GetProperty("datei").GetProperty("stimme").GetString());
	}

	[Fact]
	public async Task UebeDateiNeedsAStimmeAndAnAudioOrMidiFile()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");
		var path = $"/api/lieder/{liedId}/dateien/upload-session";

		using var noStimme = await host.PostAsync(path, new { kind = "uebe", contentType = "audio/mpeg" }, editor);
		Assert.Equal(HttpStatusCode.BadRequest, noStimme.StatusCode);
		using var pdfType = await host.PostAsync(path, new { kind = "uebe", stimme = "Alle", contentType = "application/pdf" }, editor);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, pdfType.StatusCode);

		var (mp3AsMidi, problem) = await UploadAsync(host, editor, liedId,
			new { kind = "uebe", stimme = "Alle", contentType = "audio/midi" }, Mp3, "audio/midi");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, mp3AsMidi);
		Assert.Equal(DateiKindRules.InvalidUebeMessage, problem.GetProperty("title").GetString());
		var (textAsMp3, _) = await UploadAsync(host, editor, liedId,
			new { kind = "uebe", stimme = "Alle", contentType = "audio/mpeg" }, "Kein Audio"u8.ToArray(), "audio/mpeg");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, textAsMp3);
		using var scope = host.Services.CreateScope();
		Assert.Empty(await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Dateien.ToListAsync());
	}

	[Fact]
	public async Task MemberCannotAddAnyKind()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");
		foreach (var kind in new[] { "uebe", "aufnahme", "sonstiges" })
		{
			using var response = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
				new { kind, stimme = "Alle", contentType = "audio/mpeg" }, member);
			Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		}
	}

	[Fact]
	public async Task AufnahmeWithoutAuftrittAndWithVideo()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");

		var (audioStatus, audio) = await UploadAsync(host, editor, liedId,
			new { kind = "aufnahme", fileName = "probe.mp3", contentType = "audio/mpeg" }, Mp3, "audio/mpeg");
		var (videoStatus, video) = await UploadAsync(host, editor, liedId,
			new { kind = "aufnahme", fileName = "konzert.mp4", contentType = "video/mp4" }, Mp4Video, "video/mp4");

		Assert.Equal(HttpStatusCode.OK, audioStatus);
		Assert.Equal(HttpStatusCode.OK, videoStatus);
		Assert.Equal(JsonValueKind.Null, audio.GetProperty("datei").GetProperty("stimme").ValueKind);
		Assert.Equal("video/mp4", video.GetProperty("datei").GetProperty("contentType").GetString());
		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", editor)).GetProperty("lied");
		Assert.All(lied.GetProperty("dateien").EnumerateArray(),
			d => Assert.Equal(JsonValueKind.Null, d.GetProperty("recordedAtEvent").ValueKind));
	}

	[Fact]
	public async Task AufnahmeAcceptsAPhoneQuickTimeMovie()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");

		var (status, body) = await UploadAsync(host, editor, liedId,
			new { kind = "aufnahme", fileName = "probe.mov", contentType = "video/quicktime" }, QuickTimeVideo, "video/quicktime");

		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Equal("video/quicktime", body.GetProperty("datei").GetProperty("contentType").GetString());
	}

	[Fact]
	public async Task AufnahmeNamesTheAuftrittButMembersOnlySeeThePublishedOne()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");
		var published = await SeedEventAsync(host, "Frühjahrskonzert", true);
		var draft = await SeedEventAsync(host, "Geplantes Konzert", false);

		await UploadAsync(host, editor, liedId,
			new { kind = "aufnahme", fileName = "a.mp3", contentType = "audio/mpeg", recordedAtEventId = published }, Mp3, "audio/mpeg");
		await UploadAsync(host, editor, liedId,
			new { kind = "aufnahme", fileName = "b.mp3", contentType = "audio/mpeg", recordedAtEventId = draft }, Mp3, "audio/mpeg");

		static JsonElement Find(JsonElement lied, string name) => lied.GetProperty("dateien").EnumerateArray()
			.Single(d => d.GetProperty("fileName").GetString() == name).GetProperty("recordedAtEvent");
		var asEditor = (await host.GetJsonAsync($"/api/lieder/{liedId}", editor)).GetProperty("lied");
		Assert.Equal("Frühjahrskonzert", Find(asEditor, "a.mp3").GetProperty("title").GetString());
		Assert.Equal("Geplantes Konzert", Find(asEditor, "b.mp3").GetProperty("title").GetString());
		var asMember = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		var link = Find(asMember, "a.mp3");
		Assert.Equal(published, link.GetProperty("id").GetGuid());
		Assert.Equal("Frühjahrskonzert", link.GetProperty("title").GetString());
		Assert.Equal(JsonValueKind.Null, Find(asMember, "b.mp3").ValueKind);
	}

	[Fact]
	public async Task AufnahmeWithUnknownAuftrittIsRejected()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");

		using var response = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
			new { kind = "aufnahme", contentType = "audio/mpeg", recordedAtEventId = Guid.NewGuid() }, editor);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(DateiUploads.RecordedAtEventNotFoundMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task SonstigesAcceptsWhitelistedTypesAndOffersADownload()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");

		var (status, body) = await UploadAsync(host, editor, liedId,
			new { kind = "sonstiges", fileName = "text.txt", description = "Liedtext", contentType = "text/plain" },
			"Wohlauf, Gesellen\n"u8.ToArray(), "text/plain");
		var (zipStatus, _) = await UploadAsync(host, editor, liedId,
			new { kind = "sonstiges", fileName = "arrangement.docx", contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
			Pad([0x50, 0x4B, 0x03, 0x04], 300), "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

		Assert.Equal(HttpStatusCode.OK, status);
		Assert.Equal(HttpStatusCode.OK, zipStatus);
		var access = await host.GetJsonAsync(
			$"/api/dateien/{body.GetProperty("datei").GetProperty("id").GetString()}/access", member);
		Assert.NotEqual(access.GetProperty("viewUrl").GetString(), access.GetProperty("downloadUrl").GetString());
		Assert.Equal("text/plain", access.GetProperty("contentType").GetString());
	}

	[Fact]
	public async Task SonstigesRejectsTypesOutsideTheWhitelistAndFakedContent()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Lied");

		using var html = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
			new { kind = "sonstiges", contentType = "text/html" }, editor);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, html.StatusCode);
		var (fakePdf, _) = await UploadAsync(host, editor, liedId,
			new { kind = "sonstiges", contentType = "application/pdf" }, "<script>x</script>"u8.ToArray(), "application/pdf");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, fakePdf);
	}

	private static async Task<Guid> SeedEventAsync(LiedTestHost host, string title, bool published)
	{
		await using var scope = host.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var owner = Guid.NewGuid();
		var entity = new ChoirEvent
		{
			Kind = "concert",
			Title = title,
			DateYear = 1932,
			DateMonth = 5,
			PublishedAt = published ? now : null,
			CreatedAt = now,
			CreatedByAccountId = owner,
			UpdatedAt = now,
			UpdatedByAccountId = owner,
		};
		db.Events.Add(entity);
		await db.SaveChangesAsync();
		return entity.Id;
	}
}
