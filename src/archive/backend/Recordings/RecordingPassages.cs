using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Archive.Backend.Recordings;

/// <summary>
/// Shared reading of passages (Zeitmarken) for the recording endpoints and the
/// Lied history, so one visibility decision and one timestamp-state rule
/// serve both:
/// - members only see passages of published recordings of published events;
///   editors see everything;
/// - a passage is <see cref="Current"/> only while its timestamps were taken
///   against the file members play now (<see cref="RecordingFiles.Resolve"/>),
///   otherwise it is <see cref="NeedsReview"/>: editors see the values and
///   the state, members see the Lied in the recording but no position.
/// </summary>
public static class RecordingPassages
{
	public const string Current = "current";

	public const string NeedsReview = "needsReview";

	/// <summary>Start of the conflict message that names the passages in the way; the UI keeps its input on it.</summary>
	public const string BlockedPrefix = "Zeitmarken vorhanden:";

	/// <summary>Upper bound for a timestamp: the recording duration bound (two days).</summary>
	public const double MaxSeconds = RecordingEndpoints.MaxDurationSeconds;

	/// <summary>
	/// A measured duration is rounded by the browser; a mark this close
	/// behind it still counts as inside the recording.
	/// </summary>
	public const double DurationToleranceSeconds = 1.0;

	public sealed record PassageRow(
		Guid Id, Guid RecordingId, string RecordingLabel, string RecordingKind, bool RecordingPublished,
		Guid EventId, Guid ProgrammeItemId, Guid LiedId,
		int Position, double StartSeconds, double EndSeconds,
		Guid? PlaybackDateiId, uint RowVersion, DateTimeOffset UpdatedAt);

	public sealed record PassageView(PassageRow Row, string LiedTitle, string State);

	/// <summary>Constraint names as the migration created them; the violation mappings look at these.</summary>
	public const string ProgrammeItemForeignKey = "FK_recording_passages_programme_items_ProgrammeItemId";

	public const string UniquePerRecording = "IX_recording_passages_RecordingId_ProgrammeItemId";

	public const string RecordingForeignKey = "FK_recording_passages_recordings_RecordingId_EventId";

	/// <summary>The foreign key from a passage to its Programm entry held: a passage hangs on that entry.</summary>
	public static bool IsProgrammeItemForeignKeyViolation(DbUpdateException exception) =>
		exception.InnerException is PostgresException
		{
			SqlState: PostgresErrorCodes.ForeignKeyViolation,
			ConstraintName: ProgrammeItemForeignKey,
		};

	/// <summary>The unique key (recording, entry) held: that entry is marked in this recording already.</summary>
	public static bool IsDuplicateViolation(DbUpdateException exception) =>
		exception.InnerException is PostgresException
		{
			SqlState: PostgresErrorCodes.UniqueViolation,
			ConstraintName: UniquePerRecording,
		};

	/// <summary>A passage's recording or Programm entry was removed while it was being written.</summary>
	public static bool IsParentGoneViolation(DbUpdateException exception) =>
		IsProgrammeItemForeignKeyViolation(exception)
		|| exception.InnerException is PostgresException
		{
			SqlState: PostgresErrorCodes.ForeignKeyViolation,
			ConstraintName: RecordingForeignKey,
		};

	public static string StateOf(Guid? playbackDateiId, Guid? playable) =>
		playable is { } current && playbackDateiId == current ? Current : NeedsReview;

	public static IQueryable<RecordingPassage> VisibleTo(IQueryable<RecordingPassage> passages, bool isEditor) =>
		isEditor ? passages : RecordingVisibility.OfMemberVisible(passages);

	/// <summary>Plain projection of the passages with the display fields of their recording and Programm entry.</summary>
	public static async Task<List<PassageRow>> LoadRowsAsync(
		IQueryable<RecordingPassage> passages, CancellationToken token) =>
		await passages
			.Select(p => new PassageRow(p.Id, p.RecordingId, p.Recording.Label, p.Recording.Kind,
				p.Recording.PublishedAt != null, p.EventId, p.ProgrammeItemId, p.ProgrammeItem.LiedId,
				p.ProgrammeItem.Position,
				p.StartSeconds, p.EndSeconds, p.PlaybackDateiId,
				p.RowVersion, p.UpdatedAt))
			.ToListAsync(token);

	/// <summary>The playback Datei members stream per recording; null while nothing plays.</summary>
	public static async Task<Dictionary<Guid, Guid?>> PlayableDateienAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> recordingIds, CancellationToken token)
	{
		if (recordingIds.Count == 0)
			return [];
		var recordings = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
			.Where(r => recordingIds.Contains(r.Id))
			.ToListAsync(token);
		return recordings.ToDictionary(r => r.Id, r => RecordingFiles.Resolve(r).Playable?.Id);
	}

	/// <summary>The titles of the given Lieder by id.</summary>
	public static async Task<Dictionary<Guid, string>> LoadTitlesAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> liedIds, CancellationToken token) =>
		liedIds.Count == 0
			? []
			: await db.Lieder.AsNoTracking().Where(l => liedIds.Contains(l.Id))
				.ToDictionaryAsync(l => l.Id, l => l.Title, token);

	/// <summary>
	/// Adds titles and the timestamp state.
	/// </summary>
	public static async Task<List<PassageView>> ViewsAsync(
		ArchiveDbContext db, List<PassageRow> rows, bool isEditor, CancellationToken token)
	{
		var playable = await PlayableDateienAsync(db, rows.Select(r => r.RecordingId).Distinct().ToList(), token);
		var titles = await LoadTitlesAsync(db, rows.Select(r => r.LiedId).Distinct().ToList(), token);
		var views = new List<PassageView>();
		foreach (var row in rows)
		{
			if (!titles.TryGetValue(row.LiedId, out var title))
				continue;
			views.Add(new PassageView(row, title, StateOf(row.PlaybackDateiId, playable.GetValueOrDefault(row.RecordingId))));
		}
		return views;
	}

	/// <summary>One passage in the wire shape of the recording endpoints.</summary>
	public static object Payload(PassageView view, bool isEditor)
	{
		var row = view.Row;
		var withheld = !isEditor && view.State == NeedsReview;
		return new
		{
			id = row.Id,
			recordingId = row.RecordingId,
			programmeItemId = row.ProgrammeItemId,
			liedId = row.LiedId,
			liedTitle = view.LiedTitle,
			position = row.Position,
			startSeconds = withheld ? (double?)null : row.StartSeconds,
			endSeconds = withheld ? (double?)null : row.EndSeconds,
			timestampState = view.State,
			editor = !isEditor
				? null
				: (object)new
				{
					version = row.RowVersion,
					playbackDateiId = row.PlaybackDateiId,
					updatedAt = row.UpdatedAt,
				},
		};
	}

	/// <summary>
	/// The recording links per Auftritt for the Lied history, ordered by
	/// recording label: every Zeitmarke on an entry of the given Lied at the
	/// given events.
	/// </summary>
	public static async Task<Dictionary<Guid, List<object>>> LinksByEventAsync(
		ArchiveDbContext db, bool isEditor, Guid liedId, IReadOnlyCollection<Guid> eventIds, CancellationToken token)
	{
		if (eventIds.Count == 0)
			return [];
		var rows = await LoadRowsAsync(
			VisibleTo(db.RecordingPassages.AsNoTracking(), isEditor)
				.Where(p => eventIds.Contains(p.EventId) && p.ProgrammeItem.LiedId == liedId),
			token);
		var views = await ViewsAsync(db, rows, isEditor, token);
		return views
			.OrderBy(v => v.Row.RecordingLabel, StringComparer.CurrentCulture).ThenBy(v => v.Row.Id)
			.GroupBy(v => v.Row.EventId)
			.ToDictionary(g => g.Key, g => g.Select(v => (object)new
			{
				passageId = v.Row.Id,
				recordingId = v.Row.RecordingId,
				recordingLabel = v.Row.RecordingLabel,
				kind = v.Row.RecordingKind,
				isPublished = v.Row.RecordingPublished,
				startSeconds = !isEditor && v.State == NeedsReview ? (double?)null : v.Row.StartSeconds,
				endSeconds = !isEditor && v.State == NeedsReview ? (double?)null : v.Row.EndSeconds,
				timestampState = v.State,
			}).ToList());
	}

	/// <summary>
	/// The German conflict message naming up to three Zeitmarken (Lied and
	/// recording) that stand in the way of removing the given Programm
	/// entries; null when none of them has a mark. Removing an entry never
	/// removes Zeitmarken behind the editor's back.
	/// </summary>
	public static async Task<string?> BlockedMessageAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> programmeItemIds, CancellationToken token)
	{
		if (programmeItemIds.Count == 0)
			return null;
		var rows = await db.RecordingPassages.AsNoTracking()
			.Where(p => programmeItemIds.Contains(p.ProgrammeItemId))
			.Select(p => new { p.ProgrammeItem.LiedId, Recording = p.Recording.Label })
			.ToListAsync(token);
		if (rows.Count == 0)
			return null;
		var titles = await LoadTitlesAsync(db, rows.Select(r => r.LiedId).Distinct().ToList(), token);
		var entries = rows
			.Select(r => $"„{titles.GetValueOrDefault(r.LiedId, "Ohne Titel")}“ in „{r.Recording}“")
			.Distinct().OrderBy(e => e, StringComparer.CurrentCulture).ToList();
		var named = string.Join("; ", entries.Take(3));
		var more = entries.Count > 3 ? $" und {entries.Count - 3} weitere" : string.Empty;
		return $"{BlockedPrefix} {named}{more}. Entferne zuerst diese Zeitmarken in den Aufnahmen.";
	}
}
