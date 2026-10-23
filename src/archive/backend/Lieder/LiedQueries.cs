using Archive.Backend.Data;

namespace Archive.Backend.Lieder;

/// <summary>
/// The one place that decides which Lieder are live (#146). Every display,
/// search, chat and catalogue read goes through <see cref="Visible"/>, so the
/// trash changes this single predicate and nothing else.
/// </summary>
public static class LiedQueries
{
	public static IQueryable<Lied> Visible(ArchiveDbContext db) =>
		db.Lieder.Where(l => l.DeletedAt == null);

	/// <summary>The trash: deleted Lieder, for the editor list only.</summary>
	public static IQueryable<Lied> Trashed(ArchiveDbContext db) =>
		db.Lieder.Where(l => l.DeletedAt != null);
}
