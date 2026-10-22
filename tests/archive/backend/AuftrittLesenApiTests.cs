using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #131: the reading of a dropped programme scan or photo before any Auftritt
/// exists (<c>POST /api/auftritt-lesungen</c>). The answer is suggestions for
/// the form (title, date, place) and the Programm with every entry matched
/// against the catalogue; it never writes an Auftritt or a Lied. The scripted
/// model stands in for the provider.
/// </summary>
public sealed class AuftrittLesenApiTests
{
	private static readonly Dictionary<string, string> Identity = new()
	{
		["entwurfId"] = "44444444-4444-4444-4444-444444444444",
		["anfrageId"] = "2",
	};

	/// <summary>A text PDF of a programme: every argument is one line.</summary>
	private static byte[] ProgrammPdf(params string[] lines) => ScanFixtures.TextPdf(lines);

	private static byte[] SaengerfestPdf() => ProgrammPdf(
		"Fruehjahrskonzert der Liedertafel Mining",
		"Samstag, 12. Mai 1951",
		"Ort: Gasthaus zur Post, Mining",
		"1. Ave Maria",
		"2. Der Mond ist aufgegangen",
		"3. Waldfahrt");

	private static async Task<HttpResponseMessage> PostAsync(
		LiedTestHost host, byte[] file, string? session = null, string fileName = "programm.pdf")
	{
		session ??= await host.EditorAsync();
		return await host.PostFormAsync("/api/auftritt-lesungen", session, Identity, "datei", fileName, file);
	}

	private static Dictionary<string, (string Wert, bool Sicher)> Felder(JsonElement body)
		=> body.GetProperty("felder").EnumerateArray().ToDictionary(
			f => f.GetProperty("feld").GetString()!,
			f => (f.GetProperty("wert").GetString()!, f.GetProperty("sicher").GetBoolean()));

	private static List<JsonElement> Programm(JsonElement body) => [.. body.GetProperty("programm").EnumerateArray()];

	[Fact]
	public async Task AProgrammTextPdfFillsTheAuftrittAndMatchesTheProgrammAndEchoesTheIdentity()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var ave = await host.CreateLiedIdAsync(session, "Ave Maria");
		var mond = await host.CreateLiedIdAsync(session, "Der Mond ist aufgegangen");

		using var response = await PostAsync(host, SaengerfestPdf(), session);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(Identity["entwurfId"], body.GetProperty("entwurfId").GetString());
		Assert.Equal(2, body.GetProperty("anfrageId").GetInt32());
		var felder = Felder(body);
		Assert.Equal("Fruehjahrskonzert der Liedertafel Mining", felder["title"].Wert);
		Assert.Equal("concert", felder["kind"].Wert);
		Assert.Equal("1951", felder["jahr"].Wert);
		Assert.Equal("5", felder["monat"].Wert);
		Assert.Equal("12", felder["tag"].Wert);
		Assert.Equal("Gasthaus zur Post, Mining", felder["venue"].Wert);
		Assert.DoesNotContain("unsicher", felder.Keys);

		var programm = Programm(body);
		Assert.Equal(["Ave Maria", "Der Mond ist aufgegangen", "Waldfahrt"],
			programm.Select(p => p.GetProperty("text").GetString()));
		Assert.Equal(ave.ToString(), programm[0].GetProperty("lied").GetProperty("id").GetString());
		Assert.True(programm[0].GetProperty("lied").GetProperty("sicher").GetBoolean());
		Assert.Equal(mond.ToString(), programm[1].GetProperty("lied").GetProperty("id").GetString());
	}

	[Fact]
	public async Task AnEntryThatIsNotInTheCatalogueStaysUnmatchedForTheEditor()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.CreateLiedIdAsync(session, "Ave Maria");

		using var response = await PostAsync(host, SaengerfestPdf(), session);

		var waldfahrt = Programm(await response.Content.ReadFromJsonAsync<JsonElement>())[2];
		Assert.Equal(JsonValueKind.Null, waldfahrt.GetProperty("lied").ValueKind);
		Assert.Empty(waldfahrt.GetProperty("kandidaten").EnumerateArray());
	}

	[Fact]
	public async Task ANearMissIsOfferedMarkedUncertainAndAWrongTitleIsNeverPickedByItself()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var mond = await host.CreateLiedIdAsync(session, "Der Mond ist aufgegangen");
		var wald = await host.CreateLiedIdAsync(session, "Im Wald und auf der Heide");

		using var response = await PostAsync(host, ProgrammPdf(
			"Konzert", "1. Der Mond ist aufgegangn", "2. Im Walde"), session);

		var programm = Programm(await response.Content.ReadFromJsonAsync<JsonElement>());
		// A single clearly closest Lied is pre-selected but marked as not sure.
		Assert.Equal(mond.ToString(), programm[0].GetProperty("lied").GetProperty("id").GetString());
		Assert.False(programm[0].GetProperty("lied").GetProperty("sicher").GetBoolean());
		// A weak similarity is only a candidate; the editor decides.
		Assert.Equal(JsonValueKind.Null, programm[1].GetProperty("lied").ValueKind);
		Assert.DoesNotContain(mond.ToString(),
			programm[1].GetProperty("kandidaten").EnumerateArray().Select(k => k.GetProperty("id").GetString()));
		_ = wald;
	}

	[Fact]
	public async Task TwoLiederWithTheSameTitleAreOfferedAndNoneIsPicked()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var first = await host.CreateLiedIdAsync(session, "Ave Maria");
		var second = await host.CreateLiedIdAsync(session, "Ave Maria");

		using var response = await PostAsync(host, ProgrammPdf("Konzert", "1. Ave Maria"), session);

		var eintrag = Programm(await response.Content.ReadFromJsonAsync<JsonElement>())[0];
		Assert.Equal(JsonValueKind.Null, eintrag.GetProperty("lied").ValueKind);
		var kandidaten = eintrag.GetProperty("kandidaten").EnumerateArray().Select(k => k.GetProperty("id").GetString()).ToList();
		Assert.Equal(2, kandidaten.Count);
		Assert.Contains(first.ToString(), kandidaten);
		Assert.Contains(second.ToString(), kandidaten);
	}

	[Fact]
	public async Task MatchingIgnoresCaseUmlautsAndPunctuation()
	{
		// The PDF fixture font has no umlauts, so the text comes from a photo through the stub.
		var model = new ScanModelStub
		{
			Transcriptions = { "Programm\n1. SUESSER DIE GLOCKEN NIE KLINGEN!" },
			FieldsJson = """{"felder":[],"punkte":[{"titel":"SUESSER DIE GLOCKEN NIE KLINGEN!","sicherheit":"sicher","zitat":"1. SUESSER DIE GLOCKEN NIE KLINGEN!"}]}""",
		};
		await using var host = new LiedTestHost(chatClient: model);
		var session = await host.EditorAsync();
		var lied = await host.CreateLiedIdAsync(session, "Süßer die Glocken nie klingen");

		using var response = await PostAsync(host, ScanFixtures.PageJpeg(["Programm"]), session, "p.jpg");

		var eintrag = Programm(await response.Content.ReadFromJsonAsync<JsonElement>()).Single();
		Assert.Equal(lied.ToString(), eintrag.GetProperty("lied").GetProperty("id").GetString());
		Assert.True(eintrag.GetProperty("lied").GetProperty("sicher").GetBoolean());
	}

	[Fact]
	public async Task AnApproximateYearIsReadAsYearOnlyAndMarkedUnsure()
	{
		await using var host = new LiedTestHost();

		using var response = await PostAsync(host, ProgrammPdf("Saengerfest", "um 1950", "1. Ave Maria"));

		var felder = Felder(await response.Content.ReadFromJsonAsync<JsonElement>());
		Assert.Equal("1950", felder["jahr"].Wert);
		Assert.DoesNotContain("monat", felder.Keys);
		Assert.DoesNotContain("tag", felder.Keys);
		Assert.Equal("true", felder["unsicher"].Wert);
		Assert.Equal("festival", felder["kind"].Wert);
	}

	[Fact]
	public async Task APhotographedProgrammIsReadThroughTheModel()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var ave = await host.CreateLiedIdAsync(session, "Ave Maria");

		using var response = await PostAsync(host, ScanFixtures.PageJpeg(["Programm"]), session, "programm.jpg");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var felder = Felder(body);
		Assert.Equal("Sängerfest der Liedertafel Mining", felder["title"].Wert);
		Assert.Equal("1925", felder["jahr"].Wert);
		Assert.Equal("6", felder["monat"].Wert);
		Assert.Equal("14", felder["tag"].Wert);
		var programm = Programm(body);
		Assert.Equal(ave.ToString(), programm[0].GetProperty("lied").GetProperty("id").GetString());
		Assert.Equal(3, programm.Count);
	}

	[Fact]
	public async Task AScannedProgrammPdfIsRenderedAndReadThroughTheModel()
	{
		await using var host = new LiedTestHost();

		using var response = await PostAsync(host, ScanFixtures.ScanPdf(["Seite 1"]));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var felder = Felder(await response.Content.ReadFromJsonAsync<JsonElement>());
		Assert.Equal("Sängerfest der Liedertafel Mining", felder["title"].Wert);
	}

	[Fact]
	public async Task AnEntryWhoseQuoteIsNotInTheTextIsDropped()
	{
		var model = new ScanModelStub
		{
			Transcriptions = { "Programm\n1. Ave Maria" },
			FieldsJson = """
				{"felder":[{"feld":"title","wert":"Konzert","sicherheit":"sicher","zitat":"Konzert steht nirgends"}],
				 "punkte":[
				 {"titel":"Ave Maria","sicherheit":"sicher","zitat":"1. Ave Maria"},
				 {"titel":"Erfundenes Lied","sicherheit":"sicher","zitat":"3. Erfundenes Lied"}]}
				""",
		};
		await using var host = new LiedTestHost(chatClient: model);

		using var response = await PostAsync(host, ScanFixtures.PageJpeg(["Programm"]), fileName: "p.jpg");

		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(["Ave Maria"], Programm(body).Select(p => p.GetProperty("text").GetString()));
		Assert.DoesNotContain("title", Felder(body).Keys);
	}

	[Fact]
	public async Task AnUncertainEntryIsKeptAndMarked()
	{
		var model = new ScanModelStub
		{
			Transcriptions = { "Programm\n1. Ave Mara" },
			FieldsJson = """{"felder":[],"punkte":[{"titel":"Ave Mara","sicherheit":"unsicher","zitat":"1. Ave Mara"}]}""",
		};
		await using var host = new LiedTestHost(chatClient: model);

		using var response = await PostAsync(host, ScanFixtures.PageJpeg(["Programm"]), fileName: "p.jpg");

		var eintrag = Programm(await response.Content.ReadFromJsonAsync<JsonElement>()).Single();
		Assert.False(eintrag.GetProperty("sicher").GetBoolean());
	}

	[Fact]
	public async Task ReadingWritesNeitherAnAuftrittNorALied()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.CreateLiedIdAsync(session, "Ave Maria");

		using var response = await PostAsync(host, SaengerfestPdf(), session);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		await using var scope = host.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(0, await db.Events.CountAsync());
		Assert.Equal(0, await db.Programmes.CountAsync());
		Assert.Equal(1, await db.Lieder.CountAsync());
	}

	[Fact]
	public async Task AReachedBudgetAnswers429SoTheFormStaysManual()
	{
		await using var host = new LiedTestHost(new Dictionary<string, string?> { ["Archive:Ai:MonthlyCapEur"] = "0.0000001" });
		using var response = await PostAsync(host, ScanFixtures.PageJpeg(["Programm"]), fileName: "p.jpg");
		Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
	}

	[Fact]
	public async Task AFileThatIsNeitherPdfNorPhotoIsRefused()
	{
		await using var host = new LiedTestHost();
		using var response = await PostAsync(host, "kein programm"u8.ToArray(), fileName: "p.txt");
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
	}

	[Fact]
	public async Task OnlyEditorsMayAskForAReading()
	{
		await using var host = new LiedTestHost();
		var member = await host.MemberAsync();
		using var response = await PostAsync(host, SaengerfestPdf(), member);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task AMissingCsrfTokenIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auftritt-lesungen");
		request.Headers.Add("Cookie", session);
		request.Content = new MultipartFormDataContent { { new ByteArrayContent(SaengerfestPdf()), "datei", "a.pdf" } };
		using var response = await host.Client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task AnIdentityThatIsNotWellFormedIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await host.PostFormAsync("/api/auftritt-lesungen", session,
			new Dictionary<string, string> { ["entwurfId"] = "kein-guid", ["anfrageId"] = "1" }, "datei", "a.pdf", SaengerfestPdf());
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
