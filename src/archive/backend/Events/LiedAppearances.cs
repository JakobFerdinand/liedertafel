using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

/// <summary>An Aufnahme of a Lied that names an Auftritt.</summary>
public sealed record LiedAufnahmeRef(Guid DateiId, string? FileName, string ContentType, string? Description);

/// <summary>
/// One Auftritt in a Lied's history: the Lied is in its Programm
/// (<see cref="Entries"/> times) and/or a Lied-Aufnahme names it.
/// </summary>
public sealed record LiedAppearance(
	Guid EventId, string Title, string Kind, string? Venue,
	int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
	int Entries, IReadOnlyList<LiedAufnahmeRef> Aufnahmen);

/// <summary>
/// The one "sung at" query behind the Lied history endpoint and the chat's
/// <c>lied_details</c> tool (#160): derived on every read from the Programme
/// plus the live Lied-Aufnahmen naming an Auftritt, never stored.
/// </summary>
public static class LiedAppearances
{
	public static async Task<List<LiedAppearance>> LoadAsync(ArchiveDbContext db, Guid liedId, CancellationToken token)
	{
		var entryCounts = (await db.ProgrammeItems.AsNoTracking()
				.Where(i => i.LiedId == liedId)
				.Select(i => i.Programme.EventId)
				.ToListAsync(token))
			.GroupBy(id => id)
			.ToDictionary(g => g.Key, g => g.Count());
		var aufnahmen = await DateiQueries.Visible(db).AsNoTracking()
			.Where(d => d.LiedId == liedId && d.Kind == DateiKinds.Aufnahme && d.RecordedAtEventId != null)
			.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
			.Select(d => new { EventId = d.RecordedAtEventId!.Value, d.Id, d.OriginalFileName, d.ContentType, d.Description })
			.ToListAsync(token);
		var aufnahmenByEvent = aufnahmen
			.GroupBy(a => a.EventId)
			.ToDictionary(g => g.Key, g => (IReadOnlyList<LiedAufnahmeRef>)g
				.Select(a => new LiedAufnahmeRef(a.Id, a.OriginalFileName, a.ContentType, a.Description)).ToList());
		var eventIds = entryCounts.Keys.Concat(aufnahmenByEvent.Keys).Distinct().ToList();
		var events = await EventQueries.Visible(db).AsNoTracking()
			.Where(e => eventIds.Contains(e.Id))
			.Select(e => new { e.Id, e.Title, e.Kind, e.Venue, e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate })
			.ToListAsync(token);
		return events
			.Select(e => new LiedAppearance(e.Id, e.Title, e.Kind, e.Venue, e.DateYear, e.DateMonth, e.DateDay,
				e.DateApproximate, entryCounts.GetValueOrDefault(e.Id), aufnahmenByEvent.GetValueOrDefault(e.Id) ?? []))
			.ToList();
	}

	/// <summary>
	/// Planned rather than sung: the Lied is in the Programm of an Auftritt
	/// with a known year that is still upcoming. An unknown date counts as past.
	/// </summary>
	public static bool IsPlanned(LiedAppearance appearance, DateOnly today)
		=> appearance.Entries > 0 && appearance.DateYear is not null
			&& EventDate.IsUpcoming(appearance.DateYear, appearance.DateMonth, appearance.DateDay, today);
}
