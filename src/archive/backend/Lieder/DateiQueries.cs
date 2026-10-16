using Archive.Backend.Data;

namespace Archive.Backend.Lieder;

/// <summary>
/// The one place that decides which Dateien are live. Every read of Dateien for
/// display, search or playback goes through <see cref="Visible"/>, so the trash
/// (#168) changes this single predicate and nothing else.
/// </summary>
public static class DateiQueries
{
	public static IQueryable<Datei> Visible(ArchiveDbContext db) =>
		db.Dateien.Where(d => d.DeletedAt == null);
}
