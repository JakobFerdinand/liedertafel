using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #170: Programm items, performances and the Lied history point at the new
/// flat Lied (beside the old song chain, which the final ticket drops). Every
/// test goes through the HTTP seam with <see cref="LiedTestHost"/>.
/// </summary>
public sealed class ProgrammeLiedApiTests
{
	[Fact]
	public async Task EditorBuildsAProgrammeFromLiederAndMembersSeeItOnTheAuftritt()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var waldfahrt = await host.CreateLiedAsync(editor, new { title = "Die Waldfahrt", voiceConfiguration = "TTBB", musicalKey = "G-Dur" });
		var abendlied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var waldId = Guid.Parse(waldfahrt.GetProperty("id").GetString()!);
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
		var items = saved.GetProperty("working").GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(3, items.Count);
		Assert.Equal(waldId.ToString(), items[0].GetProperty("liedId").GetString());
		Assert.Equal("Die Waldfahrt", items[0].GetProperty("liedTitle").GetString());
		Assert.Equal("TTBB", items[0].GetProperty("voiceConfiguration").GetString());
		Assert.Equal("G-Dur", items[0].GetProperty("musicalKey").GetString());
		Assert.Equal("Eröffnung", items[0].GetProperty("note").GetString());
		Assert.Equal(JsonValueKind.Null, items[0].GetProperty("musicalVersionId").ValueKind);
		Assert.NotEqual(items[0].GetProperty("id").GetString(), items[2].GetProperty("id").GetString());

		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var rows = await db.ProgrammeItems.OrderBy(i => i.Position).ToListAsync();
			Assert.All(rows, r =>
			{
				Assert.NotNull(r.LiedId);
				Assert.Null(r.SongId);
				Assert.Null(r.MusicalVersionId);
			});
		}

		using var publish = await host.PostAsync($"/api/events/{eventId}/programme/publish",
			new { rowVersion = saved.GetProperty("rowVersion").GetUInt32() }, editor);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		await PublishEventAsync(host, editor, eventId);

		var detail = await host.GetJsonAsync($"/api/events/{eventId}", member);
		var memberItems = detail.GetProperty("event").GetProperty("programme").GetProperty("published")
			.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal(["Die Waldfahrt", "Abendlied", "Die Waldfahrt"],
			memberItems.Select(i => i.GetProperty("liedTitle").GetString()).ToList());
	}

	[Fact]
	public async Task ProgrammeItemNeedsAnExistingLiedAndExactlyOneReference()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var eventId = await CreateEventAsync(host, editor, "Konzert", 2099);

		using var unknown = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = new object[] { new { liedId = Guid.NewGuid() } } }, editor);
		Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
		using var mixed = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = new object[] { new { liedId = lied, songId = Guid.NewGuid(), musicalVersionId = Guid.NewGuid() } } }, editor);
		Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
		using var empty = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = new object[] { new { note = "nichts" } } }, editor);
		Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
	}

	[Fact]
	public async Task ConfirmingALiedProgrammeWritesLiedPerformancesThatShowInTheLiedHistory()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var waldfahrt = await host.CreateLiedIdAsync(editor, "Die Waldfahrt");
		var abendlied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var zugabe = await host.CreateLiedIdAsync(editor, "Zugabe");
		var eventId = await CreateEventAsync(host, editor, "Jahreskonzert", 1950);
		await PublishEventAsync(host, editor, eventId);
		var revisionId = await SaveAndPublishAsync(host, editor, eventId, waldfahrt, abendlied);

		var review = (await host.GetJsonAsync($"/api/events/{eventId}/programme/confirmation", editor)).GetProperty("review");
		var reviewItems = review.GetProperty("items").EnumerateArray().ToList();
		Assert.Equal("Die Waldfahrt", reviewItems[0].GetProperty("liedTitle").GetString());
		using var confirm = await host.PutAsync($"/api/events/{eventId}/programme/confirmation", new
		{
			revisionId,
			rowVersion = review.GetProperty("rowVersion").GetUInt32(),
			items = reviewItems.Select((item, index) => new
			{
				programmeItemId = Guid.Parse(item.GetProperty("programmeItemId").GetString()!),
				outcome = index == 0 ? "sung" : "skipped",
			}).ToArray(),
			additions = new object[] { new { liedId = zugabe, clientKey = Guid.NewGuid() } },
		}, editor);
		Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var rows = await db.Performances.Where(p => p.EventId == eventId).OrderBy(p => p.Position).ToListAsync();
			Assert.Equal([waldfahrt, zugabe], rows.Select(r => r.LiedId!.Value).ToList());
			Assert.All(rows, r => Assert.Null(r.SongId));
		}

		// The Auftritt shows what was sung with the Lied title.
		var detail = (await host.GetJsonAsync($"/api/events/{eventId}", member)).GetProperty("event");
		var actual = detail.GetProperty("programme").GetProperty("confirmation").GetProperty("actual").EnumerateArray().ToList();
		Assert.Equal(["Die Waldfahrt", "Zugabe"], actual.Select(a => a.GetProperty("liedTitle").GetString()).ToList());

		// Members read the Lied history; the skipped Lied has none.
		var history = await host.GetJsonAsync($"/api/lieder/{waldfahrt}/performances", member);
		Assert.Equal("Die Waldfahrt", history.GetProperty("lied").GetProperty("title").GetString());
		var entry = Assert.Single(history.GetProperty("performances").EnumerateArray().ToList());
		Assert.Equal(eventId.ToString(), entry.GetProperty("eventId").GetString());
		Assert.Equal("Jahreskonzert", entry.GetProperty("eventTitle").GetString());
		Assert.Equal("confirmed", entry.GetProperty("evidenceStatus").GetString());
		Assert.Equal(1, history.GetProperty("counts").GetProperty("confirmed").GetProperty("occurrences").GetInt32());
		Assert.False(entry.TryGetProperty("sourceNote", out _));
		var skipped = await host.GetJsonAsync($"/api/lieder/{abendlied}/performances", member);
		Assert.Empty(skipped.GetProperty("performances").EnumerateArray());
	}

	[Fact]
	public async Task LiedHistoryHidesDraftEventsFromMembersAndRejectsUnknownLieder()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var draftEvent = await CreateEventAsync(host, editor, "Interner Termin", 1950);
		using var create = await host.PostAsync($"/api/events/{draftEvent}/performances",
			new { liedId = lied, evidenceStatus = "confirmed" }, editor);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);

		var forEditor = await host.GetJsonAsync($"/api/lieder/{lied}/performances", editor);
		var editorRow = Assert.Single(forEditor.GetProperty("performances").EnumerateArray().ToList());
		Assert.False(editorRow.GetProperty("eventPublished").GetBoolean());
		var forMember = await host.GetJsonAsync($"/api/lieder/{lied}/performances", member);
		Assert.Empty(forMember.GetProperty("performances").EnumerateArray());

		using var unknown = await host.GetAsync($"/api/lieder/{Guid.NewGuid()}/performances", member);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
		using var anonymous = await host.GetAsync($"/api/lieder/{lied}/performances", null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
	}

	[Fact]
	public async Task EvidenceForALiedIsRecordedOnceAndCannotBeGivenAnOldVersionChain()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var lied = await host.CreateLiedIdAsync(editor, "Abendlied");
		var eventId = await CreateEventAsync(host, editor, "Chronik 1950", 1950);

		using var first = await host.PostAsync($"/api/events/{eventId}/performances",
			new { liedId = lied, evidenceStatus = "mention", sourceNote = "Festschrift" }, editor);
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		var created = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("performance");
		Assert.Equal(lied.ToString(), created.GetProperty("liedId").GetString());
		Assert.Equal("Abendlied", created.GetProperty("liedTitle").GetString());
		using var duplicate = await host.PostAsync($"/api/events/{eventId}/performances",
			new { liedId = lied, evidenceStatus = "mention", sourceNote = "Festschrift" }, editor);
		Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
		using var withVersion = await host.PostAsync($"/api/events/{eventId}/performances",
			new { liedId = lied, musicalVersionId = Guid.NewGuid(), evidenceStatus = "confirmed" }, editor);
		Assert.Equal(HttpStatusCode.NotFound, withVersion.StatusCode);
		using var unknown = await host.PostAsync($"/api/events/{eventId}/performances",
			new { liedId = Guid.NewGuid(), evidenceStatus = "confirmed" }, editor);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
	}

	private static async Task<Guid> CreateEventAsync(LiedTestHost host, string session, string title, int year)
	{
		using var response = await host.PostAsync("/api/events",
			new { kind = "concert", title, dateYear = year, dateMonth = 5, dateDay = 12 }, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(json.GetProperty("event").GetProperty("id").GetString()!);
	}

	private static async Task PublishEventAsync(LiedTestHost host, string session, Guid eventId)
	{
		using var response = await host.PostAsync($"/api/events/{eventId}/publish", new { }, session);
		Assert.True(response.IsSuccessStatusCode);
	}

	private static async Task<Guid> SaveAndPublishAsync(LiedTestHost host, string session, Guid eventId, params Guid[] lieder)
	{
		using var save = await host.PutAsync($"/api/events/{eventId}/programme/items",
			new { items = lieder.Select(id => new { liedId = id }).ToArray() }, session);
		Assert.Equal(HttpStatusCode.OK, save.StatusCode);
		var programme = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");
		var revisionId = Guid.Parse(programme.GetProperty("working").GetProperty("id").GetString()!);
		using var publish = await host.PostAsync($"/api/events/{eventId}/programme/publish",
			new { rowVersion = programme.GetProperty("rowVersion").GetUInt32() }, session);
		Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
		return revisionId;
	}
}
