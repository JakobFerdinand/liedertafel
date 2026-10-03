using System.Text;
using System.Text.RegularExpressions;

namespace Archive.Backend.Extraction;

/// <summary>
/// What a score's embedded text says about itself. Every value is a
/// best-effort reading of the extracted text and null when nothing reliable
/// was found; entered catalogue values always take precedence over these.
/// </summary>
public sealed record ScoreFacts(
	string? Voice,
	string? MusicalKey,
	string? TimeSignature,
	string? Tempo,
	string? Composer,
	string? Lyricist,
	string? Arranger,
	string? Copyright)
{
	public bool IsEmpty => Voice is null && MusicalKey is null && TimeSignature is null && Tempo is null
		&& Composer is null && Lyricist is null && Arranger is null && Copyright is null;
}

/// <summary>Readable text plus the facts derived from one extraction result.</summary>
public sealed record ScoreTextAnalysis(string CleanText, ScoreFacts Facts);

/// <summary>
/// Pure reading of the text ARC-034 extracted from a score PDF. Notation
/// programs embed their music fonts as ordinary characters, so the raw text
/// is full of glyph remnants ("å", "åê") and sung syllables split by
/// hyphens ("Ho - si - an - na"). The analyzer drops the remnants, joins the
/// syllables back into words and reads voice, key, time signature, tempo and
/// credits from the result. It is computed on read from the stored text, so
/// improvements apply to every already extracted revision without new work.
/// </summary>
public static partial class ScoreTextAnalyzer
{
	/// <summary>Label for a document that carries several different voices.</summary>
	public const string FullScoreLabel = "Partitur";

	/// <summary>Leading tokens that count as the heading of the first page.</summary>
	private const int HeaderTokens = 40;

	/// <summary>Key and time signature sit directly beside the voice name.</summary>
	private const int SignatureTokens = 12;

	private const int MaxCreditChars = 120;

	private const int MaxCopyrightChars = 160;

	private static readonly ScoreTextAnalysis Empty = new(string.Empty,
		new ScoreFacts(null, null, null, null, null, null, null, null));

	private static readonly HashSet<string> TimeSignatures =
	[
		"2/2", "3/2", "4/2", "2/4", "3/4", "4/4", "5/4", "6/4", "3/8", "6/8", "9/8", "12/8",
	];

	private static readonly string[] TempoWords =
	[
		"Adagio", "Andante", "Andantino", "Allegro", "Allegretto", "Moderato", "Largo", "Larghetto",
		"Lento", "Presto", "Vivace", "Maestoso", "Grave", "Sostenuto", "Tranquillo",
	];

	public static ScoreTextAnalysis Analyze(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return Empty;
		var lines = new List<List<string>>();
		foreach (var rawLine in text.Split('\n'))
		{
			var tokens = rawLine
				.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
				.Select(StripGlyphs)
				.Where(t => t.Length > 0 && !IsNoise(t))
				.ToList();
			if (tokens.Count > 0)
				lines.Add(tokens);
		}
		if (lines.Count == 0)
			return Empty;
		var all = lines.SelectMany(l => l).ToList();
		var header = string.Join(' ', all.Take(HeaderTokens));
		var (composer, lyricist, arranger, copyright) = ReadCredits(lines);
		return new ScoreTextAnalysis(JoinSyllables(all), new ScoreFacts(
			ReadVoice(header, lines),
			ReadKey(header, all),
			ReadTimeSignature(all),
			ReadTempo(text, all),
			composer, lyricist, arranger, copyright));
	}

	/// <summary>Removes private-use music glyphs and control characters from a token.</summary>
	private static string StripGlyphs(string token)
	{
		if (!token.Any(c => char.IsControl(c) || c is >= '' and <= '' or '�'))
			return token;
		return new string(token.Where(c => !char.IsControl(c) && c is not (>= '' and <= '') and not '�').ToArray());
	}

	private static bool IsPlainLetter(char c) =>
		c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or 'Ä' or 'Ö' or 'Ü' or 'ä' or 'ö' or 'ü' or 'ß';

	/// <summary>
	/// A music-font remnant: letters, but none a German text would use
	/// ("å", ".åê"), or a pure lyric extender line ("__").
	/// </summary>
	private static bool IsNoise(string token)
	{
		if (token.All(c => c is '_' or '‿' or '⁀' or '~'))
			return true;
		return token.Any(char.IsLetter) && !token.Any(IsPlainLetter);
	}

	private static bool IsHyphen(char c) => c is '-' or '‐' or '‑' or '–';

	/// <summary>
	/// Flows the tokens into readable text and joins sung syllables. Only a
	/// lowercase continuation is joined, so "Wien - Graz" keeps its dash.
	/// </summary>
	private static string JoinSyllables(List<string> tokens)
	{
		var text = new StringBuilder();
		var joinNext = false;
		foreach (var token in tokens)
		{
			if (token.All(IsHyphen))
			{
				joinNext = text.Length > 0;
				continue;
			}
			var word = token.Trim('-', '‐', '‑', '–');
			var joins = text.Length > 0 && (joinNext || IsHyphen(token[0]));
			if (joins && char.IsLower(word[0]))
				text.Append(word);
			else if (joins)
				text.Append(" - ").Append(word);
			else if (text.Length > 0 && word.All(char.IsPunctuation))
				text.Append(word);
			else
				text.Append(text.Length > 0 ? " " : string.Empty).Append(word);
			joinNext = IsHyphen(token[^1]);
		}
		return text.ToString();
	}

	[GeneratedRegex(@"(?<![\p{L}\d])(?:(?<pre>[12]|II?)\.?\s*)?(?<name>Mezzosopran|MEZZOSOPRAN|Soprano?|SOPRAN|Alto?|ALT|Tenore?|TENOR|Bariton|BARITON|Bass|BASS|Baß)(?:\s*(?<post>[12]|II?)(?![\p{L}\d]))?(?![\p{L}])")]
	private static partial Regex VoicePattern();

	/// <summary>
	/// One voice name gives that voice, several numbers of the same voice a
	/// combined part and different voices the full score. Voices count in the
	/// heading and as staff labels standing alone on a line; a bare "Alt" in
	/// the lyrics is the German word, not the voice.
	/// </summary>
	private static string? ReadVoice(string header, List<List<string>> lines)
	{
		var found = new List<(string Name, string? Number)>();
		foreach (Match match in VoicePattern().Matches(header))
			found.Add(ToVoice(match));
		foreach (var line in lines.Where(l => l.Count <= 3))
		{
			var joined = string.Join(' ', line);
			var match = VoicePattern().Match(joined);
			if (!match.Success || match.Length != joined.TrimEnd('.', ':').Length || match.Index != 0)
				continue;
			var voice = ToVoice(match);
			if (voice.Name != "Alt" || voice.Number is not null)
				found.Add(voice);
		}
		// A numbered mention supersedes the bare name of the same voice.
		var voices = found.Distinct()
			.Where(v => v.Number is not null || !found.Any(o => o.Name == v.Name && o.Number is not null))
			.ToList();
		if (voices.Count == 0)
			return null;
		if (voices.Count == 1)
			return Label(voices[0]);
		if (voices.Select(v => v.Name).Distinct().Count() > 1)
			return FullScoreLabel;
		return $"{voices[0].Name} {string.Join('+', voices.Select(v => v.Number).Order())}";

		static string Label((string Name, string? Number) voice) =>
			voice.Number is null ? voice.Name : $"{voice.Name} {voice.Number}";
	}

	private static (string Name, string? Number) ToVoice(Match match)
	{
		var name = match.Groups["name"].Value.ToLowerInvariant() switch
		{
			"mezzosopran" => "Mezzosopran",
			"sopran" or "soprano" => "Sopran",
			"alt" or "alto" => "Alt",
			"tenor" or "tenore" => "Tenor",
			"bariton" => "Bariton",
			_ => "Bass",
		};
		var number = match.Groups["post"].Success ? match.Groups["post"].Value
			: match.Groups["pre"].Success ? match.Groups["pre"].Value
			: null;
		return (name, number switch { "I" => "1", "II" => "2", _ => number });
	}

	[GeneratedRegex(@"(?<![\p{L}])(?<note>[A-Ha-h](?:is|es|s)?|[A-Ga-g][♭♯b#])[- ]?(?<mode>Dur|dur|Moll|moll|[Mm]ajor|[Mm]inor)(?![\p{L}])")]
	private static partial Regex KeyPattern();

	[GeneratedRegex(@"^[A-G][♭♯#]$")]
	private static partial Regex KeySymbolPattern();

	/// <summary>
	/// A written key ("As-Dur", "d-Moll", "F major") anywhere in the heading,
	/// otherwise a note name with an accidental beside the voice ("A♭").
	/// </summary>
	private static string? ReadKey(string header, List<string> tokens)
	{
		var match = KeyPattern().Match(header);
		if (match.Success)
		{
			var minor = match.Groups["mode"].Value.ToLowerInvariant() is "moll" or "minor";
			var note = match.Groups["note"].Value;
			if (note.Length == 2 && note[1] is 'b' or '#')
				note = $"{note[0]}{(note[1] == 'b' ? '♭' : '♯')}";
			note = (minor ? char.ToLowerInvariant(note[0]) : char.ToUpperInvariant(note[0])) + note[1..];
			return $"{note}-{(minor ? "Moll" : "Dur")}";
		}
		var symbol = tokens.Take(SignatureTokens).FirstOrDefault(t => KeySymbolPattern().IsMatch(t));
		return symbol?.Replace('#', '♯');
	}

	[GeneratedRegex(@"^(\d{1,2})/(\d{1,2})$")]
	private static partial Regex FractionPattern();

	/// <summary>
	/// "3/4" in the heading, or the stacked digits of the notation font,
	/// which extract as "34" or as two consecutive numbers.
	/// </summary>
	private static string? ReadTimeSignature(List<string> tokens)
	{
		var head = tokens.Take(HeaderTokens).ToList();
		foreach (var token in head)
		{
			var fraction = FractionPattern().Match(token);
			if (fraction.Success && TimeSignatures.Contains(token))
				return token;
		}
		var signature = head.Take(SignatureTokens).ToList();
		for (var index = 0; index < signature.Count; index++)
		{
			var token = signature[index];
			if (!token.All(char.IsAsciiDigit))
				continue;
			if (token.Length is 2 or 3)
			{
				var stacked = $"{token[..^1]}/{token[^1]}";
				if (TimeSignatures.Contains(stacked))
					return stacked;
			}
			if (index + 1 < signature.Count && signature[index + 1].All(char.IsAsciiDigit))
			{
				var pair = $"{token}/{signature[index + 1]}";
				if (TimeSignatures.Contains(pair))
					return pair;
			}
		}
		return null;
	}

	[GeneratedRegex(@"=\s*(?:ca\.?\s*)?(\d{2,3})(?!\d)")]
	private static partial Regex MetronomePattern();

	/// <summary>An Italian tempo word and/or a metronome mark ("= 72") from the heading.</summary>
	private static string? ReadTempo(string text, List<string> tokens)
	{
		var word = tokens.Take(HeaderTokens)
			.Select(t => t.Trim(',', '.', ';', ':', '(', ')'))
			.FirstOrDefault(t => TempoWords.Contains(t, StringComparer.Ordinal));
		string? beats = null;
		var mark = MetronomePattern().Match(text.Length > 600 ? text[..600] : text);
		if (mark.Success && int.Parse(mark.Groups[1].Value) is >= 30 and <= 240)
			beats = $"{mark.Groups[1].Value} Schläge/min";
		return word is not null && beats is not null ? $"{word}, {beats}" : word ?? beats;
	}

	[GeneratedRegex(@"(?<![\p{L}])(?<label>Musik und Text|Text und Musik|Worte und Weise|Text und Melodie|Musik|Melodie|Weise|Komposition|Komponist|Music|Text|Worte|Words|Lyrics|Dichtung|Satz|Chorsatz|Arrangement|Arr\.|Bearbeitung|Bearb\.|Einrichtung)\s*:\s*", RegexOptions.IgnoreCase)]
	private static partial Regex CreditPattern();

	/// <summary>
	/// Labelled credits ("Musik: …", "Text: …", "Satz: …") and the copyright
	/// line; the first mention of each role wins.
	/// </summary>
	private static (string? Composer, string? Lyricist, string? Arranger, string? Copyright) ReadCredits(List<List<string>> lines)
	{
		string? composer = null, lyricist = null, arranger = null, copyright = null;
		for (var index = 0; index < lines.Count; index++)
		{
			var line = string.Join(' ', lines[index]);
			if (copyright is null && (line.Contains('©') || line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)))
				copyright = Bound(line, MaxCopyrightChars);
			var matches = CreditPattern().Matches(line);
			for (var m = 0; m < matches.Count; m++)
			{
				var start = matches[m].Index + matches[m].Length;
				var end = m + 1 < matches.Count ? matches[m + 1].Index : line.Length;
				var value = line[start..end].Trim(' ', ',', ';', '·', '-', '–', '/');
				// A label alone on its line names the person on the next one.
				if (value.Length == 0 && m + 1 == matches.Count && index + 1 < lines.Count)
					value = string.Join(' ', lines[index + 1]);
				if (value.Length == 0 || CreditPattern().IsMatch(value))
					continue;
				value = Bound(value, MaxCreditChars);
				var label = matches[m].Groups["label"].Value.ToLowerInvariant();
				var both = label.Contains(" und ");
				var isArranger = label is "satz" or "chorsatz" or "arrangement" or "arr." or "bearbeitung" or "bearb." or "einrichtung";
				var isLyricist = label is "text" or "worte" or "words" or "lyrics" or "dichtung";
				if (isArranger)
					arranger ??= value;
				else if (both || isLyricist)
					lyricist ??= value;
				if (!isArranger && (both || !isLyricist))
					composer ??= value;
			}
		}
		return (composer, lyricist, arranger, copyright);
	}

	private static string Bound(string value, int max) => value.Length > max ? value[..max].TrimEnd() : value;
}
