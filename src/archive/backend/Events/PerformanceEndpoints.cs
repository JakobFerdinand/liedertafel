using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

public sealed record CreatePerformanceRequest(Guid? SongId, Guid? MusicalVersionId, string? EvidenceStatus, string? SourceNote, string? IdempotencyKey);

/// <summary>
/// PATCH body (ARC-028): raw JSON elements keep "absent" and "explicitly
/// null" apart for the clearable fields (the EventEndpoints PATCH
/// semantics) — ValueKind.Undefined leaves the stored value untouched,
/// ValueKind.Null clears it. The evidence status only accepts the closed
/// set; an explicit null is not in the set and is rejected.
/// </summary>
public sealed record PatchPerformanceRequest(uint RowVersion, JsonElement EvidenceStatus, JsonElement MusicalVersionId, JsonElement SourceNote);

/// <summary>
/// Performance evidence API (ARC-028): editors record occurrences of a song
/// at one historical event as a confirmed performance or an unconfirmed
/// programme mention (see <see cref="PerformanceEvidenceStatus"/>), with an
/// optional known arrangement/version chain resolved like the programme
/// items (version must belong to the arrangement and the song; an unknown
/// chain is a first-class null state). Every occurrence keeps its stable
/// identity, editor attribution and an application-bumped concurrency token
/// (ARC-007 pattern); mutations never touch the referenced event row.
/// Re-submissions answer via the per-event idempotency key (returns the
/// stored occurrence) or the content duplicate guard (409); explicit
/// conflicts keep rolling back with the German ProblemDetails titles. All
/// mutations and the editor occurrence reads require the Editor or
/// Administrator role, CSRF and antiforgery validation; members receive 403
/// and unauthenticated or revoked callers receive 401. Members only ever
/// see the minimal evidence embed without source notes.
/// </summary>
public static class PerformanceEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für die Aufführungsnachweise.";

	public const string NotFoundMessage = "Aufführungsnachweis nicht gefunden.";

	public const string SongNotFoundMessage = "Lied nicht gefunden.";

	public const string InvalidEvidenceStatusMessage = "Der Nachweiswert ist ungültig.";

	public const string EvidenceNoteRequiredMessage = "Ein unverifizierter Programmhinweis braucht eine Quellenangabe.";

	public const string NoteTooLongMessage = "Die Quellenangabe ist zu lang.";

	public const string FassungPasstNichtMessage = "Die gewählte Fassung gehört nicht zu diesem Lied.";

	public const string DuplicateOccurrenceMessage = "Für dieses Lied existiert bereits ein gleicher Nachweis.";

	public const string ConcurrencyMessage = "Der Nachweis wurde zwischenzeitlich geändert.";

	public const string ConfirmedOccurrenceMessage = "Dieser Nachweis gehört zur Programmbestätigung und wird dort geändert.";

	public const string IdempotencyKeyTooLongMessage = "Der Wiederholungsschlüssel ist zu lang.";

	/// <summary>Field maximum for the source note (ARC-028 schema).</summary>
	public const int NoteMaxLength = 2000;

	/// <summary>Field maximum for the optional retry key.</summary>
	public const int IdempotencyKeyMaxLength = 200;

	public static void MapPerformanceEndpoints(this WebApplication app)
	{
		app.MapPost("/api/events/{eventId}/performances", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid eventId, CancellationToken token,
			CreatePerformanceRequest? body) =>
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
			var choirEvent = await db.Events.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == eventId, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			if (body?.SongId is not { } songId || !await db.Songs.AsNoTracking()
				.AnyAsync(s => s.Id == songId, token))
				return Results.Problem(statusCode: 404, title: SongNotFoundMessage);
			// Chain validation mirrors the programme items: the version must
			// exist with its arrangement and song, and the song must match
			// the version's arrangement parent. Unknown chains stay null on
			// both columns ("Fassung unbekannt").
			Guid? arrangementId = null;
			Guid? musicalVersionId = null;
			if (body?.MusicalVersionId is { } requestedVersionId)
			{
				var version = await db.MusicalVersions.AsNoTracking()
					.Include(v => v.Arrangement)
					.FirstOrDefaultAsync(v => v.Id == requestedVersionId, token);
				if (version is null || version.Arrangement?.SongId != songId)
					return Results.Problem(statusCode: 404, title: FassungPasstNichtMessage);
				arrangementId = version.ArrangementId;
				musicalVersionId = version.Id;
			}
			// Any capitalization matches the closed set; the canonical value
			// is stored. An explicit null (body absent) is not in the set.
			var evidenceStatus = body?.EvidenceStatus?.Trim() is { } rawStatus
				? PerformanceEvidenceStatus.Known.FirstOrDefault(s => string.Equals(
					s, rawStatus, StringComparison.OrdinalIgnoreCase))
				: null;
			if (evidenceStatus is null)
				return Results.Problem(statusCode: 400, title: InvalidEvidenceStatusMessage);
			var note = CleanNote(body?.SourceNote);
			if (note is { Length: > NoteMaxLength })
				return Results.Problem(statusCode: 400, title: NoteTooLongMessage);
			if (evidenceStatus == PerformanceEvidenceStatus.Mention && note is null)
				return Results.Problem(statusCode: 400, title: EvidenceNoteRequiredMessage);
			// Positions stay purely implicit this slice: entries append after
			// the event's current maximum (no rows yet → 1). One query so the
			// two-step above cannot observe an intermediate state.
			var maxPosition = await db.Performances.AsNoTracking()
				.Where(p => p.EventId == eventId)
				.MaxAsync(p => (int?)p.Position, token);
			var idempotencyKey = CleanKey(body?.IdempotencyKey);
			if (idempotencyKey is { Length: > IdempotencyKeyMaxLength })
				return Results.Problem(statusCode: 400, title: IdempotencyKeyTooLongMessage);
			// A lost retry (double click, second form run) returns its stored
			// occurrence instead of creating another one.
			if (idempotencyKey is not null
				&& await db.Performances.AsNoTracking().FirstOrDefaultAsync(
					p => p.EventId == eventId && p.IdempotencyKey == idempotencyKey, token) is { } replay)
				return Results.Ok(new { performance = PerformanceEmbed(replay) });
			// A second editor re-submitting the same content without a retry
			// key is refused: same song, same evidence value, same chain and
			// the same normalized source note already exist on this event. A
			// supplied key marks an explicit second submission instead, so
			// the content guard does not apply to it.
			if (idempotencyKey is null)
			{
				var existing = await db.Performances.AsNoTracking()
					.Where(p => p.EventId == eventId && p.SongId == songId)
					.ToListAsync(token);
				if (existing.Any(p => string.Equals(p.EvidenceStatus, evidenceStatus, StringComparison.OrdinalIgnoreCase)
					&& p.MusicalVersionId == musicalVersionId
					&& string.Equals(p.SourceNote, note, StringComparison.Ordinal)))
					return Results.Problem(statusCode: 409, title: DuplicateOccurrenceMessage);
			}
			var now = time.GetUtcNow();
			var performance = new Performance
			{
				EventId = eventId,
				SongId = songId,
				ArrangementId = arrangementId,
				MusicalVersionId = musicalVersionId,
				Position = (maxPosition ?? 0) + 1,
				EvidenceStatus = evidenceStatus,
				SourceNote = note,
				IdempotencyKey = idempotencyKey,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
				UpdatedAt = now,
				UpdatedByAccountId = decision.AccountId,
			};
			db.Performances.Add(performance);
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			catch (DbUpdateException)
			{
				// The retry race lost the unique (EventId, IdempotencyKey)
				// insert on PostgreSQL: answer the stored row instead of a
				// 500. InMemory tests rely on the pre-check above.
				if (idempotencyKey is not null
					&& await db.Performances.AsNoTracking().FirstOrDefaultAsync(
						p => p.EventId == eventId && p.IdempotencyKey == idempotencyKey, token) is { } stored)
					return Results.Ok(new { performance = PerformanceEmbed(stored) });
				throw;
			}
			return Results.Created($"/api/performances/{performance.Id}",
				new { performance = PerformanceEmbed(performance) });
		}).DisableAntiforgery();

		app.MapPatch("/api/performances/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchPerformanceRequest? body) =>
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
			var performance = await db.Performances
				.FirstOrDefaultAsync(p => p.Id == id, token);
			if (performance is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			// Stale edits answer before any validation or write: the client
			// must reload the fresh state and redo the change.
			if (body is null || body.RowVersion != performance.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			// Absent evidence status keeps the stored one; a present value
			// must be part of the closed set (an explicit null is not one).
			// Any capitalization matches; the canonical value is stored.
			if (body.EvidenceStatus.ValueKind is not JsonValueKind.Undefined)
			{
				var status = body.EvidenceStatus.ValueKind is JsonValueKind.String
					? PerformanceEvidenceStatus.Known.FirstOrDefault(s => string.Equals(
						s, body.EvidenceStatus.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase))
					: null;
				if (status is null)
					return Results.Problem(statusCode: 400, title: InvalidEvidenceStatusMessage);
				performance.EvidenceStatus = status;
			}
			// Absent source note keeps the stored one; explicit null clears
			// (the EventEndpoints PATCH semantics).
			if (body.SourceNote.ValueKind is not JsonValueKind.Undefined)
			{
				if (body.SourceNote.ValueKind is JsonValueKind.String)
				{
					var note = CleanNote(body.SourceNote.GetString());
					if (note is { Length: > NoteMaxLength })
						return Results.Problem(statusCode: 400, title: NoteTooLongMessage);
					performance.SourceNote = note;
				}
				else if (body.SourceNote.ValueKind is JsonValueKind.Null)
				{
					performance.SourceNote = null;
				}
				else
				{
					return Results.Problem(statusCode: 400, title: NoteTooLongMessage);
				}
			}
			// Absent chain keeps the stored one; explicit null clears both
			// columns ("Fassung unbekannt"). A set version is re-resolved and
			// the arrangement is rewritten from the resolved chain atomically.
			if (body.MusicalVersionId.ValueKind is not JsonValueKind.Undefined)
			{
				if (body.MusicalVersionId.ValueKind is JsonValueKind.Null)
				{
					performance.ArrangementId = null;
					performance.MusicalVersionId = null;
				}
				else if (body.MusicalVersionId.ValueKind is JsonValueKind.String
					&& Guid.TryParse(body.MusicalVersionId.GetString(), out var requestedVersionId))
				{
					var version = await db.MusicalVersions.AsNoTracking()
						.Include(v => v.Arrangement)
						.FirstOrDefaultAsync(v => v.Id == requestedVersionId, token);
					if (version is null || version.Arrangement?.SongId != performance.SongId)
						return Results.Problem(statusCode: 404, title: FassungPasstNichtMessage);
					performance.ArrangementId = version.ArrangementId;
					performance.MusicalVersionId = version.Id;
				}
				else
				{
					return Results.Problem(statusCode: 404, title: FassungPasstNichtMessage);
				}
			}
			var effectiveNote = performance.SourceNote;
			if (performance.EvidenceStatus == PerformanceEvidenceStatus.Mention && effectiveNote is null)
				return Results.Problem(statusCode: 400, title: EvidenceNoteRequiredMessage);
			var now = time.GetUtcNow();
			performance.UpdatedAt = now;
			performance.UpdatedByAccountId = decision!.AccountId;
			performance.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { performance = PerformanceEmbed(performance) });
		}).DisableAntiforgery();

		app.MapPost("/api/performances/{id}/delete", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, Guid id, CancellationToken token) =>
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
			var performance = await db.Performances
				.FirstOrDefaultAsync(p => p.Id == id, token);
			if (performance is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			// ARC-029: an occurrence owned by the programme confirmation is
			// changed there (skipping the entry or removing the encore), so
			// the confirmed outcome never shifts behind the review's back.
			if (performance.ConfirmationId is not null)
				return Results.Problem(statusCode: 409, title: ConfirmedOccurrenceMessage);
			// Plain delete without rowVersion (editable editor data, not
			// frozen history); nothing else is written, the event row keeps
			// its own stamps.
			db.Performances.Remove(performance);
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

		app.MapGet("/api/events/{eventId}/performances", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid eventId, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var choirEvent = await db.Events.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == eventId, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			var rows = await db.Performances.AsNoTracking()
				.Where(p => p.EventId == eventId)
				.OrderBy(p => p.Position).ThenBy(p => p.Id)
				.ToListAsync(token);
			return Results.Ok(new { performances = rows.Select(PerformanceEmbed) });
		});
	}

	/// <summary>
	/// Role-filtered evidence embed for the event detail (ARC-028): editors
	/// receive the complete occurrence embeds, members only the minimal
	/// occurrence data without editor audit fields — and never the source
	/// note. Called from every event detail build site.
	/// </summary>
	public static async Task<IReadOnlyList<object>> LoadEventPerformancesAsync(
		ArchiveDbContext db, bool isEditor, Guid eventId, CancellationToken token)
	{
		var rows = await db.Performances.AsNoTracking()
			.Where(p => p.EventId == eventId)
			.OrderBy(p => p.Position).ThenBy(p => p.Id)
			.ToListAsync(token);
		return rows
			.Select(p => isEditor ? (object)PerformanceEmbed(p) : (object)MemberEmbed(p))
			.ToList();
	}

	private static object PerformanceEmbed(Performance performance) => new
	{
		id = performance.Id,
		eventId = performance.EventId,
		songId = performance.SongId,
		arrangementId = performance.ArrangementId,
		musicalVersionId = performance.MusicalVersionId,
		position = performance.Position,
		// ARC-029: the planned entry this occurrence confirms (null for
		// historical evidence and encores) and the owning confirmation.
		programmeItemId = performance.ProgrammeItemId,
		confirmationId = performance.ConfirmationId,
		evidenceStatus = performance.EvidenceStatus,
		sourceNote = performance.SourceNote,
		// The moment we learned/confirmed this occurrence state.
		capturedAt = performance.UpdatedAt,
		createdAt = performance.CreatedAt,
		updatedAt = performance.UpdatedAt,
		rowVersion = performance.RowVersion,
	};

	private static object MemberEmbed(Performance performance) => new
	{
		id = performance.Id,
		songId = performance.SongId,
		arrangementId = performance.ArrangementId,
		musicalVersionId = performance.MusicalVersionId,
		evidenceStatus = performance.EvidenceStatus,
		position = performance.Position,
	};

	/// <summary>Trims to null so empty notes clear, mirroring event PATCH semantics.</summary>
	private static string? CleanNote(string? raw)
	{
		var trimmed = raw?.Trim();
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>Trims to null so an empty retry key means "no key".</summary>
	private static string? CleanKey(string? raw)
	{
		var trimmed = raw?.Trim();
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
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
			return (null, Results.Problem(statusCode: 403, title: ForbiddenMessage));
		return (decision, null);
	}
}
