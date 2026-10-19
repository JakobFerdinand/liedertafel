using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Lieder;

public sealed record EntwurfLiedBody(
	string? Title, string? Composer, string? Arranger, string? Lyricist, string? VoiceConfiguration,
	string? MusicalKey, string? Language, string? Lyrics);

public sealed record EntwurfLesenBody(string? Status, int? AnfrageId, string? Meldung);

public sealed record EntwurfDateiBody(
	Guid? Id, string? Name, long? SizeBytes, string? Art, string? Stimme, bool? ArtBearbeitet, bool? StimmeBearbeitet,
	EntwurfLesenBody? Lesen);

public sealed record SaveEntwurfRequest(
	Guid? EntwurfId, EntwurfLiedBody? Lied, string[]? Bearbeitet, Dictionary<string, string>? Vorschlaege,
	EntwurfDateiBody[]? Dateien);

/// <summary>One reading suggestion as stored and as sent: the #177 answer shape.</summary>
public sealed record EntwurfVorschlag(string Feld, string Wert, bool Sicher);

/// <summary>
/// "Angefangen" (#178): the persisted Lied draft. Editor-only, owned by the
/// editor who started it, CSRF-protected on every change. The browser sends its
/// whole form (<c>PUT</c>, last write wins) and the server answers with it plus
/// the readings it holds, so resume is restored from backend state. See
/// <see cref="LiedEntwurf"/> for what is kept.
/// </summary>
public static class LiedEntwurfEndpoints
{
	public const string NotFoundMessage = "Der angefangene Eintrag wurde nicht gefunden.";

	public const string InvalidMessage = "Der Entwurf ist ungültig.";

	public const string TooManyMessage = "Es sind zu viele Einträge angefangen. Bitte erst welche verwerfen.";

	public const string InterruptedMessage = "Das Lesen wurde unterbrochen. Bitte erneut lesen.";

	/// <summary>A reading that has not answered after this long is reported as interrupted.</summary>
	public static readonly TimeSpan ReadingStaleAfter = TimeSpan.FromMinutes(5);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static string SerializeFelder(IEnumerable<EntwurfVorschlag> felder) => JsonSerializer.Serialize(felder, Json);

	private static List<EntwurfVorschlag> ParseFelder(string json)
		=> JsonSerializer.Deserialize<List<EntwurfVorschlag>>(json, Json) ?? [];

	public static void MapLiedEntwurfEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/lied-entwuerfe", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var rows = await db.LiedEntwuerfe.AsNoTracking()
				.Where(e => e.OwnerAccountId == decision!.AccountId)
				.Select(e => new { e.Id, e.Title, Dateien = e.Dateien.Count, e.UpdatedAt })
				.ToListAsync(token);
			return Results.Ok(new
			{
				entwuerfe = rows.OrderByDescending(r => r.UpdatedAt)
					.Select(r => new { entwurfId = r.Id, titel = r.Title, dateien = r.Dateien, aktualisiertAm = r.UpdatedAt }),
			});
		});

		app.MapGet("/api/lied-entwuerfe/{id:guid}", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			TimeProvider time, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var entwurf = await db.LiedEntwuerfe.AsNoTracking()
				.Include(e => e.Dateien).ThenInclude(d => d.Lesung)
				.FirstOrDefaultAsync(e => e.Id == id && e.OwnerAccountId == decision!.AccountId, token);
			if (entwurf is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			return Results.Ok(new { entwurf = Payload(entwurf, time.GetUtcNow()) });
		});

		app.MapPut("/api/lied-entwuerfe/{id:guid}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token, SaveEntwurfRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			if (Validate(id, body) is { } invalid)
				return Results.Problem(statusCode: 400, title: invalid);

			var entwurf = await db.LiedEntwuerfe
				.Include(e => e.Dateien).ThenInclude(d => d.Lesung)
				.FirstOrDefaultAsync(e => e.Id == id, token);
			var now = time.GetUtcNow();
			if (entwurf is null)
			{
				var owned = await db.LiedEntwuerfe.CountAsync(e => e.OwnerAccountId == decision!.AccountId, token);
				if (owned >= LiedEntwurfRules.MaxEntwuerfeProKonto)
					return Results.Problem(statusCode: 409, title: TooManyMessage);
				entwurf = new LiedEntwurf { Id = id, OwnerAccountId = decision!.AccountId, CreatedAt = now };
				db.LiedEntwuerfe.Add(entwurf);
			}
			else if (entwurf.OwnerAccountId != decision!.AccountId)
			{
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			}

			Apply(entwurf, body!, now);
			await db.SaveChangesAsync(token);
			return Results.Ok(new { aktualisiertAm = entwurf.UpdatedAt });
		}).DisableAntiforgery();

		app.MapDelete("/api/lied-entwuerfe/{id:guid}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var entwurf = await db.LiedEntwuerfe
				.Include(e => e.Dateien).ThenInclude(d => d.Lesung)
				.FirstOrDefaultAsync(e => e.Id == id && e.OwnerAccountId == decision!.AccountId, token);
			if (entwurf is not null)
			{
				db.LiedEntwuerfe.Remove(entwurf);
				await db.SaveChangesAsync(token);
			}
			// Discarding twice, or discarding what is gone, is still discarded.
			return Results.NoContent();
		}).DisableAntiforgery();
	}

	private static string? Validate(Guid id, SaveEntwurfRequest? body)
	{
		if (body is null || body.EntwurfId != id || body.Lied is null)
			return InvalidMessage;
		var lied = body.Lied;
		if (new[] { lied.Title, lied.Composer, lied.Arranger, lied.Lyricist, lied.VoiceConfiguration, lied.MusicalKey, lied.Language }
			.Any(v => v is { Length: > LiedEndpoints.FieldMaxLength })
			|| lied.Lyrics is { Length: > LiedEndpoints.LyricsMaxLength })
			return LiedEndpoints.FieldTooLongMessage;
		if ((body.Bearbeitet ?? []).Any(f => !LiedEntwurfRules.Felder.Contains(f)))
			return InvalidMessage;
		if ((body.Vorschlaege ?? []).Any(v => !LiedEntwurfRules.Felder.Contains(v.Key) || v.Value is not ("sicher" or "unsicher")))
			return InvalidMessage;
		var dateien = body.Dateien ?? [];
		if (dateien.Length > LiedEntwurfRules.MaxDateien || dateien.Any(d => d is null || d.Id is null)
			|| dateien.Select(d => d.Id).Distinct().Count() != dateien.Length)
			return InvalidMessage;
		foreach (var datei in dateien)
		{
			if (string.IsNullOrWhiteSpace(datei.Name) || datei.Name.Length > 300 || datei.SizeBytes is null or < 0)
				return InvalidMessage;
			if (datei.Art is null || !LiedEntwurfRules.Arten.Contains(datei.Art))
				return InvalidMessage;
			if (datei.Stimme is { Length: > LiedEndpoints.FieldMaxLength })
				return LiedEndpoints.FieldTooLongMessage;
			if (datei.Lesen is { } lesen
				&& (lesen.Status is null || !LiedEntwurfRules.LesenStatus.Contains(lesen.Status)
					|| lesen.AnfrageId is null or < 0 || lesen.Meldung is { Length: > 500 }))
				return InvalidMessage;
		}
		return null;
	}

	private static void Apply(LiedEntwurf entwurf, SaveEntwurfRequest body, DateTimeOffset now)
	{
		var lied = body.Lied!;
		entwurf.Title = lied.Title ?? string.Empty;
		entwurf.Composer = lied.Composer ?? string.Empty;
		entwurf.Arranger = lied.Arranger ?? string.Empty;
		entwurf.Lyricist = lied.Lyricist ?? string.Empty;
		entwurf.VoiceConfiguration = lied.VoiceConfiguration ?? string.Empty;
		entwurf.MusicalKey = lied.MusicalKey ?? string.Empty;
		entwurf.Language = lied.Language ?? string.Empty;
		entwurf.Lyrics = lied.Lyrics ?? string.Empty;
		entwurf.EditedFields = string.Join(',', LiedEntwurfRules.Felder.Where((body.Bearbeitet ?? []).Contains));
		entwurf.Suggestions = string.Join(',', LiedEntwurfRules.Felder
			.Where((body.Vorschlaege ?? []).ContainsKey)
			.Select(f => $"{f}:{body.Vorschlaege![f]}"));
		entwurf.UpdatedAt = now;

		var wanted = body.Dateien ?? [];
		foreach (var gone in entwurf.Dateien.Where(d => wanted.All(w => w.Id != d.Id)).ToList())
			entwurf.Dateien.Remove(gone);
		for (var position = 0; position < wanted.Length; position++)
		{
			var wish = wanted[position];
			var datei = entwurf.Dateien.FirstOrDefault(d => d.Id == wish.Id);
			if (datei is null)
			{
				datei = new LiedEntwurfDatei { Id = wish.Id!.Value, EntwurfId = entwurf.Id };
				entwurf.Dateien.Add(datei);
			}
			datei.Position = position;
			datei.Name = wish.Name!.Trim();
			datei.SizeBytes = wish.SizeBytes!.Value;
			datei.Art = wish.Art!;
			datei.Stimme = wish.Stimme ?? string.Empty;
			datei.ArtEdited = wish.ArtBearbeitet == true;
			datei.StimmeEdited = wish.StimmeBearbeitet == true;
			datei.LesenStatus = wish.Lesen?.Status ?? "keine";
			datei.LesenAnfrageId = wish.Lesen?.AnfrageId ?? 0;
			datei.LesenMeldung = wish.Lesen?.Meldung ?? string.Empty;
		}
	}

	private static object Payload(LiedEntwurf e, DateTimeOffset now) => new
	{
		entwurfId = e.Id,
		lied = new
		{
			title = e.Title,
			composer = e.Composer,
			arranger = e.Arranger,
			lyricist = e.Lyricist,
			voiceConfiguration = e.VoiceConfiguration,
			musicalKey = e.MusicalKey,
			language = e.Language,
			lyrics = e.Lyrics,
		},
		bearbeitet = e.EditedFields.Split(',', StringSplitOptions.RemoveEmptyEntries),
		vorschlaege = e.Suggestions.Split(',', StringSplitOptions.RemoveEmptyEntries)
			.Select(s => s.Split(':'))
			.ToDictionary(p => p[0], p => p[1]),
		dateien = e.Dateien.OrderBy(d => d.Position).Select(d => new
		{
			id = d.Id,
			name = d.Name,
			sizeBytes = d.SizeBytes,
			art = d.Art,
			stimme = d.Stimme,
			artBearbeitet = d.ArtEdited,
			stimmeBearbeitet = d.StimmeEdited,
			lesen = new { status = d.LesenStatus, anfrageId = d.LesenAnfrageId, meldung = d.LesenMeldung },
			lesung = d.Lesung is { } l ? LesungPayload(l, now) : null,
		}),
		aktualisiertAm = e.UpdatedAt,
	};

	private static object LesungPayload(LiedEntwurfLesung lesung, DateTimeOffset now)
	{
		if (lesung.Status == "liest" && now - lesung.UpdatedAt > ReadingStaleAfter)
			return new { anfrageId = lesung.AnfrageId, status = "fehlgeschlagen", meldung = InterruptedMessage, felder = Array.Empty<EntwurfVorschlag>() };
		return new { anfrageId = lesung.AnfrageId, status = lesung.Status, meldung = lesung.Meldung, felder = ParseFelder(lesung.Felder) };
	}
}
