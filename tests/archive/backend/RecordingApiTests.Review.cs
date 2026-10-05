using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Recordings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-030 review follow-up: stricter container recognition, the empty file
/// refused without reading it, slot integrity, unchanged PATCH requests and
/// restoring an earlier playback copy.
/// </summary>
public sealed partial class RecordingApiTests
{
	[Fact]
	public void StillImageBrandsAndByteOrderMarksAreNotPlayableMedia()
	{
		// HEIC/AVIF share the ISO container with MP4 but are still images.
		foreach (var brand in new[] { "heic", "heix", "mif1", "avif", "avis", "crx " })
		{
			var format = RecordingFormats.Detect(Mp4(64, brand), RecordingKinds.Video);
			Assert.False(format.Playable, brand);
			Assert.NotEqual("video/mp4", format.ContentType);
		}
		foreach (var brand in new[] { "isom", "iso2", "mp41", "mp42", "avc1", "M4V " })
			Assert.Equal(new RecordingFormat("video/mp4", true),
				RecordingFormats.Detect(Mp4(64, brand), RecordingKinds.Video));
		Assert.Equal(new RecordingFormat("audio/mp4", true),
			RecordingFormats.Detect(Mp4(64, "M4A "), RecordingKinds.Audio));
		// An audio-only brand is not a video.
		Assert.False(RecordingFormats.Detect(Mp4(64, "M4A "), RecordingKinds.Video).Playable);

		// UTF-16 text starts FF FE; that is a byte order mark, not an MP3 frame.
		var utf16 = new byte[] { 0xFF, 0xFE, 0x44, 0x00, 0x69, 0x00, 0x65, 0x00 };
		Assert.Equal(new RecordingFormat("application/octet-stream", false),
			RecordingFormats.Detect(utf16, RecordingKinds.Audio));
		// Reserved version, bitrate or sample rate fields are no frame either.
		Assert.False(RecordingFormats.Detect([0xFF, 0xEB, 0x90, 0x00], RecordingKinds.Audio).Playable);
		Assert.False(RecordingFormats.Detect([0xFF, 0xFB, 0xF0, 0x00], RecordingKinds.Audio).Playable);
		Assert.False(RecordingFormats.Detect([0xFF, 0xFB, 0x9C, 0x00], RecordingKinds.Audio).Playable);
		// A real MPEG-1 Layer III frame header (128 kbit/s, 44.1 kHz).
		Assert.Equal(new RecordingFormat("audio/mpeg", true),
			RecordingFormats.Detect([0xFF, 0xFB, 0x90, 0x00], RecordingKinds.Audio));
	}

	[Fact]
	public async Task StillImageInAnIsoContainerIsRefusedAsPlaybackCopy()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Kamera", "video");
		var original = await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mp4(512, "heic"), "bild.heic");
		Assert.Equal("application/octet-stream", original.GetProperty("contentType").GetString());
		var slot = await AttachPlaybackAsync(stage.Client, stage.Editor, IdOf(recording));
		using var refused = await UploadRawAsync(stage.Factory, stage.Client, stage.Editor, slot, Mp4(512, "avif"), "bild.mp4");
		Assert.Equal((HttpStatusCode)422, refused.StatusCode);
		Assert.Equal("needsPlaybackCopy", Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId))
			.EnumerateArray()).GetProperty("playback").GetProperty("state").GetString());
	}

	[Fact]
	public async Task EmptyFileIsRefusedWithoutReadingIt()
	{
		// Real storage may fail a ranged read of an empty object; the size
		// alone must decide.
		var storage = new EmptyReadFailsStorage();
		await using var factory = new AuthApiFactory(storage: storage);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editor = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editor, new { kind = "concert", title = "Leer" });
		var recording = await CreateRecordingAsync(client, editor, eventId, "Leer", "audio");

		using var empty = await UploadRawAsync(factory, client, editor, OriginalAssetOf(recording), [], "leer.mp3", storage.Inner);
		Assert.Equal((HttpStatusCode)422, empty.StatusCode);
		Assert.Equal("Die Datei ist leer und kann nicht als Aufnahme gespeichert werden.", await TitleAsync(empty));
		Assert.Equal(0, storage.HeaderReads);
	}

	[Fact]
	public async Task PatchWithoutAnEffectiveChangeLeavesVersionAndStampAlone()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Mitschnitt", "audio");
		var recordingId = IdOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mp3(1024), "a.mp3");
		var saved = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId,
			new { isPublished = true, downloadEnabled = true, durationSeconds = 61.5 });
		var version = saved.GetProperty("editor").GetProperty("version").GetUInt32();
		var (updatedAt, publishedAt) = await StampsAsync();

		foreach (var body in new object[]
		{
			new { },
			new { label = "  Mitschnitt " },
			new { isPublished = true, downloadEnabled = true, durationSeconds = 61.5, expectedVersion = version },
		})
		{
			var same = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, body);
			Assert.Equal(version, same.GetProperty("editor").GetProperty("version").GetUInt32());
			Assert.Equal("Mitschnitt", same.GetProperty("label").GetString());
			Assert.True(same.GetProperty("isPublished").GetBoolean());
		}
		Assert.Equal((updatedAt, publishedAt), await StampsAsync());

		// A real change still moves the version.
		var changed = await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { downloadEnabled = false });
		Assert.True(changed.GetProperty("editor").GetProperty("version").GetUInt32() > version);

		async Task<(DateTimeOffset, DateTimeOffset?)> StampsAsync()
		{
			using var scope = stage.Factory.Services.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var row = await db.Recordings.AsNoTracking().SingleAsync(r => r.Id == recordingId);
			return (row.UpdatedAt, row.PublishedAt);
		}
	}

	[Fact]
	public async Task RestoringAnEarlierPlaybackCopyIsCreatorOnlyAndClearsTheDuration()
	{
		await using var stage = await Stage.CreateAsync();
		await SeedAsync(stage.Factory, SecondEditor, ArchiveRoles.Editor);
		var second = await SignInAsync(stage.Factory, SecondEditor);
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Kamera", "video");
		var recordingId = IdOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mov(4096), "kamera.mov");
		var slot = await AttachPlaybackAsync(stage.Client, stage.Editor, recordingId);
		var first = await UploadAsync(stage.Factory, stage.Client, stage.Editor, slot, Mp4(1000), "erste.mp4");
		var later = await UploadAsync(stage.Factory, stage.Client, stage.Editor, slot, Webm(2000), "zweite.webm");
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = true, durationSeconds = 300 });
		Assert.Equal(later.GetProperty("revisionId").GetString(),
			(await AccessAsync(stage.Client, stage.Member, recordingId)).GetProperty("revisionId").GetString());
		var restore = new
		{
			revisionId = first.GetProperty("revisionId").GetString(),
			expectedCurrentRevisionId = later.GetProperty("revisionId").GetString(),
		};

		// Another editor may not change which file the recording serves.
		using (var foreign = await PostJsonAsync(stage.Client, $"/api/assets/{slot}/current-revision", restore, second))
			Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
		Assert.Equal(300, Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray())
			.GetProperty("durationSeconds").GetDouble());

		using (var own = await PostJsonAsync(stage.Client, $"/api/assets/{slot}/current-revision", restore, stage.Editor))
			Assert.Equal(HttpStatusCode.OK, own.StatusCode);
		var listed = Assert.Single((await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray());
		Assert.True(listed.GetProperty("durationSeconds").ValueKind is JsonValueKind.Null);
		Assert.Equal(first.GetProperty("revisionId").GetString(),
			listed.GetProperty("playback").GetProperty("revisionId").GetString());
		Assert.Equal("erste.mp4", listed.GetProperty("editor").GetProperty("playbackCopy").GetProperty("file")
			.GetProperty("fileName").GetString());
		// Members now stream the restored file.
		var access = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.Equal(first.GetProperty("revisionId").GetString(), access.GetProperty("revisionId").GetString());
		Assert.Equal("video/mp4", access.GetProperty("contentType").GetString());
		Assert.Equal(1000, access.GetProperty("sizeBytes").GetInt64());
		Assert.True(access.GetProperty("durationSeconds").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task PlaybackSlotPointingAtAForeignAssetIsIgnored()
	{
		await using var stage = await Stage.CreateAsync();
		var recording = await CreateRecordingAsync(stage.Client, stage.Editor, stage.EventId, "Kamera", "video");
		var recordingId = IdOf(recording);
		await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(recording), Mov(4096), "kamera.mov");
		await PatchRecordingAsync(stage.Client, stage.Editor, recordingId, new { isPublished = true, downloadEnabled = true });
		// Another event's playable recording file, wired in behind the API's back.
		var otherEvent = await CreateEventAsync(stage.Client, stage.Editor, new { kind = "concert", title = "Anderes Konzert" });
		var other = await CreateRecordingAsync(stage.Client, stage.Editor, otherEvent, "Fremd", "video");
		var foreign = await UploadAsync(stage.Factory, stage.Client, stage.Editor, OriginalAssetOf(other), Mp4(777), "fremd.mp4");
		using (var scope = stage.Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var row = await db.Recordings.SingleAsync(r => r.Id == recordingId);
			row.PlaybackAssetId = OriginalAssetOf(other);
			await db.SaveChangesAsync();
		}

		var listed = (await ListAsync(stage.Client, stage.Editor, stage.EventId)).EnumerateArray().Single();
		Assert.Equal("needsPlaybackCopy", listed.GetProperty("playback").GetProperty("state").GetString());
		Assert.True(listed.GetProperty("editor").GetProperty("playbackCopy").ValueKind is JsonValueKind.Null);
		var access = await AccessAsync(stage.Client, stage.Member, recordingId);
		Assert.True(access.GetProperty("viewUrl").ValueKind is JsonValueKind.Null);
		Assert.NotEqual(foreign.GetProperty("revisionId").GetString(), access.GetProperty("revisionId").GetString());
		Assert.Equal("video/quicktime", access.GetProperty("contentType").GetString());
	}

	/// <summary>Fake storage whose header read fails for an empty object, as a ranged read may.</summary>
	private sealed class EmptyReadFailsStorage : IAssetStorageAdapter
	{
		public FakeAssetStorage Inner { get; } = new();

		public int HeaderReads { get; private set; }

		public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken)
			=> Inner.CreateUploadTicketAsync(blobName, lifetime, cancellationToken);

		public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, string? contentType = null, CancellationToken cancellationToken = default)
			=> Inner.CreateReadTicketAsync(blobName, lifetime, asDownload, contentType, cancellationToken);

		public Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken)
			=> Inner.ProbeAsync(blobName, cancellationToken);

		public async Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken)
		{
			HeaderReads++;
			var probe = await Inner.ProbeAsync(blobName, cancellationToken);
			if (probe is { SizeBytes: 0 })
				throw new InvalidOperationException("Bereich nicht erfüllbar.");
			return await Inner.ReadHeaderAsync(blobName, length, cancellationToken);
		}

		public Task<Stream?> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
			=> Inner.OpenReadAsync(blobName, cancellationToken);

		public Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken)
			=> Inner.PromoteAsync(sourceBlobName, targetBlobName, cancellationToken);

		public Task DeleteAsync(string blobName, CancellationToken cancellationToken)
			=> Inner.DeleteAsync(blobName, cancellationToken);
	}
}
