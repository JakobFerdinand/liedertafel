using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

public sealed record CreateEventRequest(string? Kind, string? Title, string? Venue, int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate, string? StartTime, string? Notes, string? SourceNote);

public sealed record PatchEventRequest(string? Kind, string? Title, string? Venue, EventDatePatch? Date, string? StartTime, string? Notes, string? SourceNote, uint? ExpectedRowVersion);

/// <summary>Explicit full replacement of the date block; absent values mean "unknown part".</summary>
public sealed record EventDatePatch(int? Year, int? Month, int? Day, bool Approximate);

/// <summary>
/// Historical event API (ARC-024): the choir's "Auftritte" record. Reads use
/// the shared database decision: every active member sees every saved Auftritt
/// (ADR-0001, there is no draft or published state). All mutations require the Editor or Administrator role from that decision,
/// CSRF and antiforgery validation; actors and timestamps are attributed from
/// the decision and the injected TimeProvider. Members receive 403;
/// unauthenticated or revoked callers receive 401.
/// </summary>
public static class EventEndpoints
{
	public const string NotFoundMessage = "Der Auftritt wurde nicht gefunden.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string TitleRequiredMessage = "Der Titel ist erforderlich.";

	public const string TitleTooLongMessage = "Der Titel ist zu lang.";

	public const string KindUnknownMessage = "Unbekannter Auftrittstyp.";

	public const string IncompleteDateMessage = "Das Datum ist unvollständig.";

	public const string InvalidDateMessage = "Das Datum passt nicht zum angegebenen Monat.";

	public const string YearOutOfRangeMessage = "Das Jahr liegt außerhalb des möglichen Bereichs.";

	public const string StartTimeInvalidMessage = "Die Uhrzeit muss im Format HH:MM angegeben werden.";

	public const string VenueTooLongMessage = "Der Veranstaltungsort ist zu lang.";

	public const string NotesTooLongMessage = "Die Hinweise sind zu lang.";

	public const string SourceNoteTooLongMessage = "Die Quellenangabe ist zu lang.";

	/// <summary>Field maximums per the ARC-024 schema.</summary>
	public const int KindMaxLength = 20;

	public const int TitleMaxLength = 200;

	public const int VenueMaxLength = 200;

	public const int NotesMaxLength = 2000;

	public const int SourceNoteMaxLength = 500;

	public static void MapEventEndpoints(this WebApplication app)
	{
		app.MapGet("/api/events", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, CancellationToken token,
			int? year, string? kind) =>
		{
			var (_, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			string? kindFilter = null;
			var kindParameter = (kind ?? string.Empty).Trim();
			if (kindParameter.Length > 0)
			{
				if (!EventKinds.Known.Contains(kindParameter))
					return Results.Problem(statusCode: 400, title: KindUnknownMessage);
				kindFilter = kindParameter;
			}
			// One materialized visible set so InMemory tests and PostgreSQL
			// agree: filtering and the precision-aware ARC-024 sorting run in
			// C# (unpaginated archive scale).
			var rows = await EventQueries.Visible(db).AsNoTracking()
				.Select(e => new EventRow(e.Id, e.Kind, e.Title, e.Venue, e.DateYear, e.DateMonth,
					e.DateDay, e.DateApproximate, e.StartTime))
				.ToListAsync(token);
			var visibleEvents = rows;
			// Years navigation ignores the year/kind filters: distinct years
			// of the whole visible set, descending, with the unknown-year
			// group always last under year null.
			var years = visibleEvents
				.GroupBy(e => e.DateYear)
				.Select(group => new { Year = group.Key, Count = group.Count() })
				.OrderBy(group => group.Year is null ? 1 : 0)
				.ThenByDescending(group => group.Year)
				.Select(group => new { year = group.Year, count = group.Count })
				.ToList();
			var filtered = visibleEvents.AsEnumerable();
			if (year is not null)
				filtered = filtered.Where(e => e.DateYear == year);
			if (kindFilter is not null)
				filtered = filtered.Where(e => string.Equals(e.Kind, kindFilter, StringComparison.Ordinal));
			var filteredList = filtered.ToList();
			filteredList.Sort(CompareEvents);
			return Results.Ok(new
			{
				years,
				total = filteredList.Count,
				events = filteredList.Select(EventItem),
			});
		});

		app.MapGet("/api/events/{id}", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var choirEvent = await EventQueries.Visible(db).AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			return Results.Ok(new { @event = await LoadDetailAsync(db, choirEvent, token) });
		});

		app.MapPost("/api/events", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, CancellationToken token,
			CreateEventRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			if (!TryValidateTitle(body?.Title, out var title, out var titleError))
				return titleError;
			if (!TryValidateKind(body?.Kind, out var kind, out var kindError))
				return kindError;
			var venue = CleanOptional(body?.Venue, VenueMaxLength, VenueTooLongMessage, out var venueError);
			if (venueError is not null)
				return venueError;
			var notes = CleanOptional(body?.Notes, NotesMaxLength, NotesTooLongMessage, out var notesError);
			if (notesError is not null)
				return notesError;
			var sourceNote = CleanOptional(body?.SourceNote, SourceNoteMaxLength, SourceNoteTooLongMessage, out var sourceNoteError);
			if (sourceNoteError is not null)
				return sourceNoteError;
			if (!TryValidateStartTime(body?.StartTime, out var startTime, out var startTimeError))
				return startTimeError;
			var dateError = ValidateDate(body?.DateYear, body?.DateMonth, body?.DateDay);
			if (dateError is not null)
				return dateError;
			var now = time.GetUtcNow();
			var choirEvent = new ChoirEvent
			{
				Kind = kind,
				Title = title,
				Venue = venue,
				StartTime = startTime,
				Notes = notes,
				SourceNote = sourceNote,
				DateYear = body?.DateYear,
				DateMonth = body?.DateMonth,
				DateDay = body?.DateDay,
				DateApproximate = body?.DateApproximate ?? false,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
				UpdatedAt = now,
				UpdatedByAccountId = decision.AccountId,
			};
			db.Events.Add(choirEvent);
			await db.SaveChangesAsync(token);
			return Results.Created($"/api/events/{choirEvent.Id}", new { @event = EventDetail(choirEvent, []) });
		}).DisableAntiforgery();

		app.MapPatch("/api/events/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, CatalogueWriteService writes,
			TimeProvider time, Guid id, CancellationToken token,
			PatchEventRequest? body) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var choirEvent = await EventQueries.Visible(db)
				.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			// The event patch runs through the shared write service
			// (validation and attribution).
			var outcome = await writes.PatchEventAsync(choirEvent,
				body ?? new(null, null, null, null, null, null, null, null),
				new WriteActor(decision!.AccountId, time.GetUtcNow()), automated: false, token);
			if (!outcome.IsSaved)
				return Results.Problem(statusCode: outcome.HttpCode, title: outcome.Title);
			return Results.Ok(new { @event = await LoadDetailAsync(db, choirEvent, token) });
		}).DisableAntiforgery();
	}

	/// <summary>
	/// Deterministic ARC-024 list order, newest first: known year descending;
	/// within a year, day-precision entries first (month then day, both
	/// descending), then month-only entries, then year-only entries;
	/// unknown-year events sort last, by Id (shared with the Lied history via
	/// <see cref="EventDate.CompareNewestFirst"/>). Pure C# so InMemory tests
	/// and PostgreSQL agree.
	/// </summary>
	private static int CompareEvents(EventRow left, EventRow right)
	{
		var order = EventDate.CompareNewestFirst(
			left.DateYear, left.DateMonth, left.DateDay, right.DateYear, right.DateMonth, right.DateDay);
		return order != 0 ? order : left.Id.CompareTo(right.Id);
	}

	/// <summary>Derived date precision from the set components (ARC-024); shared with the programme endpoints.</summary>
	private static string DatePrecision(int? year, int? month, int? day)
		=> EventDate.Precision(year, month, day);

	private static object EventItem(EventRow e) => new
	{
		id = e.Id,
		kind = e.Kind,
		title = e.Title,
		venue = e.Venue,
		dateYear = e.DateYear,
		dateMonth = e.DateMonth,
		dateDay = e.DateDay,
		dateApproximate = e.DateApproximate,
		datePrecision = DatePrecision(e.DateYear, e.DateMonth, e.DateDay),
		dateDisplay = EventDate.Display(e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate),
		startTime = e.StartTime,
	};

	private static object EventDetail(ChoirEvent e, IReadOnlyList<object>? documents = null, object? programme = null, IReadOnlyList<object>? liedAufnahmen = null) => new
	{
		id = e.Id,
		kind = e.Kind,
		title = e.Title,
		venue = e.Venue,
		dateYear = e.DateYear,
		dateMonth = e.DateMonth,
		dateDay = e.DateDay,
		dateApproximate = e.DateApproximate,
		datePrecision = DatePrecision(e.DateYear, e.DateMonth, e.DateDay),
		dateDisplay = EventDate.Display(e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate),
		startTime = e.StartTime,
		notes = e.Notes,
		sourceNote = e.SourceNote,
		documents = documents ?? [],
		programme,
		liedAufnahmen = liedAufnahmen ?? [],
		rowVersion = e.RowVersion,
		createdAt = e.CreatedAt,
		updatedAt = e.UpdatedAt,
	};

	/// <summary>
	/// The event detail payload: the event with its documents and photos, its
	/// one Programm and the Lied-Aufnahmen that name it.
	/// </summary>
	private static async Task<object> LoadDetailAsync(ArchiveDbContext db, ChoirEvent choirEvent, CancellationToken token)
		=> EventDetail(choirEvent,
			await LoadEventDocumentsAsync(db, choirEvent.Id, token),
			await ProgrammeEndpoints.LoadDetailEmbedAsync(db, choirEvent.Id, token),
			await LoadLiedAufnahmenAsync(db, choirEvent.Id, token));

	/// <summary>
	/// Documents and photos of the event (#171): its live <c>sonstiges</c>
	/// Dateien, sorted by createdAt then id. A photo is told from a document
	/// by its content type.
	/// </summary>
	private static async Task<List<object>> LoadEventDocumentsAsync(
		ArchiveDbContext db, Guid eventId, CancellationToken token)
	{
		var dateien = await Lieder.DateiQueries.Visible(db).AsNoTracking()
			.Where(d => d.EventId == eventId && d.Kind == Lieder.DateiKinds.Sonstiges)
			.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
			.ToListAsync(token);
		return dateien
			.Select(d => (object)new
			{
				id = d.Id,
				art = d.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "photo" : "document",
				description = d.Description,
				fileName = d.OriginalFileName,
				contentType = d.ContentType,
				sizeBytes = d.SizeBytes,
				createdAt = d.CreatedAt,
				rowVersion = d.RowVersion,
			})
			.ToList();
	}

	/// <summary>
	/// The Aufnahmen of single Lieder that name this Auftritt (GLOSSARY:
	/// "optionally naming the Auftritt it came from"), live Dateien only, so
	/// they show up on the Auftritt next to its own Aufnahmen. The player and
	/// download live on the Lied's own Dateien.
	/// </summary>
	public static async Task<List<object>> LoadLiedAufnahmenAsync(
		ArchiveDbContext db, Guid eventId, CancellationToken token)
	{
		var rows = await Lieder.DateiQueries.Visible(db).AsNoTracking()
			.Where(d => d.Kind == Lieder.DateiKinds.Aufnahme && d.LiedId != null && d.RecordedAtEventId == eventId)
			.Select(d => new
			{
				d.Id, LiedId = d.LiedId!.Value, LiedTitle = d.Lied!.Title, d.OriginalFileName, d.Description,
				d.ContentType, d.SizeBytes, d.CreatedAt,
			})
			.ToListAsync(token);
		return rows
			.OrderBy(r => r.LiedTitle, StringComparer.CurrentCulture).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
			.Select(r => (object)new
			{
				id = r.Id,
				liedId = r.LiedId,
				liedTitle = r.LiedTitle,
				fileName = r.OriginalFileName,
				description = r.Description,
				contentType = r.ContentType,
				sizeBytes = r.SizeBytes,
				createdAt = r.CreatedAt,
			})
			.ToList();
	}

	private static bool TryValidateTitle(string? raw, out string title, out IResult? error)
	{
		var title_ = (raw ?? string.Empty).Trim();
		if (title_.Length == 0)
		{
			title = string.Empty;
			error = Results.Problem(statusCode: 400, title: TitleRequiredMessage);
			return false;
		}
		if (title_.Length > TitleMaxLength)
		{
			title = string.Empty;
			error = Results.Problem(statusCode: 400, title: TitleTooLongMessage);
			return false;
		}
		title = title_;
		error = null;
		return true;
	}

	/// <summary>Kind must match the closed ARC-024 set exactly after trimming.</summary>
	private static bool TryValidateKind(string? raw, out string kind, out IResult? error)
	{
		var kind_ = (raw ?? string.Empty).Trim();
		if (!EventKinds.Known.Contains(kind_))
		{
			kind = string.Empty;
			error = Results.Problem(statusCode: 400, title: KindUnknownMessage);
			return false;
		}
		kind = kind_;
		error = null;
		return true;
	}

	private static string? CleanOptional(string? raw, int maxLength, string tooLongTitle, out IResult? error)
	{
		error = null;
		var trimmed = raw?.Trim();
		if (trimmed is not null && trimmed.Length > maxLength)
		{
			error = Results.Problem(statusCode: 400, title: tooLongTitle);
			return null;
		}
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>
	/// Validates the optional start time: empty/whitespace clears (null);
	/// otherwise it must parse exactly as "HH:mm".
	/// </summary>
	private static bool TryValidateStartTime(string? raw, out string? startTime, out IResult? error)
	{
		var trimmed = (raw ?? string.Empty).Trim();
		if (trimmed.Length == 0)
		{
			startTime = null;
			error = null;
			return true;
		}
		if (TimeOnly.TryParseExact(trimmed, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out _))
		{
			startTime = trimmed;
			error = null;
			return true;
		}
		startTime = null;
		error = Results.Problem(statusCode: 400, title: StartTimeInvalidMessage);
		return false;
	}

	/// <summary>
	/// Validates one explicit ARC-024 date block (POST body and PATCH
	/// replacement): only (nothing), (year), (year, month) and
	/// (year, month, day) are valid; the day must exist in its month and the
	/// year must lie within the historical range. Returns the German
	/// ProblemDetails error or null when valid.
	/// </summary>
	private static IResult? ValidateDate(int? year, int? month, int? day)
	{
		if (month is not null && year is null)
			return Results.Problem(statusCode: 400, title: IncompleteDateMessage);
		if (day is not null && month is null)
			return Results.Problem(statusCode: 400, title: IncompleteDateMessage);
		if (year is not null && year is < 1800 or > 2100)
			return Results.Problem(statusCode: 400, title: YearOutOfRangeMessage);
		if (month is not null && month is < 1 or > 12)
			return Results.Problem(statusCode: 400, title: InvalidDateMessage);
		if (day is not null && (day is < 1 || day.Value > DateTime.DaysInMonth(year!.Value, month!.Value)))
			return Results.Problem(statusCode: 400, title: InvalidDateMessage);
		return null;
	}

	/// <summary>Database-projected row for the C#-side list/filter/sort path.</summary>
	private sealed record EventRow(Guid Id, string Kind, string Title, string? Venue,
		int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
		string? StartTime);
}
