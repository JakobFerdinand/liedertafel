using System.Net.ServerSentEvents;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Chat;

/// <summary>
/// Member chat endpoints (ARC-022) over the AG-UI protocol: the POST run
/// endpoint validates membership, CSRF and availability, deserializes the
/// AG-UI <see cref="RunAgentInput"/> and streams the bounded agent run as
/// server-sent AG-UI events; the GET history endpoint restores a thread for
/// the owning member only. Ownership is checked on every request; a foreign
/// or unknown thread answers 404/403 without leaking existence.
/// </summary>
public static class ChatEndpoints
{
	public const string UnavailableMessage = "Der Archiv-Chat ist derzeit nicht verfügbar.";

	public const string InvalidRequestMessage = "Ungültige Anfrage.";

	public const string ThreadNotFoundMessage = "Chatverlauf nicht gefunden.";

	public const string OwnershipMessage = "Keine Berechtigung für diesen Chatverlauf.";

	public const string QuestionTooLongMessage = "Die Frage ist zu lang.";

	public static void MapChatEndpoints(this WebApplication app)
	{
		app.MapPost("/api/chat", async (HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveChatService chat, CancellationToken token, IOptionsSnapshot<ChatOptions> chatOptions) =>
		{
			var (decision, error) = await ArchiveAccess.RequireMemberAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (!chatOptions.Value.IsAvailable)
				return Results.Problem(statusCode: 503, title: UnavailableMessage);

			// The AG-UI request body is read raw and deserialized with the AG-UI
			// serializer options; default model binding would not use the
			// protocol's polymorphic message formats.
			RunAgentInput? input;
			try
			{
				using var reader = new StreamReader(context.Request.Body);
				var body = await reader.ReadToEndAsync(token);
				input = JsonSerializer.Deserialize<RunAgentInput>(body, ArchiveChatService.AguiJsonOptions);
			}
			catch (JsonException)
			{
				return Results.Problem(statusCode: 400, title: InvalidRequestMessage);
			}
			if (input is null || input.Messages.Count == 0)
				return Results.Problem(statusCode: 400, title: InvalidRequestMessage);

			var run = await chat.RunAsync(decision!, input, token);
			if (run.Error is not null)
				return run.Error;
			return TypedResults.ServerSentEvents(WrapAsSseItems(run.Events!, token));
		}).DisableAntiforgery();

		app.MapGet("/api/chat/thread/{id:guid}", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			var (decision, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			// Missing and foreign threads answer identically: existence is never disclosed.
			var thread = await db.ChatThreads.AsNoTracking()
				.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == decision!.AccountId, token);
			if (thread is null)
				return Results.Problem(statusCode: 404, title: ThreadNotFoundMessage);
			var messages = await ArchiveChatService.RecentMessages(db, id, ArchiveChatService.MessageHistoryLimit, token);
			// A stored card is never trusted as stored: it is rebuilt from the
			// records it names, so one a member may no longer see is gone.
			var restored = new List<object>();
			foreach (var m in messages)
			{
				var surfaces = m.A2uiJson is null ? null : await A2uiCatalog.RecheckAsync(db, m.A2uiJson, token);
				// An answer that was only a card is nothing once its card is gone.
				if (m.Content.Length == 0 && surfaces is not { Count: > 0 })
					continue;
				restored.Add(new { role = m.Role, content = m.Content, createdAt = m.CreatedAt, a2ui = surfaces is { Count: > 0 } ? surfaces : null });
			}
			return Results.Ok(new { threadId = id, messages = restored });
		});
	}

	/// <summary>
	/// The wire form of AG-UI events: properties that are null are left out.
	/// The server library writes protocol fields it does not use as explicit
	/// nulls (<c>parentRunId</c>, <c>usage[].model</c>, …), which a strict
	/// AG-UI client (<c>@ag-ui/client</c>, ARC-022-4) refuses as a schema
	/// violation; the protocol means "absent" by them.
	/// </summary>
	private static readonly JsonSerializerOptions WireJson = new(ArchiveChatService.AguiJsonOptions)
	{
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private static async IAsyncEnumerable<SseItem<JsonElement>> WrapAsSseItems(
		IAsyncEnumerable<BaseEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
	{
		await foreach (var @event in events.WithCancellation(token))
			yield return new SseItem<JsonElement>(JsonSerializer.SerializeToElement<BaseEvent>(@event, WireJson));
	}
}
