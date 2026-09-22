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
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
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
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			// Missing and foreign threads answer identically: existence is never disclosed.
			var thread = await db.ChatThreads.AsNoTracking()
				.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == decision!.AccountId, token);
			if (thread is null)
				return Results.Problem(statusCode: 404, title: ThreadNotFoundMessage);
			var messages = await db.ChatMessages.AsNoTracking()
				.Where(m => m.ThreadId == id)
				.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
				.ToListAsync(token);
			if (messages.Count > ArchiveChatService.MessageHistoryLimit)
				messages = messages[^ArchiveChatService.MessageHistoryLimit..];
			return Results.Ok(new
			{
				threadId = id,
				messages = messages.Select(m => new { role = m.Role, content = m.Content, createdAt = m.CreatedAt }),
			});
		});
	}

	private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
		IAsyncEnumerable<BaseEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
	{
		await foreach (var @event in events.WithCancellation(token))
			yield return new SseItem<BaseEvent>(@event);
	}

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		// Cookie presence first for a useful signed-out state: revoked or
		// signed-out callers have no principal and get 401.
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		return (decision, null);
	}
}
