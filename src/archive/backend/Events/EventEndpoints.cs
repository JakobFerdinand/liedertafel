using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

public sealed record CreateEventRequest(string? Kind, string? Title, string? Venue, int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate, string? StartTime, string? Notes, string? SourceNote);

public sealed record PatchEventRequest(string? Kind, string? Title, string? Venue, EventDatePatch? Date, string? StartTime, string? Notes, string? SourceNote);

/// <summary>Explicit full replacement of the date block; absent values mean "unknown part".</summary>
public sealed record EventDatePatch(int? Year, int? Month, int? Day, bool Approximate);

/// <summary>
/// Historical event API (ARC-024): the choir's "Auftritte" record. Reads use
/// the shared database decision: an active member sees published events,
/// editors/administrators also see drafts (see <see cref="EventVisibility"/>).
/// All mutations require the Editor or Administrator role from that decision,
/// CSRF and antiforgery validation; actors and timestamps are attributed from
/// the decision and the injected TimeProvider. Members receive 403;
/// unauthenticated or revoked callers receive 401; drafts answer an
/// indistinguishable 404 for members.
/// </summary>
public static class EventEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für das Auftrittsverzeichnis.";

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
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
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
			var rows = await db.Events.AsNoTracking()
				.Select(e => new EventRow(e.Id, e.Kind, e.Title, e.Venue, e.DateYear, e.DateMonth,
					e.DateDay, e.DateApproximate, e.StartTime, e.PublishedAt))
				.ToListAsync(token);
			var visibleEvents = rows
				.Where(e => isEditor || e.PublishedAt is not null)
				.ToList();
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
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			var choirEvent = await db.Events.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null || (!isEditor && !EventVisibility.IsMemberVisible(choirEvent)))
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			return Results.Ok(new { @event = EventDetail(choirEvent) });
		});

		app.MapPost("/api/events", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, CancellationToken token,
			CreateEventRequest? body) =>
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
			return Results.Created($"/api/events/{choirEvent.Id}", new { @event = EventDetail(choirEvent) });
		}).DisableAntiforgery();

		app.MapPatch("/api/events/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchEventRequest? body) =>
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
			if (body?.Title is not null && !TryValidateTitle(body.Title, out _, out var titleError))
				return titleError;
			if (body?.Kind is not null && !TryValidateKind(body.Kind, out _, out var kindError))
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
			if (body?.StartTime is not null && !TryValidateStartTime(body.StartTime, out _, out var startTimeError))
				return startTimeError;
			var date = body?.Date;
			if (date is not null)
			{
				var dateError = ValidateDate(date.Year, date.Month, date.Day);
				if (dateError is not null)
					return dateError;
			}
			var choirEvent = await db.Events
				.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body?.Title is not null)
				choirEvent.Title = body.Title.Trim();
			if (body?.Kind is not null)
				choirEvent.Kind = body.Kind.Trim();
			if (body?.Venue is not null)
				choirEvent.Venue = venue;
			if (body?.StartTime is not null)
			{
				// Present but empty clears the optional time.
				var trimmedStartTime = body.StartTime.Trim();
				choirEvent.StartTime = trimmedStartTime.Length == 0 ? null : trimmedStartTime;
			}
			if (body?.Notes is not null)
				choirEvent.Notes = notes;
			if (body?.SourceNote is not null)
				choirEvent.SourceNote = sourceNote;
			if (date is not null)
			{
				// The nested date object is an explicit full replacement of
				// the date block; a null year means unknown date.
				choirEvent.DateYear = date.Year;
				choirEvent.DateMonth = date.Month;
				choirEvent.DateDay = date.Day;
				choirEvent.DateApproximate = date.Approximate;
			}
			var now = time.GetUtcNow();
			choirEvent.UpdatedAt = now;
			choirEvent.UpdatedByAccountId = decision!.AccountId;
			choirEvent.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { @event = EventDetail(choirEvent) });
		}).DisableAntiforgery();

		app.MapPost("/api/events/{id}/publish", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token) =>
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
			var choirEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (choirEvent.PublishedAt is null)
			{
				choirEvent.PublishedAt = time.GetUtcNow();
				choirEvent.PublishedByAccountId = decision!.AccountId;
				choirEvent.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.Ok(new { @event = EventDetail(choirEvent) });
		}).DisableAntiforgery();

		app.MapPost("/api/events/{id}/unpublish", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token) =>
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
			var choirEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (choirEvent.PublishedAt is not null)
			{
				choirEvent.PublishedAt = null;
				choirEvent.PublishedByAccountId = null;
				choirEvent.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.Ok(new { @event = EventDetail(choirEvent) });
		}).DisableAntiforgery();
	}

	/// <summary>
	/// Deterministic ARC-024 list order, newest first: known year descending;
	/// within a year, day-precision entries first (month then day, both
	/// descending), then month-only entries, then year-only entries;
	/// unknown-year events sort last, by Id. Pure C# so InMemory tests and
	/// PostgreSQL agree.
	/// </summary>
	private static int CompareEvents(EventRow left, EventRow right)
	{
		if (left.DateYear is null || right.DateYear is null)
		{
			if (left.DateYear == right.DateYear)
				return left.Id.CompareTo(right.Id);
			return left.DateYear is null ? 1 : -1;
		}
		var yearOrder = right.DateYear.Value.CompareTo(left.DateYear.Value);
		if (yearOrder != 0)
			return yearOrder;
		var leftRank = PrecisionRank(left.DateMonth, left.DateDay);
		var rightRank = PrecisionRank(right.DateMonth, right.DateDay);
		if (leftRank != rightRank)
			return leftRank.CompareTo(rightRank);
		var monthOrder = (right.DateMonth ?? 0).CompareTo(left.DateMonth ?? 0);
		if (monthOrder != 0)
			return monthOrder;
		var dayOrder = (right.DateDay ?? 0).CompareTo(left.DateDay ?? 0);
		if (dayOrder != 0)
			return dayOrder;
		return left.Id.CompareTo(right.Id);
	}

	/// <summary>0 = day precision, 1 = month only, 2 = year only.</summary>
	private static int PrecisionRank(int? month, int? day)
		=> month is null ? 2 : day is null ? 1 : 0;

	/// <summary>Derived date precision from the set components (ARC-024).</summary>
	private static string DatePrecision(int? year, int? month, int? day)
		=> day is not null && month is not null ? "day"
			: month is not null ? "month"
			: year is not null ? "year"
			: "unknown";

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
		published = e.PublishedAt is not null,
	};

	private static object EventDetail(ChoirEvent e) => new
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
		published = e.PublishedAt is not null,
		notes = e.Notes,
		sourceNote = e.SourceNote,
		createdAt = e.CreatedAt,
		updatedAt = e.UpdatedAt,
		publishedAt = e.PublishedAt,
	};

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

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		// Cookie presence first for a useful signed-out state: revoked or
		// signed-out callers have no principal and get 401.
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
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!IsEditor(decision))
			return (null, Results.Problem(statusCode: 403, title: ForbiddenMessage));
		return (decision, null);
	}

	/// <summary>Database-projected row for the C#-side list/filter/sort path.</summary>
	private sealed record EventRow(Guid Id, string Kind, string Title, string? Venue,
		int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
		string? StartTime, DateTimeOffset? PublishedAt);
}
