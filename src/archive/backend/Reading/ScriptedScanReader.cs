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
}
