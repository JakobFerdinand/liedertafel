using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Archive.Backend.Recordings;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

/// <summary>
/// Lied history ("wann gesungen", #160): derived, never stored. A Lied was
/// sung at an Auftritt when it is in that Auftritt's Programm, and a Lied-
/// Aufnahme that names an Auftritt also puts the Lied into that Auftritt's
/// history. One row is one Auftritt; the Programm is the record of what was
/// sung, so a Lied listed twice in one Programm counts as one Auftritt with
/// two entries. Only member-visible events count; editors additionally see
/// rows of unpublished events, flagged and excluded from every aggregate.
/// Historical Auftritte with only a year are honest about it
/// (<c>dateUncertain</c>). Order is a total order and pages are fixed-size.
/// </summary>
public static class LiedHistoryEndpoints
{
	public const string LiedNotFoundMessage = "Lied nicht gefunden.";

	public const int PageSize = 20;

	public static void MapLiedHistoryEndpoints(this WebApplication app)
	{
		app.MapGet("/api/lieder/{liedId}/historie", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid liedId, CancellationToken token, int? page) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			if (accessor.Current is null)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			var decision = await access.GetDecisionAsync(context.User);
			if (decision is null || !decision.IsActive)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			var isEditor = decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

			var lied = await db.Lieder.AsNoTracking()
				.Where(l => l.Id == liedId).Select(l => new { l.Id, l.Title })
				.FirstOrDefaultAsync(token);
			if (lied is null)
				return Results.Problem(statusCode: 404, title: LiedNotFoundMessage);

			var entries = await db.ProgrammeItems.AsNoTracking()
				.Where(i => i.LiedId == liedId && (isEditor || i.Programme.Event.PublishedAt != null))
				.Select(i => new { i.Programme.EventId })
				.ToListAsync(token);
			var entryCounts = entries.GroupBy(e => e.EventId).ToDictionary(g => g.Key, g => g.Count());
			var aufnahmen = await DateiQueries.Visible(db).AsNoTracking()
				.Where(d => d.LiedId == liedId && d.Kind == DateiKinds.Aufnahme && d.RecordedAtEventId != null
					&& (isEditor || d.RecordedAtEvent!.PublishedAt != null))
				.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
				.Select(d => new AufnahmeRow(d.RecordedAtEventId!.Value, d.Id, d.OriginalFileName, d.ContentType, d.Description))
				.ToListAsync(token);
			var aufnahmenByEvent = aufnahmen.GroupBy(a => a.EventId).ToDictionary(g => g.Key, g => g.ToList());

			var eventIds = entryCounts.Keys.Concat(aufnahmenByEvent.Keys).Distinct().ToList();
			var rows = await db.Events.AsNoTracking()
				.Where(e => eventIds.Contains(e.Id))
				.Select(e => new Row(e.Id, e.Title, e.Kind, e.PublishedAt != null,
					e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate, 0))
				.ToListAsync(token);
			rows = rows.Select(r => r with { Entries = entryCounts.GetValueOrDefault(r.EventId) }).ToList();

			var counted = rows.Where(r => r.EventPublished).ToList();
			var counts = new
			{
				events = counted.Count,
				inProgramme = counted.Count(r => r.Entries > 0),
				uncertainDates = counted.Count(IsDateUncertain),
				draftEvents = isEditor ? (int?)rows.Count(r => !r.EventPublished) : null,
			};

			rows.Sort(CompareRows);
			var lastPage = Math.Max(1, (rows.Count + PageSize - 1) / PageSize);
			var requestedPage = Math.Min(page is null or < 1 ? 1 : page.Value, lastPage);
			var pageRows = rows.Skip((requestedPage - 1) * PageSize).Take(PageSize).ToList();
			var recordingLinks = await RecordingPassages.LinksByEventAsync(
				db, isEditor, liedId, pageRows.Select(r => r.EventId).ToList(), token);
			return Results.Ok(new
			{
				lied = new { id = lied.Id, title = lied.Title },
				page = requestedPage,
				pageSize = PageSize,
				total = rows.Count,
				counts,
				auftritte = pageRows.Select(row => Item(row,
					aufnahmenByEvent.GetValueOrDefault(row.EventId) ?? [],
					recordingLinks.GetValueOrDefault(row.EventId) ?? [])),
			});
		});
	}

	/// <summary>Only an exact day-precision, non-approximate date is certain (the event page's rule).</summary>
	private static bool IsDateUncertain(Row row)
		=> row.DateApproximate || EventDate.Precision(row.DateYear, row.DateMonth, row.DateDay) != "day";

	private static object Item(Row row, List<AufnahmeRow> aufnahmen, List<object> recordings) => new
	{
		eventId = row.EventId,
		eventTitle = row.EventTitle,
		eventKind = row.EventKind,
		eventPublished = row.EventPublished,
		dateYear = row.DateYear,
		dateMonth = row.DateMonth,
		dateDay = row.DateDay,
		dateApproximate = row.DateApproximate,
		dateDisplay = EventDate.Display(row.DateYear, row.DateMonth, row.DateDay, row.DateApproximate),
		datePrecision = EventDate.Precision(row.DateYear, row.DateMonth, row.DateDay),
		dateUncertain = IsDateUncertain(row),
		inProgramme = row.Entries > 0,
		entries = row.Entries,
		recordings,
		liedAufnahmen = aufnahmen.Select(a => new
		{
			id = a.DateiId,
			fileName = a.FileName,
			contentType = a.ContentType,
			description = a.Description,
		}).ToList(),
	};

	/// <summary>Total order: the shared event date order, then event id.</summary>
	private static int CompareRows(Row left, Row right)
	{
		var dateOrder = EventDate.CompareNewestFirst(
			left.DateYear, left.DateMonth, left.DateDay, right.DateYear, right.DateMonth, right.DateDay);
		return dateOrder != 0 ? dateOrder : left.EventId.CompareTo(right.EventId);
	}

	private sealed record Row(Guid EventId, string EventTitle, string EventKind, bool EventPublished,
		int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate, int Entries);

	private sealed record AufnahmeRow(Guid EventId, Guid DateiId, string? FileName, string ContentType, string? Description);
}
