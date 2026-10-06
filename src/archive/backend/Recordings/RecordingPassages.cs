using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Recordings;

/// <summary>
/// Shared reading of passages (ARC-032) for the recording endpoints, the
/// song history and the catalogue filter, so one visibility decision and one
/// timestamp-state rule serve all three:
/// - members only see passages of published recordings of published events
///   and only for published songs; editors see everything;
/// - a passage is <see cref="Current"/> only while its timestamps were taken
///   against the file members play now (<see cref="RecordingFiles.Resolve"/>),
///   otherwise it is <see cref="NeedsReview"/>: editors see the values and
///   the state, members see the song in the recording but no position, and
///   the catalogue filter does not claim a jumpable passage for them.
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
		Guid EventId, Guid PerformanceId, Guid SongId, Guid? ArrangementId, Guid? MusicalVersionId,
		int Position, string EvidenceStatus, double StartSeconds, double EndSeconds,
		Guid PlaybackRevisionId, uint RowVersion, DateTimeOffset UpdatedAt);

	public sealed record PassageView(
		PassageRow Row, string SongTitle, string? ArrangementLabel, string? VersionLabel, string State);

	public sealed record Labels(
		IReadOnlyDictionary<Guid, (string Title, bool Published)> Songs,
		IReadOnlyDictionary<Guid, string> Arrangements,
		IReadOnlyDictionary<Guid, string> Versions);

	/// <summary>A recorded song chain visible to the caller, for the catalogue filter.</summary>
	public sealed record RecordedChain(Guid SongId, Guid? ArrangementId, Guid? MusicalVersionId);

	public static string StateOf(Guid playbackRevisionId, Guid? playable) =>
		playable is { } current && current == playbackRevisionId ? Current : NeedsReview;

	public static IQueryable<RecordingPassage> VisibleTo(IQueryable<RecordingPassage> passages, bool isEditor) =>
		isEditor ? passages : RecordingVisibility.OfMemberVisible(passages);

	/// <summary>Plain projection of the passages with the display fields of their recording and occurrence.</summary>
	public static async Task<List<PassageRow>> LoadRowsAsync(
		IQueryable<RecordingPassage> passages, CancellationToken token) =>
		await passages
			.Select(p => new PassageRow(p.Id, p.RecordingId, p.Recording.Label, p.Recording.Kind,
				p.Recording.PublishedAt != null, p.EventId, p.PerformanceId, p.Performance.SongId,
				p.Performance.ArrangementId, p.Performance.MusicalVersionId, p.Performance.Position,
				p.Performance.EvidenceStatus, p.StartSeconds, p.EndSeconds, p.PlaybackRevisionId,
				p.RowVersion, p.UpdatedAt))
			.ToListAsync(token);

	/// <summary>The playback revision members stream per recording; null while nothing plays.</summary>
	public static async Task<Dictionary<Guid, Guid?>> PlayableRevisionsAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> recordingIds, CancellationToken token)
	{
		if (recordingIds.Count == 0)
			return [];
		var recordings = await RecordingEndpoints.WithFiles(db.Recordings.AsNoTracking())
			.Where(r => recordingIds.Contains(r.Id))
			.ToListAsync(token);
		return recordings.ToDictionary(r => r.Id, r => RecordingFiles.Resolve(r).Playable?.Id);
	}

	public static async Task<Labels> LoadLabelsAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> songIds, IReadOnlyCollection<Guid> arrangementIds,
		IReadOnlyCollection<Guid> versionIds, CancellationToken token)
	{
		var songs = songIds.Count == 0
			? []
			: (await db.Songs.AsNoTracking().Where(s => songIds.Contains(s.Id))
				.Select(s => new { s.Id, s.Title, Published = s.PublishedAt != null })
				.ToListAsync(token)).ToDictionary(s => s.Id, s => (s.Title, s.Published));
		var arrangements = arrangementIds.Count == 0
			? []
			: (await db.Arrangements.AsNoTracking().Where(a => arrangementIds.Contains(a.Id))
				.Select(a => new { a.Id, a.Label }).ToListAsync(token)).ToDictionary(a => a.Id, a => a.Label);
		var versions = versionIds.Count == 0
			? []
			: (await db.MusicalVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
				.Select(v => new { v.Id, v.Label }).ToListAsync(token)).ToDictionary(v => v.Id, v => v.Label);
		return new Labels(songs, arrangements, versions);
	}

	/// <summary>
	/// Adds titles, chain labels and the timestamp state. Members never get
	/// a passage of an unpublished song (not even as a gap).
	/// </summary>
	public static async Task<List<PassageView>> ViewsAsync(
		ArchiveDbContext db, List<PassageRow> rows, bool isEditor, CancellationToken token)
	{
		var playable = await PlayableRevisionsAsync(db, rows.Select(r => r.RecordingId).Distinct().ToList(), token);
		var labels = await LoadLabelsAsync(db,
			rows.Select(r => r.SongId).Distinct().ToList(),
			rows.Where(r => r.ArrangementId is not null).Select(r => r.ArrangementId!.Value).Distinct().ToList(),
			rows.Where(r => r.MusicalVersionId is not null).Select(r => r.MusicalVersionId!.Value).Distinct().ToList(),
			token);
		var views = new List<PassageView>();
		foreach (var row in rows)
		{
			if (!labels.Songs.TryGetValue(row.SongId, out var song) || (!isEditor && !song.Published))
				continue;
			views.Add(new PassageView(row, song.Title,
				row.ArrangementId is { } arrangementId ? labels.Arrangements.GetValueOrDefault(arrangementId) : null,
				row.MusicalVersionId is { } versionId ? labels.Versions.GetValueOrDefault(versionId) : null,
				StateOf(row.PlaybackRevisionId, playable.GetValueOrDefault(row.RecordingId))));
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
			performanceId = row.PerformanceId,
			songId = row.SongId,
			songTitle = view.SongTitle,
			arrangement = row.ArrangementId is { } arrangementId && view.ArrangementLabel is not null
				? new { id = arrangementId, label = view.ArrangementLabel }
				: null,
			musicalVersion = row.MusicalVersionId is { } versionId && view.VersionLabel is not null
				? new { id = versionId, label = view.VersionLabel }
				: null,
			evidenceStatus = row.EvidenceStatus,
			position = row.Position,
			startSeconds = withheld ? (double?)null : row.StartSeconds,
			endSeconds = withheld ? (double?)null : row.EndSeconds,
			timestampState = view.State,
			editor = !isEditor
				? null
				: (object)new
				{
					version = row.RowVersion,
					playbackRevisionId = row.PlaybackRevisionId,
					updatedAt = row.UpdatedAt,
				},
		};
	}

	/// <summary>The recording links per performance for the song history (ARC-032), ordered by recording label.</summary>
	public static async Task<Dictionary<Guid, List<object>>> LinksByPerformanceAsync(
		ArchiveDbContext db, bool isEditor, IReadOnlyCollection<Guid> performanceIds, CancellationToken token)
	{
		if (performanceIds.Count == 0)
			return [];
		var rows = await LoadRowsAsync(
			VisibleTo(db.RecordingPassages.AsNoTracking(), isEditor).Where(p => performanceIds.Contains(p.PerformanceId)),
			token);
		var views = await ViewsAsync(db, rows, isEditor, token);
		return views
			.OrderBy(v => v.Row.RecordingLabel, StringComparer.CurrentCulture).ThenBy(v => v.Row.Id)
			.GroupBy(v => v.Row.PerformanceId)
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
	/// The song chains with a recorded passage the caller can use (catalogue
	/// filter). Members count only passages with a trustworthy timestamp;
	/// editors count every passage, flagged ones included.
	/// </summary>
	public static async Task<List<RecordedChain>> RecordedChainsAsync(
		ArchiveDbContext db, bool isEditor, CancellationToken token)
	{
		var rows = await VisibleTo(db.RecordingPassages.AsNoTracking(), isEditor)
			.Select(p => new
			{
				p.RecordingId,
				p.PlaybackRevisionId,
				p.Performance.SongId,
				p.Performance.ArrangementId,
				p.Performance.MusicalVersionId,
			})
			.ToListAsync(token);
		if (isEditor)
			return rows.Select(r => new RecordedChain(r.SongId, r.ArrangementId, r.MusicalVersionId)).Distinct().ToList();
		var playable = await PlayableRevisionsAsync(db, rows.Select(r => r.RecordingId).Distinct().ToList(), token);
		return rows
			.Where(r => StateOf(r.PlaybackRevisionId, playable.GetValueOrDefault(r.RecordingId)) == Current)
			.Select(r => new RecordedChain(r.SongId, r.ArrangementId, r.MusicalVersionId))
			.Distinct().ToList();
	}

	/// <summary>
	/// The German conflict message naming up to three passages (song and
	/// recording) that stand in the way of removing the given occurrences;
	/// null when none of them has a passage. Deleting or skipping a
	/// performance never removes passages behind the editor's back.
	/// </summary>
	public static async Task<string?> BlockedMessageAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> performanceIds, CancellationToken token)
	{
		if (performanceIds.Count == 0)
			return null;
		var rows = await db.RecordingPassages.AsNoTracking()
			.Where(p => performanceIds.Contains(p.PerformanceId))
			.Select(p => new { p.Performance.SongId, Recording = p.Recording.Label })
			.ToListAsync(token);
		if (rows.Count == 0)
			return null;
		var songIds = rows.Select(r => r.SongId).Distinct().ToList();
		var titles = await db.Songs.AsNoTracking().Where(s => songIds.Contains(s.Id))
			.ToDictionaryAsync(s => s.Id, s => s.Title, token);
		var entries = rows
			.Select(r => $"„{titles.GetValueOrDefault(r.SongId, "Ohne Titel")}“ in „{r.Recording}“")
			.Distinct().OrderBy(e => e, StringComparer.CurrentCulture).ToList();
		var named = string.Join("; ", entries.Take(3));
		var more = entries.Count > 3 ? $" und {entries.Count - 3} weitere" : string.Empty;
		return $"{BlockedPrefix} {named}{more}. Entferne zuerst diese Zeitmarken in den Aufnahmen.";
	}
}
