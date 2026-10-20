using System.Text.Json;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

public sealed class CatalogueToolsTests
{
	[Fact]
	public async Task BrowsePagesAreBoundedStableAndCountEveryLied()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		// Insert in reverse title order: listing order must be explicit, not insertion order.
		for (var i = 53; i >= 0; i--)
			db.Lieder.Add(new Lied { Title = $"Lied {i:00}" });
		await db.SaveChangesAsync();
		var tool = (AIFunction)CatalogueTools.CreateCatalogueSearchTool(db);

		var first = await SearchAsync(tool, "", 1);
		Assert.Equal(54, first.GetProperty("totalCount").GetInt32());
		Assert.Equal(10, first.GetProperty("pageSize").GetInt32());
		Assert.Equal(2, first.GetProperty("nextPage").GetInt32());
		Assert.True(first.GetProperty("hasMore").GetBoolean());
		Assert.False(first.GetProperty("limitReached").GetBoolean());
		Assert.Equal(Enumerable.Range(0, 10).Select(i => $"Lied {i:00}"), Titles(first));
		Assert.Equal(Titles(first), Titles(await SearchAsync(tool, "  \t ", 0)));
		var second = await SearchAsync(tool, "", 2);
		Assert.Equal(Enumerable.Range(10, 10).Select(i => $"Lied {i:00}"), Titles(second));
		var fifth = await SearchAsync(tool, "", 5);
		Assert.Equal(Enumerable.Range(40, 10).Select(i => $"Lied {i:00}"), Titles(fifth));
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
		var first00 = await SearchAsync(tool, "Lied 00", 1);
		Assert.Equal(["Lied 00"], Titles(first00));
	}

	[Fact]
	public async Task EmptyCatalogueAndUnknownDetailsHaveNoCitationCandidates()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var empty = await SearchAsync((AIFunction)CatalogueTools.CreateCatalogueSearchTool(db), "", 1);
		Assert.Empty(Titles(empty));
		Assert.Equal(0, empty.GetProperty("totalCount").GetInt32());
		Assert.False(empty.GetProperty("hasMore").GetBoolean());
		Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextPage").ValueKind);
		var details = (AIFunction)CatalogueTools.CreateLiedDetailsTool(db);
		var missingResult = await details.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["liedId"] = Guid.NewGuid().ToString() }));
		var malformedResult = await details.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["liedId"] = "kein-guid" }));
		Assert.Equal("{}", missingResult?.ToString());
		Assert.Equal("{}", malformedResult?.ToString());
	}

	[Fact]
	public async Task DetailsCarryTheTextAndFactsOfTheLiedsLiveNoten()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var lied = new Lied { Title = "Hosianna" };
		var live = NewNoten(lied, now);
		var trashed = NewNoten(lied, now);
		trashed.DeletedAt = now;
		var second = NewNoten(lied, now);
		db.Lieder.Add(lied);
		db.Dateien.AddRange(live, trashed, second);
		db.ExtractionJobs.AddRange(
			new Archive.Backend.Extraction.ExtractionJob { DateiId = live.Id, Status = Archive.Backend.Extraction.ExtractionStatus.Completed, Text = "Tenor 1 A♭\n44 .å\nHo -\nåê\nsi -\nå\nan -\nå\nna,", CreatedAt = now, UpdatedAt = now },
			new Archive.Backend.Extraction.ExtractionJob { DateiId = trashed.Id, Status = Archive.Backend.Extraction.ExtractionStatus.Completed, Text = "veraltet", CreatedAt = now, UpdatedAt = now },
			new Archive.Backend.Extraction.ExtractionJob { DateiId = second.Id, Status = Archive.Backend.Extraction.ExtractionStatus.Completed, Text = "Tenor 1 A♭\n44 .å\nHo -\nåê\nsi -\nå\nan -\nå\nna,", CreatedAt = now, UpdatedAt = now });
		await db.SaveChangesAsync();

		var details = (AIFunction)CatalogueTools.CreateLiedDetailsTool(db);
		var result = await details.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["liedId"] = lied.Id.ToString() }));
		using var document = JsonDocument.Parse(result!.ToString()!);

		var dateien = document.RootElement.GetProperty("dateien").EnumerateArray().ToList();
		Assert.Equal(2, dateien.Count);
		var texts = dateien.Select(d => d.GetProperty("notenText").GetString()).ToList();
		// Identical text is sent once; the trashed Datei contributes nothing.
		Assert.Single(texts, t => t == "Tenor 1 A♭ 44 Hosianna,");
		Assert.Single(texts, t => t is null);
		Assert.DoesNotContain("veraltet", result.ToString());
		Assert.Equal("A♭", dateien.First(d => d.GetProperty("notenText").ValueKind == JsonValueKind.String)
			.GetProperty("notenFakten").GetProperty("musicalKey").GetString());
	}

	private static Archive.Backend.Lieder.Datei NewNoten(Lied lied, DateTimeOffset now) => new()
	{
		LiedId = lied.Id, Kind = Archive.Backend.Lieder.DateiKinds.Noten, IsPartitur = true,
		BlobName = $"dateien/{Guid.NewGuid():N}", ContentType = "application/pdf", SizeBytes = 10,
		CreatedByAccountId = Guid.NewGuid(), CreatedAt = now, RowVersion = 1,
	};

	[Fact]
	public async Task DetailsNameOnlyAuftritteAMemberMaySeeAndSeparatePlannedFromSung()
	{
		await using var factory = new AuthApiFactory();
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var lied = new Lied { Title = "Die Waldfahrt" };
		var sungEvent = NewEvent("Sängerfest", 1932, published: true, now);
		var recordedEvent = NewEvent("Chronik 1910", 1910, published: true, now);
		var draftEvent = NewEvent("Geheimes Konzert", 1950, published: false, now);
		var plannedEvent = NewEvent("Adventskonzert", 2098, published: true, now);
		db.Lieder.Add(lied);
		db.Events.AddRange(sungEvent, recordedEvent, draftEvent, plannedEvent);
		foreach (var choirEvent in new[] { sungEvent, draftEvent, plannedEvent })
		{
			var programme = new EventProgramme { EventId = choirEvent.Id, CreatedAt = now, UpdatedAt = now };
			programme.Items.Add(new ProgrammeItem { ProgrammeId = programme.Id, Position = 1, LiedId = lied.Id });
			db.Programmes.Add(programme);
		}
		// A Lied-Aufnahme naming an Auftritt also puts the Lied into its history.
		db.Dateien.Add(new Archive.Backend.Lieder.Datei
		{
			LiedId = lied.Id, Kind = Archive.Backend.Lieder.DateiKinds.Aufnahme, RecordedAtEventId = recordedEvent.Id,
			BlobName = $"dateien/{Guid.NewGuid():N}", ContentType = "audio/mp4", SizeBytes = 10,
			CreatedByAccountId = Guid.NewGuid(), CreatedAt = now, RowVersion = 1,
		});
		await db.SaveChangesAsync();

		var details = (AIFunction)CatalogueTools.CreateLiedDetailsTool(db);
		var result = await details.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["liedId"] = lied.Id.ToString() }));
		var text = result!.ToString()!;
		using var document = JsonDocument.Parse(text);
		var root = document.RootElement;
		Assert.Equal(lied.Id.ToString(), root.GetProperty("lieder")[0].GetProperty("id").GetString());
		var sung = root.GetProperty("gesungen").EnumerateArray().ToList();
		Assert.Equal(["Sängerfest", "Chronik 1910"], sung.Select(s => s.GetProperty("auftritt").GetString()).ToList());
		Assert.False(sung[0].TryGetProperty("nachweis", out _));
		Assert.Equal("Adventskonzert", Assert.Single(root.GetProperty("geplant").EnumerateArray().ToList()).GetProperty("auftritt").GetString());
		Assert.DoesNotContain("Geheimes Konzert", text);
	}

	private static ChoirEvent NewEvent(string title, int year, bool published, DateTimeOffset now) => new()
	{
		Kind = "concert",
		Title = title,
		DateYear = year,
		PublishedAt = published ? now : null,
		CreatedAt = now,
		UpdatedAt = now,
	};

	private static string[] Titles(JsonElement result) => result.GetProperty("lieder").EnumerateArray()
		.Select(s => s.GetProperty("title").GetString()!).ToArray();

	private static async Task<JsonElement> SearchAsync(AIFunction tool, string query, int page)
	{
		var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = query, ["page"] = page }));
		using var document = JsonDocument.Parse(result!.ToString()!);
		return document.RootElement.Clone();
	}
}
