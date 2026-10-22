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

		var treffer = await TitlesAsync(host, editor, "tannen");
		Assert.Equal(["Waldfahrt"], treffer.Select(t => t.GetProperty("title").GetString()!).ToArray());
		var single = Assert.Single(treffer);
		Assert.Equal(["in den Noten"], Values(single.GetProperty("matchIn")));
	}

	private static async Task<JsonElement[]> TitlesAsync(LiedTestHost host, string session, string q)
	{
		var json = await host.GetJsonAsync($"/api/lieder?q={Uri.EscapeDataString(q)}", session);
		return json.GetProperty("lieder").EnumerateArray().ToArray();
	}

	private static string[] Values(JsonElement array) =>
		array.EnumerateArray().Select(e => e.GetString()!).ToArray();

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
