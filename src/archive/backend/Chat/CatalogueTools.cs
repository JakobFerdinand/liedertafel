using System.ComponentModel;
using System.Text.Json;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

/// <summary>
/// The chat tool allow-list: authorized retrieval functions only, built per
/// request so the delegates capture the scoped <see cref="ArchiveDbContext"/>.
/// Authorization is enforced INSIDE every tool: every saved Lied is visible to
/// a member (ADR-0001), but a Lied's history only names performances and
/// planned appearances at member-visible (published) Auftritte, so drafts can
/// never reach the model or the thread content. No editing tools, no web
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
		"Sucht im Liedverzeichnis nach Titeln, Komponisten, Bearbeitern, Textdichtern und Liedtexten. "
		+ "Ein leerer Suchtext listet vorhandene Lieder auf. Liefert höchstens 10 Lieder pro Seite, "
		+ "totalCount, nextPage und limitReached; höchstens 5 Seiten. Nur zurückgegebene Lieder zitieren.";

	/// <summary>Creates the bounded catalogue search tool for one chat run.</summary>
	public static AITool CreateCatalogueSearchTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("Suchtext; leer (\"\") für eine Übersicht aller Lieder. Sonst müssen alle Begriffe im Titel, beim Komponisten, Bearbeiter, Textdichter oder im Liedtext vorkommen.")] string query,
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
				+ "den vorhandenen Dateien und seiner Aufführungsgeschichte: gesungen nennt die Auftritte, bei denen das Lied "
				+ "belegt gesungen wurde (nachweis bestaetigt) oder nur erwähnt ist (nachweis erwaehnt); geplant nennt "
				+ "veröffentlichte Programme, in denen das Lied steht und deren Ablauf noch nicht bestätigt ist.");

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
		var queryFold = CatalogueText.Fold(queryText);
		var matches = new List<(SearchRow Lied, int Rank)>();
		foreach (var lied in all)
		{
			var titleFold = CatalogueText.Fold(lied.Title);
			var composerFold = CatalogueText.Fold(lied.Composer);
			var arrangerFold = CatalogueText.Fold(lied.Arranger);
			var lyricistFold = CatalogueText.Fold(lied.Lyricist);
			var lyricsFold = CatalogueText.Fold(lied.Lyrics);
			if (!tokens.All(t => titleFold.Contains(t) || composerFold.Contains(t) || arrangerFold.Contains(t)
				|| lyricistFold.Contains(t) || lyricsFold.Contains(t)))
				continue;
			var rank = titleFold == queryFold ? 0
				: tokens.All(t => titleFold.Contains(t)) ? 1
				: 2;
			matches.Add((lied, rank));
		}
		var ordered = matches
			.OrderBy(m => m.Rank)
			.ThenBy(m => m.Lied.Id)
			.Skip((requestedPage - 1) * PageSize)
			.Take(PageSize)
			.ToList();
		return SerializeSearch(ordered.Select(m => m.Lied), requestedPage, matches.Count);
	}

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
		var dateien = await DateiQueries.Visible(db).AsNoTracking()
			.Where(d => d.LiedId == id)
			.OrderBy(d => d.Kind).ThenBy(d => d.Id)
			.Select(d => new { d.Kind, d.IsPartitur, d.Stimme, d.Description })
			.ToListAsync();
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
			}),
			gesungen,
			geplant,
		});
	}

	/// <summary>
	/// Where the Lied was sung or is planned, from member-visible Auftritte
	/// only (published events), newest first and bounded. Source notes and
	/// editor attribution never leave this method.
	/// </summary>
	private static async Task<(List<object> Gesungen, List<object> Geplant)> HistoryAsync(ArchiveDbContext db, Guid liedId)
	{
		var sung = await db.Performances.AsNoTracking()
			.Where(p => p.LiedId == liedId && p.Event.PublishedAt != null)
			.Select(p => new SungRow(p.Event.Title, p.Event.Kind, p.Event.Venue,
				p.Event.DateYear, p.Event.DateMonth, p.Event.DateDay, p.Event.DateApproximate, p.EvidenceStatus))
			.ToListAsync();
		sung.Sort((a, b) => EventDate.CompareNewestFirst(a.DateYear, a.DateMonth, a.DateDay, b.DateYear, b.DateMonth, b.DateDay));
		var gesungen = sung
			.Take(MaxHistoryEntries)
			.Select(p => (object)new
			{
				auftritt = p.Title,
				art = p.Kind,
				ort = p.Venue,
				datum = EventDate.Display(p.DateYear, p.DateMonth, p.DateDay, p.DateApproximate),
				nachweis = p.EvidenceStatus == PerformanceEvidenceStatus.Confirmed ? "bestaetigt" : "erwaehnt",
			})
			.ToList();

		// Planned: the newest published revision of a published event's
		// programme names the Lied and no confirmation states what was sung.
		var programmes = await db.Programmes.AsNoTracking()
			.Where(p => p.Event.PublishedAt != null)
			.Include(p => p.Event)
			.Include(p => p.Revisions).ThenInclude(r => r.Items)
			.ToListAsync();
		var confirmed = (await db.ProgrammeConfirmations.AsNoTracking()
			.Select(c => c.ProgrammeId).ToListAsync()).ToHashSet();
		var planned = programmes
			.Where(p => !confirmed.Contains(p.Id)
				&& ProgrammeVisibility.NewestPublished(p) is { } newest
				&& newest.Items.Any(i => i.LiedId == liedId))
			.OrderBy(p => p, Comparer<EventProgramme>.Create((a, b) => EventDate.CompareNewestFirst(
				a.Event.DateYear, a.Event.DateMonth, a.Event.DateDay, b.Event.DateYear, b.Event.DateMonth, b.Event.DateDay)))
			.Take(MaxHistoryEntries)
			.Select(p => (object)new
			{
				auftritt = p.Event.Title,
				art = p.Event.Kind,
				ort = p.Event.Venue,
				datum = EventDate.Display(p.Event.DateYear, p.Event.DateMonth, p.Event.DateDay, p.Event.DateApproximate),
			})
			.ToList();
		return (gesungen, planned);
	}

	private static string SerializeSearch(IEnumerable<SearchRow> lieder, int page, int totalCount) => JsonSerializer.Serialize(new
	{
		lieder = LiedPayload(lieder),
		page,
		pageSize = PageSize,
		totalCount,
		hasMore = page * PageSize < totalCount,
		nextPage = page < MaxPage && page * PageSize < totalCount ? (int?)(page + 1) : null,
		limitReached = page == MaxPage && page * PageSize < totalCount,
	});

	private static IEnumerable<object> LiedPayload(IEnumerable<SearchRow> lieder) => lieder.Select(l => new
		{
			id = l.Id,
			title = l.Title,
			composer = l.Composer,
			arranger = l.Arranger,
			lyricist = l.Lyricist,
			lyricsExcerpt = l.Lyrics is { Length: > 0 }
				? (l.Lyrics.Length > ExcerptChars ? l.Lyrics[..ExcerptChars] : l.Lyrics)
				: null,
		});

	private sealed record SungRow(string Title, string Kind, string? Venue,
		int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate, string EvidenceStatus);

	private sealed record SearchRow(Guid Id, string Title, string? Composer, string? Arranger, string? Lyricist, string? Lyrics);
}
