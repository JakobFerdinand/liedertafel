using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Extraction;
using Archive.Backend.Recordings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Archive.Backend.Lieder;

/// <summary>
/// Papierkorb recovery (#146): a deleted Lied, Auftritt or Datei can be
/// restored within seven days of its deletion, an Administrator can remove it
/// immediately, and the scheduled cleanup
/// (<see cref="PapierkorbCleaner"/>) removes it for good when the seven days
/// have passed. Deleting a Lied stamps only its row — its live Dateien turn
/// invisible through the owner visibility of <see cref="DateiQueries.Visible"/>
/// and come back with the restore; Dateien trashed individually stay in the
/// trash. An Auftritt keeps its Programm, Aufnahmen and Zeitmarken through
/// delete and restore; they are simply hidden with it.
/// </summary>
public static class PapierkorbEndpoints
{
	public const string NotFoundMessage = "Der Eintrag wurde nicht gefunden.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public static void MapPapierkorbRecoveryEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/lieder/{id}/delete", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token, DeletePapierkorbRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var lied = await LiedQueries.Visible(db).FirstOrDefaultAsync(l => l.Id == id, token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: LiedEndpoints.NotFoundMessage);
			if (body?.RowVersion is not { } expected || expected != lied.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			LiedTrash.MoveToTrash(lied, decision!.AccountId, time.GetUtcNow());
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/events/{id}/delete", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token, DeletePapierkorbRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var choirEvent = await EventQueries.Visible(db).FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			if (body?.RowVersion is not { } expected || expected != choirEvent.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			AuftrittTrash.MoveToTrash(choirEvent, decision!.AccountId, time.GetUtcNow());
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/lieder/{id}/restore", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IConfiguration configuration, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var lied = await db.Lieder.FirstOrDefaultAsync(l => l.Id == id, token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: LiedEndpoints.NotFoundMessage);
			var liedRetentionDays = configuration.GetValue(PapierkorbCleaner.RetentionDaysPath, PapierkorbCleaner.DefaultRetentionDays);
			if (PapierkorbRestore.IsExpired(lied.DeletedAt, time.GetUtcNow(), liedRetentionDays))
				return PapierkorbRestore.ExpiredProblem(liedRetentionDays);
			if (lied.DeletedAt is not null)
			{
				lied.DeletedAt = null;
				lied.DeletedByAccountId = null;
				lied.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/events/{id}/restore", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IConfiguration configuration, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var choirEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			var eventRetentionDays = configuration.GetValue(PapierkorbCleaner.RetentionDaysPath, PapierkorbCleaner.DefaultRetentionDays);
			if (PapierkorbRestore.IsExpired(choirEvent.DeletedAt, time.GetUtcNow(), eventRetentionDays))
				return PapierkorbRestore.ExpiredProblem(eventRetentionDays);
			if (choirEvent.DeletedAt is not null)
			{
				choirEvent.DeletedAt = null;
				choirEvent.DeletedByAccountId = null;
				choirEvent.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/dateien/{id}/restore", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IConfiguration configuration, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var datei = await db.Dateien.FirstOrDefaultAsync(d => d.Id == id, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			var retentionDays = configuration.GetValue(PapierkorbCleaner.RetentionDaysPath, PapierkorbCleaner.DefaultRetentionDays);
			if (PapierkorbRestore.IsExpired(datei.DeletedAt, time.GetUtcNow(), retentionDays))
				return PapierkorbRestore.ExpiredProblem(retentionDays);
			// The owner comes back first: a Datei under a trashed Lied or
			// Auftritt restores only after that parent is restored.
			if (datei.DeletedAt is not null &&
				(datei.LiedId is { } liedOwner && await db.Lieder.AnyAsync(l => l.Id == liedOwner && l.DeletedAt != null, token) ||
				 datei.EventId is { } eventOwner && await db.Events.AnyAsync(e => e.Id == eventOwner && e.DeletedAt != null, token)))
				return PapierkorbRestore.ParentInTrashProblem;
			if (datei.DeletedAt is not null)
			{
				// Restoring a replaced Datei gives it back as the current one;
				// its successor stays in the Lied beside it.
				datei.DeletedAt = null;
				datei.DeletedByAccountId = null;
				datei.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/papierkorb/lieder/{id}/remove", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, PapierkorbRemoval removal, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireAdministratorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (!await db.Lieder.AnyAsync(l => l.Id == id, token))
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			await removal.RemoveLiedAsync(id, token);
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/papierkorb/auftritte/{id}/remove", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, PapierkorbRemoval removal, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireAdministratorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (!await db.Events.AnyAsync(e => e.Id == id, token))
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			await removal.RemoveEventAsync(id, token);
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/papierkorb/dateien/{id}/remove", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, PapierkorbRemoval removal, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireAdministratorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (!await db.Dateien.AnyAsync(d => d.Id == id, token))
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			await removal.RemoveDateiAsync(id, token);
			return Results.NoContent();
		}).DisableAntiforgery();
	}
}

public sealed record DeletePapierkorbRequest(uint? RowVersion);

/// <summary>
/// The restore-side half of the seven-day policy (#146, ADR-0001):
/// an editor may restore a trashed entry only within the retention
/// (<see cref="PapierkorbCleaner.DefaultRetentionDays"/>, the same setting the
/// cleaner honors), and a Datei whose owning Lied or Auftritt is still in the
/// trash waits for that restore.
/// </summary>
internal static class PapierkorbRestore
{
	public const string ExpiredTitle = "Die Aufbewahrungsfrist ist abgelaufen.";
	public const string ParentInTrashTitle = "Der übergeordnete Eintrag ist noch im Papierkorb.";

	/// <summary>True when the deletion is at or past the retention, so the next cleanup would remove it.</summary>
	public static bool IsExpired(DateTimeOffset? deletedAt, DateTimeOffset now, int retentionDays) =>
		deletedAt is { } deleted && deleted + TimeSpan.FromDays(retentionDays) <= now;

	public static IResult ExpiredProblem(int retentionDays) => Results.Problem(
		statusCode: 409,
		title: ExpiredTitle,
		detail: $"Einträge bleiben {retentionDays} Tage im Papierkorb und werden danach endgültig entfernt.");

	public static IResult ParentInTrashProblem => Results.Problem(
		statusCode: 409,
		title: ParentInTrashTitle,
		detail: "Der übergeordnete Eintrag ist noch im Papierkorb und muss erst wiederhergestellt werden.");
}

internal static class LiedTrash
{
	/// <summary>Moves a Lied into the trash: only its row is stamped; its live Dateien turn invisible through the owner visibility and come back with the restore.</summary>
	public static void MoveToTrash(Lied lied, Guid accountId, DateTimeOffset now)
	{
		lied.DeletedAt = now;
		lied.DeletedByAccountId = accountId;
		lied.RowVersion++;
	}
}

internal static class AuftrittTrash
{
	/// <summary>Moves an Auftritt into the trash; Programm, Aufnahmen and Zeitmarken stay untouched and come back with it.</summary>
	public static void MoveToTrash(ChoirEvent choirEvent, Guid accountId, DateTimeOffset now)
	{
		choirEvent.DeletedAt = now;
		choirEvent.DeletedByAccountId = accountId;
		choirEvent.RowVersion++;
	}
}

/// <summary>
/// Permanent removal of trashed entities (#146): database rows first, then the
/// blobs behind them, released best-effort. Removal happens in ordered saves
/// so the Restrict contracts of the retained references (extraction rows,
/// recordings, Programm entries) are honored explicitly, never by a cascade.
/// </summary>
public sealed class PapierkorbRemoval(ArchiveDbContext db, IAssetStorageAdapter storage, ILogger<PapierkorbRemoval> logger)
{
	public sealed record Result(int Dateien, int Programmitems, int Aufnahmen, int Zeitmarken);

	/// <summary>Removes the Lied with its Dateien (rows and blobs) and the Programm entries that name it; false when no such Lied exists.</summary>
	public async Task<bool> RemoveLiedAsync(Guid liedId, CancellationToken token)
	{
		var lied = await db.Lieder.FirstOrDefaultAsync(l => l.Id == liedId, token);
		if (lied is null)
			return false;
		var items = await db.ProgrammeItems
			.Where(i => i.LiedId == liedId).ToListAsync(token);
		var passages = await db.RecordingPassages
			.Where(p => items.Select(i => i.Id).Contains(p.ProgrammeItemId)).ToListAsync(token);
		db.RemoveRange(passages);
		await db.SaveChangesAsync(token);
		db.RemoveRange(items);
		await db.SaveChangesAsync(token);
		var dateiIds = await db.Dateien
			.Where(d => d.LiedId == liedId).Select(d => d.Id).ToListAsync(token);
		foreach (var dateiId in dateiIds)
			await RemoveDateiAsync(dateiId, token);
		db.Lieder.Remove(lied);
		await db.SaveChangesAsync(token);
		logger.LogInformation(
			"Papierkorb: removed the Lied {Id} ({DateiCount} Dateien, {Items} Programm entries, {Zeitmarken} Zeitmarken)",
			liedId, dateiIds.Count, items.Count, passages.Count);
		return true;
	}

	/// <summary>Removes the Auftritt with its Programm, Aufnahmen, Zeitmarken and Dateien (rows and blobs); false when no such Auftritt exists.</summary>
	public async Task<bool> RemoveEventAsync(Guid eventId, CancellationToken token)
	{
		var choirEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, token);
		if (choirEvent is null)
			return false;
		var recordings = await db.Recordings
			.Where(r => r.EventId == eventId).ToListAsync(token);
		var passages = await db.RecordingPassages
			.Where(p => p.EventId == eventId).ToListAsync(token);
		db.RemoveRange(passages);
		await db.SaveChangesAsync(token);
		db.RemoveRange(recordings);
		await db.SaveChangesAsync(token);
		var programme = await db.Programmes
			.Include(p => p.Items)
			.FirstOrDefaultAsync(p => p.EventId == eventId, token);
		if (programme is not null)
		{
			db.RemoveRange(programme.Items);
			db.Programmes.Remove(programme);
			await db.SaveChangesAsync(token);
		}
		var dateiIds = await db.Dateien
			.Where(d => d.EventId == eventId).Select(d => d.Id).ToListAsync(token);
		foreach (var dateiId in dateiIds)
			await RemoveDateiAsync(dateiId, token);
		// Lied-Aufnahmen that named this Auftritt lose the naming link; the
		// Auftritt is gone for good, so the field cannot stay honest.
		foreach (var named in await db.Dateien
				.Where(d => d.RecordedAtEventId == eventId).ToListAsync(token))
			named.RecordedAtEventId = null;
		db.Events.Remove(choirEvent);
		await db.SaveChangesAsync(token);
		logger.LogInformation(
			"Papierkorb: removed the Auftritt {Id} ({DateiCount} Dateien, {Recordings} Aufnahmen, {Zeitmarken} Zeitmarken)",
			eventId, dateiIds.Count, recordings.Count, passages.Count);
		return true;
	}

	/// <summary>Removes one Datei with its extraction row and its blob; false when no such Datei exists.</summary>
	public async Task<bool> RemoveDateiAsync(Guid dateiId, CancellationToken token)
	{
		var datei = await db.Dateien.FirstOrDefaultAsync(d => d.Id == dateiId, token);
		if (datei is null)
			return false;
		db.RemoveRange(await db.ExtractionJobs
			.Where(j => j.DateiId == dateiId).ToListAsync(token));
		// A queued AI reading would hit a Restrict FK on removal; a Datei in
		// the trash should not be read anyway.
		db.ReadingJobs.RemoveRange(
			await db.ReadingJobs.Where(j => j.DateiId == dateiId).ToListAsync(token));
		// Recording slots keep their row: a removed Datei leaves its slot
		// empty, and a Zeitmarke taken against it reads as in need of review.
		foreach (var recording in await db.Recordings
				.Where(r => r.OriginalDateiId == dateiId || r.PlaybackDateiId == dateiId).ToListAsync(token))
		{
			if (recording.OriginalDateiId == dateiId)
				recording.OriginalDateiId = null;
			if (recording.PlaybackDateiId == dateiId)
				recording.PlaybackDateiId = null;
		}
		foreach (var passage in await db.RecordingPassages
				.Where(p => p.PlaybackDateiId == dateiId).ToListAsync(token))
			passage.PlaybackDateiId = null;
		db.Dateien.Remove(datei);
		await db.SaveChangesAsync(token);
		await ReleaseBlobAsync(datei.BlobName, token);
		return true;
	}

	private async Task ReleaseBlobAsync(string blobName, CancellationToken token)
	{
		try
		{
			await storage.DeleteAsync(blobName, token);
		}
		catch (Exception exception)
		{
			logger.LogWarning("Papierkorb: the blob {BlobName} could not be released ({ExceptionType}).",
				blobName, exception.GetType().Name);
		}
	}
}

/// <summary>
/// The seven-day Papierkorb policy (#146, ADR-0001, architecture §5): a finite
/// operator command (<c>--cleanup-papierkorb</c>) removes every trashed Lied,
/// Auftritt and Datei whose deletion is older than the retention (default seven
/// days, <c>Archive:Papierkorb:RetentionDays</c>), rows first and their blobs
/// afterwards through <see cref="PapierkorbRemoval"/>. <c>--dry-run</c> only
/// counts; running it again finds nothing left.
/// </summary>
public sealed class PapierkorbCleaner(
	ArchiveDbContext db, PapierkorbRemoval removal, TimeProvider time, IConfiguration configuration,
	ILogger<PapierkorbCleaner> logger)
{
	public const string RetentionDaysPath = "Archive:Papierkorb:RetentionDays";

	/// <summary>In production the Container App Job runs daily, so the scheduled seven days hold.</summary>
	public const int DefaultRetentionDays = 7;

	public sealed record Result(int Lieder, int Auftritte, int Dateien, int RetentionDays, bool DryRun);

	public async Task<Result> CleanAsync(bool dryRun, CancellationToken token)
	{
		var retentionDays = configuration.GetValue(RetentionDaysPath, DefaultRetentionDays);
		var cutoff = time.GetUtcNow() - TimeSpan.FromDays(retentionDays);
		var lieder = await db.Lieder.AsNoTracking()
			.Where(l => l.DeletedAt < cutoff).Select(l => l.Id).ToListAsync(token);
		var auftritte = await db.Events.AsNoTracking()
			.Where(e => e.DeletedAt < cutoff).Select(e => e.Id).ToListAsync(token);
		var dateien = await db.Dateien.AsNoTracking()
			.Where(d => d.DeletedAt < cutoff).Select(d => d.Id).ToListAsync(token);
		if (!dryRun)
		{
			foreach (var id in dateien)
				await removal.RemoveDateiAsync(id, token);
			foreach (var id in lieder)
				await removal.RemoveLiedAsync(id, token);
			foreach (var id in auftritte)
				await removal.RemoveEventAsync(id, token);
		}
		logger.LogInformation(
			"Papierkorb cleanup{Mode}: {Lieder} Lieder, {Auftritte} Auftritte, {Dateien} Dateien past {RetentionDays} days",
			dryRun ? " (dry run)" : string.Empty, lieder.Count, auftritte.Count, dateien.Count, retentionDays);
		return new Result(lieder.Count, auftritte.Count, dateien.Count, retentionDays, dryRun);
	}
}
