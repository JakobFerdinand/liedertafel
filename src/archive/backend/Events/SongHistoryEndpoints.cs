using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

/// <summary>
/// Song history (ARC-031): where a song is documented, read by every active
/// member. The rows are ARC-028 occurrences (<see cref="Performance"/>), so
/// the stable performance id is the key later slices hang recordings on
/// (ARC-032). Counting is deliberately plain and honest:
/// <list type="bullet">
/// <item>One row is one occurrence — documents, retries and recordings never
/// add to a total, and genuine repeats at one event stay separate rows.</item>
/// <item>Confirmed occurrences and unconfirmed programme mentions are counted
/// apart and never summed; the figures describe the recorded history only.</item>
/// <item>A hand-entered confirmed row next to a confirmation-owned row of the
/// same song at one event may describe the same performance twice (the editor
/// is warned in ARC-029). Nothing is merged: such rows stay listed, are
/// flagged <c>possiblyDuplicate</c> and counted in
/// <c>confirmed.possiblyDuplicate</c> so the total reads as an upper bound.</item>
/// <item>Only member-visible events count. Editors additionally see rows of
/// unpublished events, flagged and excluded from every aggregate.</item>
/// </list>
/// Members never receive source notes (ARC-028 contract), editor attribution,
/// programme plans or superseded revisions — only the occurrence's own
/// facts. Order is a total order and pages are fixed-size.
/// </summary>
public static class SongHistoryEndpoints
{
	public const string InvalidEvidenceFilterMessage = "Der Nachweisfilter ist ungültig.";

	public const string ArrangementNotFoundMessage = "Das Arrangement gehört nicht zu diesem Lied.";

	/// <summary>The filter value selecting occurrences without a known arrangement.</summary>
	public const string UnknownArrangementFilter = "unknown";

	public const int PageSize = 20;

	public static void MapSongHistoryEndpoints(this WebApplication app)
	{
		app.MapGet("/api/songs/{songId}/performances", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid songId, CancellationToken token,
			int? page, string? evidence, string? arrangementId) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			if (accessor.Current is null)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			var decision = await access.GetDecisionAsync(context.User);
			if (decision is null || !decision.IsActive)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			var isEditor = decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

			// An unpublished song is indistinguishable from an unknown one
			// for members; its history is never read for them.
			var song = await db.Songs.AsNoTracking()
				.Include(s => s.Arrangements).ThenInclude(a => a.MusicalVersions)
				.FirstOrDefaultAsync(s => s.Id == songId, token);
			if (song is null || (!isEditor && !CatalogueVisibility.IsMemberVisible(song)))
				return Results.Problem(statusCode: 404, title: PerformanceEndpoints.SongNotFoundMessage);

			string? evidenceFilter = null;
			var evidenceParameter = (evidence ?? string.Empty).Trim();
			if (evidenceParameter.Length > 0)
			{
				evidenceFilter = PerformanceEvidenceStatus.Known.FirstOrDefault(known => string.Equals(
					known, evidenceParameter, StringComparison.OrdinalIgnoreCase));
				if (evidenceFilter is null)
					return Results.Problem(statusCode: 400, title: InvalidEvidenceFilterMessage);
			}
			Guid? arrangementFilter = null;
			var onlyUnknownArrangement = false;
			var arrangementParameter = (arrangementId ?? string.Empty).Trim();
			if (arrangementParameter.Length > 0)
			{
				if (string.Equals(arrangementParameter, UnknownArrangementFilter, StringComparison.OrdinalIgnoreCase))
					onlyUnknownArrangement = true;
				else if (Guid.TryParse(arrangementParameter, out var requested)
					&& song.Arrangements.Any(a => a.Id == requested))
					arrangementFilter = requested;
				else
					return Results.Problem(statusCode: 404, title: ArrangementNotFoundMessage);
			}

			// One materialized set for the song so InMemory tests and
			// PostgreSQL agree; the total order, flags, aggregates and the
			// page all derive from it. Source notes are only read for editors.
			var query = db.Performances.AsNoTracking().Where(p => p.SongId == songId);
			if (!isEditor)
				query = EventVisibility.OfMemberVisibleEvents(query);
			var rows = await query
				.Select(p => new Row(p.Id, p.EventId, p.Event.Title, p.Event.Kind,
					p.Event.PublishedAt != null,
					p.Event.DateYear, p.Event.DateMonth, p.Event.DateDay, p.Event.DateApproximate,
					p.EvidenceStatus, isEditor ? p.SourceNote : null,
					p.ArrangementId, p.MusicalVersionId, p.ConfirmationId != null, p.Position))
				.ToListAsync(token);

			var arrangementLabels = song.Arrangements.ToDictionary(a => a.Id, a => a.Label);
			var versionLabels = song.Arrangements.SelectMany(a => a.MusicalVersions)
				.ToDictionary(v => v.Id, v => v.Label);

			// Event-level facts shared by the row flags (per event all rows
			// share one publication state, so groups never straddle it).
			var confirmedByEvent = rows.Where(IsConfirmed)
				.GroupBy(r => r.EventId)
				.ToDictionary(g => g.Key, g => g.OrderBy(r => r.Position).ThenBy(r => r.Id).ToList());
			var eventsWithOwnedConfirmed = rows.Where(r => IsConfirmed(r) && r.Owned)
				.Select(r => r.EventId).ToHashSet();
			bool PossiblyDuplicate(Row r) => IsConfirmed(r) && !r.Owned && eventsWithOwnedConfirmed.Contains(r.EventId);

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
					// Events whose only evidence for this song is a mention.
					onlyEvents = countedMentions.Select(r => r.EventId)
						.Where(eventId => !confirmedEvents.Contains(eventId)).Distinct().Count(),
					uncertainDates = countedMentions.Count(IsDateUncertain),
				},
				draftEventOccurrences = isEditor ? (int?)rows.Count(r => !r.EventPublished) : null,
			};

			var arrangements = song.Arrangements.OrderBy(a => a.Id).Select(a => new
			{
				id = a.Id,
				label = a.Label,
				confirmed = countedConfirmed.Count(r => r.ArrangementId == a.Id),
				unconfirmed = countedMentions.Count(r => r.ArrangementId == a.Id),
			}).ToList();
			var unknownArrangement = new
			{
				confirmed = countedConfirmed.Count(r => r.ArrangementId is null),
				unconfirmed = countedMentions.Count(r => r.ArrangementId is null),
			};

			IEnumerable<Row> listed = rows;
			if (evidenceFilter is not null)
				listed = listed.Where(r => string.Equals(r.EvidenceStatus, evidenceFilter, StringComparison.Ordinal));
			if (onlyUnknownArrangement)
				listed = listed.Where(r => r.ArrangementId is null);
			else if (arrangementFilter is { } wanted)
				listed = listed.Where(r => r.ArrangementId == wanted);
			var ordered = listed.ToList();
			ordered.Sort(CompareRows);

			var requestedPage = page is null or < 1 ? 1 : page.Value;
			var pageRows = ordered.Skip((int)Math.Min((long)(requestedPage - 1) * PageSize, ordered.Count)).Take(PageSize).ToList();
			return Results.Ok(new
			{
				song = new { id = song.Id, title = song.Title, published = song.PublishedAt is not null },
				page = requestedPage,
				pageSize = PageSize,
				total = ordered.Count,
				counts,
				arrangements,
				unknownArrangement,
				performances = pageRows.Select(row => Item(row, isEditor, arrangementLabels, versionLabels,
					confirmedByEvent, PossiblyDuplicate(row))),
			});
		});
	}

	private static bool IsConfirmed(Row row)
		=> string.Equals(row.EvidenceStatus, PerformanceEvidenceStatus.Confirmed, StringComparison.Ordinal);

	/// <summary>Only an exact day-precision, non-approximate date is certain (the event page's rule).</summary>
	private static bool IsDateUncertain(Row row)
		=> row.DateApproximate || EventDate.Precision(row.DateYear, row.DateMonth, row.DateDay) != "day";

	private static Dictionary<string, object?> Item(Row row, bool isEditor,
		IReadOnlyDictionary<Guid, string> arrangementLabels, IReadOnlyDictionary<Guid, string> versionLabels,
		IReadOnlyDictionary<Guid, List<Row>> confirmedByEvent, bool possiblyDuplicate)
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
			// "programme": confirmed through the published programme
			// (ARC-029); "record": entered from an archive source (ARC-028).
			["origin"] = row.Owned ? "programme" : "record",
			["arrangement"] = row.ArrangementId is { } arrangementId
				&& arrangementLabels.TryGetValue(arrangementId, out var arrangementLabel)
				? new { id = arrangementId, label = arrangementLabel }
				: null,
			["musicalVersion"] = row.MusicalVersionId is { } versionId
				&& versionLabels.TryGetValue(versionId, out var versionLabel)
				? new { id = versionId, label = versionLabel }
				: null,
			// Position among the confirmed occurrences of this song at the
			// event; unconfirmed mentions are not occurrences to number.
			["occurrence"] = confirmed && siblings is not null
				? new { index = siblings.FindIndex(s => s.Id == row.Id) + 1, of = siblings.Count }
				: null,
			["possiblyDuplicate"] = possiblyDuplicate,
			// A mention at an event where the song has a confirmed
			// occurrence is most likely the same performance's programme entry.
			["alsoConfirmedAtEvent"] = !confirmed && siblings is not null,
		};
		if (isEditor)
			item["sourceNote"] = row.SourceNote;
		return item;
	}

	/// <summary>
	/// Total order: newest known year first, then month and day descending
	/// (day precision before coarser ones within a year), unknown years last;
	/// ties group by event and follow the captured position, then id.
	/// </summary>
	private static int CompareRows(Row left, Row right)
	{
		if (left.DateYear is not null && right.DateYear is not null)
		{
			var yearOrder = right.DateYear.Value.CompareTo(left.DateYear.Value);
			if (yearOrder != 0)
				return yearOrder;
			var monthOrder = (right.DateMonth ?? 0).CompareTo(left.DateMonth ?? 0);
			if (monthOrder != 0)
				return monthOrder;
			var dayOrder = (right.DateDay ?? 0).CompareTo(left.DateDay ?? 0);
			if (dayOrder != 0)
				return dayOrder;
		}
		else if (left.DateYear != right.DateYear)
		{
			return left.DateYear is null ? 1 : -1;
		}
		var eventOrder = left.EventId.CompareTo(right.EventId);
		if (eventOrder != 0)
			return eventOrder;
		var positionOrder = left.Position.CompareTo(right.Position);
		return positionOrder != 0 ? positionOrder : left.Id.CompareTo(right.Id);
	}

	private sealed record Row(Guid Id, Guid EventId, string EventTitle, string EventKind, bool EventPublished,
		int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
		string EvidenceStatus, string? SourceNote, Guid? ArrangementId, Guid? MusicalVersionId,
		bool Owned, int Position);
}
