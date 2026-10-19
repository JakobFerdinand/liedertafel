using Archive.Backend.Ai;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>
/// #177: reading results for a Lied draft. <c>POST /api/lied-lesungen</c> takes
/// the PDF and the draft and request identity the browser chose
/// (<c>entwurfId</c>, <c>anfrageId</c>) and answers with the suggestions and
/// the same identity, so the browser can judge an answer on arrival. Editor-only
/// with antiforgery; a reached budget (429) or a reading that is not set up
/// (409) leaves the form to the editor.
///
/// #178: when the request also names a <c>dateiId</c> of the editor's own
/// persisted draft (<see cref="LiedEntwurf"/>), the outcome is stored for that
/// Datei as well, and the reading no longer depends on the request, so it
/// finishes if the tab was closed meanwhile. The stored outcome only lands
/// while the draft, the Datei and this very request still stand. Without a
/// persisted draft the reading stays stateless, exactly as in #177.
/// </summary>
public static class DraftReadingEndpoints
{
	public const string NotPdfMessage = "Das Lesen ist nur für PDF-Dateien möglich.";

	public const string BadIdentityMessage = "Entwurf und Anfrage fehlen oder sind ungültig.";

	public const string BudgetMessage = "Das Monatsbudget der KI ist erreicht. Das Formular lässt sich von Hand ausfüllen.";

	public const string SupersededMessage = "Die Anfrage ist überholt.";

	public const string DraftGoneMessage = "Der Entwurf wurde inzwischen verworfen.";

	public const string FailedMessage = "Das Lesen ist gescheitert. Bitte erneut versuchen.";

	/// <summary>A reading of a persisted draft is not tied to the request, so it needs its own limit.</summary>
	private static readonly TimeSpan ReadTimeLimit = TimeSpan.FromMinutes(3);

	public static void MapDraftReadingEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/lied-lesungen", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			IOptions<ReadingOptions> options, DraftReader reader, ArchiveDbContext db, TimeProvider time,
			ILoggerFactory loggers, CancellationToken token) =>
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

			var persisted = false;
			var dateiId = Guid.Empty;
			if (Guid.TryParse(form["dateiId"], out dateiId))
			{
				switch (await Claim(db, time, decision!.AccountId, entwurfId, dateiId, anfrageId))
				{
					case Target.Superseded:
						return Results.Problem(statusCode: 400, title: SupersededMessage);
					case Target.Claimed:
						persisted = true;
						break;
				}
			}
			using var limit = new CancellationTokenSource(ReadTimeLimit);
			var readToken = persisted ? limit.Token : token;

			Outcome outcome;
			try
			{
				var suggestions = await reader.ReadAsync(bytes, decision!.AccountId, readToken);
				outcome = new Outcome(suggestions, null, null, true);
			}
			catch (DraftPdfUnreadableException exception)
			{
				outcome = Outcome.Failed(422, exception.Reason, available: true);
			}
			catch (Exception exception) when (Find<AiBudgetExceededException>(exception))
			{
				outcome = Outcome.Failed(429, BudgetMessage, available: false);
			}
			catch (Exception exception) when (Find<AiInputTooLargeException>(exception))
			{
				outcome = Outcome.Failed(422, ReadingWorker.InputTooLargeReason, available: true);
			}
			catch (Exception exception) when (exception is not OperationCanceledException || persisted)
			{
				// Type only: the message can carry text of the scan.
				loggers.CreateLogger("Archive.Reading")
					.LogWarning("Draft reading failed ({ExceptionType})", exception.GetType().Name);
				outcome = Outcome.Failed(502, FailedMessage, available: true);
			}

			if (persisted && !await Store(db, time, entwurfId, dateiId, anfrageId, outcome))
				return Results.Problem(statusCode: 404, title: DraftGoneMessage);
			if (outcome.Status is { } status)
				return Results.Problem(statusCode: status, title: outcome.Title);
			return Results.Ok(new
			{
				entwurfId,
				anfrageId,
				felder = outcome.Suggestions!.Select(s => new { feld = s.Field, wert = s.Value, sicher = s.Sure }),
			});
		}).DisableAntiforgery();
	}

	private enum Target { Stateless, Superseded, Claimed }

	private sealed record Outcome(IReadOnlyList<DraftSuggestion>? Suggestions, int? Status, string? Title, bool Available)
	{
		public static Outcome Failed(int status, string title, bool available) => new(null, status, title, available);
	}

	/// <summary>
	/// Marks the request as the reading of a Datei of the editor's own persisted
	/// draft. A request that is not newer than the stored one is superseded.
	/// </summary>
	private static async Task<Target> Claim(
		ArchiveDbContext db, TimeProvider time, Guid accountId, Guid entwurfId, Guid dateiId, int anfrageId)
	{
		var datei = await db.LiedEntwurfDateien
			.Include(d => d.Lesung)
			.FirstOrDefaultAsync(d => d.EntwurfId == entwurfId && d.Id == dateiId
				&& d.Entwurf!.OwnerAccountId == accountId, CancellationToken.None);
		if (datei is null)
			return Target.Stateless;
		var now = time.GetUtcNow();
		if (datei.Lesung is { } existing)
		{
			if (existing.AnfrageId >= anfrageId)
				return Target.Superseded;
			existing.AnfrageId = anfrageId;
			existing.Status = "liest";
			existing.Meldung = string.Empty;
			existing.Felder = "[]";
			existing.UpdatedAt = now;
		}
		else
		{
			datei.Lesung = new LiedEntwurfLesung
			{
				EntwurfId = entwurfId,
				DateiId = dateiId,
				AnfrageId = anfrageId,
				Status = "liest",
				UpdatedAt = now,
			};
		}
		await db.SaveChangesAsync(CancellationToken.None);
		return Target.Claimed;
	}

	/// <summary>
	/// Writes the outcome only while this very request still stands: the draft
	/// and the Datei exist and no newer request replaced it. Otherwise the answer
	/// arrived late and changes nothing.
	/// </summary>
	private static async Task<bool> Store(
		ArchiveDbContext db, TimeProvider time, Guid entwurfId, Guid dateiId, int anfrageId, Outcome outcome)
	{
		db.ChangeTracker.Clear();
		var lesung = await db.LiedEntwurfLesungen
			.FirstOrDefaultAsync(l => l.EntwurfId == entwurfId && l.DateiId == dateiId
				&& l.AnfrageId == anfrageId && l.Status == "liest", CancellationToken.None);
		if (lesung is null)
			return false;
		lesung.UpdatedAt = time.GetUtcNow();
		if (outcome.Status is not null)
		{
			var title = outcome.Title ?? FailedMessage;
			lesung.Status = outcome.Available ? "fehlgeschlagen" : "nicht-verfuegbar";
			lesung.Meldung = title.Length > 500 ? title[..500] : title;
			lesung.Felder = "[]";
		}
		else
		{
			lesung.Status = "gelesen";
			lesung.Meldung = string.Empty;
			lesung.Felder = LiedEntwurfEndpoints.SerializeFelder(
				outcome.Suggestions!.Select(s => new EntwurfVorschlag(s.Field, s.Value, s.Sure)));
		}
		await db.SaveChangesAsync(CancellationToken.None);
		return true;
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
