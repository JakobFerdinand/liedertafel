using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #131: the persisted Auftritt draft behind "Angefangen". It keeps the form
/// the editor was filling (title, date, place, the Programm with its Lied
/// choices, the Dateien without their bytes) as the browser sent it, owned by
/// the editor who started it. It is not an Auftritt: nothing is visible to
/// members and no Lied or Programm exists until "Speichern".
/// </summary>
public sealed class AuftrittEntwurfApiTests
{
	private static readonly Guid EntwurfId = Guid.Parse("55555555-5555-5555-5555-555555555555");

	private static object Entwurf(Guid? id = null, string titel = "Sängerfest", int dateien = 1, object? formular = null)
		=> new
		{
			entwurfId = id ?? EntwurfId,
			titel,
			dateien,
			formular = formular ?? new { version = 1, werte = new { title = titel }, programm = new[] { new { text = "Ave Maria" } } },
		};

	private static string Path(Guid? id = null) => $"/api/auftritt-entwuerfe/{id ?? EntwurfId}";

	[Fact]
	public async Task ADraftIsSavedAndComesBackAsTheBrowserSentIt()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();

		using var saved = await host.PutAsync(Path(), Entwurf(), session);
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

		var body = (await host.GetJsonAsync(Path(), session)).GetProperty("entwurf");
		Assert.Equal(EntwurfId, body.GetProperty("entwurfId").GetGuid());
		Assert.Equal("Sängerfest", body.GetProperty("titel").GetString());
		Assert.Equal(1, body.GetProperty("dateien").GetInt32());
		var formular = body.GetProperty("formular");
		Assert.Equal(1, formular.GetProperty("version").GetInt32());
		Assert.Equal("Ave Maria", formular.GetProperty("programm")[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task SavingAgainReplacesTheWholeForm()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(titel: "Alt"), session);
		await host.PutAsync(Path(), Entwurf(titel: "Neu", dateien: 2), session);

		var body = (await host.GetJsonAsync(Path(), session)).GetProperty("entwurf");
		Assert.Equal("Neu", body.GetProperty("titel").GetString());
		Assert.Equal(2, body.GetProperty("dateien").GetInt32());
		Assert.Equal("Neu", body.GetProperty("formular").GetProperty("werte").GetProperty("title").GetString());
	}

	[Fact]
	public async Task TheListShowsTheOwnDraftsNewestFirst()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		var zweite = Guid.Parse("66666666-6666-6666-6666-666666666666");
		await host.PutAsync(Path(), Entwurf(titel: "Erster"), session);
		await host.PutAsync(Path(zweite), Entwurf(zweite, "Zweiter", dateien: 2), session);

		var liste = (await host.GetJsonAsync("/api/auftritt-entwuerfe", session)).GetProperty("entwuerfe").EnumerateArray().ToList();
		Assert.Equal(["Zweiter", "Erster"], liste.Select(e => e.GetProperty("titel").GetString()));
		Assert.Equal(2, liste[0].GetProperty("dateien").GetInt32());
		Assert.Equal(zweite, liste[0].GetProperty("entwurfId").GetGuid());
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
		Assert.Empty((await host.GetJsonAsync("/api/auftritt-entwuerfe", zweite)).GetProperty("entwuerfe").EnumerateArray());
		using var ueberschrieben = await host.PutAsync(Path(), Entwurf(titel: "Fremd"), zweite);
		Assert.Equal(HttpStatusCode.NotFound, ueberschrieben.StatusCode);
		using var verworfen = await host.DeleteAsync(Path(), zweite);
		Assert.Equal(HttpStatusCode.NoContent, verworfen.StatusCode);
		Assert.Equal("Sängerfest", (await host.GetJsonAsync(Path(), erste)).GetProperty("entwurf").GetProperty("titel").GetString());
	}

	[Fact]
	public async Task OnlyEditorsMayKeepDrafts()
	{
		await using var host = new LiedTestHost();
		var member = await host.MemberAsync();
		using var put = await host.PutAsync(Path(), Entwurf(), member);
		Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
		using var list = await host.GetAsync("/api/auftritt-entwuerfe", member);
		Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
		using var anonymous = await host.GetAsync("/api/auftritt-entwuerfe", null);
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
		Assert.Empty(await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().AuftrittEntwuerfe.ToListAsync());
	}

	[Fact]
	public async Task ADraftIsNotAnAuftritt()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		await host.PutAsync(Path(), Entwurf(), session);

		var events = await host.GetJsonAsync("/api/events", session);
		Assert.Equal(0, events.GetProperty("total").GetInt32());
		using var scope = host.Services.CreateScope();
		Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Programmes.CountAsync());
	}

	[Fact]
	public async Task AnIdThatDoesNotMatchThePathIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await host.PutAsync(Path(), Entwurf(Guid.NewGuid()), session);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Theory]
	[InlineData("""{"entwurfId":"55555555-5555-5555-5555-555555555555","titel":"A","dateien":1,"formular":"kein objekt"}""")]
	[InlineData("""{"entwurfId":"55555555-5555-5555-5555-555555555555","titel":"A","dateien":1}""")]
	[InlineData("""{"entwurfId":"55555555-5555-5555-5555-555555555555","titel":"A","dateien":-1,"formular":{"version":1}}""")]
	[InlineData("""{"entwurfId":"55555555-5555-5555-5555-555555555555","titel":"A","dateien":1,"formular":{"version":2}}""")]
	public async Task AFormThatIsNotTheKnownShapeIsRefused(string json)
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await host.PutAsync(Path(), JsonDocument.Parse(json).RootElement, session);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task AFormThatIsFarTooLargeIsRefused()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		using var response = await host.PutAsync(Path(),
			Entwurf(formular: new { version = 1, text = new string('x', 200_000) }), session);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task TooManyDraftsAnswer409()
	{
		await using var host = new LiedTestHost();
		var session = await host.EditorAsync();
		for (var i = 0; i < Archive.Backend.Events.AuftrittEntwurfRules.MaxEntwuerfeProKonto; i++)
		{
			var id = Guid.NewGuid();
			using var ok = await host.PutAsync(Path(id), Entwurf(id, $"Auftritt {i}"), session);
			Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
		}
		using var one = await host.PutAsync(Path(), Entwurf(), session);
		Assert.Equal(HttpStatusCode.Conflict, one.StatusCode);
	}
}
