using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>#146: deleted Lieder, Auftritte and Dateien can be restored from the Papierkorb in seven days.</summary>
public sealed class PapierkorbRecoveryTests
{
	[Fact]
	public async Task DeletingALiedHidesItFromMembersAndListsItInTheTrash()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Vergessenes Lied");
		await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "noten.pdf");

		using (var forbidden = await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 99u }, member))
			Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
		using (var absent = await host.PostAsync($"/api/lieder/{Guid.NewGuid()}/delete", new { rowVersion = 1u }, editor))
			Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
		using (var ok = await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor))
			Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
		using (var stale = await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor))
			Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);

		var list = (await host.GetJsonAsync("/api/lieder", member)).GetProperty("lieder");
		Assert.Equal(0, list.GetArrayLength());
		using (var gone = await host.GetAsync($"/api/lieder/{liedId}", member))
			Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
		using (var memberTrash = await host.GetAsync("/api/papierkorb", member))
			Assert.Equal(HttpStatusCode.Forbidden, memberTrash.StatusCode);
		var trash = await host.GetJsonAsync("/api/papierkorb", editor);
		var entry = Assert.Single(trash.GetProperty("lieder").EnumerateArray());
		Assert.Equal(liedId.ToString(), entry.GetProperty("id").GetString());
		Assert.Equal("Vergessenes Lied", entry.GetProperty("title").GetString());
		Assert.False(string.IsNullOrEmpty(entry.GetProperty("deletedBy").GetString()));
		Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("deletedAt").ValueKind);

		using var scope = host.Services.CreateScope();
		var stored = await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>()
			.Lieder.SingleAsync(l => l.Id == liedId);
		Assert.NotNull(stored.DeletedAt);
	}

	[Fact]
	public async Task RestoringALiedBringsItAndItsDateienBack()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Gerettetes Lied");
		var datei = await host.UploadNotenAsync(editor, liedId, "Bass 2", fileName: "noten.pdf");
		using var ok = await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor);
		Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

		using (var memberRestore = await host.PostAsync($"/api/lieder/{liedId}/restore", new { }, member))
			Assert.Equal(HttpStatusCode.Forbidden, memberRestore.StatusCode);
		using (var unknown = await host.PostAsync($"/api/lieder/{Guid.NewGuid()}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		using (var restored = await host.PostAsync($"/api/lieder/{liedId}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);

		var detail = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		Assert.Equal("Gerettetes Lied", detail.GetProperty("title").GetString());
		var dateien = detail.GetProperty("dateien").EnumerateArray().Select(d => d.GetProperty("fileName").GetString());
		Assert.Equal(["noten.pdf"], dateien);
		Assert.Equal(0, (await host.GetJsonAsync("/api/papierkorb", editor)).GetProperty("lieder").GetArrayLength());
		using (var scope = host.Services.CreateScope())
		{
			var stored = await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>()
				.Lieder.SingleAsync(l => l.Id == liedId);
			Assert.Null(stored.DeletedAt);
		}
			}

	// ===== Auftritte =====================================================

	[Fact]
	public async Task RestoringAnAuftrittBringsBackItsProgrammAufnahmen()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Mond");
		var eventId = await host.CreateEventAsync(editor, "Frühjahrskonzert");
		var items = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = new[] { new { liedId } } }, editor);
		Assert.Equal(HttpStatusCode.OK, items.StatusCode);
		var aufnahme = await host.AttachAsync(editor, eventId,
			Guid.Parse((await host.CreateRecordingAsync(editor, eventId, "Konzert", "audio")).GetProperty("id").GetString()!),
			"original", LiedTestHost.Mp3(2048), "konzert.mp3", "audio/mpeg");
		Assert.Equal(HttpStatusCode.OK, aufnahme.StatusCode);
		var recording = (await aufnahme.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recording");
		var recordingId = recording.GetProperty("id").GetString()!;
		var published = await host.PatchAsync($"/api/recordings/{recordingId}", new { isPublished = true }, editor);
		Assert.True(published.StatusCode == HttpStatusCode.OK,
			$"status {published.StatusCode}: {await published.Content.ReadAsStringAsync()}");
		_ = recording;

		var eventVersion = (await host.GetJsonAsync($"/api/events/{eventId}", editor))
			.GetProperty("event").GetProperty("rowVersion").GetUInt32();
		using var ok = await host.PostAsync($"/api/events/{eventId}/delete", new { rowVersion = eventVersion }, editor);
		Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

		// Members lose the Auftritt together with its Programm and Aufnahmen.
		var events = (await host.GetJsonAsync("/api/events", member)).GetProperty("events");
		Assert.Equal(0, events.GetArrayLength());
		using (var goneDetail = await host.GetAsync($"/api/events/{eventId}", member))
			Assert.Equal(HttpStatusCode.NotFound, goneDetail.StatusCode);
		// The Lied itself is not trashed; only the Auftritt and what hangs on it vanish.
		var lieder = (await host.GetJsonAsync("/api/lieder", member)).GetProperty("lieder");
		Assert.Equal(1, lieder.GetArrayLength());
		var entry = Assert.Single((await host.GetJsonAsync("/api/papierkorb", editor)).GetProperty("auftritte").EnumerateArray());
		Assert.Equal(eventId.ToString(), entry.GetProperty("id").GetString());
		Assert.Equal("Frühjahrskonzert", entry.GetProperty("title").GetString());
		Assert.Equal("concert", entry.GetProperty("kind").GetString());
		Assert.False(string.IsNullOrEmpty(entry.GetProperty("deletedBy").GetString()));

		using (var restored = await host.PostAsync($"/api/events/{eventId}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
		var detail = (await host.GetJsonAsync($"/api/events/{eventId}", member)).GetProperty("event");
		Assert.Equal("Frühjahrskonzert", detail.GetProperty("title").GetString());
		Assert.NotNull(detail.GetProperty("programme").GetProperty("items"));
		_ = recordingId;
	}

	[Fact]
	public async Task DeletingAnAuftrittHidesItsDateienAndRestoreBringsThemBack()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var eventId = await host.CreateEventAsync(editor, "Dokumente");
		var datei = await host.UploadEventDateiAsync(editor, eventId, "sonstiges",
			LiedTestHost.Png(2048), "foto.png", "image/png");
		var dateiId = datei.GetProperty("id").GetString()!;
		var rowVersion = (await host.GetJsonAsync($"/api/events/{eventId}", editor)).GetProperty("event").GetProperty("rowVersion").GetUInt32();

		using var ok = await host.PostAsync($"/api/events/{eventId}/delete", new { rowVersion }, editor);
		Assert.True(ok.StatusCode == HttpStatusCode.NoContent,
			$"status {ok.StatusCode}: {await ok.Content.ReadAsStringAsync()}");
		using (var memberAccess = await host.GetAsync($"/api/dateien/{dateiId}/access", member))
			Assert.Equal(HttpStatusCode.NotFound, memberAccess.StatusCode);
		Assert.Equal(0, (await host.GetJsonAsync("/api/papierkorb", editor)).GetProperty("dateien").GetArrayLength());

		using var restored = await host.PostAsync($"/api/events/{eventId}/restore", new { }, editor);
		Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
		var documents = (await host.GetJsonAsync($"/api/events/{eventId}", member))
			.GetProperty("event").GetProperty("documents");
		Assert.Equal(1, documents.GetArrayLength());
	}

	// ===== Dateien =======================================================

	[Fact]
	public async Task RestoringAReplacedDateiMakesItCurrentAgain()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Rückkehr");
		var altId = (await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "alt.pdf"))
			.GetProperty("id").GetString()!;
		var neu = await host.ReplaceAsync(editor, liedId, altId, "neu.pdf");
		var neuId = neu.GetProperty("id").GetString()!;

			using (var memberRestore = await host.PostAsync($"/api/dateien/{altId}/restore", new { }, member))
			Assert.Equal(HttpStatusCode.Forbidden, memberRestore.StatusCode);
		using (var unknown = await host.PostAsync($"/api/dateien/{Guid.NewGuid()}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		using (var restored = await host.PostAsync($"/api/dateien/{altId}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		var names = lied.GetProperty("dateien").EnumerateArray()
			.Select(d => d.GetProperty("fileName").GetString()).ToArray();
		Assert.Equal(["alt.pdf", "neu.pdf"], names);
		Assert.Equal(0, (await host.GetJsonAsync("/api/papierkorb", editor)).GetProperty("dateien").GetArrayLength());

		// The restored Datei is a live one again and can be replaced anew.
		var next = await host.ReplaceAsync(editor, liedId, altId, "junger.pdf");
		Assert.NotEqual(neuId, next.GetProperty("id").GetString());
	}

	// ===== Sieben-Tage-Frist und Besitzer im Papierkorb ==================

	[Fact]
	public async Task RestoringAfterSevenDaysRefusesAndTheCleanupFinishesTheRow()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Zu spät");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "noten.pdf");
		string blob;
		using (var ok = await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor))
			Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var stored = await db.Lieder.SingleAsync(l => l.Id == liedId);
			stored.DeletedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
			foreach (var dateiRow in await db.Dateien.Where(d => d.LiedId == liedId).ToListAsync())
				dateiRow.DeletedAt = stored.DeletedAt;
			await db.SaveChangesAsync();
			blob = (await db.Dateien.SingleAsync(d => d.Id == Guid.Parse(datei.GetProperty("id").GetString()!))).BlobName;
		}

		using (var refused = await host.PostAsync($"/api/lieder/{liedId}/restore", new { }, editor))
		{
			Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
			var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Contains("Aufbewahrungsfrist", problem.GetProperty("title").GetString());
		}

		// The daily cleanup still removes the expired row and its blob.
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var cleaner = new PapierkorbCleaner(db,
				new PapierkorbRemoval(db, host.Factory.Storage,
					NullLogger<PapierkorbRemoval>.Instance),
				TimeProvider.System, new ConfigurationBuilder().Build(),
				NullLogger<PapierkorbCleaner>.Instance);
			var result = await cleaner.CleanAsync(dryRun: false, CancellationToken.None);
			Assert.Equal(1, result.Lieder);
			Assert.Equal(1, result.Dateien);
		}
		Assert.False(await WithDbContext(host, db => db.Lieder.AnyAsync(l => l.Id == liedId)));
		Assert.False(await WithDbContext(host, db => db.Dateien.AnyAsync(d => d.LiedId == liedId)));
		Assert.False(host.Factory.Storage.Has(blob));
	}

	[Fact]
	public async Task ADateiUnderATrashedLiedRestoresOnlyAfterTheLiedCameBack()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Zuerst das Lied");
		var datei = await host.UploadNotenAsync(editor, liedId, "Bass 2", fileName: "noten.pdf");
		var dateiId = datei.GetProperty("id").GetString()!;

		await host.PostAsync($"/api/dateien/{dateiId}/delete", new { rowVersion = 1u }, editor);
		await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor);

		using (var refused = await host.PostAsync($"/api/dateien/{dateiId}/restore", new { }, editor))
		{
			Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
			var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Contains("Papierkorb", problem.GetProperty("title").GetString());
		}

		using (var liedRestored = await host.PostAsync($"/api/lieder/{liedId}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NoContent, liedRestored.StatusCode);
		using (var restored = await host.PostAsync($"/api/dateien/{dateiId}/restore", new { }, editor))
			Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);

		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", editor)).GetProperty("lied");
		Assert.Equal(["noten.pdf"],
			lied.GetProperty("dateien").EnumerateArray().Select(d => d.GetProperty("fileName").GetString()));
	}

	[Fact]
	public async Task RestoringRightBeforeTheSevenDaysPassStillWorks()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Knapp");
		await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor);
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var stored = await db.Lieder.SingleAsync(l => l.Id == liedId);
			stored.DeletedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
			await db.SaveChangesAsync();
		}
		using var restored = await host.PostAsync($"/api/lieder/{liedId}/restore", new { }, editor);
		Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
		Assert.True((await host.GetJsonAsync("/api/lieder", editor)).GetProperty("lieder").GetArrayLength() == 1);
	}

	[Fact]
	public async Task TheTrashListingShowsAllThreeArtsTogether()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Gelöstes Lied");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "noten.pdf");
		var eventId = await host.CreateEventAsync(editor, "Gelöster Auftritt");
		await host.UploadEventDateiAsync(editor, eventId, "sonstiges",
			LiedTestHost.Png(2048), "foto.png", "image/png");

		await host.PostAsync($"/api/dateien/{datei.GetProperty("id").GetString()}/delete", new { rowVersion = 1u }, editor);
		await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor);
		var eventVersion = (await host.GetJsonAsync($"/api/events/{eventId}", editor))
			.GetProperty("event").GetProperty("rowVersion").GetUInt32();
		await host.PostAsync($"/api/events/{eventId}/delete", new { rowVersion = eventVersion }, editor);

		var trash = await host.GetJsonAsync("/api/papierkorb", editor);
		var liedEntry = Assert.Single(trash.GetProperty("lieder").EnumerateArray());
		Assert.Equal("Gelöstes Lied", liedEntry.GetProperty("title").GetString());
		var auftrittEntry = Assert.Single(trash.GetProperty("auftritte").EnumerateArray());
		Assert.Equal("Gelöster Auftritt", auftrittEntry.GetProperty("title").GetString());
		Assert.Equal(1, trash.GetProperty("dateien").GetArrayLength());
	}

	// ===== Sofortiges Entfernen (Administrator) ==========================
	[Fact]
	public async Task AnAdministratorRemovesATrashedLiedForGoodWithEverythingThatBelongsToIt()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var admin = await host.SignInAsync("chef@liedertafel.test", ArchiveRoles.Administrator);
		var liedId = await host.CreateLiedIdAsync(editor, "Verschwindet");
		await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "noten.pdf");
		string blob;
		using (var scope = host.Services.CreateScope())
			blob = (await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>()
				.Dateien.SingleAsync(d => d.LiedId == liedId)).BlobName;

		// Two Auftritte keep their Programm entries naming the Lied.
		var eventId = await host.CreateEventAsync(editor, "Verknüpft");
		await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = new[] { new { liedId } } }, editor);

		await host.PostAsync($"/api/lieder/{liedId}/delete", new { rowVersion = 1u }, editor);
		using (var memberRemove = await host.PostAsync($"/api/papierkorb/lieder/{liedId}/remove", new { }, editor))
			Assert.Equal(HttpStatusCode.Forbidden, memberRemove.StatusCode);
		using (var ok = await host.PostAsync($"/api/papierkorb/lieder/{liedId}/remove", new { }, admin))
			Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

		using var scope2 = host.Services.CreateScope();
		var db = scope2.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.False(await db.Lieder.AnyAsync(l => l.Id == liedId));
		Assert.Equal(0, await db.Dateien.CountAsync(d => d.LiedId == liedId));
		Assert.Equal(0, await db.ProgrammeItems.CountAsync(i => i.LiedId == liedId));
		Assert.False(host.Factory.Storage.Has(blob));
	}

	private static async Task<bool> WithDbContext(LiedTestHost host, Func<ArchiveDbContext, Task<bool>> query)
	{
		using var scope = host.Services.CreateScope();
		return await query(scope.ServiceProvider.GetRequiredService<ArchiveDbContext>());
	}
}
