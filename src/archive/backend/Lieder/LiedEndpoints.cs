using Archive.Backend.Events;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Lieder;

/// <summary>The free-text fields of a Lied as a request carries them: null leaves a value, empty clears it.</summary>
public sealed record LiedFieldValues(
	string? Composer, string? Arranger, string? Lyricist, string? VoiceConfiguration,
	string? MusicalKey, string? Language, string? Lyrics, string? Occasion);

public sealed record CreateLiedRequest(
	string? Title, string? Composer, string? Arranger, string? Lyricist, string? VoiceConfiguration,
	string? MusicalKey, string? Language, string? Lyrics, string? Occasion)
{
	public LiedFieldValues Fields => new(Composer, Arranger, Lyricist, VoiceConfiguration, MusicalKey, Language, Lyrics, Occasion);
}

/// <summary>PATCH semantics: a missing field stays, an empty string clears it.</summary>
public sealed record PatchLiedRequest(
	string? Title, string? Composer, string? Arranger, string? Lyricist, string? VoiceConfiguration,
	string? MusicalKey, string? Language, string? Lyrics, string? Occasion, uint? RowVersion)
{
	public LiedFieldValues Fields => new(Composer, Arranger, Lyricist, VoiceConfiguration, MusicalKey, Language, Lyrics, Occasion);
}

/// <summary>
/// The flat Lied API (#166). Every active member reads every Lied: there is no
/// published state (ADR-0001). Mutations need the Editor or Administrator role,
/// CSRF validation and are attributed from the access decision.
/// </summary>
public static class LiedEndpoints
{
	public const int PageSize = 20;

	public const string NotFoundMessage = "Das Lied wurde nicht gefunden.";

	public const string TitleRequiredMessage = "Bitte einen Titel angeben.";

	public const string FieldTooLongMessage = "Ein Feld ist zu lang.";

	public const string SearchTooLongMessage = "Die Suche ist zu lang.";

	public const string UnknownDateiTypMessage = "Unbekannter Dateityp.";

	public const string ForbiddenMessage = "Keine Berechtigung für das Liedverzeichnis.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const int FieldMaxLength = 200;

	public const int LyricsMaxLength = 5000;

	/// <summary>
	/// The folded extracted text of every Lied's live Noten, joined per Lied —
	/// the shared loader of member search and chat's catalogue search (#142).
	/// </summary>
	private static Task<Dictionary<Guid, string>> LoadNotenTextsAsync(ArchiveDbContext db, CancellationToken token)
		=> CatalogueSearch.LoadNotenTextsAsync(db, token);

	public static void MapLiedEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/lieder", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			CancellationToken token, string? q, int? page, string? besetzung, string? voiceConfiguration,
			string? anlass, string? occasion, string[]? dateiTyp) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await LiedAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var query = (q ?? string.Empty).Trim();
			if (query.Length > FieldMaxLength)
				return Results.Problem(statusCode: 400, title: SearchTooLongMessage);
			var wantedKinds = (dateiTyp ?? []).Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList();
			if (wantedKinds.Any(k => !DateiKinds.IsKnown(k)))
				return Results.Problem(statusCode: 400, title: UnknownDateiTypMessage);
			var voiceFilter = CatalogueText.Fold((besetzung ?? voiceConfiguration)?.Trim());
			var occasionFilter = CatalogueText.Fold((anlass ?? occasion)?.Trim());
			var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(CatalogueText.Fold).Where(t => t.Length > 0).ToList();

			// About 500 Lieder: project once, match in C# so InMemory tests and
			// PostgreSQL agree on the German folding.
			var rows = await db.Lieder.AsNoTracking()
				.Select(l => new ListRow(l.Id, l.Title, l.Composer, l.Arranger, l.Lyricist, l.Lyrics, l.VoiceConfiguration,
					l.MusicalKey, l.Language, l.Occasion))
				.ToListAsync(token);
			var kindRows = await DateiQueries.Visible(db).AsNoTracking()
				.Where(d => d.LiedId != null)
				.Select(d => new { LiedId = d.LiedId!.Value, d.Kind })
				.Distinct()
				.ToListAsync(token);
			var kindsByLied = kindRows.GroupBy(k => k.LiedId)
				.ToDictionary(g => g.Key, g => g.Select(k => k.Kind).ToHashSet());

			// Plain search reads the text of each Lied's live Noten (#169) and,
			// since #142, matches the Liedtext and the creators too: every word
			// must stand in at least one of those fields, the Treffer names them.
			var noteTexts = tokens.Count == 0
				? []
				: await LoadNotenTextsAsync(db, token);

			var matched = rows
				.Select(r => (Row: r, Fields: CatalogueSearch.FieldsOf(r.Title, r.Composer, r.Arranger,
					r.Lyricist, r.Lyrics, noteTexts.GetValueOrDefault(r.Id) ?? string.Empty)))
				.Where(r => voiceFilter.Length == 0 || CatalogueText.Fold(r.Row.VoiceConfiguration) == voiceFilter)
				.Where(r => occasionFilter.Length == 0 || CatalogueText.Fold(r.Row.Occasion) == occasionFilter)
				.Where(r => wantedKinds.Count == 0
					|| (kindsByLied.TryGetValue(r.Row.Id, out var kinds) && wantedKinds.All(kinds.Contains)))
				.Where(r => CatalogueSearch.Matches(tokens, r.Fields))
				.OrderBy(r => CatalogueText.Fold(r.Row.Title), StringComparer.Ordinal)
				.ThenBy(r => r.Row.Id)
				.ToList();
			var requestedPage = page is null or < 1 ? 1 : page.Value;
			var pageRows = matched.Skip((requestedPage - 1) * PageSize).Take(PageSize).ToList();
			return Results.Ok(new
			{
				query = query.Length == 0 ? null : query,
				page = requestedPage,
				pageSize = PageSize,
				total = matched.Count,
				filters = new
				{
					voiceConfiguration = Clean(besetzung ?? voiceConfiguration),
					occasion = Clean(anlass ?? occasion),
					dateiTypen = wantedKinds,
				},
				options = new
				{
					voiceConfigurations = Distinct(rows.Select(r => r.VoiceConfiguration)),
					occasions = Distinct(rows.Select(r => r.Occasion)),
				},
				lieder = pageRows.Select(r => new
				{
					id = r.Row.Id,
					title = r.Row.Title,
					composer = r.Row.Composer,
					arranger = r.Row.Arranger,
					lyricist = r.Row.Lyricist,
					voiceConfiguration = r.Row.VoiceConfiguration,
					musicalKey = r.Row.MusicalKey,
					language = r.Row.Language,
					occasion = r.Row.Occasion,
					// Where the Treffer stands, in the labels the members read
					// ("im Liedtext", "in den Noten", …); empty without a query.
					matchIn = tokens.Count == 0 ? [] : CatalogueSearch.MatchLabels(tokens, r.Fields),
					dateiTypen = DateiKinds.All.Where(k => kindsByLied.TryGetValue(r.Row.Id, out var kinds) && kinds.Contains(k)),
				}),
			});
		});

		app.MapGet("/api/lieder/{id}", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var lied = await db.Lieder.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			return Results.Ok(new { lied = await DetailAsync(db, lied, LiedAccess.IsEditor(decision!), token) });
		});

		app.MapPost("/api/lieder", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, CatalogueWriteService writes, TimeProvider time, CancellationToken token, CreateLiedRequest? body) =>
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
			var (outcome, lied) = await writes.CreateLiedAsync(
				body?.Title, body?.Fields ?? new LiedFieldValues(null, null, null, null, null, null, null, null),
				new WriteActor(decision!.AccountId, time.GetUtcNow()), token);
			if (!outcome.IsSaved)
				return Results.Problem(statusCode: outcome.HttpCode, title: outcome.Title);
			return Results.Created($"/api/lieder/{lied!.Id}", new { lied = await DetailAsync(db, lied, true, token) });
		}).DisableAntiforgery();

		app.MapPatch("/api/lieder/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, CatalogueWriteService writes, TimeProvider time, Guid id, CancellationToken token, PatchLiedRequest? body) =>
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
			var lied = await db.Lieder.FirstOrDefaultAsync(l => l.Id == id, token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			var outcome = await writes.PatchLiedAsync(
				lied, body?.Title, body?.Fields ?? new LiedFieldValues(null, null, null, null, null, null, null, null),
				body?.RowVersion, new WriteActor(decision!.AccountId, time.GetUtcNow()), automated: false, token);
			if (!outcome.IsSaved)
				return Results.Problem(statusCode: outcome.HttpCode, title: outcome.Title);
			return Results.Ok(new { lied = await DetailAsync(db, lied, true, token) });
		}).DisableAntiforgery();
	}

	private static string? Clean(string? value)
	{
		var trimmed = value?.Trim();
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	private static string[] Distinct(IEnumerable<string?> values) =>
		values.Where(v => !string.IsNullOrWhiteSpace(v))
			.GroupBy(v => CatalogueText.Fold(v))
			.Select(g => g.OrderBy(v => v, StringComparer.Ordinal).First()!)
			.OrderBy(v => CatalogueText.Fold(v), StringComparer.Ordinal)
			.ToArray();

	private static async Task<object> DetailAsync(ArchiveDbContext db, Lied lied, bool isEditor, CancellationToken token)
	{
		var dateien = await DateiQueries.Visible(db).AsNoTracking()
			.Where(d => d.LiedId == lied.Id)
			.ToListAsync(token);
		// An Aufnahme names its Auftritt; the link goes to the Auftritt page.
		var eventIds = dateien.Where(d => d.RecordedAtEventId != null).Select(d => d.RecordedAtEventId!.Value).Distinct().ToList();
		var events = eventIds.Count == 0
			? []
			: (await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToListAsync(token))
				.ToDictionary(e => e.Id);
		return new
		{
			id = lied.Id,
			title = lied.Title,
			composer = lied.Composer,
			arranger = lied.Arranger,
			lyricist = lied.Lyricist,
			voiceConfiguration = lied.VoiceConfiguration,
			musicalKey = lied.MusicalKey,
			language = lied.Language,
			lyrics = lied.Lyrics,
			occasion = lied.Occasion,
			createdAt = lied.CreatedAt,
			updatedAt = lied.UpdatedAt,
			rowVersion = lied.RowVersion,
			dateien = dateien
				.OrderBy(d => DateiKinds.All.ToList().IndexOf(d.Kind))
				.ThenByDescending(d => d.IsPartitur)
				.ThenBy(d => d.Stimme, StringComparer.OrdinalIgnoreCase)
				.ThenBy(d => d.CreatedAt)
				.ThenBy(d => d.Id)
				.Select(d => DateiPayloads.Summary(d, d.RecordedAtEventId is { } id && events.TryGetValue(id, out var e)
					? new { id = e.Id, title = e.Title, dateDisplay = EventDate.Display(e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate) }
					: null)),
		};
	}

	private sealed record ListRow(Guid Id, string Title, string? Composer, string? Arranger, string? Lyricist, string? Lyrics,
		string? VoiceConfiguration, string? MusicalKey, string? Language, string? Occasion);
}

/// <summary>Wire shape of a Datei, shared by every endpoint that returns one.</summary>
public static class DateiPayloads
{
	public static object Summary(Datei d, object? recordedAtEvent = null) => new
	{
		id = d.Id,
		kind = d.Kind,
		isPartitur = d.IsPartitur,
		stimme = d.Stimme,
		description = d.Description,
		fileName = d.OriginalFileName,
		contentType = d.ContentType,
		sizeBytes = d.SizeBytes,
		createdAt = d.CreatedAt,
		rowVersion = d.RowVersion,
		recordedAtEvent,
	};
}

/// <summary>Access decisions shared by the Lied and Datei endpoints.</summary>
internal static class LiedAccess
{
	public static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		return (decision, null);
	}

	/// <summary>The 400 answer for a missing or wrong antiforgery token, otherwise null.</summary>
	public static async Task<IResult?> ValidateAntiforgeryAsync(IAntiforgery antiforgery, HttpContext context)
	{
		try
		{
			await antiforgery.ValidateRequestAsync(context);
			return null;
		}
		catch (AntiforgeryValidationException)
		{
			return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
		}
	}

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		var (decision, error) = await RequireMemberAsync(context, accessor, access);
		if (error is not null)
			return (null, error);
		if (!IsEditor(decision!))
			return (null, Results.Problem(statusCode: 403, title: LiedEndpoints.ForbiddenMessage));
		return (decision, null);
	}
}
