using System.Runtime.CompilerServices;
using System.Text.Json;
using Archive.Backend.Catalogue;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;

/// <summary>One citation candidate collected from this run's catalogue tool results.</summary>
internal sealed record ToolSong(string Id, string Title);

/// <summary>What one chat run learns while it runs; shared by the run's pipeline parts.</summary>
internal sealed class ChatRunState
{
	/// <summary>Records the authorized tools returned in this run: the only citable sources.</summary>
	public List<ToolSong> ToolSongs { get; } = [];

	/// <summary>Model calls of this run; a repeated first call counts once.</summary>
	public int ModelCalls { get; set; }

	/// <summary>The model still asked for tools after the run's last allowed model call.</summary>
	public bool ToolLimitReached { get; set; }

	public bool IsCitable(string title)
	{
		var folded = CatalogueText.Fold(title);
		return ToolSongs.Any(s => CatalogueText.Fold(s.Title) == folded);
	}

	/// <summary>
	/// Collects the (id, title) pairs a catalogue tool result carries; these
	/// are the citation candidates for this run's answer.
	/// </summary>
	public void CollectToolSongs(string resultJson)
	{
		try
		{
			using var document = JsonDocument.Parse(resultJson);
			if (document.RootElement.ValueKind is not JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("songs", out var songsElement)
				|| songsElement.ValueKind is not JsonValueKind.Array)
				return;
			foreach (var element in songsElement.EnumerateArray())
			{
				var id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
				var title = element.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
				if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
					ToolSongs.Add(new ToolSong(id, title));
			}
		}
		catch (JsonException)
		{
			// Malformed tool output contributes no citation candidates.
		}
	}
}

/// <summary>
/// Validates inline <c>[Quelle: …]</c> markers of every model response against
/// this run's tool results before the text leaves the tool loop, so the
/// stream, the agent's response and the persisted history all carry the same
/// filtered answer whichever provider sits below.
/// </summary>
internal sealed class CitationFilteringChatClient(IChatClient inner, ChatRunState state) : DelegatingChatClient(inner)
{
	public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// Hold only an unfinished citation marker across provider chunks. Never
		// stream or persist a source label that this run did not retrieve.
		var filter = new CitationTextFilter(state.IsCitable);
		ChatResponseUpdate? lastText = null;
		await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
		{
			for (var i = 0; i < update.Contents.Count; i++)
			{
				if (update.Contents[i] is TextContent text)
				{
					update.Contents[i] = new TextContent(filter.Append(text.Text));
					lastText = update;
				}
			}
			yield return update;
		}
		var trailing = filter.Complete();
		if (trailing.Length > 0)
		{
			yield return new ChatResponseUpdate(ChatRole.Assistant, trailing)
			{
				MessageId = lastText?.MessageId,
				ResponseId = lastText?.ResponseId,
			};
		}
	}
}

/// <summary>
/// The bounds of a chat run's model calls (ARC-021): a run makes at most
/// <c>maxModelCalls</c> calls, the first update of every call must arrive
/// within the no-token window, and the run's first call is repeated once
/// when the provider fails before any update arrived. Each attempt passes
/// the budget below, so a repeated call is charged too.
/// </summary>
internal sealed class ModelCallBoundsChatClient(
	IChatClient inner, ChatRunState state, int maxModelCalls, TimeSpan noTokenWindow, ILogger logger) : DelegatingChatClient(inner)
{
	public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// The tool loop stops itself at the bound (see InvokeToolAsync); a
		// further call would be a defect and never reaches the provider.
		if (state.ModelCalls >= maxModelCalls)
		{
			state.ToolLimitReached = true;
			throw new InvalidOperationException("The chat run exceeded its model-call bound.");
		}
		var firstCallOfRun = state.ModelCalls == 0;
		state.ModelCalls++;
		// The agent stamps its name on the updates it yields, and the tool
		// loop turns those updates into the next call's messages. The
		// provider would receive it as a participant name on the assistant's
		// tool-call message, which the ARC-022 request never had: the name
		// belongs in telemetry, the conversation is sent as it was.
		var list = messages.Select(m =>
		{
			if (m.AuthorName is null)
				return m;
			var copy = m.Clone();
			copy.AuthorName = null;
			return copy;
		}).ToList();
		CancellationTokenSource noToken;
		IAsyncEnumerator<ChatResponseUpdate> updates;
		bool hasUpdate;
		for (var attempt = 1; ; attempt++)
		{
			noToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			noToken.CancelAfter(noTokenWindow);
			updates = base.GetStreamingResponseAsync(list, options, noToken.Token).GetAsyncEnumerator(noToken.Token);
			try
			{
				hasUpdate = await updates.MoveNextAsync();
				break;
			}
			catch (Exception ex)
			{
				await updates.DisposeAsync();
				noToken.Dispose();
				if (ex is OperationCanceledException or Ai.AiBudgetExceededException or Ai.AiBudgetUnavailableException
					|| !firstCallOfRun || attempt > 1)
					throw;
				// Exception type only: provider messages may carry content or credentials.
				logger.LogError(
					"Archiv-Chat: Modellanruf vor dem ersten Zeichen fehlgeschlagen ({ExceptionType}); einmaliger Wiederholungsversuch.",
					ex.GetType().Name);
			}
		}
		try
		{
			// The first update arrived: the window is disarmed for the rest of this call.
			noToken.CancelAfter(Timeout.InfiniteTimeSpan);
			while (hasUpdate)
			{
				yield return updates.Current;
				hasUpdate = await updates.MoveNextAsync();
			}
		}
		finally
		{
			await updates.DisposeAsync();
			noToken.Dispose();
		}
	}
}
