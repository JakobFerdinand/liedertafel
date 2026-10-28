using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Recordings;
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
	public const string UnknownTierMessage = "Die Speicher-Stufe ist unbekannt; erlaubt sind Hot und Cold.";

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
				&& !await RecordingFiles.HasSeparatePlaybackAsync(db, datei.Id, datei.Kind, datei.ContentType, token))
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
}

/// <summary>
/// #148 coordination between the Papierkorb and Cold storage: a Cold
/// original is only honest while a separate, live playback Datei serves the
/// members. The moment that copy leaves — trashed, replaced, re-attached or
/// removed — the original returns to Hot in the caller's save, as editor
/// state and as the blob's real tier, so playback never silently streams a
/// Cold object. Only Cold → Hot is automatic; the editor alone moves an
/// original to Cold. The provider move runs first: a refusal changes
/// nothing, so a caller can answer the shared storage failure and the state
/// stays exactly as it was.
/// </summary>
internal static class DateiTierRestore
{
	/// <summary>
	/// Returns every Cold original for which the given Datei serves as the
	/// playback copy to Hot, in the caller's save. False when a provider
	/// move was refused — nothing is written then and the caller answers
	/// <see cref="UploadEndpoints.StorageFailureMessage"/>; true covers
	/// "nothing to do" as well.
	/// </summary>
	public static async Task<bool> ReturnServedOriginalsToHotAsync(
		ArchiveDbContext db, IAssetStorageAdapter storage, Guid copyId, DateTimeOffset now, CancellationToken token)
	{
		foreach (var original in await RecordingFiles.PlaybackOriginalsAsync(db, copyId, token))
		{
			if (original.StorageTier != AssetStorageTiers.Cold)
				continue;
			try
			{
				await storage.SetAccessTierAsync(original.BlobName, AssetStorageTiers.Hot, token);
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			RestoreRow(original, now);
		}
		return true;
	}

	/// <summary>
	/// The restore path's self-heal: a Cold Datei whose separate playback
	/// copy is still gone returns to Hot with the restore — it would
	/// otherwise stream at retrieval charge while the editor state claims a
	/// live copy. False when a provider move was refused; true covers
	/// "nothing to do" as well.
	/// </summary>
	public static async Task<bool> ReturnRestoredToHotAsync(
		ArchiveDbContext db, IAssetStorageAdapter storage, Datei datei, DateTimeOffset now, CancellationToken token)
	{
		if (datei.StorageTier != AssetStorageTiers.Cold
			|| await RecordingFiles.HasSeparatePlaybackAsync(db, datei.Id, datei.Kind, datei.ContentType, token))
			return true;
		try
		{
			await storage.SetAccessTierAsync(datei.BlobName, AssetStorageTiers.Hot, token);
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		RestoreRow(datei, now);
		return true;
	}

	/// <summary>The editor state of the move; the caller's save persists it.</summary>
	private static void RestoreRow(Datei original, DateTimeOffset now)
	{
		original.StorageTier = AssetStorageTiers.Hot;
		original.TierChangedAt = now;
		original.RowVersion++;
	}
}
