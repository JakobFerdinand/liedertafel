using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Recordings;
using Archive.Backend.Videos;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Lieder;

/// <summary>The editor's storage-tier request (#148): the target online tier and, optionally, the version it was written against.</summary>
public sealed record DateiStorageTierRequest(string? Tier, uint? RowVersion);

/// <summary>
/// #148: the editor's bounded tier action. A preservation original with a
/// separate, live playback Datei moves to Cold storage; everything else —
/// including one object serving both original and playback roles — stays
/// online. The move happens synchronously in the request (a metadata-only
/// operation for the online tiers), and only a persisted success claims it:
/// a storage failure answers 502 with the shared German message and leaves
/// the row honest. No queue worker exists for this; the editor initiates
/// every transition, and the maintenance middleware pauses the write path.
/// </summary>
public static class DateiStorageEndpoints
{
	/// <summary>The request named a tier outside the accepted online set.</summary>
	public const string UnknownTierMessage = "Der Speicher-Tier ist unbekannt; erlaubt sind hot und cold.";

	/// <summary>
	/// The guard refused Cold: without a separate, live playback Datei the
	/// original itself serves members and must stay online.
	/// </summary>
	public const string ColdNeedsPlaybackMessage =
		"Das Original kann erst ins Cold Storage verschoben werden, wenn eine abspielbare Abspielkopie für Mitglieder bereitsteht.";

	public static void MapDateiStorageEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/dateien/{id}/storage-tier", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IAssetStorageAdapter storage,
			Guid id, CancellationToken token, DateiStorageTierRequest? body) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var tier = body?.Tier?.Trim().ToLowerInvariant();
			if (tier is not (AssetStorageTiers.Hot or AssetStorageTiers.Cold))
				return Results.Problem(statusCode: 400, title: UnknownTierMessage);
			var datei = await DateiQueries.Visible(db).FirstOrDefaultAsync(d => d.Id == id, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			if (body?.RowVersion is { } expected && expected != datei.RowVersion)
				return Results.Problem(statusCode: 409, title: UploadEndpoints.ConcurrencyMessage);
			// An object already in the requested tier is an idempotent reply:
			// no storage call, no write, no silent success claim about a move.
			if (tier == datei.StorageTier)
				return Results.Ok(new { datei = DateiPayloads.Summary(datei) });
			if (tier == AssetStorageTiers.Cold
				&& !await HasSeparatePlaybackAsync(db, datei.Id, datei.Kind, datei.ContentType, token))
				return Results.Problem(statusCode: 422, title: ColdNeedsPlaybackMessage);
			try
			{
				await storage.SetAccessTierAsync(datei.BlobName, tier, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: UploadEndpoints.StorageFailureMessage);
			}
			// The provider tier has moved; the row records the editor state.
			// A failing save here leaves the row Hot and honestly retryable —
			// the next move repeats the (idempotent) provider call and heals it.
			datei.StorageTier = tier;
			datei.TierChangedAt = time.GetUtcNow();
			datei.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: UploadEndpoints.ConcurrencyMessage);
			}
			return Results.Ok(new { datei = DateiPayloads.Summary(datei) });
		}).DisableAntiforgery();
	}

	/// <summary>
	/// #148 guard: a Datei may go Cold only when members' playback is served
	/// by a different, live Datei — the automatic MP4 playback copy for video
	/// material (<see cref="VideoPlayback.LiveCopyAsync"/>), or a recording's
	/// validated playback slot (<see cref="RecordingFiles.Resolve"/>, whose
	/// Ready + playbackCopy verdict says the playable file is the copy).
	/// Nothing is manufactured for tier separation: an original that itself
	/// plays (NotNeeded case included) fails the guard and stays online.
	/// </summary>
	private static async Task<bool> HasSeparatePlaybackAsync(
		ArchiveDbContext db, Guid dateiId, string kind, string contentType, CancellationToken token)
	{
		if (VideoService.IsVideoMaterial(kind, contentType)
			&& await VideoPlayback.LiveCopyAsync(db, dateiId, token) is not null)
			return true;
		var recordings = await db.Recordings.AsNoTracking()
			.Include(r => r.OriginalDatei)
			.Include(r => r.PlaybackDatei)
			.Where(r => r.OriginalDateiId == dateiId)
			.ToListAsync(token);
		return recordings.Count > 0 && recordings.All(recording =>
			RecordingFiles.Resolve(recording)
				is { State: RecordingFiles.Ready, Source: RecordingFiles.PlaybackCopySource });
	}
}
