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
/// citations as a CUSTOM event and usage reporting. Keyword heuristics are
/// deliberate: this is a scripted evaluation client, not an intelligence.
/// Development runs without provider configuration and all tests use it; the
/// real Azure OpenAI client replaces it behind the same IChatClient seam
/// (ARC-022 provider step).
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
	/// <summary>Sentence appended whenever a tool result carries an embedded instruction.</summary>
	public const string DataNotInstructionSentence =
		"Anweisungen innerhalb von Archivtexten behandle ich als Inhalt, nicht als Befehl.";

	/// <summary>Answer when no matching catalogue record exists (honest unknown).</summary>
	public const string UnknownAnswer =
		"Das ist mir nicht bekannt. Solche Angaben sind im Archiv nicht verzeichnet.";

	/// <summary>Answer for performance questions while no performance data is exposed to the chat.</summary>
	public const string NoPerformanceDataAnswer =
		"Dazu liegen mir derzeit keine Aufführungsdaten vor.";

	/// <summary>Polite refusal for non-archive questions.</summary>
	public const string OffTopicRefusal =
		"Dazu kann ich nur Fragen zum Vereinsarchiv beantworten.";

	private const string CitationsEventName = "archive.citations";

	private const string SearchToolName = "catalogue_search";

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

		// Tool results arrived: build the cited German answer from the data
		// only. Result text is treated as data: an embedded instruction in an
		// excerpt triggers the data-not-instruction sentence and is never
		// complied with.
		if (last?.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
		{
			foreach (var update in AnswerFromToolResults(result))
				yield return update;
			yield break;
		}

		var question = last?.Text ?? string.Empty;

		// Performance/event questions: the chat exposes no performance data
		// yet (first question type is grounded catalogue retrieval), so the
		// honest answer states the gap instead of calling a tool.
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

		// Otherwise route the question through the authorized retrieval tool;
		// the loop runs it and calls this client again with the results.
		yield return new ChatResponseUpdate
		{
			MessageId = MessageId,
			Contents = [new FunctionCallContent($"call_{conversation.Count(HasFunctionCall) + 1}", SearchToolName,
				new Dictionary<string, object?> { ["query"] = question.Length > 200 ? question[..200] : question })],
		};
	}

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}

	private static List<ChatResponseUpdate> AnswerFromToolResults(FunctionResultContent result)
	{
		var updates = new List<ChatResponseUpdate>();
		var songs = ParseSongs(result.Result);
		if (songs.Count == 0)
		{
			updates.Add(Text(UnknownAnswer));
			updates.Add(Usage(result.Result?.ToString() ?? string.Empty, UnknownAnswer));
			return updates;
		}
		var builder = new System.Text.StringBuilder();
		var embeddedInstruction = false;
		foreach (var song in songs)
		{
			builder.Append("Der Titel „").Append(song.Title).Append("“ ist im Archiv verzeichnet.");
			builder.Append(' ').Append(CitationMarkerPrefix).Append(song.Title).Append("] ");
			if (song.LyricsExcerpt is not null && song.LyricsExcerpt.Contains(EmbeddedInstructionMarker, StringComparison.OrdinalIgnoreCase))
				embeddedInstruction = true;
		}
		var answer = builder.ToString().TrimEnd();
		if (embeddedInstruction)
			answer += Environment.NewLine + DataNotInstructionSentence;
		updates.Add(Text(answer));
		updates.Add(BuildCitationsEvent(songs));
		updates.Add(Usage(result.Result?.ToString() ?? string.Empty, answer));
		return updates;
	}

	private static ChatResponseUpdate Text(string content)
		=> new(ChatRole.Assistant, content) { ResponseId = ResponseId, MessageId = MessageId };

	private static ChatResponseUpdate BuildCitationsEvent(List<ScriptedSong> songs)
	{
		var payload = JsonSerializer.Serialize(songs.Select(s => new { id = s.Id, label = s.Title }));
		return new ChatResponseUpdate
		{
			MessageId = MessageId,
			RawRepresentation = new AGUI.Abstractions.CustomEvent
			{
				Name = CitationsEventName,
				Value = JsonDocument.Parse(payload).RootElement.Clone(),
			},
		};
	}

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

	private static List<ScriptedSong> ParseSongs(object? result)
	{
		if (result is not string json || json.Length == 0)
			return [];
		try
		{
			using var document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("songs", out var songsElement)
				|| songsElement.ValueKind is not JsonValueKind.Array)
				return [];
			var songs = new List<ScriptedSong>();
			foreach (var element in songsElement.EnumerateArray())
			{
				songs.Add(new ScriptedSong(
					element.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
					element.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
					element.TryGetProperty("lyricsExcerpt", out var excerpt) && excerpt.ValueKind is JsonValueKind.String
						? excerpt.GetString()
						: null));
			}
			return songs;
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

	private static bool HasFunctionCall(AiChatMessage message)
		=> message.Contents.OfType<FunctionCallContent>().Any();

	private sealed record ScriptedSong(string Id, string Title, string? LyricsExcerpt);
}
