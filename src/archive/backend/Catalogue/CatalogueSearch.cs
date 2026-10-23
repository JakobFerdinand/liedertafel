using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Catalogue;

/// <summary>
/// One searchable field of a Lied with its German label for the place a
/// Treffer was found ("im Liedtext", "in den Noten", …).
/// </summary>
public sealed record SearchField(string Label, string Folded);

/// <summary>
/// Shared matching over the folded fields of a Lied (#142): the member search
/// on <c>/api/lieder</c> and chat's <c>catalogue_search</c> use the same
/// <see cref="CatalogueText.Fold"/> and the same tokenized AND rule — a Lied
/// matches when every token stands in at least one field — plus the same
/// labels for where a Treffer came from.
/// </summary>
public static class CatalogueSearch
{
	public const string TitleLabel = "im Titel";

	public const string ComposerLabel = "beim Komponisten";

	public const string ArrangerLabel = "beim Bearbeiter";

	public const string LyricistLabel = "beim Texter";

	public const string LyricsLabel = "im Liedtext";

	public const string NotenLabel = "in den Noten";

	/// <summary>True when every token stands in at least one field.</summary>
	public static bool Matches(IReadOnlyList<string> tokens, IReadOnlyList<SearchField> fields)
	{
		foreach (var token in tokens)
			if (!fields.Any(f => f.Folded.Contains(token)))
				return false;
		return true;
	}

	/// <summary>The labels of the fields at least one token stands in.</summary>
	public static List<string> MatchLabels(IReadOnlyList<string> tokens, IReadOnlyList<SearchField> fields) =>
		fields.Where(f => tokens.Any(t => f.Folded.Contains(t))).Select(f => f.Label).ToList();

	/// <summary>
	/// The searchable fields of one Lied (title, creators, lyrics and the text
	/// of its live Noten), folded once per row; empty fields contribute nothing.
	/// </summary>
	public static List<SearchField> FieldsOf(string? title, string? composer, string? arranger,
		string? lyricist, string? lyrics, string? foldedNoten)
	{
		var fields = new List<SearchField>(6);
		Add(fields, TitleLabel, title);
		Add(fields, ComposerLabel, composer);
		Add(fields, ArrangerLabel, arranger);
		Add(fields, LyricistLabel, lyricist);
		Add(fields, LyricsLabel, lyrics);
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
