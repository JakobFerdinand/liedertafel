using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #160: an Auftritt has one editable Programm. Saving it is the whole
/// publication step (no revisions, no draft, no confirmation); entries keep
/// their id when reordered. Every test goes through the HTTP seam.
/// </summary>
public sealed class AuftrittProgrammTests
{
	[Fact]
	public async Task EditorSavesAProgrammeOfAPublishedAuftrittAndMembersSeeItImmediately()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var waldfahrt = await host.CreateLiedAsync(editor, new { title = "Die Waldfahrt", voiceConfiguration = "TTBB", musicalKey = "G-Dur" });
		var waldId = Guid.Parse(waldfahrt.GetProperty("id").GetString()!);
		var abendlied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var eventId = await CreateEventAsync(host, editor, "Frühjahrskonzert", 2099);

		using var save = await host.PutAsync($"/api/events/{eventId}/programme/items", new
		{
			items = new object[]
			{
				new { liedId = waldId, note = "Eröffnung" },
				new { liedId = abendlied },
				new { liedId = waldId },
			},
		}, editor);
		Assert.Equal(HttpStatusCode.OK, save.StatusCode);
		var saved = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");
		// One Programm: no revision, draft, history or confirmation parts.
		foreach (var gone in new[] { "working", "published", "history", "confirmation" })
			Assert.False(saved.TryGetProperty(gone, out _), gone);
		var items = saved.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal([1, 2, 3], items.Select(i => i.GetProperty("position").GetInt32()).ToList());
		Assert.Equal("Die Waldfahrt", items[0].GetProperty("liedTitle").GetString());
		Assert.Equal("TTBB", items[0].GetProperty("voiceConfiguration").GetString());
		Assert.Equal("Eröffnung", items[0].GetProperty("note").GetString());
		Assert.NotEqual(items[0].GetProperty("id").GetString(), items[2].GetProperty("id").GetString());

		// Saving was enough: the member sees it without a publish step.
		var detail = (await host.GetJsonAsync($"/api/events/{eventId}", member)).GetProperty("event");
		Assert.False(detail.TryGetProperty("performances", out _));
		var memberItems = detail.GetProperty("programme").GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(["Die Waldfahrt", "Abendlied", "Die Waldfahrt"],
			memberItems.Select(i => i.GetProperty("liedTitle").GetString()).ToList());
		var upcoming = (await host.GetJsonAsync("/api/programmes", member)).GetProperty("programmes").EnumerateArray().ToList();
		var row = Assert.Single(upcoming);
		Assert.Equal(eventId.ToString(), row.GetProperty("eventId").GetString());
		Assert.Equal(3, row.GetProperty("itemCount").GetInt32());

		using var publish = await host.PostAsync($"/api/events/{eventId}/programme/publish", new { rowVersion = 1 }, editor);
		Assert.False(publish.IsSuccessStatusCode);
	}

	[Fact]
	public async Task ReorderingAfterwardsKeepsEntryIdsAndTheOrderIsWhatMembersRead()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var a = await host.CreateLiedIdAsync(editor, "Erstes Lied");
		var b = await host.CreateLiedIdAsync(editor, "Zweites Lied");
		var c = await host.CreateLiedIdAsync(editor, "Drittes Lied");
		var eventId = await CreateEventAsync(host, editor, "Jahreskonzert", 1950);

		var first = await SaveAsync(host, editor, eventId, new object[] { new { liedId = a }, new { liedId = b }, new { liedId = c } });
		var ids = first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

		// Swap two survivors, edit a note, drop the middle one, add a new one.
		var second = await SaveAsync(host, editor, eventId, new object[]
		{
			new { id = ids[2], liedId = c },
			new { id = ids[0], liedId = a, note = "Zugabe" },
			new { liedId = b },
		}, first.GetProperty("rowVersion").GetUInt32());
		var items = second.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal([ids[2], ids[0]], items.Take(2).Select(i => i.GetProperty("id").GetString()).ToList());
		Assert.DoesNotContain(ids[1], items.Select(i => i.GetProperty("id").GetString()));
		Assert.Equal([1, 2, 3], items.Select(i => i.GetProperty("position").GetInt32()).ToList());
		Assert.Equal("Zugabe", items[1].GetProperty("note").GetString());
		Assert.Equal(first.GetProperty("rowVersion").GetUInt32() + 1, second.GetProperty("rowVersion").GetUInt32());

		var detail = (await host.GetJsonAsync($"/api/events/{eventId}", member)).GetProperty("event");
		Assert.Equal(["Drittes Lied", "Erstes Lied", "Zweites Lied"],
			detail.GetProperty("programme").GetProperty("items").EnumerateArray()
				.Select(i => i.GetProperty("liedTitle").GetString()).ToList());

		// A stale row version is refused and writes nothing.
		using var stale = await host.PutAsync($"/api/events/{eventId}/programme/items", new
		{
			items = new object[] { new { liedId = a } },
			rowVersion = first.GetProperty("rowVersion").GetUInt32(),
		}, editor);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(3, await db.ProgrammeItems.CountAsync());
	}

	[Fact]
	public async Task InvalidEntriesAreRefusedWithGermanProblemsAndWriteNothing()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var a = await host.CreateLiedIdAsync(editor, "Erstes Lied");
		var eventId = await CreateEventAsync(host, editor, "Konzert", 2099);

		async Task Rejected(object body, string title)
		{
			using var response = await host.PutAsync($"/api/events/{eventId}/programme/items", body, editor);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			Assert.Equal(title, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
		}

		await Rejected(new { items = new object[] { new { liedId = a, note = new string('x', 501) } } }, ProgrammeEndpoints.NoteTooLongMessage);
		await Rejected(new { items = new object[] { new { liedId = Guid.NewGuid() } } }, ProgrammeEndpoints.MissingLiedMessage);
		await Rejected(new { items = new object[] { new { note = "ohne Lied" } } }, ProgrammeEndpoints.MissingLiedMessage);
		var saved = await SaveAsync(host, editor, eventId, new object[] { new { liedId = a } });
		var id = saved.GetProperty("items")[0].GetProperty("id").GetString();
		await Rejected(new
		{
			items = new object[] { new { id, liedId = a }, new { id, liedId = a } },
			rowVersion = saved.GetProperty("rowVersion").GetUInt32(),
		}, ProgrammeEndpoints.InvalidItemMessage);

		using var unknownEvent = await host.PutAsync($"/api/events/{Guid.NewGuid()}/programme/items",
			new { items = new object[] { new { liedId = a } } }, editor);
		Assert.Equal(HttpStatusCode.NotFound, unknownEvent.StatusCode);
	}

	[Fact]
	public async Task OnlyEditorsSaveAndDraftAuftritteStayHiddenFromMembers()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var draft = await CreateEventAsync(host, editor, "Interner Termin", 2099, publish: false);
		await SaveAsync(host, editor, draft, new object[] { new { liedId = lied } });

		using var asMember = await host.PutAsync($"/api/events/{draft}/programme/items",
			new { items = new object[] { new { liedId = lied } } }, member);
		Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);

		using var hidden = await host.GetAsync($"/api/events/{draft}", member);
		Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
		var upcoming = await host.GetJsonAsync("/api/programmes", member);
		Assert.Empty(upcoming.GetProperty("programmes").EnumerateArray());
		var forEditor = await host.GetJsonAsync("/api/programmes", editor);
		Assert.Single(forEditor.GetProperty("programmes").EnumerateArray());
	}

	private static async Task<JsonElement> SaveAsync(LiedTestHost host, string session, Guid eventId, object[] items, uint? rowVersion = null)
	{
		using var save = await host.PutAsync($"/api/events/{eventId}/programme/items", new { items, rowVersion }, session);
		Assert.Equal(HttpStatusCode.OK, save.StatusCode);
		return (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme").Clone();
	}

	private static async Task<Guid> CreateEventAsync(LiedTestHost host, string session, string title, int year, bool publish = true)
	{
		using var response = await host.PostAsync("/api/events",
			new { kind = "concert", title, dateYear = year, dateMonth = 5, dateDay = 12 }, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var id = Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("event").GetProperty("id").GetString()!);
		if (publish)
		{
			using var published = await host.PostAsync($"/api/events/{id}/publish", new { }, session);
			Assert.True(published.IsSuccessStatusCode);
		}
		return id;
	}
}
