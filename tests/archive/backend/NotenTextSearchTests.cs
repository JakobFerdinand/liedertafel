using System.Net;
using System.Text.Json;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #169: plain search on /api/lieder also finds words that stand only in the
/// extracted text of a Lied's live Noten.
/// </summary>
public sealed class NotenTextSearchTests
{
	[Fact]
	public async Task AWordThatStandsOnlyInTheNotenFindsTheLied()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var waldfahrt = await host.CreateLiedIdAsync(editor, "Waldfahrt");
		await host.CreateLiedIdAsync(editor, "Heimweh");
		var noten = await host.UploadNotenAsync(editor, waldfahrt, "Alle", isPartitur: true);
		await SetTextAsync(host, Guid.Parse(noten.GetProperty("id").GetString()!), "Im Wald da singen die Nachtigallen");

		Assert.Equal(["Waldfahrt"], await TitlesAsync(host, editor, "nachtigallen"));
		// Every word must stand in the title or in the text.
		Assert.Equal(["Waldfahrt"], await TitlesAsync(host, editor, "waldfahrt nachtigallen"));
		Assert.Empty(await TitlesAsync(host, editor, "heimweh nachtigallen"));
		Assert.Empty(await TitlesAsync(host, editor, "kuckuck"));
	}

	[Fact]
	public async Task MembersFindLiederByNotenTextToo()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var noten = await host.UploadNotenAsync(editor, lied, "Alle", isPartitur: true);
		await SetTextAsync(host, Guid.Parse(noten.GetProperty("id").GetString()!), "Der Mond ist aufgegangen");

		Assert.Equal(["Abendlied"], await TitlesAsync(host, member, "MOND"));
	}

	[Fact]
	public async Task NotenInTheTrashAndOtherKindsDoNotContributeText()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var old = await host.UploadNotenAsync(editor, lied, "Alle", isPartitur: true);
		var oldId = Guid.Parse(old.GetProperty("id").GetString()!);
		await SetTextAsync(host, oldId, "Veraltete Strophe");
		Assert.Equal(["Abendlied"], await TitlesAsync(host, editor, "veraltete"));

		// Replacing sends the old Datei to the trash; its text stops matching.
		using var replace = await host.PostAsync($"/api/lieder/{lied}/dateien/upload-session",
			new { kind = "noten", replacesDateiId = oldId, fileName = "neu.pdf", sizeBytes = 1024 }, editor);
		Assert.Equal(HttpStatusCode.Created, replace.StatusCode);
		var replaced = await host.ReplaceAsync(editor, lied, oldId.ToString(), "neu.pdf");
		Assert.NotEqual(oldId.ToString(), replaced.GetProperty("id").GetString());

		Assert.Empty(await TitlesAsync(host, editor, "veraltete"));
	}

	private static async Task<List<string>> TitlesAsync(LiedTestHost host, string session, string q)
	{
		var json = await host.GetJsonAsync($"/api/lieder?q={Uri.EscapeDataString(q)}", session);
		return json.GetProperty("lieder").EnumerateArray().Select(l => l.GetProperty("title").GetString()!).ToList();
	}

	private static async Task SetTextAsync(LiedTestHost host, Guid dateiId, string text)
	{
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var job = await db.ExtractionJobs.SingleAsync(j => j.DateiId == dateiId);
		job.Status = ExtractionStatus.Completed;
		job.Text = text;
		await db.SaveChangesAsync();
	}
}
