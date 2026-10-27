using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Catalogue;

/// <summary>
/// One searchable field of a Lied with its German label for the place a
/// Treffer was found ("im Titel", "in den Noten", …).
/// </summary>
public sealed record SearchField(string Label, string Folded);

/// <summary>One Lied a query matched, with its rank and the fields where the words stand.</summary>
public sealed record SearchMatch(Lied Lied, int Rank, IReadOnlyList<SearchField> Fields);

/// <summary>
/// One search contract (#192): folding, tokenizing, matching, ranking and
/// paging live here, so the member search on <c>/api/lieder</c> and the chat's
/// <c>catalogue_search</c> are two adapters over it and return the same
/// matches for the same query. A Lied matches when every token stands in at
/// least one searchable field — the catalog decides which those are — or in
/// the text of its live Noten; the order is rank, then title, then id. Only
/// the page size stays each adapter's own. Pure over rows, testable without a
/// database.
/// </summary>
public static class LiedSearch
{
	public const string TitleLabel = "im Titel";

	public const string NotenLabel = "in den Noten";

	/// <summary>The tokens of a query, folded; an empty query matches every Lied.</summary>
	public static List<string> TokensOf(string query)
		=> query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(CatalogueText.Fold)
			.Where(t => t.Length > 0)
			.ToList();

	/// <summary>True when every token stands in at least one field.</summary>
	public static bool Matches(IReadOnlyList<string> tokens, IReadOnlyList<SearchField> fields)
	{
		foreach (var token in tokens)
			if (!fields.Any(f => f.Folded.Contains(token)))
				return false;
		return true;
	}

	/// <summary>The labels of the fields at least one token stands in.</summary>
	public static List<string> Labels(IReadOnlyList<string> tokens, IReadOnlyList<SearchField> fields) =>
		fields.Where(f => tokens.Any(t => f.Folded.Contains(t))).Select(f => f.Label).ToList();

	/// <summary>
	/// The searchable fields of one Lied, folded once per row: the title, the
	/// fields the catalog marks searchable, and the text of its live Noten.
	/// A field without a search label is not searchable by accident (#192);
	/// empty fields contribute nothing.
	/// </summary>
	public static List<SearchField> FieldsOf(Lied lied, string? foldedNoten)
	{
		var fields = new List<SearchField>(2);
		Add(fields, TitleLabel, lied.Title);
		foreach (var field in FieldCatalog.LiedSearchFields)
			Add(fields, field.SearchLabel!, field.Get(lied));
		Add(fields, NotenLabel, foldedNoten);
		return fields;
	}

	private static void Add(List<SearchField> fields, string label, string? value)
	{
		var folded = CatalogueText.Fold(value);
		if (folded.Length > 0)
			fields.Add(new(label, folded));
	}

	/// <summary>
	/// 0: the query is the folded title, 1: every token stands in the title,
	/// 2: an empty query or a Lied matched somewhere else.
	/// </summary>
	public static int Rank(IReadOnlyList<string> tokens, string query, string? title)
	{
		if (tokens.Count == 0)
			return 2;
		if (CatalogueText.Fold(title) == CatalogueText.Fold(query))
			return 0;
		return tokens.All(t => CatalogueText.Fold(title).Contains(t)) ? 1 : 2;
	}

	/// <summary>
	/// The Lieder a query matches, ranked and in the one search order (rank,
	/// then title, then id): the selection both adapters serve.
	/// </summary>
	public static List<SearchMatch> Match(IReadOnlyList<string> tokens, IEnumerable<Lied> lieder,
		IReadOnlyDictionary<Guid, string> notenTexts, string query)
		=> lieder
			.Select(lied => (Lied: lied, Fields: FieldsOf(lied, notenTexts.GetValueOrDefault(lied.Id) ?? string.Empty)))
			.Where(r => Matches(tokens, r.Fields))
			.Select(r => new SearchMatch(r.Lied, Rank(tokens, query, r.Lied.Title), r.Fields))
			.OrderBy(m => m.Rank)
			.ThenBy(m => CatalogueText.Fold(m.Lied.Title), StringComparer.Ordinal)
			.ThenBy(m => m.Lied.Id)
			.ToList();

	/// <summary>One bounded page of matches, pages counting from one.</summary>
	public static List<SearchMatch> Page(IReadOnlyList<SearchMatch> matches, int page, int pageSize) =>
		pageSize <= 0 ? [] : matches.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToList();

	/// <summary>The requested page of an adapter: absent or below one reads as the first.</summary>
	public static int RequestedPage(int? page) => page is null or < 1 ? 1 : page.Value;

	/// <summary>The Noten texts an adapter's query needs: none without tokens.</summary>
	public static Task<Dictionary<Guid, string>> LoadNotenTextsAsync(
		ArchiveDbContext db, IReadOnlyList<string> tokens, CancellationToken token)
		=> tokens.Count == 0
			? Task.FromResult(new Dictionary<Guid, string>())
			: LoadNotenTextsAsync(db, token);

	/// <summary>
	/// The folded extracted text of every Lied's live Noten, joined per Lied.
	/// Dateien in the trash contribute nothing, so a replaced or deleted Noten
	/// stops being found.
	/// </summary>
	public static async Task<Dictionary<Guid, string>> LoadNotenTextsAsync(ArchiveDbContext db, CancellationToken token)
	{
		var texts = await (
			from job in db.ExtractionJobs.AsNoTracking()
			join datei in DateiQueries.Visible(db) on job.DateiId equals datei.Id
			where datei.LiedId != null && datei.Kind == DateiKinds.Noten
				&& job.Status == Extraction.ExtractionStatus.Completed && job.Text != null
			select new { LiedId = datei.LiedId!.Value, job.Text })
			.ToListAsync(token);
		return texts.GroupBy(t => t.LiedId)
			.ToDictionary(g => g.Key, g => string.Join('\n', g.Select(t => CatalogueText.Fold(t.Text))));
	}
}
