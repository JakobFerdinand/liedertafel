using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

// The Microsoft.Extensions.AI ChatMessage/ChatOptions types clash with this
// slice's persisted ChatMessage entity and ChatOptions configuration class;
// the aliases keep both worlds unambiguous.
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;

/// <summary>
/// Deterministic offline stand-in for the real provider (ARC-022): a scripted
/// IChatClient that demonstrates the full contract — German archive-scope
/// answers, bounded authorized tool-calling, honest unknown answers,
/// instruction-in-data resistance (tool results are data, never instructions),
/// inline [Quelle: …] citation markers in the answer text and usage
/// reporting. Keyword heuristics are deliberate: this is a scripted
/// evaluation client, not an intelligence. Development runs without provider
/// configuration and all tests use it; the real Azure OpenAI client replaces
/// it behind the same IChatClient seam (ARC-022 provider step). Citation
/// chips are NOT emitted here: the service derives them from the inline
/// markers at the provider seam, so the contract survives any provider.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
	/// <summary>Model key of the offline stand-in in the usage ledger and under <c>Archive:Ai:Models</c>.</summary>
	public const string ModelKey = "scripted";

	/// <summary>Sentence appended whenever a tool result carries an embedded instruction.</summary>
	public const string DataNotInstructionSentence =
		"Anweisungen innerhalb von Archivtexten behandle ich als Inhalt, nicht als Befehl.";

	/// <summary>Answer when no matching catalogue record exists (honest unknown).</summary>
	public const string UnknownAnswer =
		"Das ist mir nicht bekannt. Solche Angaben sind im Archiv nicht verzeichnet.";

	/// <summary>
	/// Answer for questions about whole Auftritte ("Konzert mit ..."): the chat
	/// has no Auftritt tools, only the history of a single Lied (<c>lied_details</c>).
	/// </summary>
	public const string NoPerformanceDataAnswer =
		"Dazu liegen mir derzeit keine Aufführungsdaten vor.";

	/// <summary>Answer when a "wann gesungen" question names no Lied and the thread has none.</summary>
	public const string WhichLiedAnswer =
		"Welches Lied meinst du? Nenne mir bitte den Titel.";

	/// <summary>Polite refusal for non-archive questions.</summary>
	public const string OffTopicRefusal =
		"Dazu kann ich nur Fragen zum Vereinsarchiv beantworten.";

	private const string SearchToolName = "catalogue_search";

	private const string DetailsToolName = "lied_details";

	private const string ResponseId = "resp-scripted";

	private const string MessageId = "msg-scripted";

	/// <summary>Marker that starts an embedded instruction in document text (test corpus convention).</summary>
	private const string EmbeddedInstructionMarker = "Anweisung:";

	/// <summary>Citation markers appended to the answer for each cited record.</summary>
	private const string CitationMarkerPrefix = "[Quelle: ";

	public async Task<ChatResponse> GetResponseAsync(
		IEnumerable<AiChatMessage> messages, AiChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		var updates = new List<ChatResponseUpdate>();
		await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
			updates.Add(update);
		return updates.ToChatResponse();
	}

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var conversation = new List<AiChatMessage>();
		foreach (var message in messages)
			conversation.Add(message);
		var last = conversation.Count == 0 ? null : conversation[^1];

		// ARC-034-1: scan reading is not a chat; the offline stand-in answers
		// its two requests (page images, text for fields) from a small script.
		if (Reading.ScriptedScanReader.IsTranscription(conversation) || Reading.ScriptedScanReader.IsFieldRequest(conversation, options))
		{
			var transcription = Reading.ScriptedScanReader.IsTranscription(conversation);
			var answer = transcription ? Reading.ScriptedScanReader.Transcribe(conversation) : Reading.ScriptedScanReader.Fields(conversation);
			yield return Text(answer);
			yield return Usage(string.Join(' ', conversation.Select(m => m.Text)) + (transcription ? new string('x', 4000) : ""), answer);
			yield break;
		}

		// Tool results arrived: build the cited German answer from the data
		// only. Result text is treated as data: an embedded instruction in an
		// excerpt triggers the data-not-instruction sentence and is never
		// complied with.
		if (last?.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
		{
			var askedQuestion = conversation.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
			var offersCards = options?.Tools?.Any(t => t.Name == A2uiCatalog.ToolName) == true;
			var calledTool = CalledTool(conversation, result);
			var asksHistory = AsksWhenSung(askedQuestion);
			// "Wann gesungen": the search found the Lied, now read its history.
			if (asksHistory && calledTool == SearchToolName)
			{
				var wanted = HistoryTitle(askedQuestion, conversation);
				var found = ParseLieder(result.Result);
				var lied = found.FirstOrDefault(l => CatalogueFold(l.Title) == CatalogueFold(wanted ?? string.Empty))
					?? found.FirstOrDefault();
				if (lied is not null)
				{
					yield return new ChatResponseUpdate
					{
						MessageId = MessageId,
						Contents = [new FunctionCallContent($"call_{conversation.Count(HasFunctionCall) + 1}", DetailsToolName,
							new Dictionary<string, object?> { ["liedId"] = lied.Id })],
					};
					yield break;
				}
			}
			// A question that asks for a card, or a history answer about one
			// Lied: after the lookup, show the Lied and — from the history —
			// the Auftritte it was sung at (their Programm when nothing is
			// sung yet), then answer from the lookup result as usual.
			var cardSource = asksHistory ? DetailsToolName : SearchToolName;
			if (offersCards && (asksHistory || AsksForCard(askedQuestion)) && calledTool == cardSource
				&& ParseLieder(result.Result) is [{ } first, ..])
			{
				var komponenten = asksHistory
					? HistorySurfaces(result.Result, first.Id)
					: new[] { new { komponente = "LiedKarte", liedId = first.Id } };
				yield return new ChatResponseUpdate
				{
					MessageId = MessageId,
					Contents = [new FunctionCallContent($"call_{conversation.Count(HasFunctionCall) + 1}", A2uiCatalog.ToolName,
						new Dictionary<string, object?>
						{
							["komponenten"] = JsonDocument.Parse(
								JsonSerializer.Serialize(komponenten)).RootElement.Clone(),
						})],
				};
				yield break;
			}
			// After the surface: the answer is built from the lookup result.
			if (calledTool == A2uiCatalog.ToolName)
				result = conversation.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
					.LastOrDefault(r => CalledTool(conversation, r) is SearchToolName or DetailsToolName) ?? result;
			foreach (var update in AnswerFromToolResults(result, CalledTool(conversation, result) == DetailsToolName))
				yield return update;
			yield break;
		}

		var question = last?.Text ?? string.Empty;

		// "Wann haben wir dieses Lied gesungen?": find the Lied (named in the
		// question or cited in the thread) and read its history.
		if (AsksWhenSung(question))
		{
			var title = HistoryTitle(question, conversation);
			if (title is null)
			{
				yield return Text(WhichLiedAnswer);
				yield return Usage(question, WhichLiedAnswer);
				yield break;
			}
			yield return new ChatResponseUpdate
			{
				MessageId = MessageId,
				Contents = [new FunctionCallContent($"call_{conversation.Count(HasFunctionCall) + 1}", SearchToolName,
					new Dictionary<string, object?> { ["query"] = title, ["page"] = 1 })],
			};
			yield break;
		}

		// Questions about whole Auftritte: the chat has no Auftritt tools, so
		// the honest answer states the gap instead of calling a tool.
		if (ContainsAny(question, "wann wurde", "aufgeführt", "konzert"))
		{
			yield return Text(NoPerformanceDataAnswer);
			yield return Usage(question, NoPerformanceDataAnswer);
			yield break;
		}

		// Clearly off-topic requests receive the archive-scope refusal.
		if (ContainsAny(question, "wetter", "gedicht schreiben"))
		{
			yield return Text(OffTopicRefusal);
			yield return Usage(question, OffTopicRefusal);
			yield break;
		}

		var query = CatalogueQuery(question);
		var page = 1;
		if (IsNextPageRequest(question)
			&& conversation.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text is { } previous)
		{
			const string nextPagePrefix = "Soll ich Seite ";
			var start = previous.IndexOf(nextPagePrefix, StringComparison.Ordinal);
			if (start >= 0 && int.TryParse(previous[(start + nextPagePrefix.Length)..].Split(' ')[0], out var nextPage))
			{
				var originalQuestion = conversation.LastOrDefault(m => m.Role == ChatRole.User && !IsNextPageRequest(m.Text))?.Text;
				query = CatalogueQuery(originalQuestion ?? question);
				page = nextPage;
			}
		}
		// Otherwise route the question through the authorized retrieval tool;
		// the loop runs it and calls this client again with the results.
		yield return new ChatResponseUpdate
		{
			MessageId = MessageId,
			Contents = [new FunctionCallContent($"call_{conversation.Count(HasFunctionCall) + 1}", SearchToolName,
				new Dictionary<string, object?> { ["query"] = query.Length > 200 ? query[..200] : query, ["page"] = page })],
		};
	}

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}

	private static List<ChatResponseUpdate> AnswerFromToolResults(FunctionResultContent result, bool history)
	{
		var updates = new List<ChatResponseUpdate>();
		var lieder = ParseLieder(result.Result);
		if (lieder.Count == 0)
		{
			updates.Add(Text(UnknownAnswer));
			updates.Add(Usage(result.Result?.ToString() ?? string.Empty, UnknownAnswer));
			return updates;
		}
		var builder = new System.Text.StringBuilder();
		var embeddedInstruction = false;
		if (history && result.Result is string detailsJson)
		{
			var lied = lieder[0];
			builder.Append(HistorySentences(lied.Title, detailsJson));
			builder.Append(' ').Append(CitationMarkerPrefix).Append(lied.Title).Append(']');
			var historyAnswer = builder.ToString().TrimEnd();
			updates.Add(Text(historyAnswer));
			updates.Add(Usage(detailsJson, historyAnswer));
			return updates;
		}
		foreach (var lied in lieder)
		{
			builder.Append("Der Titel „").Append(lied.Title).Append("“ ist im Archiv verzeichnet.");
			builder.Append(' ').Append(CitationMarkerPrefix).Append(lied.Title).Append("] ");
			if (lied.LyricsExcerpt is not null && lied.LyricsExcerpt.Contains(EmbeddedInstructionMarker, StringComparison.OrdinalIgnoreCase))
				embeddedInstruction = true;
		}
		var answer = builder.ToString().TrimEnd();
		if (result.Result is string json)
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.TryGetProperty("page", out var page) && root.TryGetProperty("totalCount", out var total))
				answer += $" Seite {page.GetInt32()} von insgesamt {total.GetInt32()} veröffentlichten Treffern.";
			if (root.TryGetProperty("nextPage", out var next) && next.ValueKind == JsonValueKind.Number)
				answer += $" Es gibt weitere Lieder. Soll ich Seite {next.GetInt32()} zeigen?";
			if (root.TryGetProperty("limitReached", out var limit) && limit.GetBoolean())
				answer += " Die Seitengrenze ist erreicht. Bitte grenze die Suche ein.";
		}
		if (embeddedInstruction)
			answer += Environment.NewLine + DataNotInstructionSentence;
		updates.Add(Text(answer));
		updates.Add(Usage(result.Result?.ToString() ?? string.Empty, answer));
		return updates;
	}

	private static ChatResponseUpdate Text(string content)
		=> new(ChatRole.Assistant, content) { ResponseId = ResponseId, MessageId = MessageId };

	private static ChatResponseUpdate Usage(string input, string output)
		=> new()
		{
			MessageId = MessageId,
			Contents =
			[
				new UsageContent(new UsageDetails
				{
					InputTokenCount = input.Length == 0 ? 0 : (input.Length + 3) / 4,
					OutputTokenCount = output.Length == 0 ? 0 : (output.Length + 3) / 4,
				}),
			],
		};

	private static List<ScriptedLied> ParseLieder(object? result)
	{
		if (result is not string json || json.Length == 0)
			return [];
		try
		{
			using var document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("lieder", out var liederElement)
				|| liederElement.ValueKind is not JsonValueKind.Array)
				return [];
			var lieder = new List<ScriptedLied>();
			foreach (var element in liederElement.EnumerateArray())
			{
				lieder.Add(new ScriptedLied(
					element.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
					element.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
					element.TryGetProperty("lyricsExcerpt", out var excerpt) && excerpt.ValueKind is JsonValueKind.String
						? excerpt.GetString()
						: null));
			}
			return lieder;
		}
		catch (JsonException)
		{
			// Malformed tool output stays unknown instead of inventing content.
			return [];
		}
	}

	private static bool ContainsAny(string text, params string[] values)
	{
		foreach (var value in values)
		{
			if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>"Wann haben wir (dieses Lied) gesungen / wurde X aufgeführt?"</summary>
	private static bool AsksWhenSung(string question)
		=> ContainsAny(question, "wann") && ContainsAny(question, "gesungen", "aufgeführt", "aufgefuehrt");

	private static readonly HashSet<string> HistoryFillerWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"wann", "wurde", "wurden", "haben", "hat", "wir", "ihr", "das", "dieses", "diesem", "dieser", "lied", "gesungen",
		"aufgeführt", "aufgefuehrt", "schon", "mal", "eigentlich", "zuletzt", "denn",
	};

	/// <summary>
	/// The Lied a "wann gesungen" question is about: the title words left in
	/// the question, else the title the previous answer cited, else null.
	/// </summary>
	private static string? HistoryTitle(string question, List<AiChatMessage> conversation)
	{
		var named = string.Join(' ', question
			.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Select(word => word.Trim('?', '!', '.', ',', '„', '“', '"'))
			.Where(word => word.Length > 0 && !HistoryFillerWords.Contains(word)));
		if (named.Length > 0)
			return named;
		var previous = conversation.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
		if (previous is null)
			return null;
		var start = previous.IndexOf(CitationMarkerPrefix, StringComparison.Ordinal);
		if (start < 0)
			return null;
		var end = previous.IndexOf(']', start);
		return end < 0 ? null : previous[(start + CitationMarkerPrefix.Length)..end].Trim();
	}

	private static string CatalogueFold(string text) => Archive.Backend.Catalogue.CatalogueText.Fold(text);

	/// <summary>
	/// The cards a scripted history answer shows (in this order): the Lied,
	/// then each sung Auftritt as its card, and a Programm only when nothing
	/// is sung yet. Every id comes from the details result — the model never
	/// invents one. Bounded to the run's surface bound.
	/// </summary>
	private static object[] HistorySurfaces(object? detailsJson, string liedId)
	{
		var list = new List<object>();
		try
		{
			if (detailsJson is string json)
			{
				using var document = JsonDocument.Parse(json);
				var sung = AuftrittIds(document.RootElement, "gesungen").ToList();
				var geplant = AuftrittIds(document.RootElement, "geplant").ToList();
				list.Add(new { komponente = "LiedKarte", liedId });
				foreach (var auftrittId in sung)
					list.Add(new { komponente = "AuftrittKarte", auftrittId });
				if (sung.Count == 0 && geplant.Count > 0)
				{
					list.Add(new { komponente = "ProgrammListe", auftrittId = geplant[0] });
					foreach (var auftrittId in geplant.Skip(1))
						list.Add(new { komponente = "AuftrittKarte", auftrittId });
				}
			}
		}
		catch (JsonException)
		{
			// Malformed tool output shows nothing extra; the text answer stands.
			return [];
		}
		return [.. list.Take(A2uiCatalog.MaxSurfacesPerAnswer)];
	}

	private static IEnumerable<string> AuftrittIds(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var list) || list.ValueKind is not JsonValueKind.Array)
			yield break;
		foreach (var item in list.EnumerateArray())
		{
			if (item.TryGetProperty("auftrittId", out var id) && id.GetString() is { Length: > 0 } value)
				yield return value;
		}
	}


	/// <summary>The German answer sentences for a Lied's history, from the details result only.</summary>
	private static string HistorySentences(string title, string detailsJson)
	{
		var sentences = new List<string>();
		try
		{
			using var document = JsonDocument.Parse(detailsJson);
			foreach (var entry in Entries(document.RootElement, "gesungen"))
			{
				sentences.Add($"„{title}“ wurde beim Auftritt „{entry.Auftritt}“ ({entry.Datum}) gesungen.");
				if (entry.Zeitmarken.Count > 0)
					sentences.Add($"Dafür gibt es eine Zeitmarke in der Aufnahme „{entry.Zeitmarken[0].Label}“.");
				if (entry.Aufnahmen.Count > 0)
					sentences.Add("Dafür gibt es auch eine Aufnahme des Liedes.");
			}
			foreach (var entry in Entries(document.RootElement, "geplant"))
				sentences.Add($"„{title}“ steht im Programm des kommenden Auftritts „{entry.Auftritt}“ ({entry.Datum}).");
		}
		catch (JsonException)
		{
			// Malformed tool output stays unknown instead of inventing content.
		}
		return sentences.Count == 0
			? $"Zu „{title}“ sind im Archiv keine Auftritte verzeichnet."
			: string.Join(' ', sentences);

		static IEnumerable<HistoryEntry> Entries(JsonElement root, string name)
		{
			if (!root.TryGetProperty(name, out var list) || list.ValueKind is not JsonValueKind.Array)
				yield break;
			foreach (var item in list.EnumerateArray())
			{
				var marks = new List<(string Label, double? Start)>();
				if (item.TryGetProperty("zeitmarken", out var zeitmarken) && zeitmarken.ValueKind is JsonValueKind.Array)
				{
					foreach (var mark in zeitmarken.EnumerateArray())
					{
						if (mark.TryGetProperty("aufnahme", out var label) && label.GetString() is { Length: > 0 } labelValue)
							marks.Add((labelValue,
								mark.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number
									? start.GetDouble() : null));
					}
				}
				var files = new List<string>();
				if (item.TryGetProperty("aufnahmen", out var aufnahmen) && aufnahmen.ValueKind is JsonValueKind.Array)
					files.AddRange(aufnahmen.EnumerateArray()
						.Where(f => f.ValueKind is JsonValueKind.String)
						.Select(f => f.GetString() ?? string.Empty)
						.Where(f => f.Length > 0));
				yield return new HistoryEntry(
					item.TryGetProperty("auftritt", out var a) ? a.GetString() ?? string.Empty : string.Empty,
					item.TryGetProperty("datum", out var d) ? d.GetString() ?? string.Empty : string.Empty,
					marks, files);
			}
		}
	}

	private sealed record HistoryEntry(string Auftritt, string Datum, List<(string Label, double? Start)> Zeitmarken, List<string> Aufnahmen);

	private static bool IsNextPageRequest(string question) => ContainsAny(question, "weitere lieder", "nächste seite");

	private static string CatalogueQuery(string question) =>
		ContainsAny(question, "welche lieder gibt es", "welche lieder sind", "liste alle lieder", "zeige alle lieder")
			? string.Empty
			: AsksForCard(question) ? WithoutCardWords(question) : question;

	/// <summary>A question that asks for the Lied "als Karte" (ARC-022-4).</summary>
	private static bool AsksForCard(string question) => ContainsAny(question, " als karte");

	private static string WithoutCardWords(string question) => string.Join(' ',
		question.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Where(word => !word.Equals("zeige", StringComparison.OrdinalIgnoreCase)
				&& !word.Equals("mir", StringComparison.OrdinalIgnoreCase)
				&& !word.Equals("als", StringComparison.OrdinalIgnoreCase)
				&& !word.Equals("karte", StringComparison.OrdinalIgnoreCase)));

	/// <summary>The name of the tool a result answers, found through its call id.</summary>
	private static string? CalledTool(List<AiChatMessage> conversation, FunctionResultContent result)
		=> conversation.SelectMany(m => m.Contents.OfType<FunctionCallContent>())
			.LastOrDefault(call => call.CallId == result.CallId)?.Name;

	private static bool HasFunctionCall(AiChatMessage message)
		=> message.Contents.OfType<FunctionCallContent>().Any();

	private sealed record ScriptedLied(string Id, string Title, string? LyricsExcerpt);
}
