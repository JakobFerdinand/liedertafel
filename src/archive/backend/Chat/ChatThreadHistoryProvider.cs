using System.Text;
using Archive.Backend.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

/// <summary>
/// The chat agent's history (ARC-022-3): the existing <c>chat_threads</c> /
/// <c>chat_messages</c> tables behind an Agent Framework history provider.
/// It supplies the last persisted turns of the member's thread as the only
/// grounding history and stores each run's question and final answer. Only
/// text turns are kept: tool calls and tool results are never persisted, and
/// a run that fails or stops at the tool bound stores the question alone.
/// One instance serves one run on the request's context.
/// </summary>
internal sealed class ChatThreadHistoryProvider(
	ArchiveDbContext db, ChatThread thread, TimeProvider time, ChatRunState state, int maxAnswerChars)
	: ChatHistoryProvider
{
	protected override async ValueTask<IEnumerable<AiChatMessage>> ProvideChatHistoryAsync(
		InvokingContext context, CancellationToken cancellationToken = default)
	{
		var history = await ArchiveChatService.RecentMessages(db, thread.Id, ArchiveChatService.MessageHistoryLimit, cancellationToken);
		return history
			.Where(m => m.Role is "user" or "assistant")
			.Select(m => new AiChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Content))
			.ToList();
	}

	protected override async ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
	{
		if (context.InvokeException is null)
		{
			await base.InvokedCoreAsync(context, cancellationToken);
			return;
		}
		// A failed, timed-out or budget-stopped run keeps the question only.
		var asked = context.RequestMessages
			.Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory);
		await StoreTurnAsync(asked, answer: null);
	}

	protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
	{
		var responses = context.ResponseMessages?.ToList() ?? [];
		// A run that stopped at the model-call bound while the model still
		// asked for tools (ChatRunState.ToolLimitReached) has no answer to keep.
		var answer = new StringBuilder();
		foreach (var message in responses.Where(m => m.Role == ChatRole.Assistant))
			answer.Append(message.Text);
		await StoreTurnAsync(context.RequestMessages, state.ToolLimitReached ? null : answer.ToString());
	}

	private async Task StoreTurnAsync(IEnumerable<AiChatMessage> requestMessages, string? answer)
	{
		var now = time.GetUtcNow();
		thread.UpdatedAt = now;
		foreach (var question in requestMessages.Where(m => m.Role == ChatRole.User))
			db.ChatMessages.Add(new ChatMessage { ThreadId = thread.Id, Role = "user", Content = question.Text, CreatedAt = now });
		if (answer is { Length: > 0 })
		{
			if (answer.Length > maxAnswerChars)
				answer = answer[..maxAnswerChars];
			// One tick later: the ordering key is (CreatedAt, Id) and Guid v7
			// ids are not monotonic within a millisecond, so the question and
			// the answer must not share one timestamp — otherwise the
			// authoritative history order would be a coin flip.
			db.ChatMessages.Add(new ChatMessage { ThreadId = thread.Id, Role = "assistant", Content = answer, CreatedAt = now.AddTicks(1) });
		}
		// A new thread and its turns commit as one batch; the request may
		// already be cancelled, the turn is stored regardless.
		await db.SaveChangesAsync(CancellationToken.None);
		// The trim is its own bounded batch and runs after the turns.
		await TrimThreadAsync();
		await db.SaveChangesAsync(CancellationToken.None);
	}

	private async Task TrimThreadAsync()
	{
		var keep = await ArchiveChatService.RecentMessages(db, thread.Id, ArchiveChatService.MessageHistoryLimit, CancellationToken.None);
		if (keep.Count < ArchiveChatService.MessageHistoryLimit)
			return;
		var keepIds = keep.Select(m => m.Id).ToHashSet();
		var messages = db.ChatMessages.Where(m => m.ThreadId == thread.Id).AsEnumerable();
		db.ChatMessages.RemoveRange(messages.Where(m => !keepIds.Contains(m.Id)));
	}
}
