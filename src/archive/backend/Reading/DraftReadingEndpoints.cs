using Archive.Backend.Ai;
using Archive.Backend.Auth;
using Archive.Backend.Extraction;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>
/// #177: reading results for a Lied draft that does not exist on the server
/// yet. <c>POST /api/lied-lesungen</c> takes the PDF and the draft and request
/// identity the browser chose (<c>entwurfId</c>, <c>anfrageId</c>) and answers
/// with the suggestions and the same identity, so the browser can judge an
/// answer on arrival. Nothing is stored: a persisted draft (#178) keeps the
/// same request and answer shapes. Editor-only with antiforgery; a reached
/// budget (429) or a reading that is not set up (409) leaves the form to the
/// editor.
/// </summary>
public static class DraftReadingEndpoints
{
	public const string NotPdfMessage = "Das Lesen ist nur für PDF-Dateien möglich.";

	public const string BadIdentityMessage = "Entwurf und Anfrage fehlen oder sind ungültig.";

	public const string BudgetMessage = "Das Monatsbudget der KI ist erreicht. Das Formular lässt sich von Hand ausfüllen.";

	public const string FailedMessage = "Das Lesen ist gescheitert. Bitte erneut versuchen.";

	public static void MapDraftReadingEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/lied-lesungen", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			IOptions<ReadingOptions> options, DraftReader reader, ILoggerFactory loggers, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			var (decision, error) = await ExtractionEndpoints.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			if (!context.Request.HasFormContentType)
				return Results.Problem(statusCode: 400, title: BadIdentityMessage);
			var form = await context.Request.ReadFormAsync(token);
			if (!Guid.TryParse(form["entwurfId"], out var entwurfId)
				|| !int.TryParse(form["anfrageId"], out var anfrageId) || anfrageId < 1)
				return Results.Problem(statusCode: 400, title: BadIdentityMessage);
			if (!options.Value.Enabled)
				return Results.Problem(statusCode: 409, title: ReadingEndpoints.NotEnabledMessage);
			var file = form.Files["datei"];
			if (file is null || file.Length == 0 || file.Length > options.Value.MaxPdfBytes)
				return Results.Problem(statusCode: 422, title: NotPdfMessage);

			byte[] bytes;
			await using (var stream = file.OpenReadStream())
			using (var buffer = new MemoryStream())
			{
				await stream.CopyToAsync(buffer, token);
				bytes = buffer.ToArray();
			}
			if (!bytes.AsSpan().StartsWith("%PDF-"u8))
				return Results.Problem(statusCode: 422, title: NotPdfMessage);

			try
			{
				var suggestions = await reader.ReadAsync(bytes, decision!.AccountId, token);
				return Results.Ok(new
				{
					entwurfId,
					anfrageId,
					felder = suggestions.Select(s => new { feld = s.Field, wert = s.Value, sicher = s.Sure }),
				});
			}
			catch (DraftPdfUnreadableException exception)
			{
				return Results.Problem(statusCode: 422, title: exception.Reason);
			}
			catch (Exception exception) when (Find<AiBudgetExceededException>(exception))
			{
				return Results.Problem(statusCode: 429, title: BudgetMessage);
			}
			catch (Exception exception) when (Find<AiInputTooLargeException>(exception))
			{
				return Results.Problem(statusCode: 422, title: ReadingWorker.InputTooLargeReason);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Type only: the message can carry text of the scan.
				loggers.CreateLogger("Archive.Reading")
					.LogWarning("Draft reading failed ({ExceptionType})", exception.GetType().Name);
				return Results.Problem(statusCode: 502, title: FailedMessage);
			}
		}).DisableAntiforgery();
	}

	private static bool Find<T>(Exception exception) where T : Exception
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (current is T)
				return true;
		}
		return false;
	}
}
