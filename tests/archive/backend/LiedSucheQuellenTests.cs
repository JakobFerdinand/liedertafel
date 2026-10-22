using System.Text.Json;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #142: plain search finds a Lied by Titel, Komponist, Bearbeiter, Texter,
/// Liedtext and words inside its Noten (embedded text and text read from
/// scans), and every Treffer says where the match stands.
/// </summary>
public sealed class LiedSucheQuellenTests
{
	[Fact]
	public async Task APhraseReadFromAScanFindsTheLiedAndNamesTheSource()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var waldfahrt = await host.CreateLiedIdAsync(editor, "Waldfahrt");
		await host.CreateLiedIdAsync(editor, "Heimweh");
		var noten = await host.UploadNotenAsync(editor, waldfahrt, "Alle", isPartitur: true);
		await SetTextAsync(host, Guid.Parse(noten.GetProperty("id").GetString()!), "Im Wald grünen die Tannen", ExtractionTextSource.Reading);

		var treffer = await RowsAsync(host, editor, "tannen");
		Assert.Equal(["Waldfahrt"], treffer.Select(t => t.GetProperty("title").GetString()!).ToArray());
		Assert.Equal(["in den Noten"], Arranged(Assert.Single(treffer)));
	}

	[Fact]
	public async Task LiedtextAndCreatorsAreSearchedAndNamed()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		await host.CreateLiedAsync(editor, new { title = "Peter und der Drache", composer = "Eduard Mörike" });
		await host.CreateLiedAsync(editor, new { title = "Wanderschaft", arranger = "Emma Weiß" });
		await host.CreateLiedAsync(editor, new { title = "Ohne Angaben", lyricist = "Matthias Claudius" });
		await host.CreateLiedAsync(editor, new { title = "Stille Frische", lyrics = "Im grünen Walde ruht der Tag" });

		Assert.Equal(["beim Komponisten"], await MatchInAsync(host, editor, "moerike"));
		Assert.Equal(["beim Bearbeiter"], await MatchInAsync(host, editor, "weiss"));
		Assert.Equal(["beim Texter"], await MatchInAsync(host, editor, "claudius"));
		Assert.Equal(["im Liedtext"], await MatchInAsync(host, editor, "ruht"));
		// Tokens distributed over two fields still match and name both places.
		var both = await RowsAsync(host, editor, "frische walde");
		Assert.Equal(["Stille Frische"], both.Select(t => t.GetProperty("title").GetString()!).ToArray());
		Assert.Equal(["im Titel", "im Liedtext"], Arranged(Assert.Single(both)));
	}

	[Fact]
	public async Task GermanSpellVariantsMatchRegardlessOfUmlautsAndSz()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		await host.CreateLiedAsync(editor, new { title = "Weinheimer Straße", lyrics = "Es grünt so grün im Kuhtal" });

		// ß / ss, ü / ue und u: both spellings find the same Lied.
		Assert.Equal(["Weinheimer Straße"], await TitlesAsync(host, editor, "strasse"));
		Assert.Equal(["Weinheimer Straße"], await TitlesAsync(host, editor, "straße"));
		Assert.Equal(["Weinheimer Straße"], await TitlesAsync(host, editor, "gruen"));
		Assert.Equal(["Weinheimer Straße"], await TitlesAsync(host, editor, "grün"));
	}

	private static async Task<JsonElement[]> RowsAsync(LiedTestHost host, string session, string q)
	{
		var json = await host.GetJsonAsync($"/api/lieder?q={Uri.EscapeDataString(q)}", session);
		return json.GetProperty("lieder").EnumerateArray().ToArray();
	}

	private static async Task<List<string>> TitlesAsync(LiedTestHost host, string session, string q) =>
		(await RowsAsync(host, session, q)).Select(t => t.GetProperty("title").GetString()!).ToList();

	private static async Task<List<string>> MatchInAsync(LiedTestHost host, string session, string q) =>
		Arranged(Assert.Single(await RowsAsync(host, session, q))).ToList();

	private static string[] Arranged(JsonElement lied) =>
		lied.GetProperty("matchIn").EnumerateArray().Select(e => e.GetString()!).ToArray();

	private static async Task SetTextAsync(LiedTestHost host, Guid dateiId, string text, ExtractionTextSource source)
	{
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var job = await db.ExtractionJobs.SingleAsync(j => j.DateiId == dateiId);
		job.Status = ExtractionStatus.Completed;
		job.Text = text;
		job.TextSource = source;
		await db.SaveChangesAsync();
	}
}
