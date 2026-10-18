using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;

namespace Archive.Backend.Tests;

/// <summary>#166: the flat Lied at the HTTP seam (create, read, patch, list, filters).</summary>
public sealed class LiedApiTests
{
	private static readonly object Full = new
	{
		title = "Der Mond ist aufgegangen",
		composer = "J. A. P. Schulz",
		arranger = "Max Mustermann",
		lyricist = "Matthias Claudius",
		voiceConfiguration = "TTBB",
		musicalKey = "F-Dur",
		language = "Deutsch",
		lyrics = "Der Mond ist aufgegangen",
		occasion = "Abendlied",
	};

	[Fact]
	public async Task EditorCreatesLiedAndMemberSeesItImmediately()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();

		var lied = await host.CreateLiedAsync(editor, Full);
		var id = lied.GetProperty("id").GetString()!;
		Assert.Equal("Der Mond ist aufgegangen", lied.GetProperty("title").GetString());
		Assert.Equal("Max Mustermann", lied.GetProperty("arranger").GetString());
		Assert.Equal("TTBB", lied.GetProperty("voiceConfiguration").GetString());
		Assert.Equal("Abendlied", lied.GetProperty("occasion").GetString());

		var seen = (await host.GetJsonAsync($"/api/lieder/{id}", member)).GetProperty("lied");
		Assert.Equal("F-Dur", seen.GetProperty("musicalKey").GetString());
		Assert.Equal("Matthias Claudius", seen.GetProperty("lyricist").GetString());
		Assert.Equal(0, seen.GetProperty("dateien").GetArrayLength());

		var list = await host.GetJsonAsync("/api/lieder", member);
		Assert.Equal(1, list.GetProperty("total").GetInt32());
		Assert.Equal(id, list.GetProperty("lieder")[0].GetProperty("id").GetString());
	}

	[Fact]
	public async Task CreateRequiresTitleAndEditorRole()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();

		using var memberResponse = await host.PostAsync("/api/lieder", Full, member);
		Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
		using var noTitle = await host.PostAsync("/api/lieder", new { title = "  " }, editor);
		Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
		using var anonymous = await host.GetAsync("/api/lieder", null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
	}

	[Fact]
	public async Task PatchChangesFieldsAndStaleRowVersionConflicts()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var lied = await host.CreateLiedAsync(editor, Full);
		var id = lied.GetProperty("id").GetString()!;
		var version = lied.GetProperty("rowVersion").GetUInt32();

		using var patched = await host.PatchAsync($"/api/lieder/{id}",
			new { occasion = "Weihnachten", composer = "", rowVersion = version }, editor);
		Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
		var after = (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lied");
		Assert.Equal("Weihnachten", after.GetProperty("occasion").GetString());
		Assert.Equal(JsonValueKind.Null, after.GetProperty("composer").ValueKind);
		Assert.Equal("Matthias Claudius", after.GetProperty("lyricist").GetString());
		Assert.True(after.GetProperty("rowVersion").GetUInt32() > version);

		using var stale = await host.PatchAsync($"/api/lieder/{id}", new { title = "Neu", rowVersion = version }, editor);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		using var forbidden = await host.PatchAsync($"/api/lieder/{id}", new { title = "Neu" }, member);
		Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
		using var missing = await host.PatchAsync($"/api/lieder/{Guid.NewGuid()}", new { title = "Neu" }, editor);
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task ListSearchesTitlesAndFiltersByBesetzungAnlassAndDateiTyp()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var mond = await host.CreateLiedAsync(editor, Full);
		await host.CreateLiedAsync(editor, new { title = "Über den Wolken", voiceConfiguration = "SATB", occasion = "Frühling" });
		var plain = await host.CreateLiedIdAsync(editor, "Ohne Angaben");
		await host.UploadNotenAsync(editor, Guid.Parse(mond.GetProperty("id").GetString()!), "Tenor 1");

		async Task<string[]> TitlesAsync(string query)
		{
			var list = await host.GetJsonAsync($"/api/lieder{query}", member);
			return list.GetProperty("lieder").EnumerateArray().Select(l => l.GetProperty("title").GetString()!).ToArray();
		}

		Assert.Equal(["Der Mond ist aufgegangen", "Ohne Angaben", "Über den Wolken"], await TitlesAsync(""));
		Assert.Equal(["Über den Wolken"], await TitlesAsync("?q=ueber"));
		Assert.Equal(["Der Mond ist aufgegangen"], await TitlesAsync("?q=mond"));
		Assert.Equal(["Der Mond ist aufgegangen"], await TitlesAsync("?besetzung=ttbb"));
		Assert.Equal(["Über den Wolken"], await TitlesAsync("?anlass=fruehling"));
		Assert.Equal(["Der Mond ist aufgegangen"], await TitlesAsync("?dateiTyp=noten"));
		Assert.Empty(await TitlesAsync("?dateiTyp=uebe"));
		Assert.Empty(await TitlesAsync("?besetzung=TTBB&anlass=Frühling"));

		var list = await host.GetJsonAsync("/api/lieder", member);
		var options = list.GetProperty("options");
		Assert.Equal(["SATB", "TTBB"], options.GetProperty("voiceConfigurations").EnumerateArray().Select(e => e.GetString()!).ToArray());
		Assert.Equal(["Abendlied", "Frühling"], options.GetProperty("occasions").EnumerateArray().Select(e => e.GetString()!).ToArray());
		var withNoten = list.GetProperty("lieder").EnumerateArray().Single(l => l.GetProperty("title").GetString() == "Der Mond ist aufgegangen");
		Assert.Equal(["noten"], withNoten.GetProperty("dateiTypen").EnumerateArray().Select(e => e.GetString()!).ToArray());
		Assert.NotEqual(Guid.Empty, plain);

		using var tooLong = await host.GetAsync($"/api/lieder?q={new string('a', 201)}", member);
		Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
		using var unknown = await host.GetAsync("/api/lieder?dateiTyp=video", member);
		Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
	}

	[Fact]
	public async Task ListPagesByTwenty()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		for (var i = 0; i < 21; i++)
			await host.CreateLiedIdAsync(editor, $"Lied {i:00}");

		var first = await host.GetJsonAsync("/api/lieder", editor);
		Assert.Equal(21, first.GetProperty("total").GetInt32());
		Assert.Equal(20, first.GetProperty("lieder").GetArrayLength());
		var second = await host.GetJsonAsync("/api/lieder?page=2", editor);
		Assert.Equal(1, second.GetProperty("lieder").GetArrayLength());
		Assert.Equal("Lied 20", second.GetProperty("lieder")[0].GetProperty("title").GetString());
	}
}
