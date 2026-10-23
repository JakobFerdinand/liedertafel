using System.ComponentModel;
using System.Text.Json;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

/// <summary>
/// The chat tool allow-list: authorized retrieval functions only, built per
/// request so the delegates capture the scoped <see cref="ArchiveDbContext"/>.
/// Authorization is enforced INSIDE every tool: every saved Lied is visible to
/// a member (ADR-0001), but a Lied's history only names the Auftritte it was sung at and
/// planned appearances. No editing tools, no web
/// access.
/// </summary>
public static class CatalogueTools
{
	public const string SearchToolName = "catalogue_search";

	public const string DetailsToolName = "lied_details";

	private const int MaxQueryChars = 200;

	private const int MaxPage = 5;

	private const int PageSize = 10;

	private const int ExcerptChars = 300;

	/// <summary>Auftritte listed per Lied in the history part of the details.</summary>
	private const int MaxHistoryEntries = 20;

	private const string SearchToolDescription =
		"Sucht im Liedverzeichnis nach Titeln, Komponisten, Bearbeitern, Textdichtern, Liedtexten "
		+ "und Wörtern in den Noten (aus PDFs gelesen); leerer Suchtext listet vorhandene Lieder. "
		+ "trefferIn nennt die Fundstellen jedes Liedes. Liefert höchstens 10 Lieder pro Seite, "
		+ "totalCount, nextPage und limitReached; höchstens 5 Seiten. Nur zurückgegebene Lieder zitieren.";

	/// <summary>Creates the bounded catalogue search tool for one chat run.</summary>
	public static AITool CreateCatalogueSearchTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("Suchtext; leer (\"\") für eine Übersicht aller Lieder. Sonst müssen alle Begriffe im Titel, beim Komponisten, Bearbeiter, Textdichter, im Liedtext oder in den Noten vorkommen.")] string query,
				[Description("Seitennummer ab 1, höchstens 5; für weitere Treffer nextPage aus dem Ergebnis verwenden.")] int page = 1,
				CancellationToken cancellationToken = default) => await SearchAsync(db, query, page, cancellationToken),
			name: SearchToolName,
			description: SearchToolDescription);

	/// <summary>Creates the single-Lied lookup tool for one chat run.</summary>
	public static AITool CreateLiedDetailsTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("ID des gesuchten Liedes aus dem Liedverzeichnis.")] string liedId) => await DetailsAsync(db, liedId),
			name: DetailsToolName,
			description: "Liest ein einzelnes Lied aus dem Verzeichnis mit seinen Angaben (Besetzung, Tonart, Sprache, Anlass), "
				+ "den vorhandenen Dateien und seiner Aufführungsgeschichte: gesungen nennt die vergangenen Auftritte, in deren "
				+ "Programm das Lied steht oder zu denen eine Aufnahme des Liedes gehört; geplant nennt kommende Auftritte, "
				+ "in deren Programm das Lied steht.");

	private static async Task<string> SearchAsync(ArchiveDbContext db, string query, int page, CancellationToken token)
	{
		// Same folding and tokenized AND semantics as the member Lied search;
		// bounds are enforced inside the tool so the model cannot push past them.
		var queryText = (query ?? string.Empty).Trim();
		if (queryText.Length > MaxQueryChars)
			queryText = queryText[..MaxQueryChars];
		var requestedPage = Math.Max(1, page);
		if (requestedPage > MaxPage)
			return JsonSerializer.Serialize(new { lieder = Array.Empty<object>(), error = "Höchstens 5 Seiten abrufbar. Bitte die Suche eingrenzen.", limitReached = true });
		var tokens = queryText
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(CatalogueText.Fold)
			.Where(t => t.Length > 0)
			.ToList();
		if (tokens.Count == 0)
		{
			// Browsing is a bounded database page, not an empty search result.
			var total = await db.Lieder.AsNoTracking().CountAsync(token);
			var lieder = await db.Lieder.AsNoTracking().OrderBy(l => l.Title).ThenBy(l => l.Id)
				.Skip((requestedPage - 1) * PageSize).Take(PageSize)
				.Select(l => new SearchRow(l.Id, l.Title, l.Composer, l.Arranger, l.Lyricist, l.Lyrics))
				.ToListAsync(token);
			return SerializeSearch(lieder, requestedPage, total);
		}

		var all = await db.Lieder.AsNoTracking()
			.OrderBy(l => l.Id)
			.Select(l => new SearchRow(l.Id, l.Title, l.Composer, l.Arranger, l.Lyricist, l.Lyrics))
			.ToListAsync(token);
		// Same Noten text as the member search (#142): words that only stand in
		// the extracted text of a Lied's live Noten find it here too.
		var noteTexts = await CatalogueSearch.LoadNotenTextsAsync(db, token);
		var queryFold = CatalogueText.Fold(queryText);
		var matches = new List<(SearchRow Lied, int Rank, IReadOnlyList<SearchField> Fields)>();
		foreach (var lied in all)
		{
			var notes = noteTexts.GetValueOrDefault(lied.Id) ?? string.Empty;
			var fields = CatalogueSearch.FieldsOf(lied.Title, lied.Composer, lied.Arranger, lied.Lyricist, lied.Lyrics, notes);
			var titleFold = CatalogueText.Fold(lied.Title);
			if (!CatalogueSearch.Matches(tokens, fields))
				continue;
			var rank = titleFold == queryFold ? 0
				: tokens.All(t => titleFold.Contains(t)) ? 1
				: 2;
			matches.Add((lied, rank, fields));
		}
		var trefferIn = matches
			.Where(m => m.Rank < 3)
			.ToDictionary(m => m.Lied.Id, m => CatalogueSearch.MatchLabels(tokens, m.Fields).ToArray());
		var ordered = matches
			.OrderBy(m => m.Rank)
			.ThenBy(m => m.Lied.Id)
			.Skip((requestedPage - 1) * PageSize)
			.Take(PageSize)
			.ToList();
		return SerializeSearch(ordered.Select(m => m.Lied), requestedPage, matches.Count, trefferIn);
	}

	/// <summary>Noten text sent per Datei and per Lied (characters).</summary>
	private const int ScoreTextChars = 2000;

	private const int ScoreTextBudget = 6000;

	private static async Task<string> DetailsAsync(ArchiveDbContext db, string liedId)
	{
		if (!Guid.TryParse(liedId, out var id))
			return "{}";
		// Unknown ids return the same empty payload: the chat surface never
		// distinguishes a missing Lied from a malformed id.
		var lied = await db.Lieder.AsNoTracking()
			.Where(l => l.Id == id)
			.FirstOrDefaultAsync();
		if (lied is null)
			return "{}";
		var rows = await (
			from d in DateiQueries.Visible(db).AsNoTracking()
			where d.LiedId == id
			join e in db.ExtractionJobs.AsNoTracking() on d.Id equals e.DateiId into texts
			from e in texts.DefaultIfEmpty()
			orderby d.Kind, d.Id
			select new { d.Kind, d.IsPartitur, d.Stimme, d.Description, Status = e == null ? (ExtractionStatus?)null : e.Status, Text = e == null ? null : e.Text })
			.ToListAsync();
		// Noten text read from the Dateien (#169): bounded per Datei and per
		// Lied, and identical text (the same lyrics in every Stimme) goes out once.
		var budget = ScoreTextBudget;
		var sent = new HashSet<string>();
		var dateien = rows.Select(d =>
		{
			var analysis = d.Kind == DateiKinds.Noten && d.Status == ExtractionStatus.Completed
				? ScoreTextAnalyzer.Analyze(d.Text)
				: null;
			string? text = null;
			if (analysis is { CleanText.Length: > 0 } && budget > 0 && sent.Add(analysis.CleanText))
			{
				var length = Math.Min(Math.Min(analysis.CleanText.Length, ScoreTextChars), budget);
				text = analysis.CleanText[..length];
				budget -= length;
			}
			return new { d.Kind, d.IsPartitur, d.Stimme, d.Description, Facts = ExtractionEndpoints.FactsPayload(analysis?.Facts), Text = text };
		}).ToList();
		var (gesungen, geplant) = await HistoryAsync(db, id);
		return JsonSerializer.Serialize(new
		{
			lieder = LiedPayload([new SearchRow(lied.Id, lied.Title, lied.Composer, lied.Arranger, lied.Lyricist, lied.Lyrics)]),
			besetzung = lied.VoiceConfiguration,
			tonart = lied.MusicalKey,
			sprache = lied.Language,
			anlass = lied.Occasion,
			dateien = dateien.Select(d => new
			{
				art = d.Kind,
				partitur = d.IsPartitur,
				stimme = d.Stimme,
				beschreibung = d.Description,
				notenFakten = d.Facts,
				notenText = d.Text,
			}),
			gesungen,
			geplant,
		});
	}

	/// <summary>
	/// Where the Lied was sung or is planned, from member-visible Auftritte
	/// only (published events), newest first and bounded. The Programm is the
	/// record of what was sung; a Lied-Aufnahme naming an Auftritt also counts
	/// as sung there. An Auftritt with an unknown date counts as past.
	/// </summary>
	private static async Task<(List<object> Gesungen, List<object> Geplant)> HistoryAsync(ArchiveDbContext db, Guid liedId)
	{
		var events = await LiedAppearances.LoadAsync(db, liedId, CancellationToken.None);
		events.Sort((a, b) => EventDate.CompareNewestFirst(a.DateYear, a.DateMonth, a.DateDay, b.DateYear, b.DateMonth, b.DateDay));
		var today = EventDate.ViennaToday(DateTimeOffset.UtcNow);
		static object Entry(LiedAppearance e) => new
		{
			auftritt = e.Title,
			art = e.Kind,
			ort = e.Venue,
			datum = EventDate.Display(e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate),
		};
		var gesungen = events.Where(e => !LiedAppearances.IsPlanned(e, today)).Take(MaxHistoryEntries).Select(Entry).ToList();
		var geplant = events.Where(e => LiedAppearances.IsPlanned(e, today)).Take(MaxHistoryEntries).Select(Entry).ToList();
		return (gesungen, geplant);
	}

	private static string SerializeSearch(IEnumerable<SearchRow> lieder, int page, int totalCount,
		IReadOnlyDictionary<Guid, string[]>? trefferIn = null) => JsonSerializer.Serialize(new
	{
		lieder = LiedPayload(lieder, trefferIn),
		page,
		pageSize = PageSize,
		totalCount,
		hasMore = page * PageSize < totalCount,
		nextPage = page < MaxPage && page * PageSize < totalCount ? (int?)(page + 1) : null,
		limitReached = page == MaxPage && page * PageSize < totalCount,
	});

	private static IEnumerable<object> LiedPayload(IEnumerable<SearchRow> lieder, IReadOnlyDictionary<Guid, string[]>? trefferIn = null) => lieder.Select(l => new
		{
			id = l.Id,
			title = l.Title,
			composer = l.Composer,
			arranger = l.Arranger,
			lyricist = l.Lyricist,
			lyricsExcerpt = l.Lyrics is { Length: > 0 }
				? (l.Lyrics.Length > ExcerptChars ? l.Lyrics[..ExcerptChars] : l.Lyrics)
				: null,
			// Wo der Treffer stand ("im Liedtext", "in den Noten", …); beim
			// Durchblättern ohne Suchbegriff bleibt es leer.
			trefferIn = trefferIn is { } map && map.TryGetValue(l.Id, out var labels)
				? labels
				: Array.Empty<string>(),
		});

	private sealed record SearchRow(Guid Id, string Title, string? Composer, string? Arranger, string? Lyricist, string? Lyrics);
}
