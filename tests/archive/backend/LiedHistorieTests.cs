using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #160 Lied history ("wann gesungen") at the HTTP seam: derived from the
/// Programme of Auftritte plus the Lied-Aufnahmen that name an Auftritt. There
/// is no stored evidence, so there is nothing to confirm; editing a Programm
/// changes the history at once.
/// </summary>
public sealed class LiedHistorieTests
{
	[Fact]
	public async Task HistoryListsAuftritteFromProgrammesNewestFirstAndKeepsYearOnlyDatesHonest()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Geschichtslied");
		var other = await host.CreateLiedIdAsync(editor, "Anderes Lied");
		var e1975 = await CreateEventAsync(host, editor, "Späterer Auftritt", 1975, 6, 1);
		var e1960 = await CreateEventAsync(host, editor, "Ungefähres Fest", 1960, null, null, approximate: true);
		var yearOnly = await CreateEventAsync(host, editor, "Nur ein Jahr", 1950, null, null);
		var undated = await CreateEventAsync(host, editor, "Undatiert", null, null, null);
		var without = await CreateEventAsync(host, editor, "Ohne das Lied", 1999, 1, 1);
		foreach (var id in new[] { e1975, e1960, yearOnly, undated })
			await SaveProgrammeAsync(host, editor, id, other, lied, lied);
		await SaveProgrammeAsync(host, editor, without, other);

		var history = await host.GetJsonAsync($"/api/lieder/{lied}/historie", member);
		Assert.Equal("Geschichtslied", history.GetProperty("lied").GetProperty("title").GetString());
		var rows = history.GetProperty("auftritte").EnumerateArray().ToList();
		Assert.Equal(
			new[] { e1975, e1960, yearOnly, undated },
			rows.Select(r => Guid.Parse(r.GetProperty("eventId").GetString()!)).ToArray());
		// A Lied twice in one Programm is one Auftritt with two entries.
		Assert.All(rows, r => Assert.Equal(2, r.GetProperty("entries").GetInt32()));
		Assert.All(rows, r => Assert.True(r.GetProperty("inProgramme").GetBoolean()));
		Assert.Equal("1. Juni 1975", rows[0].GetProperty("dateDisplay").GetString());
		Assert.False(rows[0].GetProperty("dateUncertain").GetBoolean());
		Assert.Equal("um 1960", rows[1].GetProperty("dateDisplay").GetString());
		Assert.True(rows[1].GetProperty("dateUncertain").GetBoolean());
		Assert.Equal("1950", rows[2].GetProperty("dateDisplay").GetString());
		Assert.Equal("year", rows[2].GetProperty("datePrecision").GetString());
		Assert.True(rows[2].GetProperty("dateUncertain").GetBoolean());
		Assert.True(rows[3].GetProperty("dateUncertain").GetBoolean());
		var counts = history.GetProperty("counts");
		Assert.Equal(4, counts.GetProperty("events").GetInt32());
		Assert.Equal(4, counts.GetProperty("inProgramme").GetInt32());
		Assert.Equal(3, counts.GetProperty("uncertainDates").GetInt32());
		Assert.Equal(4, history.GetProperty("total").GetInt32());
		// Nothing of the removed evidence model is left in the answer.
		Assert.False(rows[0].TryGetProperty("evidenceStatus", out _));
		Assert.False(counts.TryGetProperty("confirmed", out _));
	}

	[Fact]
	public async Task EditingTheProgrammeChangesTheHistoryAtOnce()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Wanderlied");
		var other = await host.CreateLiedIdAsync(editor, "Abendlied");
		var concert = await CreateEventAsync(host, editor, "Jahreskonzert", 2001, 5, 12);

		async Task<int> TotalAsync() => (await host.GetJsonAsync($"/api/lieder/{lied}/historie", member)).GetProperty("total").GetInt32();
		Assert.Equal(0, await TotalAsync());

		var saved = await SaveProgrammeAsync(host, editor, concert, other, lied);
		Assert.Equal(1, await TotalAsync());

		// The actual order differed: reordering keeps the Lied in the history.
		var ids = saved.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
		using var reorder = await host.PutAsync($"/api/events/{concert}/programme/items", new
		{
			items = new object[] { new { id = ids[1], liedId = lied }, new { id = ids[0], liedId = other } },
			rowVersion = saved.GetProperty("rowVersion").GetUInt32(),
		}, editor);
		Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
		Assert.Equal(1, await TotalAsync());

		// It was not sung after all: removing the entry removes the history.
		var current = (await reorder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");
		using var remove = await host.PutAsync($"/api/events/{concert}/programme/items", new
		{
			items = new object[] { new { id = ids[0], liedId = other } },
			rowVersion = current.GetProperty("rowVersion").GetUInt32(),
		}, editor);
		Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
		Assert.Equal(0, await TotalAsync());
	}

	[Fact]
	public async Task ALiedAufnahmeNamingAnAuftrittAppearsInTheLiedHistoryAndOnTheAuftritt()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Aufgenommenes Lied");
		var concert = await CreateEventAsync(host, editor, "Sängerfest", 1988, 9, 3);
		var elsewhere = await CreateEventAsync(host, editor, "Anderswo", 1990, 1, 1);
		var trashed = Guid.Empty;
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var kept = NewAufnahme(lied, concert, "kept.mp3");
			var gone = NewAufnahme(lied, elsewhere, "gone.mp3");
			gone.DeletedAt = DateTimeOffset.UtcNow;
			trashed = gone.Id;
			db.Dateien.AddRange(kept, gone, NewAufnahme(lied, null, "ohne-auftritt.mp3"));
			await db.SaveChangesAsync();
		}

		// The Lied was not in any Programm, yet its Aufnahme puts it into the history.
		var history = await host.GetJsonAsync($"/api/lieder/{lied}/historie", member);
		var row = Assert.Single(history.GetProperty("auftritte").EnumerateArray());
		Assert.Equal(concert.ToString(), row.GetProperty("eventId").GetString());
		Assert.False(row.GetProperty("inProgramme").GetBoolean());
		Assert.Equal(0, row.GetProperty("entries").GetInt32());
		Assert.Equal("kept.mp3", Assert.Single(row.GetProperty("liedAufnahmen").EnumerateArray()).GetProperty("fileName").GetString());

		// The Auftritt shows the same Aufnahme with its Lied.
		var detail = (await host.GetJsonAsync($"/api/events/{concert}", member)).GetProperty("event");
		var aufnahme = Assert.Single(detail.GetProperty("liedAufnahmen").EnumerateArray());
		Assert.Equal(lied.ToString(), aufnahme.GetProperty("liedId").GetString());
		Assert.Equal("Aufgenommenes Lied", aufnahme.GetProperty("liedTitle").GetString());
		Assert.Equal("kept.mp3", aufnahme.GetProperty("fileName").GetString());
		var other = (await host.GetJsonAsync($"/api/events/{elsewhere}", member)).GetProperty("event");
		Assert.Empty(other.GetProperty("liedAufnahmen").EnumerateArray());
		Assert.NotEqual(Guid.Empty, trashed);
	}

	[Fact]
	public async Task UnknownLiedIs404AndAnonymousCallersAre401()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");

		using var unknown = await host.GetAsync($"/api/lieder/{Guid.NewGuid()}/historie", member);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		using var anonymous = await host.GetAsync($"/api/lieder/{lied}/historie", null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
	}

	[Fact]
	public async Task HistoryPagesAreFixedSizeAndDeterministic()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Dauerbrenner");
		for (var i = 0; i < 25; i++)
		{
			var id = await CreateEventAsync(host, editor, $"Auftritt {i:00}", 1900 + i, 5, 5);
			await SaveProgrammeAsync(host, editor, id, lied);
		}

		var first = await host.GetJsonAsync($"/api/lieder/{lied}/historie", editor);
		Assert.Equal(25, first.GetProperty("total").GetInt32());
		Assert.Equal(20, first.GetProperty("pageSize").GetInt32());
		var firstTitles = first.GetProperty("auftritte").EnumerateArray().Select(r => r.GetProperty("eventTitle").GetString()).ToList();
		Assert.Equal(20, firstTitles.Count);
		Assert.Equal("Auftritt 24", firstTitles[0]);
		var second = await host.GetJsonAsync($"/api/lieder/{lied}/historie?page=2", editor);
		Assert.Equal(["Auftritt 04", "Auftritt 03", "Auftritt 02", "Auftritt 01", "Auftritt 00"],
			second.GetProperty("auftritte").EnumerateArray().Select(r => r.GetProperty("eventTitle").GetString()).ToList());
		var beyond = await host.GetJsonAsync($"/api/lieder/{lied}/historie?page=99", editor);
		Assert.Equal(2, beyond.GetProperty("page").GetInt32());
	}

	private static Datei NewAufnahme(Guid liedId, Guid? eventId, string fileName) => new()
	{
		LiedId = liedId,
		Kind = DateiKinds.Aufnahme,
		RecordedAtEventId = eventId,
		BlobName = $"dateien/{Guid.NewGuid():N}",
		ContentType = "audio/mp4",
		OriginalFileName = fileName,
		SizeBytes = 10,
		CreatedByAccountId = Guid.NewGuid(),
		CreatedAt = DateTimeOffset.UtcNow,
		RowVersion = 1,
	};

	private static async Task<JsonElement> SaveProgrammeAsync(LiedTestHost host, string session, Guid eventId, params Guid[] lieder)
	{
		using var response = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = lieder.Select(id => new { liedId = id }).ToArray() }, session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme").Clone();
	}

	private static async Task<Guid> CreateEventAsync(LiedTestHost host, string session, string title,
		int? year, int? month, int? day, bool approximate = false)
	{
		using var response = await host.PostAsync("/api/events", new
		{
			kind = "concert", title, dateYear = year, dateMonth = month, dateDay = day, dateApproximate = approximate,
		}, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("event").GetProperty("id").GetString()!);
	}
}
