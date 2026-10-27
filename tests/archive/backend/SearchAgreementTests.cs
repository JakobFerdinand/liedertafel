using System.Text.Json;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #192: member search and the chat's catalogue_search are two adapters over
/// one search module — for the same query both return the same matches in the
/// same order; only the page size stays the chat's smaller one.
/// </summary>
public sealed class SearchAgreementTests
{
	private static readonly string[] Titel =
	[
		"Lied 00", "Lied 01", "Lied 02", "Lied 03", "Lied 04", "Lied 05", "Lied 06", "Lied 07", "Lied 08", "Lied 09",
		// The chat pages with ten; the member list pages with twenty. A twelfth
		// Lied makes a same-order paging difference visible.
		"Lied 10", "Lied 11",
	];

	[Fact]
	public async Task EmptyQueriesShowTheSameLiederInTheSameOrder()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		foreach (var titel in Titel)
			await host.CreateLiedIdAsync(editor, titel);

		var (memberListe, chat) = await BeideAsync(host, member, "");
		var chatTitel = LiedTitel(chat);
		Assert.Equal(12, chat.GetProperty("totalCount").GetInt32());
		Assert.Equal(12, memberListe.GetProperty("total").GetInt32());
		// The chat shows its one first page of ten; the member list shows all
		// of them, and the chat's page is the member list's first ten.
		Assert.Equal(10, chatTitel.Count);
		Assert.Equal(Enumerable.Range(0, 10).Select(i => $"Lied {i:00}"), chatTitel);
		Assert.Equal(Enumerable.Range(0, 12).Select(i => $"Lied {i:00}"), Liste(memberListe));
	}

	[Fact]
	public async Task AQueryReturnsTheSameMatchesInTheSameOrder()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		await GezaehteLiederAsync(host, editor);

		// Every rank appears: the exact title, the title that carries every
		// token, a match in the Liedtext — in the one search order.
		var (memberListe, chat) = await BeideAsync(host, member, "mond wald");
		Assert.Equal(["Mond Wald", "Mond Waldlied", "Anders"], Liste(memberListe));
		Assert.Equal(Liste(memberListe), LiedTitel(chat));
		Assert.Equal(3, chat.GetProperty("totalCount").GetInt32());
		Assert.Equal(3, memberListe.GetProperty("total").GetInt32());
	}

	private static async Task GezaehteLiederAsync(LiedTestHost host, string editor)
	{
		await host.CreateLiedIdAsync(editor, "Mond Wald");
		await host.CreateLiedIdAsync(editor, "Mond Waldlied");
		await host.CreateLiedAsync(editor, new { title = "Kuckuck", composer = "Eduard Künneke" });
		await host.CreateLiedAsync(editor, new { title = "Anders", lyrics = "die wald ruht stille immond anders" });
	}

	private static async Task<(JsonElement Member, JsonElement Chat)> BeideAsync(LiedTestHost host, string member, string query)
	{
		var json = await host.GetJsonAsync($"/api/lieder?q={Uri.EscapeDataString(query)}", member);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var tool = (AIFunction)CatalogueTools.CreateCatalogueSearchTool(db);
		var chat = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["query"] = query,
			["page"] = 1,
		}));
		using var chatText = JsonDocument.Parse(chat!.ToString()!);
		return (json, chatText.RootElement.Clone());
	}

	private static List<string> Liste(JsonElement json) => json.GetProperty("lieder")
		.EnumerateArray().Select(l => l.GetProperty("title").GetString()!).ToList();

	private static List<string> LiedTitel(JsonElement chat) => chat.GetProperty("lieder")
		.EnumerateArray().Select(l => l.GetProperty("title").GetString()!).ToList();
}
