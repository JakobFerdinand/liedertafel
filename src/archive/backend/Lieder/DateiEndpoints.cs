using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Videos;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Lieder;

/// <summary>
/// Datei endpoints (#166): opening an upload session for a Lied and the
/// scoped read tickets. Renew, cancel and finalize are the shared
/// <c>/api/upload-sessions/{id}/...</c> routes in <see cref="UploadEndpoints"/>,
/// which hand Datei sessions to <see cref="DateiUploads"/>.
/// </summary>
public sealed record PatchDateiRequest(string? Description, uint? RowVersion);

public static class DateiEndpoints
{
	public static void MapDateiEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/lieder/{id}/dateien/upload-session", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			Guid id, CancellationToken token, CreateDateiUploadRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			return await DateiUploads.CreateForLiedAsync(
				db, time, options.Value, storage, decision!.AccountId, id, body, token);
		}).DisableAntiforgery();

		// #171: Dateien of an Auftritt (documents, photos, concert files).
		app.MapPost("/api/events/{id}/dateien/upload-session", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			Guid id, CancellationToken token, CreateDateiUploadRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			return await DateiUploads.CreateForEventAsync(
				db, time, options.Value, storage, decision!.AccountId, id, body, token);
		}).DisableAntiforgery();

		// The description of a document or photo of an Auftritt.
		app.MapPatch("/api/dateien/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token, PatchDateiRequest? body) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var datei = await DateiQueries.Visible(db)
				.FirstOrDefaultAsync(d => d.Id == id && d.EventId != null && d.Kind == DateiKinds.Sonstiges, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			var description = body?.Description?.Trim();
			if (description is { Length: > 500 })
				return Results.Problem(statusCode: 400, title: UploadEndpoints.DescriptionTooLongMessage);
			if (body?.RowVersion is { } expected && expected != datei.RowVersion)
				return Results.Problem(statusCode: 409, title: UploadEndpoints.ConcurrencyMessage);
			if (body?.Description is not null && (description ?? string.Empty) != (datei.Description ?? string.Empty))
			{
				datei.Description = string.IsNullOrEmpty(description) ? null : description;
				datei.RowVersion++;
				try
				{
					await db.SaveChangesAsync(token);
				}
				catch (DbUpdateConcurrencyException)
				{
					return Results.Problem(statusCode: 409, title: UploadEndpoints.ConcurrencyMessage);
				}
			}
			return Results.Ok(new { datei = DateiPayloads.Summary(datei) });
		}).DisableAntiforgery();

		app.MapGet("/api/dateien/{id}/access", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			TimeProvider time, IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			Guid id, CancellationToken token) =>
		{
			var (decision, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var datei = await DateiQueries.Visible(db).AsNoTracking()
				.Include(d => d.Event)
				.FirstOrDefaultAsync(d => d.Id == id, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			// #171: Dateien of an Auftritt follow the Auftritt's visibility;
			// members get concert files only through their recording's own
			// ticket (publication and download switch), editors may also fetch
			// the preserved original here.
			if (datei.Event is not null && !ArchiveAccess.IsEditor(decision!)
				&& datei.Kind == DateiKinds.Aufnahme)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			// #161: the member PLAYS the automatic MP4 playback copy while the
			// download stays the original; editors who confirm the copy in the
			// lists still see it under `playbackDateiId`. With no copy yet the
			// view ticket serves the original itself.
			var copy = VideoService.IsVideoMaterial(datei.Kind, datei.ContentType)
				? await VideoPlayback.LiveCopyAsync(db, datei.Id, token)
				: null;
			var viewFile = copy?.PlaybackDatei ?? datei;
			var lifetime = options.Value.ReadTicketLifetime;
			string viewUrl;
			string downloadUrl;
			try
			{
				viewUrl = await storage.CreateReadTicketAsync(
					viewFile.BlobName, lifetime, asDownload: false, contentType: viewFile.ContentType, token);
				downloadUrl = await storage.CreateReadTicketAsync(
					datei.BlobName, lifetime, asDownload: true, contentType: datei.ContentType, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: UploadEndpoints.StorageFailureMessage);
			}
			return Results.Ok(new
			{
				dateiId = datei.Id,
				kind = datei.Kind,
				contentType = viewFile.ContentType,
				sizeBytes = viewFile.SizeBytes,
				fileName = datei.OriginalFileName,
				createdAt = datei.CreatedAt,
				playbackDateiId = copy?.PlaybackDateiId,
				viewUrl,
				downloadUrl,
				expiresAt = time.GetUtcNow() + lifetime,
			});
		});
	}
}
