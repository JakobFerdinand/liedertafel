using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Lieder;

/// <summary>
/// Datei endpoints (#166): opening an upload session for a Lied and the
/// scoped read tickets. Renew, cancel and finalize are the shared
/// <c>/api/upload-sessions/{id}/...</c> routes in <see cref="AssetEndpoints"/>,
/// which hand Datei sessions to <see cref="DateiUploads"/>.
/// </summary>
public static class DateiEndpoints
{
	public static void MapDateiEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/lieder/{id}/dateien/upload-session", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			Guid id, CancellationToken token, CreateDateiUploadRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			return await DateiUploads.CreateForLiedAsync(
				db, time, options.Value, storage, decision!.AccountId, id, body, token);
		}).DisableAntiforgery();

		app.MapGet("/api/dateien/{id}/access", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			TimeProvider time, IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await LiedAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			// Dateien of an Auftritt become reachable with #171.
			var datei = await DateiQueries.Visible(db).AsNoTracking()
				.FirstOrDefaultAsync(d => d.Id == id && d.LiedId != null, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			var lifetime = options.Value.ReadTicketLifetime;
			string viewUrl;
			string downloadUrl;
			try
			{
				viewUrl = await storage.CreateReadTicketAsync(
					datei.BlobName, lifetime, asDownload: false, contentType: datei.ContentType, token);
				downloadUrl = await storage.CreateReadTicketAsync(
					datei.BlobName, lifetime, asDownload: true, contentType: datei.ContentType, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: AssetEndpoints.StorageFailureMessage);
			}
			return Results.Ok(new
			{
				dateiId = datei.Id,
				kind = datei.Kind,
				contentType = datei.ContentType,
				sizeBytes = datei.SizeBytes,
				fileName = datei.OriginalFileName,
				createdAt = datei.CreatedAt,
				viewUrl,
				downloadUrl,
				expiresAt = time.GetUtcNow() + lifetime,
			});
		});
	}
}
