using System.Globalization;
using System.Text;

namespace Archive.Backend.Reading;

/// <summary>A Lied of the catalogue as the matcher sees it.</summary>
public sealed record MatchableLied(Guid Id, string Title);

/// <summary>
/// How one programme entry relates to the catalogue (#131). <see cref="Lied"/>
/// is a pre-selection for the editor, never a write: it is set only when the
/// title leaves no doubt, and <see cref="Sure"/> is false when it is merely
/// the clearly closest Lied. Everything else is offered as
/// <see cref="Candidates"/> for the editor to pick, or left to create a Lied.
/// </summary>
public sealed record ProgrammMatch(MatchableLied? Lied, bool Sure, IReadOnlyList<MatchableLied> Candidates);

/// <summary>
/// Matches the entries of a programme scan against the Lieder of the catalogue
/// by title, deterministically and without a model. Wrong pre-selections are
/// the dangerous case, so only an exact (normalised) title that names exactly
/// one Lied is sure; a single clearly closest near miss is pre-selected but
/// marked unsure; ties and weak similarities are only candidates.
/// </summary>
public static class ProgrammMatcher
{
	/// <summary>Similarity from which a title is a candidate at all.</summary>
	public const double CandidateSimilarity = 0.6;

	/// <summary>Similarity from which a single closest Lied is pre-selected (unsure).</summary>
	public const double PreselectSimilarity = 0.85;

	/// <summary>How far the best Lied must lead the second best to be pre-selected.</summary>
	public const double PreselectMargin = 0.1;

	public const int MaxCandidates = 5;

	public static ProgrammMatch Match(string entryTitle, IReadOnlyList<MatchableLied> lieder)
	{
		var wanted = Normalize(entryTitle);
		if (wanted.Length == 0)
			return new ProgrammMatch(null, false, []);

		// Umlauts are written ae/oe/ue or plain a/o/u on old print and in OCR; both spellings are the same title.
		var wantedBare = Normalize(entryTitle, bare: true);
		var exact = lieder.Where(l => Normalize(l.Title) == wanted || Normalize(l.Title, bare: true) == wantedBare).ToList();
		if (exact.Count == 1)
			return new ProgrammMatch(exact[0], true, []);
		if (exact.Count > 1)
		{
			return new ProgrammMatch(null, false, exact
				.OrderBy(l => l.Title, StringComparer.Ordinal).ThenBy(l => l.Id)
				.Take(MaxCandidates).ToList());
		}

		var scored = lieder
			.Select(l => (Lied: l, Score: Similarity(wanted, Normalize(l.Title))))
			.Where(x => x.Score >= CandidateSimilarity)
			.OrderByDescending(x => x.Score)
			.ThenBy(x => x.Lied.Title, StringComparer.Ordinal)
			.ThenBy(x => x.Lied.Id)
			.ToList();
		if (scored.Count == 0)
			return new ProgrammMatch(null, false, []);
		var best = scored[0];
		var lead = scored.Count == 1 ? best.Score : best.Score - scored[1].Score;
		var candidates = scored.Take(MaxCandidates).Select(x => x.Lied).ToList();
		if (best.Score >= PreselectSimilarity && lead >= PreselectMargin)
			return new ProgrammMatch(best.Lied, false, candidates);
		return new ProgrammMatch(null, false, candidates);
	}

	/// <summary>
	/// Lower case, umlauts folded (ä to ae, ß to ss; with <paramref name="bare"/>
	/// ä to a), other accents dropped, everything that is not a letter or digit
	/// replaced by one blank. "SÜSSER die, Glocken!" and "Süßer die Glocken" are
	/// the same title.
	/// </summary>
	public static string Normalize(string? title, bool bare = false)
	{
		if (string.IsNullOrWhiteSpace(title))
			return string.Empty;
		var folded = title.Replace("ß", "ss").Replace("ẞ", "ss");
		if (!bare)
		{
			folded = folded.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
				.Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue");
		}
		var decomposed = folded.Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(decomposed.Length);
		var gap = false;
		foreach (var character in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
				continue;
			if (char.IsLetterOrDigit(character))
			{
				if (gap && builder.Length > 0)
					builder.Append(' ');
				gap = false;
				builder.Append(char.ToLowerInvariant(character));
			}
			else
			{
				gap = true;
			}
		}
		return builder.ToString();
	}

	/// <summary>One minus the Levenshtein distance relative to the longer title.</summary>
	private static double Similarity(string left, string right)
	{
		if (left.Length == 0 || right.Length == 0)
			return 0;
		return 1.0 - (double)Distance(left, right) / Math.Max(left.Length, right.Length);
	}

	private static int Distance(string left, string right)
	{
		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];
		for (var j = 0; j <= right.Length; j++)
			previous[j] = j;
		for (var i = 1; i <= left.Length; i++)
		{
			current[0] = i;
			for (var j = 1; j <= right.Length; j++)
			{
				var cost = left[i - 1] == right[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}
		return previous[right.Length];
	}
}
