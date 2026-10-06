namespace Archive.Backend.Chat;

/// <summary>
/// A persisted chat conversation (ARC-022). Owned by the asking member via a
/// plain account id column like the catalogue attribution pattern
/// (<see cref="Catalogue.Song.PublishedByAccountId"/>); no navigation to
/// Identity, ownership is validated on every request. Bounded to the last
/// <see cref="ArchiveChatService.MessageHistoryLimit"/> messages.
/// </summary>
public sealed class ChatThread
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid AccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>
/// One stored turn ("user" or "assistant"). Persisted server-side after each
/// run; the server database is authoritative for history so client-provided
/// older messages can never tamper the grounding context.
/// </summary>
public sealed class ChatMessage
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public Guid ThreadId { get; set; }

	/// <summary>Either "user" or "assistant".</summary>
	public required string Role { get; set; }

	public required string Content { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}
