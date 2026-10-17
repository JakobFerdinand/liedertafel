using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Recordings;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

/// <summary>
/// Lied history (#170, ARC-031 on the flat model): where a Lied is documented,
/// read by every active member. Rows are <see cref="Performance"/>
/// occurrences, so the stable performance id stays the key recordings hang on.
/// Counting stays plain and honest: one row is one occurrence, confirmed
/// occurrences and unconfirmed programme mentions are counted apart and never
/// summed, a hand-entered confirmed row next to a confirmation-owned row of
/// the same event is flagged <c>possiblyDuplicate</c> and nothing is merged.
/// Only member-visible events count; editors additionally see rows of
/// unpublished events, flagged and excluded from every aggregate. Members
/// never receive source notes. Order is a total order and pages are fixed-size.
/// </summary>
public static class LiedHistoryEndpoints
{
	public const string LiedNotFoundMessage = "Lied nicht gefunden.";

	public const int PageSize = 20;

	public static void MapLiedHistoryEndpoints(this WebApplication app)
	{
		app.MapGet("/api/lieder/{liedId}/performances", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid liedId, CancellationToken token,
			int? page, string? evidence) =>
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

			string? evidenceFilter = null;
			var evidenceParameter = (evidence ?? string.Empty).Trim();
			if (evidenceParameter.Length > 0)
			{
				evidenceFilter = PerformanceEvidenceStatus.Known.FirstOrDefault(known => string.Equals(
					known, evidenceParameter, StringComparison.OrdinalIgnoreCase));
				if (evidenceFilter is null)
					return Results.Problem(statusCode: 400, title: SongHistoryEndpoints.InvalidEvidenceFilterMessage);
			}

			var query = db.Performances.AsNoTracking().Where(p => p.LiedId == liedId);
			if (!isEditor)
				query = EventVisibility.OfMemberVisibleEvents(query);
			var rows = await query
				.Select(p => new Row(p.Id, p.EventId, p.Event.Title, p.Event.Kind,
					p.Event.PublishedAt != null,
					p.Event.DateYear, p.Event.DateMonth, p.Event.DateDay, p.Event.DateApproximate,
					p.EvidenceStatus, isEditor ? p.SourceNote : null,
					p.ConfirmationId != null, p.Position))
				.ToListAsync(token);

			var confirmedByEvent = rows.Where(IsConfirmed)
				.GroupBy(r => r.EventId)
				.ToDictionary(g => g.Key, g => g.OrderBy(r => r.Position).ThenBy(r => r.Id).ToList());
			var eventsWithOwnedConfirmed = rows.Where(r => IsConfirmed(r) && r.Owned)
				.Select(r => r.EventId).ToHashSet();
			bool PossiblyDuplicate(Row r) => IsConfirmed(r) && !r.Owned && eventsWithOwnedConfirmed.Contains(r.EventId);
			var eventsWithPossibleDuplicate = rows.Where(PossiblyDuplicate)
				.Select(r => r.EventId).ToHashSet();

			var counted = rows.Where(r => r.EventPublished).ToList();
			var countedConfirmed = counted.Where(IsConfirmed).ToList();
			var countedMentions = counted.Where(r => !IsConfirmed(r)).ToList();
			var confirmedEvents = countedConfirmed.Select(r => r.EventId).ToHashSet();
			var counts = new
			{
				confirmed = new
				{
					occurrences = countedConfirmed.Count,
					events = confirmedEvents.Count,
					uncertainDates = countedConfirmed.Count(IsDateUncertain),
					possiblyDuplicate = countedConfirmed.Count(PossiblyDuplicate),
				},
				unconfirmed = new
				{
					occurrences = countedMentions.Count,
					events = countedMentions.Select(r => r.EventId).Distinct().Count(),
					onlyEvents = countedMentions.Select(r => r.EventId)
						.Where(eventId => !confirmedEvents.Contains(eventId)).Distinct().Count(),
					uncertainDates = countedMentions.Count(IsDateUncertain),
				},
				draftEventOccurrences = isEditor ? (int?)rows.Count(r => !r.EventPublished) : null,
			};

			IEnumerable<Row> listed = rows;
			if (evidenceFilter is not null)
				listed = listed.Where(r => string.Equals(r.EvidenceStatus, evidenceFilter, StringComparison.Ordinal));
			var ordered = listed.ToList();
			ordered.Sort(CompareRows);

			var lastPage = Math.Max(1, (ordered.Count + PageSize - 1) / PageSize);
			var requestedPage = Math.Min(page is null or < 1 ? 1 : page.Value, lastPage);
			var pageRows = ordered.Skip((requestedPage - 1) * PageSize).Take(PageSize).ToList();
			var recordingLinks = await RecordingPassages.LinksByPerformanceAsync(
				db, isEditor, pageRows.Select(r => r.Id).ToList(), token);
			return Results.Ok(new
			{
				lied = new { id = lied.Id, title = lied.Title },
				page = requestedPage,
				pageSize = PageSize,
				total = ordered.Count,
				counts,
				performances = pageRows.Select(row => Item(row, isEditor, confirmedByEvent,
					PossiblyDuplicate(row), eventsWithPossibleDuplicate.Contains(row.EventId),
					recordingLinks.GetValueOrDefault(row.Id) ?? [])),
			});
		});
	}

	private static bool IsConfirmed(Row row)
		=> string.Equals(row.EvidenceStatus, PerformanceEvidenceStatus.Confirmed, StringComparison.Ordinal);

	/// <summary>Only an exact day-precision, non-approximate date is certain (the event page's rule).</summary>
	private static bool IsDateUncertain(Row row)
		=> row.DateApproximate || EventDate.Precision(row.DateYear, row.DateMonth, row.DateDay) != "day";

	private static Dictionary<string, object?> Item(Row row, bool isEditor,
		IReadOnlyDictionary<Guid, List<Row>> confirmedByEvent, bool possiblyDuplicate, bool eventHasPossibleDuplicate,
		List<object> recordings)
	{
		var confirmed = IsConfirmed(row);
		var siblings = confirmedByEvent.GetValueOrDefault(row.EventId);
		var item = new Dictionary<string, object?>
		{
			["id"] = row.Id,
			["eventId"] = row.EventId,
			["eventTitle"] = row.EventTitle,
			["eventKind"] = row.EventKind,
			["eventPublished"] = row.EventPublished,
			["dateYear"] = row.DateYear,
			["dateMonth"] = row.DateMonth,
			["dateDay"] = row.DateDay,
			["dateApproximate"] = row.DateApproximate,
			["dateDisplay"] = EventDate.Display(row.DateYear, row.DateMonth, row.DateDay, row.DateApproximate),
			["datePrecision"] = EventDate.Precision(row.DateYear, row.DateMonth, row.DateDay),
			["dateUncertain"] = IsDateUncertain(row),
			["evidenceStatus"] = row.EvidenceStatus,
			["origin"] = row.Owned ? "programme" : "record",
			["occurrence"] = confirmed && siblings is not null
				? new { index = siblings.FindIndex(s => s.Id == row.Id) + 1, of = siblings.Count }
				: null,
			["possiblyDuplicate"] = possiblyDuplicate,
			["possiblyDuplicateAtEvent"] = confirmed && eventHasPossibleDuplicate,
			["alsoConfirmedAtEvent"] = !confirmed && siblings is not null,
			["recordings"] = recordings,
		};
		if (isEditor)
			item["sourceNote"] = row.SourceNote;
		return item;
	}

	/// <summary>Total order: the shared event date order, then event, captured position and id.</summary>
	private static int CompareRows(Row left, Row right)
	{
		var dateOrder = EventDate.CompareNewestFirst(
			left.DateYear, left.DateMonth, left.DateDay, right.DateYear, right.DateMonth, right.DateDay);
		if (dateOrder != 0)
			return dateOrder;
		var eventOrder = left.EventId.CompareTo(right.EventId);
		if (eventOrder != 0)
			return eventOrder;
		var positionOrder = left.Position.CompareTo(right.Position);
		return positionOrder != 0 ? positionOrder : left.Id.CompareTo(right.Id);
	}

	private sealed record Row(Guid Id, Guid EventId, string EventTitle, string EventKind, bool EventPublished,
		int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
		string EvidenceStatus, string? SourceNote, bool Owned, int Position);
}
