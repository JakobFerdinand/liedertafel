using System.Net;
using System.Text.Json;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>#168: replacing and deleting a Datei sends it to the editor trash; members never see it.</summary>
public sealed class DateiPapierkorbTests
{
	[Fact]
	public async Task ReplacingKeepsOnlyTheNewDateiVisibleAndTrashesTheOldOne()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Ersatzlied");
		var old = await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "alt.pdf");
		var oldId = old.GetProperty("id").GetString()!;
		string oldBlob;
		using (var scope = host.Services.CreateScope())
			oldBlob = (await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Dateien.SingleAsync()).BlobName;

		var replacement = await host.ReplaceAsync(editor, liedId, oldId, "neu.pdf");

		Assert.NotEqual(oldId, replacement.GetProperty("id").GetString());
		Assert.Equal("Tenor 1", replacement.GetProperty("stimme").GetString());
		Assert.Equal("noten", replacement.GetProperty("kind").GetString());
		foreach (var session in new[] { editor, member })
		{
			var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", session)).GetProperty("lied");
			var names = lied.GetProperty("dateien").EnumerateArray().Select(d => d.GetProperty("fileName").GetString()!).ToArray();
			Assert.Equal(["neu.pdf"], names);
		}
		using (var response = await host.GetAsync($"/api/dateien/{oldId}/access", member))
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.True(host.Factory.Storage.Has(oldBlob));

		var trash = (await host.GetJsonAsync("/api/papierkorb", editor)).GetProperty("dateien").EnumerateArray().ToArray();
		var entry = Assert.Single(trash);
		Assert.Equal(oldId, entry.GetProperty("id").GetString());
		Assert.Equal("alt.pdf", entry.GetProperty("fileName").GetString());
		Assert.Equal("Ersatzlied", entry.GetProperty("liedTitle").GetString());
		Assert.Equal(liedId.ToString(), entry.GetProperty("liedId").GetString());
		Assert.True(entry.GetProperty("replaced").GetBoolean());
		Assert.False(string.IsNullOrEmpty(entry.GetProperty("deletedBy").GetString()));
		Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("deletedAt").ValueKind);

		using var scope2 = host.Services.CreateScope();
		var stored = await scope2.ServiceProvider.GetRequiredService<ArchiveDbContext>().Dateien.SingleAsync(d => d.Id == Guid.Parse(oldId));
		Assert.Equal(Guid.Parse(replacement.GetProperty("id").GetString()!), stored.ReplacedByDateiId);
		Assert.NotNull(stored.DeletedByAccountId);
	}

	[Fact]
	public async Task DeletingMovesTheDateiToTheTrashAndHidesItFromMembers()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Loeschlied");
		var datei = await host.UploadNotenAsync(editor, liedId, "Bass 1", fileName: "weg.pdf");
		var id = datei.GetProperty("id").GetString()!;
		var version = datei.GetProperty("rowVersion").GetUInt32();

		using (var forbidden = await host.PostAsync($"/api/dateien/{id}/delete", new { rowVersion = version }, member))
			Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
		using (var stale = await host.PostAsync($"/api/dateien/{id}/delete", new { rowVersion = 99u }, editor))
			Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		using (var ok = await host.PostAsync($"/api/dateien/{id}/delete", new { rowVersion = version }, editor))
			Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		Assert.Equal(0, lied.GetProperty("dateien").GetArrayLength());
		using (var access = await host.GetAsync($"/api/dateien/{id}/access", member))
			Assert.Equal(HttpStatusCode.NotFound, access.StatusCode);
		using (var memberTrash = await host.GetAsync("/api/papierkorb", member))
			Assert.Equal(HttpStatusCode.Forbidden, memberTrash.StatusCode);
		var entry = Assert.Single((await host.GetJsonAsync("/api/papierkorb", editor)).GetProperty("dateien").EnumerateArray().ToArray());
		Assert.Equal("weg.pdf", entry.GetProperty("fileName").GetString());
		Assert.False(entry.GetProperty("replaced").GetBoolean());
		using (var again = await host.PostAsync($"/api/dateien/{id}/delete", new { rowVersion = version }, editor))
			Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
	}

	[Fact]
	public async Task OnlyEditorsMayReplaceAndOnlyLiveDateienOfTheSameLiedCanBeReplaced()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Eins");
		var otherId = await host.CreateLiedIdAsync(editor, "Zwei");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1");
		var id = datei.GetProperty("id").GetString()!;

		using (var forbidden = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
			new { kind = "noten", replacesDateiId = id, fileName = "x.pdf", sizeBytes = 64 }, member))
			Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
		using (var wrongLied = await host.PostAsync($"/api/lieder/{otherId}/dateien/upload-session",
			new { kind = "noten", replacesDateiId = id, fileName = "x.pdf", sizeBytes = 64 }, editor))
			Assert.Equal(HttpStatusCode.NotFound, wrongLied.StatusCode);
		using (var unknown = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
			new { kind = "noten", replacesDateiId = Guid.NewGuid(), fileName = "x.pdf", sizeBytes = 64 }, editor))
			Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

		await host.ReplaceAsync(editor, liedId, id, "neu.pdf");
		using var replaceTrashed = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
			new { kind = "noten", replacesDateiId = id, fileName = "x.pdf", sizeBytes = 64 }, editor);
		Assert.Equal(HttpStatusCode.NotFound, replaceTrashed.StatusCode);
	}

	[Fact]
	public async Task FinalizeConflictsWhenTheReplacedDateiWasDeletedMeanwhile()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Rennen");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1");
		var id = datei.GetProperty("id").GetString()!;
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId,
			new { kind = "noten", replacesDateiId = id, fileName = "neu.pdf", sizeBytes = 64 });
		using (var del = await host.PostAsync($"/api/dateien/{id}/delete", new { rowVersion = datei.GetProperty("rowVersion").GetUInt32() }, editor))
			Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
		host.Factory.Storage.Store(host.Factory.Storage.Find(uploadUrl)!.BlobName, LiedTestHost.ValidPdf(64));

		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.Conflict, finalize.StatusCode);
		using var scope = host.Services.CreateScope();
		Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Dateien.CountAsync());
	}
}
