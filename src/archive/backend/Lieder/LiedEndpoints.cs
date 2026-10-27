using System.Text.Json;
using Archive.Backend.Events;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Videos;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Lieder;

/// <summary>
/// The flat Lied API (#166). Every active member reads every Lied: there is no
/// published state (ADR-0001). Mutations need the Editor or Administrator role,
/// CSRF validation and are attributed from the access decision. The request
/// bodies and their field vocabulary are the field catalog's (#192), so an
/// unknown field is rejected, never silently dropped.
/// </summary>
public static class LiedEndpoints
{
	public const int PageSize = 20;

	public const string NotFoundMessage = "Das Lied wurde nicht gefunden.";

	public const string TitleRequiredMessage = "Bitte einen Titel angeben.";

	public const string FieldTooLongMessage = "Ein Feld ist zu lang.";

	public const string SearchTooLongMessage = "Die Suche ist zu lang.";

	public const string UnknownDateiTypMessage = "Unbekannter Dateityp.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	/// <summary>Maximum lengths live in the field catalog.</summary>
	public const int FieldMaxLength = FieldCatalog.FieldMaxLength;

	public const int LyricsMaxLength = FieldCatalog.LyricsMaxLength;

	public static void MapLiedEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/lieder", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			CancellationToken token, string? q, int? page, string? besetzung, string? voiceConfiguration,
			string? anlass, string? occasion, string[]? dateiTyp) =>
		{
			var (_, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var query = (q ?? string.Empty).Trim();
			if (query.Length > FieldCatalog.FieldMaxLength)
				return Results.Problem(statusCode: 400, title: SearchTooLongMessage);
			var wantedKinds = (dateiTyp ?? []).Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList();
			if (wantedKinds.Any(k => !DateiKinds.IsKnown(k)))
				return Results.Problem(statusCode: 400, title: UnknownDateiTypMessage);
			// The filter fields are named in the request as besetzung/anlass
			// or voiceConfiguration/occasion and act as plain equality filters.
			var voiceField = FieldCatalog.LiedField("voice_configuration");
			var occasionField = FieldCatalog.LiedField("occasion");
			var voiceFilter = CatalogueText.Fold((besetzung ?? voiceConfiguration)?.Trim());
			var occasionFilter = CatalogueText.Fold((anlass ?? occasion)?.Trim());
			var tokens = LiedSearch.TokensOf(query);

			// About 500 Lieder: materialize once, then filter, match and rank
			// in memory so InMemory tests and PostgreSQL agree on the German
			// folding.
			var rows = await LiedQueries.Visible(db).AsNoTracking().ToListAsync(token);
			var kindRows = await DateiQueries.Visible(db).AsNoTracking()
				.Where(d => d.LiedId != null)
				.Select(d => new { LiedId = d.LiedId!.Value, d.Kind })
				.Distinct()
				.ToListAsync(token);
			var kindsByLied = kindRows.GroupBy(k => k.LiedId)
				.ToDictionary(g => g.Key, g => g.Select(k => k.Kind).ToHashSet());

			// Plain search reads the text of each Lied's live Noten (#169) and,
			// since #142, matches the Liedtext and the creators too.
			var noteTexts = tokens.Count == 0
				? []
				: await LiedSearch.LoadNotenTextsAsync(db, token);

			var filtered = rows
				.Where(r => voiceFilter.Length == 0 || CatalogueText.Fold(voiceField.Get(r)) == voiceFilter)
				.Where(r => occasionFilter.Length == 0 || CatalogueText.Fold(occasionField.Get(r)) == occasionFilter)
				.Where(r => wantedKinds.Count == 0
					|| (kindsByLied.TryGetValue(r.Id, out var kinds) && wantedKinds.All(kinds.Contains)));
			var matches = LiedSearch.Match(tokens, filtered, noteTexts, query);
			var requestedPage = page is null or < 1 ? 1 : page.Value;
			var pageRows = LiedSearch.Page(matches, requestedPage, PageSize);
			return Results.Ok(new
			{
				query = query.Length == 0 ? null : query,
				page = requestedPage,
				pageSize = PageSize,
				total = matches.Count,
				filters = new
				{
					voiceConfiguration = Clean(besetzung ?? voiceConfiguration),
					occasion = Clean(anlass ?? occasion),
					dateiTypen = wantedKinds,
				},
				options = new
				{
					voiceConfigurations = Distinct(rows.Select(r => voiceField.Get(r))),
					occasions = Distinct(rows.Select(r => occasionField.Get(r))),
				},
				lieder = pageRows.Select(m => Row(m, tokens, kindsByLied)),
			});
		});

		app.MapGet("/api/lieder/{id}", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			Guid id, CancellationToken token) =>
		{
			var (decision, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var lied = await LiedQueries.Visible(db).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			return Results.Ok(new { lied = await DetailAsync(db, lied, ArchiveAccess.IsEditor(decision!), token) });
		});

		app.MapPost("/api/lieder", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, CatalogueWriteService writes, TimeProvider time, CancellationToken token, CreateLiedRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			// A field the browser sent that the catalog does not know is
			// rejected, never silently dropped (#192).
			if (body is { Unknown.Count: > 0 })
				return Results.Problem(statusCode: 400, title: CatalogueWriteService.UnknownFieldMessage);
			var (outcome, lied) = await writes.CreateLiedAsync(
				body?.Title, body ?? LiedFieldValues.Empty,
				new WriteActor(decision!.AccountId, time.GetUtcNow()), token);
			if (!outcome.IsSaved)
				return Results.Problem(statusCode: outcome.HttpCode, title: outcome.Title);
			return Results.Created($"/api/lieder/{lied!.Id}", new { lied = await DetailAsync(db, lied, true, token) });
		}).DisableAntiforgery();

		app.MapPatch("/api/lieder/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, CatalogueWriteService writes, TimeProvider time, Guid id, CancellationToken token, PatchLiedRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var lied = await LiedQueries.Visible(db).FirstOrDefaultAsync(l => l.Id == id, token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body is { Unknown.Count: > 0 })
				return Results.Problem(statusCode: 400, title: CatalogueWriteService.UnknownFieldMessage);
			var outcome = await writes.PatchLiedAsync(
				lied, body?.Title, body ?? LiedFieldValues.Empty,
				body?.RowVersion, new WriteActor(decision!.AccountId, time.GetUtcNow()), automated: false, token);
			if (!outcome.IsSaved)
				return Results.Problem(statusCode: outcome.HttpCode, title: outcome.Title);
			return Results.Ok(new { lied = await DetailAsync(db, lied, true, token) });
		}).DisableAntiforgery();
	}

	/// <summary>
	/// One list row: the plain fields the catalog knows in the order the
	/// members read them, without the Liedtext (that is the detail's); where
	/// the Treffer stands, in the field labels of the search ("im Liedtext",
	/// "in den Noten", …), empty without a query.
	/// </summary>
	private static object Row(SearchMatch match, IReadOnlyList<string> tokens,
		IReadOnlyDictionary<Guid, HashSet<string>> kindsByLied)
	{
		var lied = match.Lied;
		var row = new Dictionary<string, object?>
		{
			["id"] = lied.Id,
			["title"] = lied.Title,
		};
		foreach (var field in FieldCatalog.LiedFields.Where(f => f.MaxLength <= FieldCatalog.FieldMaxLength))
			row[field.Form] = field.Get(lied);
		row["matchIn"] = tokens.Count == 0
			? Array.Empty<string>()
			: LiedSearch.Labels(tokens, match.Fields);
		row["dateiTypen"] = DateiKinds.All
			.Where(k => kindsByLied.TryGetValue(lied.Id, out var kinds) && kinds.Contains(k));
		return row;
	}

	/// <summary>One detail: the title, every catalogued field, the bookkeeping and the visible Dateien.</summary>
	private static async Task<object> DetailAsync(ArchiveDbContext db, Lied lied, bool isEditor, CancellationToken token)
	{
		var dateien = await DateiQueries.Visible(db).AsNoTracking()
			.Where(d => d.LiedId == lied.Id)
			.ToListAsync(token);
		// #161: automatic playback copies never show as entries of their own.
		dateien = await VideoPlayback.WithoutCopiesAsync(db, dateien, token);
		// An Aufnahme names its Auftritt; the link goes to the Auftritt page.
		var eventIds = dateien.Where(d => d.RecordedAtEventId != null).Select(d => d.RecordedAtEventId!.Value).Distinct().ToList();
		var events = eventIds.Count == 0
			? []
			: (await EventQueries.Visible(db).AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToListAsync(token))
				.ToDictionary(e => e.Id);
		var detail = new Dictionary<string, object?>
		{
			["id"] = lied.Id,
			["title"] = lied.Title,
		};
		foreach (var field in FieldCatalog.LiedFields)
			detail[field.Form] = field.Get(lied);
		detail["createdAt"] = lied.CreatedAt;
		detail["updatedAt"] = lied.UpdatedAt;
		detail["rowVersion"] = lied.RowVersion;
		detail["dateien"] = dateien
			.OrderBy(d => DateiKinds.All.ToList().IndexOf(d.Kind))
			.ThenByDescending(d => d.IsPartitur)
			.ThenBy(d => d.Stimme, StringComparer.OrdinalIgnoreCase)
			.ThenBy(d => d.CreatedAt)
			.ThenBy(d => d.Id)
			.Select(d => DateiPayloads.Summary(d, d.RecordedAtEventId is { } id && events.TryGetValue(id, out var e)
				? new { id = e.Id, title = e.Title, dateDisplay = EventDate.Display(e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate) }
				: null));
		return detail;
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
