using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Videos;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Lieder;

public sealed record DeleteDateiRequest(uint? RowVersion);

/// <summary>
/// Sends the replaced Datei to the trash in the same save that creates its
/// successor (#168). The blob stays: restore and the seven-day cleanup are #146.
/// </summary>
public sealed class DateiReplacementHook(IAssetStorageAdapter storage) : IDateiFinalizedHook
{
	public async Task BeforeSaveAsync(Datei datei, PendingUploadContext upload, ArchiveDbContext db, CancellationToken token)
	{
		if (upload.ReplacesDateiId is not { } replacesId)
			return;
		var old = await DateiQueries.Visible(db).FirstOrDefaultAsync(d => d.Id == replacesId, token);
		if (old is null)
			return;
		DateiTrash.MoveToTrash(old, upload.ActorAccountId, upload.Now);
		old.ReplacedByDateiId = datei.Id;
		// #161: the playback copy of a replaced video leaves with it.
		await VideoTrash.TrashCopyOfAsync(db, old, upload.ActorAccountId, upload.Now, token);
		// #148: if the replaced Datei was a Cold original's live playback copy,
		// the original returns to Hot in this save; a refused provider move
		// fails the replacement unchanged, so it can simply be retried.
		if (!await DateiTierRestore.ReturnServedOriginalsToHotAsync(db, storage, old.Id, upload.Now, token))
			throw new InvalidOperationException(UploadEndpoints.StorageFailureMessage);
	}
}

internal static class DateiTrash
{
	public static void MoveToTrash(Datei datei, Guid accountId, DateTimeOffset now)
	{
		datei.DeletedAt = now;
		datei.DeletedByAccountId = accountId;
		datei.RowVersion++;
	}
}

/// <summary>Delete a Datei and the read-only editor trash list (#168).</summary>
public static class DateiTrashEndpoints
{
	public static void MapDateiTrashEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/dateien/{id}/delete", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IAssetStorageAdapter storage, Guid id, CancellationToken token,
			DeleteDateiRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var datei = await DateiQueries.Visible(db).FirstOrDefaultAsync(d => d.Id == id, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			if (body?.RowVersion is not { } expected || expected != datei.RowVersion)
				return Results.Problem(statusCode: 409, title: UploadEndpoints.ConcurrencyMessage);
			DateiTrash.MoveToTrash(datei, decision!.AccountId, time.GetUtcNow());
			// #161: the playback copy of a deleted video leaves with it.
			await Archive.Backend.Videos.VideoTrash.TrashCopyOfAsync(db, datei, decision.AccountId,
				time.GetUtcNow(), token);
			// #148: a Cold original whose live playback copy leaves here returns
			// to Hot in the same save; a refused provider move aborts the trash
			// unchanged, so the state never drifts.
			if (!await DateiTierRestore.ReturnServedOriginalsToHotAsync(db, storage, datei.Id, time.GetUtcNow(), token))
				return Results.Problem(statusCode: 502, title: UploadEndpoints.StorageFailureMessage);
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: UploadEndpoints.ConcurrencyMessage);
			}
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapGet("/api/papierkorb", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var dateiRows = await DateiQueries.Trashed(db).AsNoTracking()
				.Select(d => new
				{
					d.Id,
					d.Kind,
					d.IsPartitur,
					d.Stimme,
					d.OriginalFileName,
					d.SizeBytes,
					d.LiedId,
					LiedTitle = d.Lied != null ? d.Lied.Title : null,
					d.EventId,
					EventTitle = d.Event != null ? d.Event.Title : null,
					d.DeletedAt,
					d.DeletedByAccountId,
					Replaced = d.ReplacedByDateiId != null,
				})
				.ToListAsync(token);
			var liedRows = await LiedQueries.Trashed(db).AsNoTracking()
				.Select(l => new { l.Id, l.Title, l.DeletedAt, l.DeletedByAccountId })
				.ToListAsync(token);
			var eventRows = await EventQueries.Trashed(db).AsNoTracking()
				.Select(e => new { e.Id, e.Title, e.Kind, e.DeletedAt, e.DeletedByAccountId })
				.ToListAsync(token);
			var owners = dateiRows.Select(r => r.DeletedByAccountId)
				.Concat(liedRows.Select(r => r.DeletedByAccountId))
				.Concat(eventRows.Select(r => r.DeletedByAccountId))
				.OfType<Guid>().Distinct().ToList();
			var names = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id))
				.ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? u.UserName, token);
			return Results.Ok(new
			{
				dateien = dateiRows.OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Select(r => new
				{
					id = r.Id,
					kind = r.Kind,
					isPartitur = r.IsPartitur,
					stimme = r.Stimme,
					fileName = r.OriginalFileName,
					sizeBytes = r.SizeBytes,
					liedId = r.LiedId,
					liedTitle = r.LiedTitle,
					eventId = r.EventId,
					eventTitle = r.EventTitle,
					deletedAt = r.DeletedAt,
					deletedBy = r.DeletedByAccountId is { } by && names.TryGetValue(by, out var name) ? name : null,
					replaced = r.Replaced,
				}),
				lieder = liedRows.OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Select(r => new
				{
					id = r.Id,
					title = r.Title,
					deletedAt = r.DeletedAt,
					deletedBy = r.DeletedByAccountId is { } by && names.TryGetValue(by, out var name) ? name : null,
				}),
				auftritte = eventRows.OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Select(r => new
				{
					id = r.Id,
					kind = r.Kind,
					title = r.Title,
					deletedAt = r.DeletedAt,
					deletedBy = r.DeletedByAccountId is { } by && names.TryGetValue(by, out var name) ? name : null,
				}),
			});
		});
	}
}
