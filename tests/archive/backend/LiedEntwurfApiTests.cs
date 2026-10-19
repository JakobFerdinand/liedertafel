using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #178: the persisted Lied draft behind "Angefangen". The draft keeps the
/// form (fields, edited-field marks, Dateien without their bytes) and the
/// reading results the server holds per Datei. It is owned by the editor who
/// started it and never carries where a value came from.
/// </summary>
public sealed class LiedEntwurfApiTests
{
	private static readonly Guid EntwurfId = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid DateiId = Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly Guid ZweiteDatei = Guid.Parse("33333333-3333-3333-3333-333333333333");

	private static object Datei(Guid id, string name = "Ave_Maria.pdf", string art = "partitur", string stimme = "",
		bool artBearbeitet = false, bool stimmeBearbeitet = false, string lesenStatus = "keine", int lesenAnfrage = 0)
		=> new
		{
			id,
			name,
			sizeBytes = 1234,
			art,
			stimme,
			artBearbeitet,
			stimmeBearbeitet,
			lesen = new { status = lesenStatus, anfrageId = lesenAnfrage, meldung = "" },
		};

	private static object Entwurf(Guid? id = null, string title = "Ave Maria", string composer = "",
		string[]? bearbeitet = null, Dictionary<string, string>? vorschlaege = null, object[]? dateien = null)
		=> new
		{
			entwurfId = id ?? EntwurfId,
			lied = new
			{
				title,
				composer,
				arranger = "",
				lyricist = "",
				voiceConfiguration = "",
				musicalKey = "",
				language = "",
				lyrics = "",
			},
			bearbeitet = bearbeitet ?? [],
			vorschlaege = vorschlaege ?? [],
			dateien = dateien ?? [Datei(DateiId)],
		};

	private static string Path(Guid? id = null) => $"/api/lied-entwuerfe/{id ?? EntwurfId}";

	private static byte[] AvePdf() => ScanFixtures.TextPdf(
		"Ave Maria", "Musik: Anton Bruckner", "Satz: Franz Bearbeiter", "Text: Anna Beispiel", "G-Dur", "Tenor I",
		"Maria voll der Gnade gegruesst seist du");

	private static Task<HttpResponseMessage> LesenAsync(
		LiedTestHost host, string session, byte[] pdf, int anfrageId = 1, Guid? dateiId = null, Guid? entwurfId = null)
		=> host.PostFormAsync("/api/lied-lesungen", session, new Dictionary<string, string>
		{
			["entwurfId"] = (entwurfId ?? EntwurfId).ToString(),
			["anfrageId"] = anfrageId.ToString(),
			["dateiId"] = (dateiId ?? DateiId).ToString(),
		}, "datei", "Ave_Maria.pdf", pdf);

	[Fact]
	public async Task ADraftIsSavedAndComesBackAsItWas()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var saved = await host.PutAsync(Path(), Entwurf(
			composer: "Bruckner", bearbeitet: ["composer", "lyrics"],
			vorschlaege: new() { ["title"] = "unsicher" },
			dateien: [Datei(DateiId, "Ave_Maria_T1.mp3", "uebe", "Tenor 1", stimmeBearbeitet: true)]), session);
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

		var body = (await host.GetJsonAsync(Path(), session)).GetProperty("entwurf");
		Assert.Equal(EntwurfId, body.GetProperty("entwurfId").GetGuid());
		Assert.Equal("Bruckner", body.GetProperty("lied").GetProperty("composer").GetString());
		Assert.Equal(["composer", "lyrics"], body.GetProperty("bearbeitet").EnumerateArray().Select(e => e.GetString()));
		Assert.Equal("unsicher", body.GetProperty("vorschlaege").GetProperty("title").GetString());
		var datei = Assert.Single(body.GetProperty("dateien").EnumerateArray());
		Assert.Equal("Ave_Maria_T1.mp3", datei.GetProperty("name").GetString());
		Assert.Equal("uebe", datei.GetProperty("art").GetString());
		Assert.Equal("Tenor 1", datei.GetProperty("stimme").GetString());
		Assert.True(datei.GetProperty("stimmeBearbeitet").GetBoolean());
		Assert.False(datei.GetProperty("artBearbeitet").GetBoolean());
		Assert.Equal(JsonValueKind.Null, datei.GetProperty("lesung").ValueKind);
	}

	[Fact]
	public async Task SavingAgainReplacesTheFormAndTheDateienButAnEmptyFieldStaysEmpty()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(composer: "Bruckner", dateien: [Datei(DateiId), Datei(ZweiteDatei, "b.mp3", "uebe", "Bass 1")]), session);
		await host.PutAsync(Path(), Entwurf(composer: "", bearbeitet: ["composer"], dateien: [Datei(ZweiteDatei, "b.mp3", "uebe", "Bass 1")]), session);

		var body = (await host.GetJsonAsync(Path(), session)).GetProperty("entwurf");
		Assert.Equal("", body.GetProperty("lied").GetProperty("composer").GetString());
		Assert.Equal(["composer"], body.GetProperty("bearbeitet").EnumerateArray().Select(e => e.GetString()));
		var datei = Assert.Single(body.GetProperty("dateien").EnumerateArray());
		Assert.Equal(ZweiteDatei, datei.GetProperty("id").GetGuid());
	}

	[Fact]
	public async Task TheListShowsTheOwnDraftsNewestFirstWithTitleAndFileCount()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var zweite = Guid.Parse("44444444-4444-4444-4444-444444444444");
		await host.PutAsync(Path(), Entwurf(title: "Ave Maria"), session);
		await host.PutAsync(Path(zweite), Entwurf(zweite, "Waldfahrt", dateien: [Datei(Guid.NewGuid()), Datei(Guid.NewGuid())]), session);

		var liste = (await host.GetJsonAsync("/api/lied-entwuerfe", session)).GetProperty("entwuerfe").EnumerateArray().ToList();
		Assert.Equal(2, liste.Count);
		Assert.Equal("Waldfahrt", liste[0].GetProperty("titel").GetString());
		Assert.Equal(2, liste[0].GetProperty("dateien").GetInt32());
		Assert.Equal(zweite, liste[0].GetProperty("entwurfId").GetGuid());
		Assert.Equal("Ave Maria", liste[1].GetProperty("titel").GetString());
	}

	[Fact]
	public async Task ADraftBelongsToItsEditor()
	{
		await using var host = new LiedTestHost();
		var erste = await host.EditorAsync();
		var zweite = await host.SignInAsync(LiedTestHost.SecondEditor, Archive.Backend.Auth.ArchiveRoles.Editor);
		await host.PutAsync(Path(), Entwurf(), erste);

		using var gelesen = await host.GetAsync(Path(), zweite);
		Assert.Equal(HttpStatusCode.NotFound, gelesen.StatusCode);
		var liste = await host.GetJsonAsync("/api/lied-entwuerfe", zweite);
		Assert.Empty(liste.GetProperty("entwuerfe").EnumerateArray());
		using var ueberschrieben = await host.PutAsync(Path(), Entwurf(title: "Fremd"), zweite);
		Assert.Equal(HttpStatusCode.NotFound, ueberschrieben.StatusCode);
		using var verworfen = await host.DeleteAsync(Path(), zweite);
		Assert.Equal(HttpStatusCode.NoContent, verworfen.StatusCode);
		// Das fremde Verwerfen hat nichts gelöscht.
		Assert.Equal("Ave Maria",
			(await host.GetJsonAsync(Path(), erste)).GetProperty("entwurf").GetProperty("lied").GetProperty("title").GetString());
	}

	[Fact]
	public async Task OnlyEditorsMayKeepDrafts()
	{
		await using var host = new LiedTestHost();
		var member = await host.MemberAsync();
		using var put = await host.PutAsync(Path(), Entwurf(), member);
		Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
		using var list = await host.GetAsync("/api/lied-entwuerfe", member);
		Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
		using var anonymous = await host.GetAsync("/api/lied-entwuerfe", null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
	}

	[Fact]
	public async Task AMissingCsrfTokenIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var request = new HttpRequestMessage(HttpMethod.Put, Path());
		request.Headers.Add("Cookie", session);
		request.Content = JsonContent.Create(Entwurf());
		using var response = await host.Client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task DiscardingDeletesTheDraftAndIsRepeatable()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);
		using var first = await host.DeleteAsync(Path(), session);
		using var second = await host.DeleteAsync(Path(), session);
		Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
		Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
		using var gone = await host.GetAsync(Path(), session);
		Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Empty(await db.LiedEntwuerfe.ToListAsync());
		Assert.Empty(await db.LiedEntwurfDateien.ToListAsync());
		Assert.Empty(await db.LiedEntwurfLesungen.ToListAsync());
	}

	[Theory]
	[InlineData("unbekannte-art")]
	[InlineData("")]
	public async Task AnUnknownFileTypeIsRefused(string art)
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await host.PutAsync(Path(), Entwurf(dateien: [Datei(DateiId, art: art)]), session);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task TooLongFieldsUnknownMarksAndDuplicateDateienAreRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var tooLong = await host.PutAsync(Path(), Entwurf(title: new string('x', 201)), session);
		using var unknownMark = await host.PutAsync(Path(), Entwurf(bearbeitet: ["herkunft"]), session);
		using var unknownSuggestion = await host.PutAsync(Path(), Entwurf(vorschlaege: new() { ["title"] = "vielleicht" }), session);
		using var duplicate = await host.PutAsync(Path(), Entwurf(dateien: [Datei(DateiId), Datei(DateiId)]), session);
		using var mismatch = await host.PutAsync(Path(Guid.NewGuid()), Entwurf(), session);
		Assert.All([tooLong, unknownMark, unknownSuggestion, duplicate, mismatch],
			r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
	}

	[Fact]
	public async Task TheReadingOfAPersistedDraftIsKeptSoItSurvivesALeftTab()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(dateien: [Datei(DateiId, lesenStatus: "liest", lesenAnfrage: 1)]), session);

		using var response = await LesenAsync(host, session, AvePdf());
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(1, answer.GetProperty("anfrageId").GetInt32());

		var datei = Assert.Single((await host.GetJsonAsync(Path(), session)).GetProperty("entwurf").GetProperty("dateien").EnumerateArray());
		var lesung = datei.GetProperty("lesung");
		Assert.Equal("gelesen", lesung.GetProperty("status").GetString());
		Assert.Equal(1, lesung.GetProperty("anfrageId").GetInt32());
		var felder = lesung.GetProperty("felder").EnumerateArray()
			.ToDictionary(f => f.GetProperty("feld").GetString()!, f => (f.GetProperty("wert").GetString(), f.GetProperty("sicher").GetBoolean()));
		Assert.Equal(("Anton Bruckner", true), felder["composer"]);
	}

	[Fact]
	public async Task AReadingThatFinishesAfterTheDraftWasDiscardedChangesNothing()
	{
		var model = new ScanModelStub();
		await using var host = new LiedTestHost(chatClient: model);
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);
		var discarded = false;
		model.BeforeFieldAnswer = async () =>
		{
			using var _ = await host.DeleteAsync(Path(), session);
			discarded = true;
		};

		using var response = await LesenAsync(host, session, AvePdf());
		Assert.True(discarded);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Empty(await db.LiedEntwuerfe.ToListAsync());
		Assert.Empty(await db.LiedEntwurfLesungen.ToListAsync());
	}

	[Fact]
	public async Task AReadingForAFileTheDraftNoLongerHasChangesNothing()
	{
		var model = new ScanModelStub();
		await using var host = new LiedTestHost(chatClient: model);
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(dateien: [Datei(DateiId), Datei(ZweiteDatei, "b.pdf")]), session);
		var removed = false;
		model.BeforeFieldAnswer = async () =>
		{
			using var _ = await host.PutAsync(Path(), Entwurf(dateien: [Datei(ZweiteDatei, "b.pdf")]), session);
			removed = true;
		};

		using var response = await LesenAsync(host, session, AvePdf());
		Assert.True(removed);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Empty(await db.LiedEntwurfLesungen.ToListAsync());
	}

	[Fact]
	public async Task ASupersededRequestIsRefusedAndKeepsTheNewerReading()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);
		using var newer = await LesenAsync(host, session, AvePdf(), anfrageId: 3);
		using var older = await LesenAsync(host, session, ScanFixtures.TextPdf("Anderes Lied"), anfrageId: 2);
		Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, older.StatusCode);

		var datei = Assert.Single((await host.GetJsonAsync(Path(), session)).GetProperty("entwurf").GetProperty("dateien").EnumerateArray());
		Assert.Equal(3, datei.GetProperty("lesung").GetProperty("anfrageId").GetInt32());
	}

	[Fact]
	public async Task ARetryWithAHigherRequestReplacesTheEarlierReading()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);
		using var first = await LesenAsync(host, session, ScanFixtures.TextPdf("Erstes Lied"), anfrageId: 1);
		using var retry = await LesenAsync(host, session, AvePdf(), anfrageId: 2);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var datei = Assert.Single((await host.GetJsonAsync(Path(), session)).GetProperty("entwurf").GetProperty("dateien").EnumerateArray());
		Assert.Equal(2, datei.GetProperty("lesung").GetProperty("anfrageId").GetInt32());
		Assert.Equal("gelesen", datei.GetProperty("lesung").GetProperty("status").GetString());
	}

	[Fact]
	public async Task AReachedBudgetIsKeptSoTheResumedFormStaysManual()
	{
		await using var host = new LiedTestHost(new Dictionary<string, string?> { ["Archive:Ai:MonthlyCapEur"] = "0.0000001" });
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);
		using var response = await LesenAsync(host, session, ScanFixtures.ScanPdf(["Seite 1"]));
		Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

		var datei = Assert.Single((await host.GetJsonAsync(Path(), session)).GetProperty("entwurf").GetProperty("dateien").EnumerateArray());
		var lesung = datei.GetProperty("lesung");
		Assert.Equal("nicht-verfuegbar", lesung.GetProperty("status").GetString());
		Assert.False(string.IsNullOrWhiteSpace(lesung.GetProperty("meldung").GetString()));
	}

	[Fact]
	public async Task AReadingThatIsStillRunningIsReportedAsRunningThenAsInterruptedWhenItIsTooOld()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
			db.LiedEntwurfLesungen.Add(new Archive.Backend.Lieder.LiedEntwurfLesung
			{
				EntwurfId = EntwurfId,
				DateiId = DateiId,
				AnfrageId = 1,
				Status = "liest",
				UpdatedAt = time.GetUtcNow(),
			});
			await db.SaveChangesAsync();
		}
		var frisch = Assert.Single((await host.GetJsonAsync(Path(), session)).GetProperty("entwurf").GetProperty("dateien").EnumerateArray());
		Assert.Equal("liest", frisch.GetProperty("lesung").GetProperty("status").GetString());

		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var lesung = await db.LiedEntwurfLesungen.SingleAsync();
			lesung.UpdatedAt = lesung.UpdatedAt.AddHours(-1);
			await db.SaveChangesAsync();
		}
		var alt = Assert.Single((await host.GetJsonAsync(Path(), session)).GetProperty("entwurf").GetProperty("dateien").EnumerateArray());
		Assert.Equal("fehlgeschlagen", alt.GetProperty("lesung").GetProperty("status").GetString());
	}

	[Fact]
	public async Task ReadingWithoutAPersistedDraftStaysStatelessAsIn177()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await LesenAsync(host, session, AvePdf());
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Empty(await db.LiedEntwurfLesungen.ToListAsync());
	}

	[Fact]
	public async Task AnotherEditorsReadingNeverTouchesMyDraft()
	{
		await using var host = new LiedTestHost();
		var meine = await host.EditorAsync();
		var fremde = await host.SignInAsync(LiedTestHost.SecondEditor, Archive.Backend.Auth.ArchiveRoles.Editor);
		await host.PutAsync(Path(), Entwurf(), meine);
		using var response = await LesenAsync(host, fremde, AvePdf());
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Empty(await db.LiedEntwurfLesungen.ToListAsync());
	}
}
