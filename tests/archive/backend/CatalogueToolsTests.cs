using System.Text.Json;
using Archive.Backend.Catalogue;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

public sealed class CatalogueToolsTests
{
	[Fact]
	public async Task BrowsePagesAreBoundedStableAndCountOnlyPublishedSongs()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		// Insert in reverse title order: listing order must be explicit, not insertion order.
		for (var i = 53; i >= 0; i--)
			db.Songs.Add(new Song { Title = $"Lied {i:00}", PublishedAt = i == 0 ? null : DateTimeOffset.UtcNow });
		await db.SaveChangesAsync();
		var tool = (AIFunction)CatalogueTools.CreateCatalogueSearchTool(db);

		var first = await SearchAsync(tool, "", 1);
		Assert.Equal(53, first.GetProperty("totalCount").GetInt32());
		Assert.Equal(10, first.GetProperty("pageSize").GetInt32());
		Assert.Equal(2, first.GetProperty("nextPage").GetInt32());
		Assert.True(first.GetProperty("hasMore").GetBoolean());
		Assert.False(first.GetProperty("limitReached").GetBoolean());
		Assert.Equal(Enumerable.Range(1, 10).Select(i => $"Lied {i:00}"), Titles(first));
		Assert.Equal(Titles(first), Titles(await SearchAsync(tool, "  \t ", 0)));
		var second = await SearchAsync(tool, "", 2);
		Assert.Equal(Enumerable.Range(11, 10).Select(i => $"Lied {i:00}"), Titles(second));
		var fifth = await SearchAsync(tool, "", 5);
		Assert.Equal(Enumerable.Range(41, 10).Select(i => $"Lied {i:00}"), Titles(fifth));
		Assert.True(fifth.GetProperty("hasMore").GetBoolean());
		Assert.True(fifth.GetProperty("limitReached").GetBoolean());
		Assert.Equal(JsonValueKind.Null, fifth.GetProperty("nextPage").ValueKind);
		var pastLimit = await SearchAsync(tool, "", int.MaxValue);
		Assert.Empty(Titles(pastLimit));
		Assert.True(pastLimit.GetProperty("limitReached").GetBoolean());
		Assert.Contains("eingrenzen", pastLimit.GetProperty("error").GetString());

		// Narrowed searches keep token matching and expose the same page contract.
		var narrowed = await SearchAsync(tool, "Lied 53", 1);
		Assert.Equal(["Lied 53"], Titles(narrowed));
		Assert.Equal(1, narrowed.GetProperty("totalCount").GetInt32());
		Assert.False(narrowed.GetProperty("hasMore").GetBoolean());
		var pastEnd = await SearchAsync(tool, "Lied 53", 2);
		Assert.Empty(Titles(pastEnd));
		Assert.Equal(1, pastEnd.GetProperty("totalCount").GetInt32());
		Assert.False(pastEnd.GetProperty("hasMore").GetBoolean());
		var hidden = await SearchAsync(tool, "Lied 00", 1);
		Assert.Empty(Titles(hidden));
		Assert.Equal(0, hidden.GetProperty("totalCount").GetInt32());
	}

	[Fact]
	public async Task EmptyCatalogueAndDraftDetailsHaveNoCitationCandidates()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var draft = new Song { Title = "Geheime Probe" };
		db.Songs.Add(draft);
		await db.SaveChangesAsync();
		var empty = await SearchAsync((AIFunction)CatalogueTools.CreateCatalogueSearchTool(db), "", 1);
		Assert.Empty(Titles(empty));
		Assert.Equal(0, empty.GetProperty("totalCount").GetInt32());
		Assert.False(empty.GetProperty("hasMore").GetBoolean());
		Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextPage").ValueKind);
		var details = (AIFunction)CatalogueTools.CreateSongDetailsTool(db);
		var draftResult = await details.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["songId"] = draft.Id.ToString() }));
		var missingResult = await details.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["songId"] = Guid.NewGuid().ToString() }));
		Assert.Equal("{}", draftResult?.ToString());
		Assert.Equal(draftResult?.ToString(), missingResult?.ToString());
	}

	private static string[] Titles(JsonElement result) => result.GetProperty("songs").EnumerateArray()
		.Select(s => s.GetProperty("title").GetString()!).ToArray();

	private static async Task<JsonElement> SearchAsync(AIFunction tool, string query, int page)
	{
		var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = query, ["page"] = page }));
		using var document = JsonDocument.Parse(result!.ToString()!);
		return document.RootElement.Clone();
	}
}
