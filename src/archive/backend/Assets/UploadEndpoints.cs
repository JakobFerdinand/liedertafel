using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Assets;

/// <summary>
/// The shared upload-session protocol (ARC-015/017) for every Datei: the
/// session is created through the Lied or Auftritt endpoint
/// (<see cref="Lieder.DateiUploads"/>); renewing a ticket, cancelling and
/// finalizing are the same for all of them and live here. Signed ticket URLs
/// exist only in JSON responses and are never logged.
/// </summary>
public static class UploadEndpoints
{
	public const string UploadNotFoundMessage = "Upload nicht gefunden.";

	public const string UploadOwnerMessage = "Nur die anlegendende Person kann den Upload abschließen.";

	public const string DescriptionTooLongMessage = "Die Beschreibung ist zu lang.";

	public const string UploadAbandonedMessage = "Upload wurde abgebrochen.";

	public const string UploadExpiredMessage = "Der Uploadzeitraum ist abgelaufen.";

	public const string UploadAlreadyFinalizedMessage = "Der Upload wurde bereits abgeschlossen.";

	public const string UploadCancelledMessage = "Der Upload wurde abgebrochen.";

	public const string UploadMismatchMessage = "Die Datei passt nicht zur bestehenden Uploadsitzung.";

	public const string FileNameTooLongMessage = "Der Dateiname ist zu lang.";

	public const string UploadMissingMessage = "Die Datei wurde noch nicht übertragen.";

	public const string UploadTooLargeMessage = "Die Datei ist zu groß.";

	public const string InvalidPdfMessage = "Die Datei ist kein gültiges PDF.";

	public const string InvalidAudioMessage = "Die Datei ist keine gültige Audiodatei.";

	public const string EmptyRecordingMessage = "Die Datei ist leer und kann nicht als Aufnahme gespeichert werden.";

	public const string InvalidPlaybackMessage =
		"Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme.";

	public const string StorageFailureMessage = "Speicherdienst nicht erreichbar.";

	/// <summary>ARC-017: storage default for block-list commits without an explicit blob content type.</summary>
	public const string StorageDefaultContentType = "application/octet-stream";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string PdfContentType = "application/pdf";

	public const string Mp3ContentType = "audio/mpeg";

	public const string MidiContentType = "audio/midi";

	public const string XMidiContentType = "audio/x-midi";

	public const string JpegContentType = "image/jpeg";

	public const string PngContentType = "image/png";

	public const string WebpContentType = "image/webp";

	public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/upload-sessions/{id}/renew", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, token);
			if (session is null)
				return Results.Problem(statusCode: 404, title: UploadNotFoundMessage);
			if (session.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: UploadOwnerMessage);
			if (session.State == PendingUploadState.Finalized)
				return Results.Problem(statusCode: 409, title: UploadAlreadyFinalizedMessage);
			if (session.State != PendingUploadState.Pending)
				return Results.Problem(statusCode: 409, title: UploadAbandonedMessage);
			var storageOptions = options.Value;
			session.UploadTicketExpiresAt = time.GetUtcNow() + storageOptions.UploadSessionLifetime;
			await db.SaveChangesAsync(token);
			string uploadUrl;
			try
			{
				uploadUrl = await storage.CreateUploadTicketAsync(
					session.BlobName, storageOptions.UploadSessionLifetime, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: StorageFailureMessage);
			}
			return Results.Ok(new
			{
				uploadSessionId = session.Id,
				blobName = session.BlobName,
				uploadUrl,
				expiresAt = session.UploadTicketExpiresAt,
				maxBytes = session.MaxSizeBytes,
				blockBytes = storageOptions.UploadBlockBytes,
			});
		}).DisableAntiforgery();

		/// <summary>
		/// ARC-017: editors abandon a stale transfer explicitly; cancellation
		/// is terminal, releases the pending blob immediately and keeps the
		/// session row so late renew/finalize calls report the cancelled state.
		/// </summary>
		app.MapDelete("/api/upload-sessions/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IAssetStorageAdapter storage, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, token);
			if (session is null)
				return Results.Problem(statusCode: 404, title: UploadNotFoundMessage);
			if (session.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: UploadOwnerMessage);
			if (session.State is PendingUploadState.Finalized or PendingUploadState.Abandoned)
				return Results.Problem(statusCode: 409, title: UploadAbandonedMessage);
			if (session.State == PendingUploadState.Cancelled)
				return Results.Problem(statusCode: 409, title: UploadCancelledMessage);
			session.State = PendingUploadState.Cancelled;
			await db.SaveChangesAsync(token);
			await DeletePendingBestEffortAsync(storage, session.BlobName, token);
			return Results.NoContent();
		}).DisableAntiforgery();


		app.MapPost("/api/upload-sessions/{id}/finalize", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			IServiceProvider services,
			Guid id, CancellationToken token,
			FinalizeUploadRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, token);
			if (session is null)
				return Results.Problem(statusCode: 404, title: UploadNotFoundMessage);
			if (session.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: UploadOwnerMessage);
			return await Lieder.DateiUploads.FinalizeAsync(db, time, options.Value, storage,
				services.GetServices<Lieder.IDateiFinalizedHook>(), session, decision.AccountId, body, token);
		}).DisableAntiforgery();
	}

	private static async Task DeletePendingBestEffortAsync(
		IAssetStorageAdapter storage, string blobName, CancellationToken token)
	{
		try
		{
			await storage.DeleteAsync(blobName, token);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!IsEditor(decision))
			return (null, Results.Problem(statusCode: 403, title: "Keine Berechtigung für das Liedverzeichnis."));
		return (decision, null);
	}
}

/// <summary>ARC-017: optional observed file identity at finalization, compared against the declaration.</summary>
public sealed record FinalizeUploadRequest(long? SizeBytes, string? FileName);
