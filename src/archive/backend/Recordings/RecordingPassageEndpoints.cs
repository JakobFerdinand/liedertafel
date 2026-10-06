using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Archive.Backend.Recordings;

/// <summary>Both times are required; the performance must be an occurrence of the recording's event.</summary>
public sealed record CreatePassageRequest(Guid? PerformanceId, double? StartSeconds, double? EndSeconds);

/// <summary>Absent times stay as they are; <c>ExpectedVersion</c> protects against overwriting a newer state.</summary>
public sealed record PatchPassageRequest(double? StartSeconds, double? EndSeconds, uint? ExpectedVersion);

public sealed record DeletePassageRequest(uint? ExpectedVersion);

/// <summary>Without ids every passage in need of review is confirmed.</summary>
public sealed record ReviewPassagesRequest(List<Guid>? PassageIds);

/// <summary>
/// Passages in whole recordings (ARC-032). An editor marks where one
/// performance occurrence starts and ends inside a recording (any editor, not
/// only the one who uploaded the files); members read them with the recording
/// and in the song history.
/// - Ids are checked against their stated parents: a passage is addressed
///   through its recording, and the occurrence must belong to the
///   recording's event (also guaranteed by the database).
/// - A passage is anchored to the playback revision it was timed against.
///   Creating or editing takes the revision members play now; when that file
///   changes later, passages read "needsReview" until an editor edits or
///   confirms them (<c>/review</c>). Members never get a position from such a
///   passage.
/// - Marking never creates or changes an occurrence, so no history count
///   moves; performances with passages cannot be deleted or skipped
///   (<see cref="RecordingPassages.BlockedMessageAsync"/>).
/// - Stale-state 409 ("changed in between") and not-allowed 409 (duplicate
///   passage, no playable file) carry different messages.
/// </summary>
public static class RecordingPassageEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für die Zeitmarken.";

	public const string NotFoundMessage = "Zeitmarke nicht gefunden.";

	public const string PerformanceNotInEventMessage = "Die Aufführung gehört nicht zu diesem Auftritt.";

	public const string StartInvalidMessage = "Der Anfang ist ungültig.";

	public const string EndInvalidMessage = "Das Ende ist ungültig.";

	public const string OrderMessage = "Das Ende muss nach dem Anfang liegen.";

	public const string BeyondEndMessage = "Das Ende liegt hinter dem Ende der Aufnahme.";

	/// <summary>Not allowed in this state: a file has to play first.</summary>
	public const string NoPlayableFileMessage =
		"Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.";

	/// <summary>Not allowed in this state: edit the existing passage instead.</summary>
	public const string DuplicateMessage =
		"Für diese Aufführung gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.";

	/// <summary>Stale state: the editor acted on an outdated copy and should reload.</summary>
	public const string ConcurrencyMessage = "Die Zeitmarke wurde zwischenzeitlich geändert.";

	public static void MapRecordingPassageEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/recordings/{id}/passages", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.Include(r => r.Event)
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null || (!isEditor && !RecordingVisibility.IsMemberVisible(recording, recording.Event)))
				return Results.Problem(statusCode: 404, title: RecordingEndpoints.NotFoundMessage);
			var views = await LoadViewsAsync(db, id, isEditor, token);
			var passages = views.Select(v => RecordingPassages.Payload(v, isEditor)).ToList();
			if (!isEditor)
				return Results.Ok(new { recordingId = id, eventId = recording.EventId, passages });
			return Results.Ok(new
			{
				recordingId = id,
				eventId = recording.EventId,
				playbackRevisionId = RecordingFiles.Resolve(recording).Playable?.Id,
				durationSeconds = recording.DurationSeconds,
				passages,
				occurrences = await OccurrencesAsync(db, recording.EventId, views, token),
				hasPublishedProgramme = await db.ProgrammeRevisions.AsNoTracking()
					.AnyAsync(r => r.Programme.EventId == recording.EventId && r.PublishedAt != null, token),
			});
		});

		app.MapPost("/api/recordings/{id}/passages", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreatePassageRequest? body) =>
		{
			if (await RejectAntiforgeryAsync(antiforgery, context) is { } rejected)
				return rejected;
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var start = Round(body?.StartSeconds);
			var end = Round(body?.EndSeconds);
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null)
				return Results.Problem(statusCode: 404, title: RecordingEndpoints.NotFoundMessage);
			// The occurrence must belong to this recording's own event.
			var performanceId = body?.PerformanceId;
			if (performanceId is null || !await db.Performances.AsNoTracking()
				.AnyAsync(p => p.Id == performanceId && p.EventId == recording.EventId, token))
				return Results.Problem(statusCode: 404, title: PerformanceNotInEventMessage);
			if (ValidateTimes(start, end, recording.DurationSeconds) is { } invalid)
				return invalid;
			var playable = RecordingFiles.Resolve(recording).Playable;
			if (playable is null)
				return Results.Problem(statusCode: 409, title: NoPlayableFileMessage);
			var existing = await db.RecordingPassages.AsNoTracking()
				.FirstOrDefaultAsync(p => p.RecordingId == id && p.PerformanceId == performanceId, token);
			if (existing is not null)
			{
				// A lost retry of the identical statement answers the stored passage.
				if (existing.StartSeconds == start && existing.EndSeconds == end
					&& existing.PlaybackRevisionId == playable.Id)
					return Results.Ok(new { passage = await LoadOneAsync(db, existing.Id, token) });
				return Results.Problem(statusCode: 409, title: DuplicateMessage);
			}
			var now = time.GetUtcNow();
			var passage = new RecordingPassage
			{
				RecordingId = id,
				PerformanceId = performanceId.Value,
				EventId = recording.EventId,
				PlaybackRevisionId = playable.Id,
				StartSeconds = start!.Value,
				EndSeconds = end!.Value,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
				UpdatedAt = now,
				UpdatedByAccountId = decision.AccountId,
				RowVersion = 1,
			};
			db.RecordingPassages.Add(passage);
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateException exception) when (IsConflict(exception))
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Created($"/api/recordings/{id}/passages/{passage.Id}",
				new { passage = await LoadOneAsync(db, passage.Id, token) });
		}).DisableAntiforgery();

		app.MapPatch("/api/recordings/{id}/passages/{passageId}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, Guid passageId,
			CancellationToken token, PatchPassageRequest? body) =>
		{
			if (await RejectAntiforgeryAsync(antiforgery, context) is { } rejected)
				return rejected;
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var passage = await db.RecordingPassages
				.FirstOrDefaultAsync(p => p.Id == passageId && p.RecordingId == id, token);
			if (passage is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body?.ExpectedVersion is { } expected && expected != passage.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.FirstAsync(r => r.Id == id, token);
			var start = Round(body?.StartSeconds) ?? passage.StartSeconds;
			var end = Round(body?.EndSeconds) ?? passage.EndSeconds;
			if (ValidateTimes(start, end, recording.DurationSeconds) is { } invalid)
				return invalid;
			var playable = RecordingFiles.Resolve(recording).Playable;
			if (playable is null)
				return Results.Problem(statusCode: 409, title: NoPlayableFileMessage);
			// Saving a pair against the file members play now is also the
			// editor's review of it, even when both values stayed. Only a
			// pair that is already current and unchanged is a no-op.
			if (start == passage.StartSeconds && end == passage.EndSeconds
				&& passage.PlaybackRevisionId == playable.Id)
				return Results.Ok(new { passage = await LoadOneAsync(db, passage.Id, token) });
			passage.StartSeconds = start;
			passage.EndSeconds = end;
			passage.PlaybackRevisionId = playable.Id;
			passage.UpdatedAt = time.GetUtcNow();
			passage.UpdatedByAccountId = decision!.AccountId;
			passage.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateException exception) when (IsConflict(exception))
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { passage = await LoadOneAsync(db, passage.Id, token) });
		}).DisableAntiforgery();

		app.MapPost("/api/recordings/{id}/passages/{passageId}/delete", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, Guid id, Guid passageId,
			CancellationToken token, DeletePassageRequest? body) =>
		{
			if (await RejectAntiforgeryAsync(antiforgery, context) is { } rejected)
				return rejected;
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var passage = await db.RecordingPassages
				.FirstOrDefaultAsync(p => p.Id == passageId && p.RecordingId == id, token);
			if (passage is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body?.ExpectedVersion is { } expected && expected != passage.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			db.RecordingPassages.Remove(passage);
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

		app.MapPost("/api/recordings/{id}/passages/review", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id,
			CancellationToken token, ReviewPassagesRequest? body) =>
		{
			if (await RejectAntiforgeryAsync(antiforgery, context) is { } rejected)
				return rejected;
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null)
				return Results.Problem(statusCode: 404, title: RecordingEndpoints.NotFoundMessage);
			var playable = RecordingFiles.Resolve(recording).Playable;
			if (playable is null)
				return Results.Problem(statusCode: 409, title: NoPlayableFileMessage);
			var stale = await db.RecordingPassages
				.Where(p => p.RecordingId == id && p.PlaybackRevisionId != playable.Id)
				.ToListAsync(token);
			if (body?.PassageIds is { } wanted)
				stale = stale.Where(p => wanted.Contains(p.Id)).ToList();
			var now = time.GetUtcNow();
			foreach (var passage in stale)
			{
				passage.PlaybackRevisionId = playable.Id;
				passage.UpdatedAt = now;
				passage.UpdatedByAccountId = decision!.AccountId;
				passage.RowVersion++;
			}
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateException exception) when (IsConflict(exception))
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			var views = await LoadViewsAsync(db, id, isEditor: true, token);
			return Results.Ok(new
			{
				recordingId = id,
				playbackRevisionId = playable.Id,
				passages = views.Select(v => RecordingPassages.Payload(v, isEditor: true)).ToList(),
			});
		}).DisableAntiforgery();
	}

	/// <summary>Milliseconds are the precision of a mark; the database checks see the rounded values.</summary>
	private static double? Round(double? seconds) => seconds is not { } value || !double.IsFinite(value)
		? seconds
		: Math.Round(value, 3);

	private static IResult? ValidateTimes(double? start, double? end, double? duration)
	{
		if (start is not { } from || !double.IsFinite(from) || from < 0 || from > RecordingPassages.MaxSeconds)
			return Results.Problem(statusCode: 400, title: StartInvalidMessage);
		if (end is not { } to || !double.IsFinite(to) || to < 0)
			return Results.Problem(statusCode: 400, title: EndInvalidMessage);
		if (to <= from)
			return Results.Problem(statusCode: 400, title: OrderMessage);
		if (to > RecordingPassages.MaxSeconds
			|| (duration is { } length && to > length + RecordingPassages.DurationToleranceSeconds))
			return Results.Problem(statusCode: 400, title: BeyondEndMessage);
		return null;
	}

	/// <summary>A competing change won a token, a unique slot, or removed a referenced row.</summary>
	private static bool IsConflict(DbUpdateException exception) =>
		RevisionChanges.IsLostRace(exception)
		|| exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };

	private static async Task<List<RecordingPassages.PassageView>> LoadViewsAsync(
		ArchiveDbContext db, Guid recordingId, bool isEditor, CancellationToken token)
	{
		var rows = await RecordingPassages.LoadRowsAsync(
			RecordingPassages.VisibleTo(db.RecordingPassages.AsNoTracking(), isEditor)
				.Where(p => p.RecordingId == recordingId),
			token);
		var views = await RecordingPassages.ViewsAsync(db, rows, isEditor, token);
		return views.OrderBy(v => v.Row.Position).ThenBy(v => v.Row.Id).ToList();
	}

	/// <summary>A passage after a write, loaded through the full display chain.</summary>
	private static async Task<object> LoadOneAsync(ArchiveDbContext db, Guid passageId, CancellationToken token)
	{
		var rows = await RecordingPassages.LoadRowsAsync(
			db.RecordingPassages.AsNoTracking().Where(p => p.Id == passageId), token);
		var views = await RecordingPassages.ViewsAsync(db, rows, isEditor: true, token);
		return RecordingPassages.Payload(views.Single(), isEditor: true);
	}

	/// <summary>
	/// The marker list for editors: every occurrence of the event in captured
	/// (programme) order with its passage in this recording, if any. A
	/// confirmed programme has made its songs occurrences; a published
	/// programme that was not confirmed yet has none to mark.
	/// </summary>
	private static async Task<List<object>> OccurrencesAsync(
		ArchiveDbContext db, Guid eventId, List<RecordingPassages.PassageView> views, CancellationToken token)
	{
		var performances = await db.Performances.AsNoTracking()
			.Where(p => p.EventId == eventId)
			.OrderBy(p => p.Position).ThenBy(p => p.Id)
			.Select(p => new { p.Id, p.Position, p.SongId, p.ArrangementId, p.MusicalVersionId, p.EvidenceStatus })
			.ToListAsync(token);
		var labels = await RecordingPassages.LoadLabelsAsync(db,
			performances.Select(p => p.SongId).Distinct().ToList(),
			performances.Where(p => p.ArrangementId is not null).Select(p => p.ArrangementId!.Value).Distinct().ToList(),
			performances.Where(p => p.MusicalVersionId is not null).Select(p => p.MusicalVersionId!.Value).Distinct().ToList(),
			token);
		var byPerformance = views.ToDictionary(v => v.Row.PerformanceId, v => v.Row.Id);
		return performances.Select(p => (object)new
		{
			performanceId = p.Id,
			position = p.Position,
			songId = p.SongId,
			songTitle = labels.Songs.TryGetValue(p.SongId, out var song) ? song.Title : null,
			arrangement = p.ArrangementId is { } arrangementId && labels.Arrangements.TryGetValue(arrangementId, out var arrangement)
				? new { id = arrangementId, label = arrangement }
				: null,
			musicalVersion = p.MusicalVersionId is { } versionId && labels.Versions.TryGetValue(versionId, out var version)
				? new { id = versionId, label = version }
				: null,
			evidenceStatus = p.EvidenceStatus,
			passageId = byPerformance.TryGetValue(p.Id, out var passageId) ? (Guid?)passageId : null,
		}).ToList();
	}

	private static async Task<IResult?> RejectAntiforgeryAsync(IAntiforgery antiforgery, HttpContext context)
	{
		try
		{
			await antiforgery.ValidateRequestAsync(context);
			return null;
		}
		catch (AntiforgeryValidationException)
		{
			return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
		}
	}

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		return (decision, null);
	}

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		var (decision, error) = await RequireMemberAsync(context, accessor, access);
		if (error is not null)
			return (null, error);
		if (!IsEditor(decision!))
			return (null, Results.Problem(statusCode: 403, title: ForbiddenMessage));
		return (decision, null);
	}
}
