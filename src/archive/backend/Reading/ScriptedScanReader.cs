using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Reading;

/// <summary>
/// The offline stand-in for the model side of scan reading (ARC-034-1), used
/// by <see cref="Chat.ScriptedChatClient"/> in Development and in tests: no
/// provider is called and nothing needs credentials. It is deterministic and
/// deliberately simple: a transcription returns a fixed German sample page
/// (it cannot see images), and the field answer picks credit lines, key,
/// voice and lyrics out of the text by keywords — so the whole journey (jobs,
/// budget, quote check, field writes) can run locally.
/// </summary>
public static partial class ScriptedScanReader
{
	/// <summary>True for a request that carries page images.</summary>
	public static bool IsTranscription(IReadOnlyList<ChatMessage> messages)
		=> messages.Any(m => m.Contents.Any(c => c is DataContent));

	/// <summary>
	/// True for the field request: the agent's own instructions (never a chat
	/// message a member could write) name the NOTENTEXT marks, and the prompt
	/// carries the text between them.
	/// </summary>
	public static bool IsFieldRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
		=> options?.Instructions?.Contains("NOTENTEXT-ANFANG", StringComparison.Ordinal) == true
			|| messages.Any(m => m.Role == ChatRole.System && m.Text.Contains("NOTENTEXT-ANFANG", StringComparison.Ordinal));

	/// <summary>
	/// True for the programme transcription (#131): page images asked for by the
	/// programme agent, whose own instructions (never a chat message) say so.
	/// </summary>
	public static bool IsProgrammTranscription(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
		=> IsTranscription(messages) && HasMarker(messages, options, "Programmzettel");

	/// <summary>True for the programme field request (#131): the text stands between the PROGRAMMTEXT marks.</summary>
	public static bool IsProgrammFieldRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
		=> HasMarker(messages, options, ScanPrompts.ProgrammTextStart);

	private static bool HasMarker(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string marker)
		=> options?.Instructions?.Contains(marker, StringComparison.Ordinal) == true
			|| messages.Any(m => m.Role == ChatRole.System && m.Text.Contains(marker, StringComparison.Ordinal));

	/// <summary>The fixed sample programme the scripted transcriber "reads" from any image (it cannot see).</summary>
	public const string ProgrammSample = "Sängerfest der Liedertafel Mining\n14. Juni 1925\nOrt: Gasthaus zur Post, Mining\n"
		+ "1. Ave Maria\n2. Der Mond ist aufgegangen\n3. Waldfahrt";

	public static string TranscribeProgramm() => "=== Seite 1 ===\n" + ProgrammSample;

	public static string Transcribe(IReadOnlyList<ChatMessage> messages)
	{
		var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
		var numbers = PageNumbers().Match(prompt) is { Success: true } match
			? match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			: ["1"];
		var pages = numbers.Select(number => number == "1" ? FirstPage : $"Zweite Strophe auf Seite {number}\nWir singen laut im Chor");
		return string.Join('\n', numbers.Zip(pages, (number, page) => $"=== Seite {number} ===\n{page}"));
	}

	private const string FirstPage = "Waldfahrt\nMusik: Franz Schubert\nText: Anna Beispiel\nTenor I\nG-Dur\n"
		+ "Im Wald da singen die Vögel\nUnd alle stimmen mit ein";

	public static string Fields(IReadOnlyList<ChatMessage> messages)
	{
		var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
		var wanted = WantedFields().Match(prompt) is { Success: true } match
			? match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			: [];
		var start = prompt.IndexOf("NOTENTEXT-ANFANG", StringComparison.Ordinal);
		var end = prompt.IndexOf("NOTENTEXT-ENDE", StringComparison.Ordinal);
		var text = start >= 0 && end > start ? prompt[(start + "NOTENTEXT-ANFANG".Length)..end] : string.Empty;
		var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		var fields = new List<object>();
		foreach (var field in wanted)
		{
			var read = field switch
			{
				"title" => Title(lines),
				"voice_configuration" => Pattern(lines, ConfigurationPattern()),
				"composer" => Credit(lines, "Musik"),
				"lyricist" => Credit(lines, "Text"),
				"arranger" => Credit(lines, "Satz"),
				"musical_key" => Pattern(lines, KeyPattern()),
				"voice_label" => Pattern(lines, VoicePattern()),
				"lyrics" => Lyrics(lines),
				_ => null,
			};
			if (read is { } found)
				fields.Add(new { feld = field, wert = found.Value, sicherheit = "sicher", zitat = found.Quote });
		}
		return JsonSerializer.Serialize(new { felder = fields });
	}

	private static (string Value, string Quote)? Credit(string[] lines, string label)
	{
		foreach (var line in lines)
		{
			var colon = line.IndexOf(':');
			if (colon > 0 && line[..colon].Trim().Equals(label, StringComparison.OrdinalIgnoreCase) && colon + 1 < line.Length)
				return (line[(colon + 1)..].Trim(), line);
		}
		return null;
	}

	private static (string Value, string Quote)? Title(string[] lines)
	{
		var line = lines.FirstOrDefault(l => !l.Contains(':') && !KeyPattern().IsMatch(l) && !VoicePattern().IsMatch(l));
		return line is null ? null : (line, line);
	}

	private static (string Value, string Quote)? Pattern(string[] lines, Regex pattern)
	{
		foreach (var line in lines)
		{
			if (pattern.Match(line) is { Success: true } match)
				return (match.Value, line);
		}
		return null;
	}

	private static (string Value, string Quote)? Lyrics(string[] lines)
	{
		var sung = lines
			.Where(line => !line.Contains(':') && line.Split(' ').Length >= 3 && !KeyPattern().IsMatch(line))
			.ToList();
		return sung.Count == 0 ? null : (string.Join('\n', sung), sung[0]);
	}

	[GeneratedRegex(@"Seitenzahlen in dieser Reihenfolge: ([0-9, ]+)")]
	private static partial Regex PageNumbers();

	[GeneratedRegex(@"Gesuchte Felder: ([a-z_, ]+)")]
	private static partial Regex WantedFields();

	[GeneratedRegex(@"\b[A-H](?:is|es|s)?-(?:Dur|Moll)\b")]
	private static partial Regex KeyPattern();

	[GeneratedRegex(@"\b(?:Männerchor|Frauenchor|gemischter Chor|TTBB|SATB)\b")]
	private static partial Regex ConfigurationPattern();

	[GeneratedRegex(@"\b(?:Tenor|Bass|Bariton)(?: (?:I|II))?\b")]
	private static partial Regex VoicePattern();

	private static readonly string[] MonthNames =
		["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];

	/// <summary>
	/// The scripted programme agent (#131): deliberately simple conventions, like
	/// the score reader. The first plain line is the title; a line with a
	/// year is the date ("14. Juni 1925", "Mai 1925", "um 1950"); "Ort:" or the
	/// line after the date names the place; numbered lines are the entries.
	/// </summary>
	public static string Programm(IReadOnlyList<ChatMessage> messages)
	{
		var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
		var start = prompt.IndexOf(ScanPrompts.ProgrammTextStart, StringComparison.Ordinal);
		var end = prompt.IndexOf(ScanPrompts.ProgrammTextEnd, StringComparison.Ordinal);
		var text = start >= 0 && end > start ? prompt[(start + ScanPrompts.ProgrammTextStart.Length)..end] : string.Empty;
		var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

		var fields = new List<object>();
		var entries = new List<object>();
		string? titleLine = null;
		string? venueLine = null;
		var afterDate = false;
		foreach (var line in lines)
		{
			if (line.StartsWith("===", StringComparison.Ordinal))
				continue;
			if (DateLine(line) is { } date)
			{
				fields.Add(Field("year", date.Year.ToString(), line));
				if (date.Month is { } month)
					fields.Add(Field("month", month.ToString(), line));
				if (date.Day is { } day)
					fields.Add(Field("day", day.ToString(), line));
				if (date.Approximate)
					fields.Add(Field("approximate", "ja", line));
				afterDate = true;
				continue;
			}
			if (line.StartsWith("Ort:", StringComparison.OrdinalIgnoreCase))
			{
				venueLine = line;
				continue;
			}
			if (EntryLine().Match(line) is { Success: true } entry)
			{
				entries.Add(new { titel = entry.Groups[1].Value.Trim(), sicherheit = "sicher", zitat = line });
				afterDate = false;
				continue;
			}
			if (titleLine is null)
				titleLine = line;
			else if (afterDate && venueLine is null)
				venueLine = line;
			afterDate = false;
		}
		if (titleLine is not null)
		{
			fields.Insert(0, Field("title", titleLine, titleLine));
			if (Kind(titleLine) is { } kind)
				fields.Insert(1, Field("kind", kind, titleLine));
		}
		if (venueLine is not null)
		{
			var venue = venueLine.StartsWith("Ort:", StringComparison.OrdinalIgnoreCase) ? venueLine[4..].Trim() : venueLine;
			fields.Add(Field("venue", venue, venueLine));
		}
		return JsonSerializer.Serialize(new { felder = fields, punkte = entries });
	}

	private static object Field(string field, string value, string quote)
		=> new { feld = field, wert = value, sicherheit = "sicher", zitat = quote };

	private static string? Kind(string title)
	{
		var lower = title.ToLowerInvariant();
		if (lower.Contains("hochzeit") || lower.Contains("trauung"))
			return "wedding";
		if (lower.Contains("begräbnis") || lower.Contains("beerdigung") || lower.Contains("requiem") || lower.Contains("trauerfeier"))
			return "funeral";
		if (lower.Contains("gottesdienst") || lower.Contains("messe") || lower.Contains("andacht"))
			return "service";
		if (lower.Contains("fest"))
			return "festival";
		if (lower.Contains("konzert") || lower.Contains("liederabend"))
			return "concert";
		return null;
	}

	private sealed record ScriptedDate(int Year, int? Month, int? Day, bool Approximate);

	private static ScriptedDate? DateLine(string line)
	{
		var year = YearPattern().Match(line);
		if (!year.Success)
			return null;
		int? month = null;
		for (var i = 0; i < MonthNames.Length; i++)
		{
			if (line.Contains(MonthNames[i], StringComparison.OrdinalIgnoreCase))
				month = i + 1;
		}
		int? day = null;
		if (month is not null && DayPattern().Match(line) is { Success: true } dayMatch)
			day = int.Parse(dayMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
		var approximate = ApproximatePattern().IsMatch(line);
		return new ScriptedDate(int.Parse(year.Value, System.Globalization.CultureInfo.InvariantCulture), month, day, approximate);
	}

	[GeneratedRegex(@"^\s*\d{1,3}\s*[.)]\s+(.+)$")]
	private static partial Regex EntryLine();

	[GeneratedRegex(@"(?<!\d)(1[89]\d{2}|20\d{2})(?!\d)")]
	private static partial Regex YearPattern();

	[GeneratedRegex(@"(?<!\d)(\d{1,2})\.\s*(?=[A-Za-zÄÖÜäöü])")]
	private static partial Regex DayPattern();

	[GeneratedRegex(@"\b(?:um|ca\.?|circa|etwa)\s", RegexOptions.IgnoreCase)]
	private static partial Regex ApproximatePattern();
}
