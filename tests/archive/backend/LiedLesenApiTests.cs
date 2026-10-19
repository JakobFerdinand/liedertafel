using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Archive.Backend.Tests;

/// <summary>
/// #177: the reading of a draft's PDF before any Lied exists. The request
/// carries the draft and request identity back unchanged; the answer is a list
/// of suggestions, never a write. The scripted model stands in for the provider.
/// </summary>
public sealed class LiedLesenApiTests
{
	private static readonly Dictionary<string, string> Identity = new()
	{
		["entwurfId"] = "11111111-1111-1111-1111-111111111111",
		["anfrageId"] = "3",
	};

	private static byte[] AvePdf() => ScanFixtures.TextPdf(
		"Ave Maria", "Musik: Anton Bruckner", "Satz: Franz Bearbeiter", "Text: Anna Beispiel", "G-Dur", "Tenor I",
		"Maria voll der Gnade gegruesst seist du");

	private static async Task<HttpResponseMessage> PostAsync(
		LiedTestHost host, byte[] pdf, string? session = null, string fileName = "Ave_Maria.pdf")
	{
		session ??= await host.EditorAsync();
		return await host.PostFormAsync("/api/lied-lesungen", session, Identity, "datei", fileName, pdf);
	}

	private static Dictionary<string, (string Wert, bool Sicher)> Felder(JsonElement body)
		=> body.GetProperty("felder").EnumerateArray().ToDictionary(
			f => f.GetProperty("feld").GetString()!,
			f => (f.GetProperty("wert").GetString()!, f.GetProperty("sicher").GetBoolean()));

	[Fact]
	public async Task ATextPdfYieldsSuggestionsForTheFormAndEchoesTheIdentity()
	{
		await using var host = new LiedTestHost();
		using var response = await PostAsync(host, AvePdf());
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(Identity["entwurfId"], body.GetProperty("entwurfId").GetString());
		Assert.Equal(3, body.GetProperty("anfrageId").GetInt32());
		var felder = Felder(body);
		Assert.Equal("Ave Maria", felder["title"].Wert);
		Assert.Equal("Anton Bruckner", felder["composer"].Wert);
		Assert.Equal("Anna Beispiel", felder["lyricist"].Wert);
		Assert.Equal("G-Dur", felder["musicalKey"].Wert);
		Assert.Contains("Maria voll der Gnade", felder["lyrics"].Wert);
		Assert.All(felder.Values, f => Assert.True(f.Sicher));
	}

	[Fact]
	public async Task AScannedPdfIsReadThroughTheModelAndFillsTitleAndComposer()
	{
		await using var host = new LiedTestHost();
		using var response = await PostAsync(host, ScanFixtures.ScanPdf(["Seite 1"]));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var felder = Felder(await response.Content.ReadFromJsonAsync<JsonElement>());
		Assert.Equal("Waldfahrt", felder["title"].Wert);
		Assert.Equal("Franz Schubert", felder["composer"].Wert);
	}

	[Fact]
	public async Task AnUncertainReadingIsMarkedAndAnUnquotedOneIsDropped()
	{
		var model = new ScanModelStub
		{
			FieldsJson = """
				{"felder":[
				 {"feld":"title","wert":"Ave Maria","sicherheit":"unsicher","zitat":"Ave Maria"},
				 {"feld":"language","wert":"Latein","sicherheit":"sicher","zitat":"steht nirgends im Text"}]}
				""",
		};
		await using var host = new LiedTestHost(chatClient: model);
		using var response = await PostAsync(host, ScanFixtures.TextPdf("Ave Maria"));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var felder = Felder(await response.Content.ReadFromJsonAsync<JsonElement>());
		Assert.Equal(("Ave Maria", false), felder["title"]);
		Assert.DoesNotContain("language", felder.Keys);
	}

	[Fact]
	public async Task AReachedBudgetAnswers429SoTheFormStaysManual()
	{
		await using var host = new LiedTestHost(new Dictionary<string, string?> { ["Archive:Ai:MonthlyCapEur"] = "0.0000001" });
		using var response = await PostAsync(host, ScanFixtures.ScanPdf(["Seite 1"]));
		Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
	}

	[Fact]
	public async Task ANonPdfIsRefused()
	{
		await using var host = new LiedTestHost();
		using var response = await PostAsync(host, "kein pdf"u8.ToArray());
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
	}

	[Fact]
	public async Task OnlyEditorsMayAskForAReading()
	{
		await using var host = new LiedTestHost();
		var member = await host.MemberAsync();
		using var response = await PostAsync(host, AvePdf(), member);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task AMissingCsrfTokenIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/lied-lesungen");
		request.Headers.Add("Cookie", session);
		request.Content = new MultipartFormDataContent { { new ByteArrayContent(AvePdf()), "datei", "a.pdf" } };
		using var response = await host.Client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task AnIdentityThatIsNotWellFormedIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await host.PostFormAsync("/api/lied-lesungen", session,
			new Dictionary<string, string> { ["entwurfId"] = "kein-guid", ["anfrageId"] = "1" }, "datei", "a.pdf", AvePdf());
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
