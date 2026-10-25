using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Recordings;

/// <summary>Both times are required; the entry must belong to the Programm of the recording's event.</summary>
/// <remarks>
/// <c>ExpectedPlaybackDateiId</c> is the playback revision the editor's
/// list was built against (<c>playbackDateiId</c> of the GET); it is
/// required on every write that anchors times to a file, so times seen
/// against one file are never stamped onto another.
/// </remarks>
public sealed record CreatePassageRequest(
	Guid? ProgrammeItemId, double? StartSeconds, double? EndSeconds, Guid? ExpectedPlaybackDateiId);

/// <summary>
/// Absent times stay as they are, except on a Zeitmarke in need of review,
/// where both are stated; <c>ExpectedVersion</c> protects against
/// overwriting a newer state.
/// </summary>
public sealed record PatchPassageRequest(
	double? StartSeconds, double? EndSeconds, uint? ExpectedVersion, Guid? ExpectedPlaybackDateiId);

public sealed record DeletePassageRequest(uint? ExpectedVersion);

/// <summary>One Zeitmarke the editor listed, with the row token it saw.</summary>
public sealed record ReviewPassageItem(Guid? Id, uint? ExpectedVersion);

/// <summary>The review acts on exactly the Zeitmarken named here, nothing else.</summary>
public sealed record ReviewPassagesRequest(Guid? ExpectedPlaybackDateiId, List<ReviewPassageItem?>? Passages);

/// <summary>
/// Zeitmarken in whole recordings. An editor marks where one entry of the
/// Programm starts and ends inside a recording (any editor, not
/// only the one who uploaded the files); members read them with the recording
/// and in the Lied history.
/// - Ids are checked against their stated parents: a Zeitmarke is addressed
///   through its recording, and the Programm entry must belong to the
///   recording's event.
/// - A Zeitmarke is anchored to the playback revision it was timed against.
///   Creating or editing takes the revision members play now; when that file
///   changes later, Zeitmarken read "needsReview" until an editor edits or
///   confirms them (<c>/review</c>). Members never get a position from such a
///   Zeitmarke.
/// - Marking never changes the Programm; entries with Zeitmarken cannot be
///   removed (<see cref="RecordingPassages.BlockedMessageAsync"/>).
/// - Writes carry the playback revision the editor saw
///   (<c>expectedPlaybackDateiId</c>); when the file members play has
///   changed since, the write is a stale-state 409 and writes nothing.
///   The bulk review acts only on the Zeitmarken (and row tokens) it is given.
/// - Stale-state 409 ("changed in between") and not-allowed 409 (duplicate
///   Zeitmarke, no playable file) carry different messages.
/// </summary>
public static class RecordingPassageEndpoints
{
	public const string NotFoundMessage = "Zeitmarke nicht gefunden.";

	public const string ProgrammeItemNotInEventMessage = "Der Programmpunkt gehört nicht zum Programm dieses Auftritts.";

	public const string StartInvalidMessage = "Der Anfang ist ungültig.";

	public const string EndInvalidMessage = "Das Ende ist ungültig.";

	public const string OrderMessage = "Das Ende muss nach dem Anfang liegen.";

	public const string BeyondEndMessage = "Das Ende liegt hinter dem Ende der Aufnahme.";

	/// <summary>Not allowed in this state: a file has to play first.</summary>
	public const string NoPlayableFileMessage =
		"Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.";

	/// <summary>Not allowed in this state: edit the existing Zeitmarke instead.</summary>
	public const string DuplicateMessage =
		"Für diesen Programmpunkt gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.";

	/// <summary>Stale state: the editor acted on an outdated copy and should reload.</summary>
	public const string ConcurrencyMessage = "Die Zeitmarke wurde zwischenzeitlich geändert.";

	/// <summary>Stale state: the file the times were seen against is no longer the one members play.</summary>
	public const string StalePlaybackMessage = "Die Datei der Aufnahme wurde zwischenzeitlich ersetzt.";

	public const string DateiRequiredMessage = "Die Dateiversion der Aufnahme fehlt.";

	public const string ReviewBothBoundsMessage = "Zum Prüfen einer Zeitmarke bitte Anfang und Ende angeben.";

	public const string NoReviewSelectionMessage = "Es wurden keine Zeitmarken zum Bestätigen angegeben.";

	public static void MapRecordingPassageEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/recordings/{id}/passages", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			var (decision, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = ArchiveAccess.IsEditor(decision!);
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
				playbackDateiId = RecordingFiles.Resolve(recording).Playable?.Id,
				durationSeconds = recording.DurationSeconds,
				passages,
				entries = await EntriesAsync(db, recording.EventId, views, token),
			});
		});

		app.MapPost("/api/recordings/{id}/passages", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreatePassageRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (body?.ExpectedPlaybackDateiId is not { } expectedDatei)
				return Results.Problem(statusCode: 400, title: DateiRequiredMessage);
			var start = Round(body.StartSeconds);
			var end = Round(body.EndSeconds);
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null)
				return Results.Problem(statusCode: 404, title: RecordingEndpoints.NotFoundMessage);
			// The entry must belong to the Programm of this recording's own event.
			var programmeItemId = body.ProgrammeItemId;
			if (programmeItemId is null || !await db.ProgrammeItems.AsNoTracking()
				.AnyAsync(i => i.Id == programmeItemId && i.Programme.EventId == recording.EventId, token))
				return Results.Problem(statusCode: 404, title: ProgrammeItemNotInEventMessage);
			if (ValidateTimes(start, end, recording.DurationSeconds) is { } invalid)
				return invalid;
			var playable = RecordingFiles.Resolve(recording).Playable;
			if (playable is null)
				return Results.Problem(statusCode: 409, title: NoPlayableFileMessage);
			if (playable.Id != expectedDatei)
				return Results.Problem(statusCode: 409, title: StalePlaybackMessage);
			var existing = await db.RecordingPassages.AsNoTracking()
				.FirstOrDefaultAsync(p => p.RecordingId == id && p.ProgrammeItemId == programmeItemId, token);
			if (existing is not null)
			{
				// A lost retry of the identical statement answers the stored Zeitmarke.
				if (existing.StartSeconds == start && existing.EndSeconds == end
					&& existing.PlaybackDateiId == playable.Id)
					return Results.Ok(new { passage = await LoadOneAsync(db, existing.Id, token) });
				return Results.Problem(statusCode: 409, title: DuplicateMessage);
			}
			var now = time.GetUtcNow();
			var passage = new RecordingPassage
			{
				RecordingId = id,
				ProgrammeItemId = programmeItemId.Value,
				EventId = recording.EventId,
				PlaybackDateiId = playable.Id,
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
			catch (DbUpdateException exception) when (RecordingPassages.IsDuplicateViolation(exception))
			{
				// A competing request marked the same occurrence first.
				return Results.Problem(statusCode: 409, title: DuplicateMessage);
			}
			catch (DbUpdateException exception) when (RecordingPassages.IsParentGoneViolation(exception))
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
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (body?.ExpectedPlaybackDateiId is not { } expectedDatei)
				return Results.Problem(statusCode: 400, title: DateiRequiredMessage);
			var passage = await db.RecordingPassages
				.FirstOrDefaultAsync(p => p.Id == passageId && p.RecordingId == id, token);
			if (passage is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body.ExpectedVersion is { } expected && expected != passage.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.FirstAsync(r => r.Id == id, token);
			var playable = RecordingFiles.Resolve(recording).Playable;
			if (playable is null)
				return Results.Problem(statusCode: 409, title: NoPlayableFileMessage);
			if (playable.Id != expectedDatei)
				return Results.Problem(statusCode: 409, title: StalePlaybackMessage);
			// A pair taken against another file is confirmed only by stating
			// both bounds: neither of them has been looked at on this file.
			if (passage.PlaybackDateiId != playable.Id
				&& (body.StartSeconds is null || body.EndSeconds is null))
				return Results.Problem(statusCode: 400, title: ReviewBothBoundsMessage);
			var start = Round(body.StartSeconds) ?? passage.StartSeconds;
			var end = Round(body.EndSeconds) ?? passage.EndSeconds;
			if (ValidateTimes(start, end, recording.DurationSeconds) is { } invalid)
				return invalid;
			// Saving a pair against the file members play now is also the
			// editor's review of it, even when both values stayed. Only a
			// pair that is already current and unchanged is a no-op.
			if (start == passage.StartSeconds && end == passage.EndSeconds
				&& passage.PlaybackDateiId == playable.Id)
				return Results.Ok(new { passage = await LoadOneAsync(db, passage.Id, token) });
			passage.StartSeconds = start;
			passage.EndSeconds = end;
			passage.PlaybackDateiId = playable.Id;
			passage.UpdatedAt = time.GetUtcNow();
			passage.UpdatedByAccountId = decision!.AccountId;
			passage.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
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
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
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
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (body?.ExpectedPlaybackDateiId is not { } expectedDatei)
				return Results.Problem(statusCode: 400, title: DateiRequiredMessage);
			if (body.Passages is not { Count: > 0 } listed
				|| listed.Any(item => item?.Id is null || item.ExpectedVersion is null))
				return Results.Problem(statusCode: 400, title: NoReviewSelectionMessage);
			var recording = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null)
				return Results.Problem(statusCode: 404, title: RecordingEndpoints.NotFoundMessage);
			var playable = RecordingFiles.Resolve(recording).Playable;
			if (playable is null)
				return Results.Problem(statusCode: 409, title: NoPlayableFileMessage);
			if (playable.Id != expectedDatei)
				return Results.Problem(statusCode: 409, title: StalePlaybackMessage);
			// Exactly the Zeitmarken the editor listed, each with the token it
			// saw: anything else — another recording's id, a concurrent edit —
			// refuses the whole review before a row is touched.
			var ids = listed.Select(item => item!.Id!.Value).Distinct().ToList();
			var rows = await db.RecordingPassages
				.Where(p => p.RecordingId == id && ids.Contains(p.Id))
				.ToListAsync(token);
			if (rows.Count != ids.Count)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (listed.Any(item => rows.First(p => p.Id == item!.Id).RowVersion != item!.ExpectedVersion))
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			var now = time.GetUtcNow();
			foreach (var passage in rows.Where(p => p.PlaybackDateiId != playable.Id))
			{
				passage.PlaybackDateiId = playable.Id;
				passage.UpdatedAt = now;
				passage.UpdatedByAccountId = decision!.AccountId;
				passage.RowVersion++;
			}
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			var views = await LoadViewsAsync(db, id, isEditor: true, token);
			return Results.Ok(new
			{
				recordingId = id,
				playbackDateiId = playable.Id,
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

	/// <summary>A Zeitmarke after a write, loaded through the full display chain.</summary>
	private static async Task<object> LoadOneAsync(ArchiveDbContext db, Guid passageId, CancellationToken token)
	{
		var rows = await RecordingPassages.LoadRowsAsync(
			db.RecordingPassages.AsNoTracking().Where(p => p.Id == passageId), token);
		var views = await RecordingPassages.ViewsAsync(db, rows, isEditor: true, token);
		return RecordingPassages.Payload(views.Single(), isEditor: true);
	}

	/// <summary>
	/// The Zeitmarkenliste for editors: every entry of the event's Programm in
	/// order with its Zeitmarke in this recording, if any.
	/// </summary>
	private static async Task<List<object>> EntriesAsync(
		ArchiveDbContext db, Guid eventId, List<RecordingPassages.PassageView> views, CancellationToken token)
	{
		var items = await db.ProgrammeItems.AsNoTracking()
			.Where(i => i.Programme.EventId == eventId)
			.OrderBy(i => i.Position).ThenBy(i => i.Id)
			.Select(i => new { i.Id, i.Position, i.LiedId })
			.ToListAsync(token);
		var titles = await RecordingPassages.LoadTitlesAsync(db, items.Select(i => i.LiedId).Distinct().ToList(), token);
		var byItem = views.ToDictionary(v => v.Row.ProgrammeItemId, v => v.Row.Id);
		return items.Select(i => (object)new
		{
			programmeItemId = i.Id,
			position = i.Position,
			liedId = i.LiedId,
			liedTitle = titles.GetValueOrDefault(i.LiedId),
			passageId = byItem.TryGetValue(i.Id, out var passageId) ? (Guid?)passageId : null,
		}).ToList();
	}
}
