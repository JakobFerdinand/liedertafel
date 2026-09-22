namespace Archive.Backend.Catalogue;

/// <summary>
/// German text folding for catalogue search (ARC-020): pure helper that maps
/// text to a comparable form — lowercase invariant, <c>ä→ae</c>, <c>ö→oe</c>,
/// <c>ü→ue</c>, <c>ß→ss</c>, then <c>ae→a</c>, <c>oe→o</c>, <c>ue→u</c> — so
/// „Müller", „Mueller" and „Muller" all fold to the same form on both the
/// query and the haystack side.
/// </summary>
public static class CatalogueText
{
	/// <summary>
	/// Folds German text for search comparisons (see the type summary).
	/// Pure and side-effect free; returns an empty string for null input.
	/// </summary>
	public static string Fold(string? text)
	{
		if (string.IsNullOrEmpty(text))
			return string.Empty;
		var lowered = text.ToLowerInvariant();
		var builder = new System.Text.StringBuilder(lowered.Length);
		foreach (var character in lowered)
		{
			switch (character)
			{
				case 'ä': builder.Append("ae"); break;
				case 'ö': builder.Append("oe"); break;
				case 'ü': builder.Append("ue"); break;
				case 'ß': builder.Append("ss"); break;
				default: builder.Append(character); break;
			}
		}
		return builder
			.Replace("ae", "a")
			.Replace("oe", "o")
			.Replace("ue", "u")
			.ToString();
	}
}
