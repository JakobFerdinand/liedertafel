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
}
