using Archive.Backend.Data;

namespace Archive.Backend.Events;

/// <summary>
/// The one place that decides which Auftritte are live (#146). Every list,
/// detail, history, recording and chat read goes through <see cref="Visible"/>,
/// so the trash changes this single predicate and nothing else.
/// </summary>
public static class EventQueries
{
	public static IQueryable<ChoirEvent> Visible(ArchiveDbContext db) =>
		db.Events.Where(e => e.DeletedAt == null);

	/// <summary>The trash: deleted Auftritte, for the editor list only.</summary>
	public static IQueryable<ChoirEvent> Trashed(ArchiveDbContext db) =>
		db.Events.Where(e => e.DeletedAt != null);
}
