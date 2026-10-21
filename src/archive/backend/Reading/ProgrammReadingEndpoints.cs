using Archive.Backend.Ai;
using Archive.Backend.Auth;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>
/// #131: the reading of a programme scan or photo for a new Auftritt.
/// <c>POST /api/auftritt-lesungen</c> takes the file and the draft and request
/// identity the browser chose (<c>entwurfId</c>, <c>anfrageId</c>) and answers
/// with the form suggestions, the Programm entries matched against the
/// catalogue and the same identity, so the browser can judge an answer on
/// arrival. Like the Lied draft reading it runs the model inside the request
/// through <see cref="ProgrammReader"/> and the AI gateway, and it writes
/// nothing: the editor's form takes the suggestions over, "Speichern" creates
/// the Auftritt. Editor-only with antiforgery; a reached budget (429) or a
/// reading that is not set up (409) leaves the form to the editor.
/// </summary>
public static class ProgrammReadingEndpoints
{
	public const string NotReadableMessage = "Das Lesen ist nur für PDF-Dateien und Fotos (JPEG, PNG, WebP) möglich.";

	public const string BadIdentityMessage = "Entwurf und Anfrage fehlen oder sind ungültig.";

	public const string BudgetMessage = "Das Monatsbudget der KI ist erreicht. Das Formular lässt sich von Hand ausfüllen.";

	public const string FailedMessage = "Das Lesen ist gescheitert. Bitte erneut versuchen.";

	public static void MapProgrammReadingEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/auftritt-lesungen", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			IOptions<ReadingOptions> options, ProgrammReader reader, ILoggerFactory loggers, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			if (await LiedAccess.ValidateAntiforgeryAsync(antiforgery, context) is { } csrf)
				return csrf;
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
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
				return Results.Problem(statusCode: 422, title: NotReadableMessage);

			byte[] bytes;
			await using (var stream = file.OpenReadStream())
			using (var buffer = new MemoryStream())
			{
				await stream.CopyToAsync(buffer, token);
				bytes = buffer.ToArray();
			}
			if (!ProgrammReader.IsPdf(bytes) && !ProgrammReader.IsPhoto(bytes))
				return Results.Problem(statusCode: 422, title: NotReadableMessage);

			ProgrammLesung lesung;
			try
			{
				lesung = await reader.ReadAsync(bytes, decision!.AccountId, token);
			}
			catch (ProgrammFileUnreadableException exception)
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
					.LogWarning("Programme reading failed ({ExceptionType})", exception.GetType().Name);
				return Results.Problem(statusCode: 502, title: FailedMessage);
			}

			return Results.Ok(new
			{
				entwurfId,
				anfrageId,
				felder = lesung.Felder.Select(f => new { feld = f.Field, wert = f.Value, sicher = f.Sure }),
				programm = lesung.Programm.Select(p => new
				{
					text = p.Text,
					sicher = p.Sure,
					lied = p.Match.Lied is { } lied ? new { id = lied.Id, titel = lied.Title, sicher = p.Match.Sure } : null,
					kandidaten = p.Match.Candidates.Select(k => new { id = k.Id, titel = k.Title }),
				}),
			});
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
