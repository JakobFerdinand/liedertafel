using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// #148: the preservation original of a recording moves to Cold storage while
/// members keep playing the verified playback copy. Only originals with a
/// separate, live playback Datei may go Cold (one object serving both roles
/// stays online); failed tier changes surface as a German error instead of
/// claiming success, and Cold originals stay removable through the
/// Papierkorb release.
/// </summary>
public sealed class DateiStorageTierTests
{
	[Fact]
	public async Task EditorMovesARecordingsOriginalWithReadyPlaybackCopyToColdStorage()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var (eventId, recordingId, originalId, version, originalBlob) = await SeedReadyRecordingAsync(host, editor);

		using (var forbidden = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold", rowVersion = version }, member))
			Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

		using var move = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold", rowVersion = version }, editor);
		Assert.Equal(HttpStatusCode.OK, move.StatusCode);
		var datei = (await move.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("datei");
		Assert.Equal("cold", datei.GetProperty("storageTier").GetString());
		Assert.NotEqual(JsonValueKind.Null, datei.GetProperty("tierChangedAt").ValueKind);
		Assert.Equal("cold", host.Factory.Storage.TierOf(originalBlob));
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var stored = await db.Dateien.AsNoTracking().SingleAsync(d => d.Id == originalId);
			Assert.Equal("cold", stored.StorageTier);
			Assert.NotNull(stored.TierChangedAt);
		}
		// Members keep playing the separate playback copy, unaffected by the tier.
		var access = await host.GetJsonAsync($"/api/recordings/{recordingId}/access", member);
		Assert.Equal("playbackCopy", access.GetProperty("source").GetString());
	}

	[Fact]
	public async Task OriginalWithoutSeparatePlaybackCopyIsRefused()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();

		// a) A recording whose original itself plays: one object, both roles.
		var eventId = await host.CreateEventAsync(editor, "Einklang");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Nur Original", "video");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);
		var attached = await host.UploadEventDateiAsync(editor, eventId, "aufnahme",
			LiedTestHost.Mp4(512), "roh.mp4", "video/mp4");
		var originalId = Guid.Parse(attached.GetProperty("id").GetString()!);
		using (var put = await host.PutAsync($"/api/recordings/{recordingId}/dateien/original",
			new { dateiId = originalId }, editor))
			Assert.True(put.IsSuccessStatusCode);
		var recordings = (await host.GetJsonAsync($"/api/events/{eventId}/recordings", editor))
			.GetProperty("recordings").EnumerateArray().Single();
		Assert.Equal("original", recordings.GetProperty("playback").GetProperty("source").GetString());

		using (var refused = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold" }, editor))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
			var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(DateiStorageEndpoints.ColdNeedsPlaybackMessage, problem.GetProperty("title").GetString());
		}
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal("hot", (await db.Dateien.AsNoTracking().SingleAsync(d => d.Id == originalId)).StorageTier);
		}

		// b) A Lied audio Aufnahme: members play the original itself.
		var liedId = await host.CreateLiedIdAsync(editor, "Tonspur");
		var audio = await host.UploadLiedDateiAsync(editor, liedId, "aufnahme", LiedTestHost.Mp3(128),
			"probe.mp3", "audio/mpeg");
		using (var refused = await host.PostAsync($"/api/dateien/{audio.GetProperty("id").GetString()}/storage-tier",
			new { tier = "cold" }, editor))
			Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
	}

	[Fact]
	public async Task LiedVideoWithCompletedPlaybackCopyMayGoColdAndMembersKeepPlayingIt()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var (liedId, originalId, _, originalBlob, copyId) = await SeedLiedVideoWithCopyAsync(host, editor);

		using var move = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold" }, editor);
		Assert.Equal(HttpStatusCode.OK, move.StatusCode);
		Assert.Equal("cold", host.Factory.Storage.TierOf(originalBlob));

		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", editor)).GetProperty("lied");
		var listed = lied.GetProperty("dateien").EnumerateArray().Single();
		Assert.Equal(originalId.ToString(), listed.GetProperty("id").GetString());
		Assert.Equal("cold", listed.GetProperty("storageTier").GetString());

		// The member view ticket still serves the live playback copy.
		var access = await host.GetJsonAsync($"/api/dateien/{originalId}/access", member);
		Assert.Equal(copyId, access.GetProperty("playbackDateiId").GetString());
	}

	[Fact]
	public async Task UnknownTierUnknownDateiAndTrashAreRejected()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Fehlerquellen");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1");

		using (var badTier = await host.PostAsync($"/api/dateien/{datei.GetProperty("id").GetString()}/storage-tier",
			new { tier = "archive" }, editor))
			Assert.Equal(HttpStatusCode.BadRequest, badTier.StatusCode);
		using (var noTier = await host.PostAsync($"/api/dateien/{datei.GetProperty("id").GetString()}/storage-tier",
			new { }, editor))
			Assert.Equal(HttpStatusCode.BadRequest, noTier.StatusCode);
		using (var unknown = await host.PostAsync($"/api/dateien/{Guid.NewGuid()}/storage-tier",
			new { tier = "cold" }, editor))
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

		using var del = await host.PostAsync($"/api/dateien/{datei.GetProperty("id").GetString()}/delete",
			new { rowVersion = datei.GetProperty("rowVersion").GetUInt32() }, editor);
		Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
		using (var trashed = await host.PostAsync($"/api/dateien/{datei.GetProperty("id").GetString()}/storage-tier",
			new { tier = "cold" }, editor))
			Assert.Equal(HttpStatusCode.NotFound, trashed.StatusCode);
	}

	[Fact]
	public async Task FailedTierChangeSurfacesGermanErrorAndKeepsEverythingHot()
	{
		var storage = new FakeAssetStorage { ThrowOnSetAccessTier = true };
		await using var host = new LiedTestHost(storage: storage);
		var editor = await host.EditorAsync();
		var (_, originalId, _, originalBlob, _) = await SeedLiedVideoWithCopyAsync(host, editor, storage);

		using var move = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold" }, editor);
		Assert.Equal(HttpStatusCode.BadGateway, move.StatusCode);
		var problem = await move.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(UploadEndpoints.StorageFailureMessage, problem.GetProperty("title").GetString());
		Assert.Equal("hot", storage.TierOf(originalBlob));
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal("hot", (await db.Dateien.AsNoTracking().SingleAsync(d => d.Id == originalId)).StorageTier);
	}

	[Fact]
	public async Task ColdOriginalReturnsToHotStorage()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var (_, originalId, version, originalBlob, _) = await SeedLiedVideoWithCopyAsync(host, editor);

		using (var cold = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold", rowVersion = version }, editor))
			Assert.Equal(HttpStatusCode.OK, cold.StatusCode);
		var coldVersion = await CurrentVersionAsync(host, originalId);
		using var warm = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "hot", rowVersion = coldVersion }, editor);
		Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
		Assert.Equal("hot", host.Factory.Storage.TierOf(originalBlob));
	}

	[Fact]
	public async Task StaleRowVersionIsRejected()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var (_, originalId, version, _, _) = await SeedLiedVideoWithCopyAsync(host, editor);

		using var stale = await host.PostAsync($"/api/dateien/{originalId}/storage-tier",
			new { tier = "cold", rowVersion = version + 99 }, editor);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal("hot", host.Factory.Storage.TierOf(
			(await BlobNameAsync(host, originalId))!));
	}

	[Fact]
	public async Task ColdTrashedOriginalIsStillRemovedByThePapierkorbRelease()
	{
		var (db, storage, removal) = SeedColdTrash();

		Assert.True(await removal.RemoveDateiAsync(DateiId(db), CancellationToken.None));
		Assert.False(await db.Dateien.AnyAsync(d => d.BlobName == "dateien/kalt/x"));
		Assert.False(storage.Has("dateien/kalt/x"));
	}

	/// <summary>Editor + Auftritt recording with a non-playable original and a live MP4 playback copy.</summary>
	private static async Task<(Guid EventId, Guid RecordingId, Guid OriginalId, uint Version, string Blob)>
		SeedReadyRecordingAsync(LiedTestHost host, string editor)
	{
		var eventId = await host.CreateEventAsync(editor, "Herbstkonzert");
		var recording = await host.CreateRecordingAsync(editor, eventId, "Erste Fassung", "video");
		var recordingId = Guid.Parse(recording.GetProperty("id").GetString()!);
		var original = await host.UploadEventDateiAsync(editor, eventId, "aufnahme",
			LiedTestHost.Mov(512), "roh.mov", "video/quicktime");
		var copy = await host.UploadEventDateiAsync(editor, eventId, "aufnahme",
			LiedTestHost.Mp4(256), "konzert.mp4", "video/mp4");
		var originalId = Guid.Parse(original.GetProperty("id").GetString()!);
		using (var attachOriginal = await host.PutAsync($"/api/recordings/{recordingId}/dateien/original",
			new { dateiId = originalId }, editor))
			Assert.True(attachOriginal.IsSuccessStatusCode);
		using (var attachCopy = await host.PutAsync($"/api/recordings/{recordingId}/dateien/playback",
			new { dateiId = Guid.Parse(copy.GetProperty("id").GetString()!) }, editor))
			Assert.True(attachCopy.IsSuccessStatusCode);
		// Published so the member path (the one the tier must not disturb) is reachable.
		using (var publish = await host.PatchAsync($"/api/recordings/{recordingId}",
			new { isPublished = true }, editor))
			Assert.True(publish.IsSuccessStatusCode);
		var recordings = (await host.GetJsonAsync($"/api/events/{eventId}/recordings", editor))
			.GetProperty("recordings").EnumerateArray().Single();
		Assert.Equal("playbackCopy", recordings.GetProperty("playback").GetProperty("source").GetString());
		var version = original.GetProperty("rowVersion").GetUInt32();
		return (eventId, recordingId, originalId, version,
			(await BlobNameAsync(host, originalId))!);
	}

	/// <summary>Lied video Aufnahme whose automatic playback copy is completed and live.</summary>
	private static async Task<(Guid LiedId, Guid OriginalId, uint Version, string Blob, string CopyId)>
		SeedLiedVideoWithCopyAsync(LiedTestHost host, string editor, FakeAssetStorage? backingStorage = null)
	{
		// Without an override the factory's fake is the app's storage; with
		// one, the given fake must be the very object the app was built with.
		var storage = backingStorage ?? host.Factory.Storage;
		var liedId = await host.CreateLiedIdAsync(editor, "Videolied");
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId,
			new { kind = "aufnahme", contentType = "video/quicktime", fileName = "roh.mov", sizeBytes = 512L });
		storage.Store(storage.Find(uploadUrl)!.BlobName, LiedTestHost.Mov(512));
		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var original = (await finalize.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("datei");
		var originalId = Guid.Parse(original.GetProperty("id").GetString()!);
		var version = original.GetProperty("rowVersion").GetUInt32();
		var copyId = Guid.NewGuid();
		var copyBlob = $"dateien/{copyId}/{Guid.CreateVersion7()}.mp4";
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var now = DateTimeOffset.UtcNow;
			db.Dateien.Add(new Datei
			{
				Id = copyId, LiedId = liedId, Kind = DateiKinds.Aufnahme,
				BlobName = copyBlob, ContentType = "video/mp4", SizeBytes = 128,
				OriginalFileName = "roh - Abspielkopie.mp4", CreatedByAccountId = Guid.NewGuid(), CreatedAt = now,
			});
			var job = await db.VideoJobs.SingleAsync(j => j.DateiId == originalId);
			job.Status = Backend.Videos.VideoStatus.Completed;
			job.PlaybackDateiId = copyId;
			job.CompletedAt = now;
			await db.SaveChangesAsync();
		}
		storage.Store(copyBlob, LiedTestHost.Mp4(128));
		return (liedId, originalId, version,
			(await BlobNameAsync(host, originalId))!, copyId.ToString());
	}

	private static async Task<string?> BlobNameAsync(LiedTestHost host, Guid dateiId)
	{
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		return (await db.Dateien.AsNoTracking().SingleAsync(d => d.Id == dateiId)).BlobName;
	}

	private static async Task<uint> CurrentVersionAsync(LiedTestHost host, Guid dateiId)
	{
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		return (await db.Dateien.AsNoTracking().SingleAsync(d => d.Id == dateiId)).RowVersion;
	}

	private static (ArchiveDbContext Db, FakeAssetStorage Storage, Backend.Lieder.PapierkorbRemoval Removal) SeedColdTrash()
	{
		var identity = new Microsoft.Extensions.Options.OptionsWrapper<Microsoft.AspNetCore.Identity.IdentityOptions>(
			new Microsoft.AspNetCore.Identity.IdentityOptions
			{
				Stores = { SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3 },
			});
		var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
		services.AddSingleton(Microsoft.Extensions.Options.Options.Create(identity.Value));
		var provider = services.BuildServiceProvider();
		var db = new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
			.UseInMemoryDatabase($"datei-storage-tier-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
			.UseApplicationServiceProvider(provider).Options);
		var account = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		var lied = new Backend.Lieder.Lied { Title = "Kaltes Lied", CreatedByAccountId = account, CreatedAt = now, RowVersion = 1 };
		var datei = new Datei
		{
			LiedId = lied.Id, Kind = DateiKinds.Aufnahme, OriginalFileName = "roh.mov",
			ContentType = "video/quicktime", BlobName = "dateien/kalt/x", SizeBytes = 12,
			StorageTier = Archive.Backend.Assets.AssetStorageTiers.Cold,
			CreatedByAccountId = account, CreatedAt = now, RowVersion = 1,
			DeletedAt = now.AddDays(-10),
		};
		db.Lieder.Add(lied);
		db.Dateien.Add(datei);
		db.SaveChanges();
		var storage = new FakeAssetStorage();
		storage.Store("dateien/kalt/x", [1, 2, 3]);
		return (db, storage,
			new Backend.Lieder.PapierkorbRemoval(db, storage, NullLogger<Backend.Lieder.PapierkorbRemoval>.Instance));
	}

	private static Guid DateiId(ArchiveDbContext db) =>
		db.Dateien.AsNoTracking().Single(d => d.BlobName == "dateien/kalt/x").Id;
}
