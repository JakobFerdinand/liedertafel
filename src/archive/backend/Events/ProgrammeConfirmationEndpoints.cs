using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Recordings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

/// <summary>
/// One planned entry's outcome in a confirmation: "sung" (optionally with a
/// corrected musical version of the same song) or "skipped". The optional
/// <paramref name="PerformanceId"/> adopts an existing occurrence of this
/// confirmation (carry-over after a republication); the optional
/// <paramref name="RowVersion"/> is the occurrence token the editor saw.
/// </summary>
public sealed record ConfirmationItemRequest(
	Guid? ProgrammeItemId, string? Outcome, Guid? PerformanceId, uint? RowVersion, Guid? MusicalVersionId);

/// <summary>
/// An additional song sung beyond the plan (an encore). The client-generated
/// <paramref name="ClientKey"/> makes a retry return the same occurrence.
/// </summary>
public sealed record ConfirmationAdditionRequest(
	Guid? PerformanceId, Guid? ClientKey, Guid? SongId, Guid? MusicalVersionId, uint? RowVersion);

/// <summary>An occurrence of this confirmation as the editor last saw it.</summary>
public sealed record KnownOccurrence(Guid? PerformanceId, uint RowVersion);

/// <param name="KnownOccurrences">
/// Every occurrence the client loaded with its row token. When present, an
/// occurrence the statement removes must be among them with an unchanged
/// token, so a source note patched in the meantime is never deleted blindly.
/// </param>
public sealed record SaveConfirmationRequest(
	Guid? RevisionId, uint RowVersion, List<ConfirmationItemRequest>? Items,
	List<ConfirmationAdditionRequest>? Additions, List<KnownOccurrence>? KnownOccurrences = null);

/// <summary>
/// Confirmation of the actual programme (ARC-029). An editor reviews one
/// published programme revision after the event and states what was really
/// sung: planned entries sung as planned, with a corrected musical version,
/// or skipped, plus additional songs (encores). The outcome persists as
/// ARC-028 occurrences — always evidence status confirmed — each linked to
/// its planned <see cref="ProgrammeItem"/> or owned by the programme's
/// <see cref="ProgrammeConfirmation"/>; the planned revision rows are never
/// written. The PUT is a full, idempotent statement of the outcome: the
/// same occurrences are updated in place (stable performance ids), a retry
/// changes nothing, genuine repeated songs are distinct entries, a stale
/// published revision or token is refused (409) and ARC-028 historical
/// evidence of the event is never touched. Editor/Administrator only, CSRF
/// and antiforgery on the mutation, German ProblemDetails, <c>no-store</c>.
/// </summary>
public static class ProgrammeConfirmationEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für die Programmbestätigung.";

	public const string NoProgrammeMessage = "Für diesen Auftritt gibt es kein veröffentlichtes Programm.";

	public const string RevisionNotFoundMessage = "Die Programmrevision wurde nicht gefunden.";

	public const string StaleRevisionMessage = "Das Programm wurde zwischenzeitlich neu veröffentlicht. Bitte lade die Bestätigung neu.";

	public const string ConcurrencyMessage = "Die Bestätigung wurde zwischenzeitlich geändert.";

	public const string FutureEventMessage = "Der Auftritt liegt noch in der Zukunft.";

	public const string VersionMismatchMessage = "Die gewählte Fassung gehört nicht zu diesem Lied.";

	public const string DuplicateKeyMessage = "Ein Wiederholungsschlüssel kommt mehrfach vor.";

	public const string InvalidMessage = "Die Bestätigung ist ungültig.";

	public const string OutcomeSung = "sung";

	public const string OutcomeSkipped = "skipped";

	/// <summary>Review states of one planned entry.</summary>
	private const string StateOpen = "open";

	private const string StateUnconfirmed = "unconfirmed";

	public static void MapProgrammeConfirmationEndpoints(this WebApplication app)
	{
		app.MapGet("/api/events/{eventId}/programme/confirmation", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, Guid eventId, Guid? revisionId, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var reviewedEvent = await db.Events.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == eventId, token);
			if (reviewedEvent is null)
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			var programme = await ProgrammeEndpoints.LoadProgrammeWithChainAsync(db, eventId, token);
			var newest = programme is null ? null : ProgrammeVisibility.NewestPublished(programme);
			if (programme is null || newest is null)
				return Results.Problem(statusCode: 404, title: NoProgrammeMessage);
			var revision = revisionId is null
				? newest
				: programme.Revisions.FirstOrDefault(r => r.Id == revisionId && r.PublishedAt is not null);
			if (revision is null)
				return Results.Problem(statusCode: 404, title: RevisionNotFoundMessage);
			var future = IsDefinitelyFuture(reviewedEvent, ProgrammeEndpoints.ViennaToday(time.GetUtcNow()));
			return Results.Ok(new
			{
				review = await ReviewAsync(db, programme, newest.Id, revision.Id,
					future ? FutureEventMessage : null, token),
			});
		});

		app.MapPut("/api/events/{eventId}/programme/confirmation", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid eventId,
			CancellationToken token, SaveConfirmationRequest? body) =>
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
			var choirEvent = await db.Events.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == eventId, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			if (body?.RevisionId is not { } requestedRevisionId || body.Items is null)
				return Results.Problem(statusCode: 400, title: InvalidMessage);
			var programme = await db.Programmes
				.Include(p => p.Revisions).ThenInclude(r => r.Items)
				.FirstOrDefaultAsync(p => p.EventId == eventId, token);
			var newest = programme is null ? null : ProgrammeVisibility.NewestPublished(programme);
			if (programme is null || newest is null)
				return Results.Problem(statusCode: 404, title: NoProgrammeMessage);
			var revision = programme.Revisions
				.FirstOrDefault(r => r.Id == requestedRevisionId && r.PublishedAt is not null);
			if (revision is null)
				return Results.Problem(statusCode: 404, title: RevisionNotFoundMessage);
			// A superseded revision is refused: the editor reloads the review
			// of the newest publication (the plan itself stays untouched).
			if (revision.Id != newest.Id)
				return Results.Problem(statusCode: 409, title: StaleRevisionMessage);
			// Merely passing the event date confirms nothing, and an event
			// that is certainly still ahead cannot have been sung yet.
			if (IsDefinitelyFuture(choirEvent, ProgrammeEndpoints.ViennaToday(time.GetUtcNow())))
				return Results.Problem(statusCode: 409, title: FutureEventMessage);

			var confirmation = await db.ProgrammeConfirmations
				.FirstOrDefaultAsync(c => c.ProgrammeId == programme.Id, token);
			var owned = confirmation is null
				? []
				: await db.Performances.Where(p => p.ConfirmationId == confirmation.Id).ToListAsync(token);
			// Hand-entered ARC-028 evidence of this event: never touched unless
			// the editor adopts a row explicitly by its id.
			var unowned = await db.Performances
				.Where(p => p.EventId == eventId && p.ConfirmationId == null).ToListAsync(token);

			// Everything is validated against the request and the catalogue
			// before any row is touched (all-or-nothing).
			var (plan, planError) = await PlanAsync(db, body, revision, owned, unowned, token);
			if (planError is not null)
				return planError;

			var now = time.GetUtcNow();
			var changes = plan!.Entries.Where(e => e.NeedsWrite).ToList();
			var removed = owned.Where(row => !plan.Claimed.Contains(row.Id)).ToList();
			var unchanged = changes.Count == 0 && removed.Count == 0
				&& confirmation is not null && confirmation.RevisionId == revision.Id;
			if (unchanged)
			{
				// An identical (retried) statement answers the stored state
				// without a write, even when its token is stale.
				var stored = await ProgrammeEndpoints.LoadProgrammeWithChainAsync(db, eventId, token);
				return Results.Ok(new { review = await ReviewAsync(db, stored!, newest.Id, revision.Id, null, token) });
			}
			var expectedToken = confirmation?.RowVersion ?? 0u;
			if (body.RowVersion != expectedToken)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			if (changes.Any(e => e.Existing is not null && e.RequestedRowVersion is { } seen
					&& seen != e.Existing.RowVersion))
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			// Removing an occurrence deletes whatever an editor patched onto it
			// (source note, evidence); the client must have seen its current
			// state — or the row is new to it.
			if (body.KnownOccurrences is { } known
				&& removed.Any(row => !known.Any(k => k is not null && k.PerformanceId == row.Id
					&& k.RowVersion == row.RowVersion)))
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			// ARC-032: skipping an entry or dropping an encore removes its
			// occurrence; one that recordings mark is kept and named instead
			// of letting the foreign key surface as a generic error. Only
			// asked once the form is known to be current: a stale form is
			// told to reload first, not to delete passages.
			if (await RecordingPassages.BlockedMessageAsync(db, removed.Select(r => r.Id).ToList(), token)
				is { } blocked)
				return Results.Problem(statusCode: 409, title: blocked);

			if (confirmation is null)
			{
				confirmation = new ProgrammeConfirmation
				{
					ProgrammeId = programme.Id,
					RevisionId = revision.Id,
					CreatedAt = now,
					CreatedByAccountId = decision!.AccountId,
					UpdatedAt = now,
					UpdatedByAccountId = decision.AccountId,
					// Token 0 is what a client holds before any confirmation
					// exists, so the first stored state already differs from it.
					RowVersion = 1,
				};
				db.ProgrammeConfirmations.Add(confirmation);
			}
			else
			{
				confirmation.RevisionId = revision.Id;
				confirmation.UpdatedAt = now;
				confirmation.UpdatedByAccountId = decision!.AccountId;
				confirmation.RowVersion++;
			}
			db.Performances.RemoveRange(removed);
			// New occurrences append after the event's current end so the
			// captured order of historical evidence never shifts.
			var lastPosition = await db.Performances.AsNoTracking()
				.Where(p => p.EventId == eventId)
				.MaxAsync(p => (int?)p.Position, token) ?? 0;
			foreach (var entry in changes)
			{
				if (entry.Existing is { } row)
				{
					row.SongId = entry.SongId;
					row.ConfirmationId = confirmation.Id;
					// An occurrence moving into a planned entry takes that entry's
					// retry key, so its former encore key cannot clash later.
					if (entry.ProgrammeItemId is not null
						&& row.IdempotencyKey is { } oldKey
						&& (oldKey.StartsWith("programme-addition:", StringComparison.Ordinal)
							|| oldKey.StartsWith("programme-item:", StringComparison.Ordinal)))
						row.IdempotencyKey = entry.RetryKey;
					row.ArrangementId = entry.ArrangementId;
					row.MusicalVersionId = entry.MusicalVersionId;
					row.EvidenceStatus = PerformanceEvidenceStatus.Confirmed;
					row.ProgrammeItemId = entry.ProgrammeItemId;
					row.UpdatedAt = now;
					row.UpdatedByAccountId = decision!.AccountId;
					row.RowVersion++;
				}
				else
				{
					db.Performances.Add(new Performance
					{
						EventId = eventId,
						SongId = entry.SongId,
						ArrangementId = entry.ArrangementId,
						MusicalVersionId = entry.MusicalVersionId,
						Position = ++lastPosition,
						EvidenceStatus = PerformanceEvidenceStatus.Confirmed,
						IdempotencyKey = entry.RetryKey,
						ProgrammeItemId = entry.ProgrammeItemId,
						ConfirmationId = confirmation.Id,
						CreatedAt = now,
						CreatedByAccountId = decision!.AccountId,
						UpdatedAt = now,
						UpdatedByAccountId = decision.AccountId,
					});
				}
			}
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException)
			{
				// A passage marked since the check keeps its occurrence: the
				// foreign key held, so say why instead of "changed".
				if (exception is DbUpdateException update && RecordingPassages.IsPerformanceForeignKeyViolation(update))
				{
					var removedIds = removed.Select(r => r.Id).ToList();
					db.ChangeTracker.Clear();
					if (await RecordingPassages.BlockedMessageAsync(db, removedIds, token) is { } nowBlocked)
						return Results.Problem(statusCode: 409, title: nowBlocked);
				}
				// A competing confirmation won an optimistic token or one of
				// the unique slots (planned entry, retry key, position).
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			// The answer is built from the chain-loaded programme: the tracked
			// one above has no display labels.
			var fresh = await ProgrammeEndpoints.LoadProgrammeWithChainAsync(db, eventId, token);
			return Results.Ok(new { review = await ReviewAsync(db, fresh!, newest.Id, revision.Id, null, token) });
		}).DisableAntiforgery();
	}

	/// <summary>
	/// Role-filtered actual-programme embed for the event detail: members
	/// read what was sung and what was skipped (no attribution, no tokens),
	/// editors additionally the concurrency token. Null before the first
	/// confirmation — an unreviewed programme is simply "not confirmed yet".
	/// When the confirmation rests on a superseded revision, members keep the
	/// sung songs but receive nothing taken from that older revision (skipped
	/// entries, planned labels): older revisions are never member-visible.
	/// </summary>
	internal static async Task<object?> LoadEmbedAsync(
		ArchiveDbContext db, EventProgramme programme, ProgrammeRevision? published,
		bool isEditor, CancellationToken token)
	{
		var confirmation = await db.ProgrammeConfirmations.AsNoTracking()
			.FirstOrDefaultAsync(c => c.ProgrammeId == programme.Id, token);
		if (confirmation is null)
			return null;
		var revision = programme.Revisions.FirstOrDefault(r => r.Id == confirmation.RevisionId);
		if (revision is null)
			return null;
		var view = await LoadViewAsync(db, programme.EventId, confirmation, token);
		var upToDate = published is not null && published.Id == revision.Id;
		var showPlan = isEditor || upToDate;
		var planned = revision.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
		var byItem = view.Owned.Where(r => r.ProgrammeItemId is not null)
			.ToDictionary(r => r.ProgrammeItemId!.Value);
		var actual = new List<object>();
		foreach (var item in planned)
		{
			if (byItem.TryGetValue(item.Id, out var row))
				actual.Add(ActualEntry(view, row, showPlan ? item : null, added: false));
		}
		foreach (var row in view.Owned.Where(r => r.ProgrammeItemId is null))
			actual.Add(ActualEntry(view, row, null, added: true));
		var skipped = showPlan
			? planned.Where(item => !byItem.ContainsKey(item.Id))
				.Select(item => (object)new
				{
					programmeItemId = item.Id,
					position = item.Position,
					songId = item.SongId,
					arrangementId = item.ArrangementId,
					musicalVersionId = item.MusicalVersionId,
					songTitle = item.MusicalVersion?.Arrangement?.Song?.Title,
					arrangementLabel = item.MusicalVersion?.Arrangement?.Label,
					voiceConfiguration = item.MusicalVersion?.Arrangement?.VoiceConfiguration,
					musicalVersionLabel = item.MusicalVersion?.Label,
					musicalKey = item.MusicalVersion?.MusicalKey,
				})
				.ToList()
			: [];
		// Members never see the concurrency token.
		if (!isEditor)
		{
			return new
			{
				revisionId = revision.Id,
				revisionNumber = revision.Number,
				confirmedAt = confirmation.CreatedAt,
				updatedAt = confirmation.UpdatedAt,
				upToDate,
				actual,
				skipped,
			};
		}
		return new
		{
			revisionId = revision.Id,
			revisionNumber = revision.Number,
			confirmedAt = confirmation.CreatedAt,
			updatedAt = confirmation.UpdatedAt,
			upToDate,
			rowVersion = confirmation.RowVersion,
			actual,
			skipped,
		};
	}

	private static object ActualEntry(ConfirmationView view, Performance row, ProgrammeItem? planned, bool added)
	{
		var chain = row.MusicalVersion?.Arrangement;
		var differs = planned is not null && planned.MusicalVersionId != row.MusicalVersionId;
		return new
		{
			performanceId = row.Id,
			programmeItemId = row.ProgrammeItemId,
			added,
			differsFromPlan = differs,
			songId = row.SongId,
			songTitle = view.SongTitles.GetValueOrDefault(row.SongId),
			arrangementId = row.ArrangementId,
			musicalVersionId = row.MusicalVersionId,
			arrangementLabel = chain?.Label,
			voiceConfiguration = chain?.VoiceConfiguration,
			musicalVersionLabel = row.MusicalVersion?.Label,
			musicalKey = row.MusicalVersion?.MusicalKey,
			evidenceStatus = row.EvidenceStatus,
			plannedMusicalVersionLabel = differs ? planned!.MusicalVersion?.Label : null,
		};
	}

	/// <summary>
	/// The confirmation's owned occurrences and the event's hand-entered
	/// (unowned) ARC-028 occurrences, with their song titles.
	/// </summary>
	private sealed record ConfirmationView(
		List<Performance> Owned, List<Performance> Unowned, IReadOnlyDictionary<Guid, string> SongTitles,
		IReadOnlyDictionary<Guid, int> PassageCounts);

	private static async Task<ConfirmationView> LoadViewAsync(
		ArchiveDbContext db, Guid eventId, ProgrammeConfirmation? confirmation, CancellationToken token)
	{
		var owned = confirmation is null
			? []
			: await db.Performances.AsNoTracking()
				.Include(p => p.MusicalVersion).ThenInclude(v => v!.Arrangement)
				.Where(p => p.ConfirmationId == confirmation.Id)
				.OrderBy(p => p.Position).ThenBy(p => p.Id)
				.ToListAsync(token);
		var unowned = await db.Performances.AsNoTracking()
			.Include(p => p.MusicalVersion).ThenInclude(v => v!.Arrangement)
			.Where(p => p.EventId == eventId && p.ConfirmationId == null)
			.OrderBy(p => p.Position).ThenBy(p => p.Id)
			.ToListAsync(token);
		var songIds = owned.Concat(unowned).Select(p => p.SongId).Distinct().ToList();
		var titles = await db.Songs.AsNoTracking()
			.Where(s => songIds.Contains(s.Id))
			.ToDictionaryAsync(s => s.Id, s => s.Title, token);
		// ARC-032: how many recording passages hang on each occurrence, so
		// the review can warn before a skip that would be refused.
		var passageCounts = (await db.RecordingPassages.AsNoTracking()
			.Where(p => p.EventId == eventId)
			.GroupBy(p => p.PerformanceId)
			.Select(g => new { PerformanceId = g.Key, Count = g.Count() })
			.ToListAsync(token)).ToDictionary(g => g.PerformanceId, g => g.Count);
		return new ConfirmationView(owned, unowned, titles, passageCounts);
	}

	/// <summary>
	/// The editor's review of one published revision: every planned entry
	/// with its outcome and the linked occurrence (open / sung / skipped /
	/// unconfirmed when the evidence was later downgraded), suggested
	/// occurrences for adoption — earlier occurrences of an older revision
	/// and hand-entered ARC-028 occurrences of the same song — the added
	/// songs, the hand-entered rows that would be counted twice if not
	/// adopted, and whether the event can be confirmed at all.
	/// </summary>
	private static async Task<object> ReviewAsync(
		ArchiveDbContext db, EventProgramme programme, Guid newestId, Guid revisionId,
		string? unconfirmableReason, CancellationToken token)
	{
		// Both come from the chain-loaded programme handed in: display
		// labels exist only there.
		var revision = programme.Revisions.First(r => r.Id == revisionId);
		var newest = programme.Revisions.First(r => r.Id == newestId);
		var confirmation = await db.ProgrammeConfirmations.AsNoTracking()
			.FirstOrDefaultAsync(c => c.ProgrammeId == programme.Id, token);
		var view = await LoadViewAsync(db, programme.EventId, confirmation, token);
		var planned = revision.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
		var plannedIds = planned.Select(i => i.Id).ToHashSet();
		var byItem = view.Owned.Where(r => r.ProgrammeItemId is { } id && plannedIds.Contains(id))
			.ToDictionary(r => r.ProgrammeItemId!.Value);
		var boundHere = confirmation is not null && confirmation.RevisionId == revision.Id;
		// Carry-over: occurrences still linked to entries of an older
		// revision are offered to the same song's entries of this one, in
		// order. Hand-entered occurrences of the same song follow. The editor
		// adopts a suggestion explicitly by sending its id.
		var carry = new List<Performance>();
		if (confirmation is not null && !boundHere && revision.Id == newest.Id)
			carry = view.Owned.Where(r => r.ProgrammeItemId is not null).ToList();
		var pool = view.Unowned.ToList();
		var items = new List<object>();
		foreach (var item in planned)
		{
			byItem.TryGetValue(item.Id, out var row);
			string state;
			if (row is not null)
				state = row.EvidenceStatus == PerformanceEvidenceStatus.Confirmed ? "sung" : StateUnconfirmed;
			else
				state = boundHere ? OutcomeSkipped : StateOpen;
			Performance? suggested = null;
			if (row is null)
			{
				suggested = carry.FirstOrDefault(c => c.SongId == item.SongId)
					?? pool.FirstOrDefault(c => c.SongId == item.SongId);
				if (suggested is not null && !carry.Remove(suggested))
					pool.Remove(suggested);
			}
			items.Add(new
			{
				programmeItemId = item.Id,
				position = item.Position,
				songId = item.SongId,
				songTitle = item.MusicalVersion?.Arrangement?.Song?.Title,
				arrangementId = item.ArrangementId,
				musicalVersionId = item.MusicalVersionId,
				arrangementLabel = item.MusicalVersion?.Arrangement?.Label,
				voiceConfiguration = item.MusicalVersion?.Arrangement?.VoiceConfiguration,
				musicalVersionLabel = item.MusicalVersion?.Label,
				musicalKey = item.MusicalVersion?.MusicalKey,
				note = item.Note,
				outcome = state,
				performance = row is null ? null : PerformanceView(view, row),
				suggestedPerformanceId = suggested?.Id,
				suggestedPerformance = suggested is null ? null : PerformanceView(view, suggested),
			});
		}
		var additions = view.Owned.Where(r => r.ProgrammeItemId is null)
			.Select(row => PerformanceView(view, row)).ToList();
		// Hand-entered rows of a planned song: counted a second time when the
		// confirmation creates its own occurrence instead of adopting them.
		var plannedSongs = planned.Select(i => i.SongId).ToHashSet();
		var unownedOccurrences = view.Unowned.Where(r => plannedSongs.Contains(r.SongId))
			.Select(row => PerformanceView(view, row)).ToList();
		return new
		{
			revision = new { id = revision.Id, number = revision.Number, publishedAt = revision.PublishedAt },
			currentRevisionId = newest.Id,
			upToDate = revision.Id == newest.Id,
			confirmable = unconfirmableReason is null,
			unconfirmableReason,
			confirmation = confirmation is null
				? null
				: new
				{
					id = confirmation.Id,
					revisionId = confirmation.RevisionId,
					revisionNumber = programme.Revisions
						.FirstOrDefault(r => r.Id == confirmation.RevisionId)?.Number,
					confirmedAt = confirmation.CreatedAt,
					updatedAt = confirmation.UpdatedAt,
				},
			rowVersion = confirmation?.RowVersion ?? 0u,
			items,
			additions,
			unownedOccurrences,
		};
	}

	private static object PerformanceView(ConfirmationView view, Performance row) => new
	{
		id = row.Id,
		songId = row.SongId,
		songTitle = view.SongTitles.GetValueOrDefault(row.SongId),
		arrangementId = row.ArrangementId,
		musicalVersionId = row.MusicalVersionId,
		arrangementLabel = row.MusicalVersion?.Arrangement?.Label,
		voiceConfiguration = row.MusicalVersion?.Arrangement?.VoiceConfiguration,
		musicalVersionLabel = row.MusicalVersion?.Label,
		musicalKey = row.MusicalVersion?.MusicalKey,
		evidenceStatus = row.EvidenceStatus,
		sourceNote = row.SourceNote,
		programmeItemId = row.ProgrammeItemId,
		rowVersion = row.RowVersion,
		passageCount = view.PassageCounts.GetValueOrDefault(row.Id),
	};

	/// <summary>One occurrence the request asks for.</summary>
	private sealed class PlannedEntry
	{
		public required Guid SongId { get; init; }

		public Guid? ArrangementId { get; init; }

		public Guid? MusicalVersionId { get; init; }

		public Guid? ProgrammeItemId { get; init; }

		public Performance? Existing { get; init; }

		public uint? RequestedRowVersion { get; init; }

		/// <summary>Retry key stored on newly created rows.</summary>
		public required string RetryKey { get; init; }

		/// <summary>
		/// True when the entry creates a row, adopts a hand-entered one or
		/// differs from the stored one.
		/// </summary>
		public bool NeedsWrite => Existing is null
			|| Existing.ConfirmationId is null
			|| Existing.SongId != SongId
			|| Existing.ArrangementId != ArrangementId
			|| Existing.MusicalVersionId != MusicalVersionId
			|| Existing.ProgrammeItemId != ProgrammeItemId
			|| Existing.EvidenceStatus != PerformanceEvidenceStatus.Confirmed;
	}

	private sealed record Plan(List<PlannedEntry> Entries, HashSet<Guid> Claimed);

	/// <summary>
	/// Validates the request against the revision, the catalogue and the
	/// occurrences it may adopt (the confirmation's own and the event's
	/// hand-entered ones), then resolves every requested occurrence to an
	/// existing row (by adopted id, planned entry or retry key) or a new one.
	/// An adopted row must keep its song (ARC-028 keeps it immutable).
	/// Returns the German ProblemDetails error instead when anything is off;
	/// no row has been touched at that point.
	/// </summary>
	private static async Task<(Plan? Plan, IResult? Error)> PlanAsync(
		ArchiveDbContext db, SaveConfirmationRequest body, ProgrammeRevision revision,
		List<Performance> owned, List<Performance> unowned, CancellationToken token)
	{
		var itemRequests = body.Items!;
		var additionRequests = body.Additions ?? [];
		if (itemRequests.Any(e => e is null) || additionRequests.Any(e => e is null))
			return Invalid();
		if (additionRequests.Where(e => e.ClientKey is not null)
			.GroupBy(e => e.ClientKey).Any(g => g.Count() > 1))
			return (null, Results.Problem(statusCode: 400, title: DuplicateKeyMessage));
		var plannedItems = revision.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
		var plannedById = plannedItems.ToDictionary(i => i.Id);
		// The review is complete: every planned entry is stated exactly once
		// (sung or skipped), so a missing entry is never read as "skipped".
		if (itemRequests.Any(e => e.ProgrammeItemId is not { } id || !plannedById.ContainsKey(id))
			|| itemRequests.GroupBy(e => e.ProgrammeItemId).Any(g => g.Count() > 1)
			|| itemRequests.Count != plannedItems.Count
			|| itemRequests.Any(e => !IsOutcome(e.Outcome)))
			return Invalid();
		var adoptableById = owned.Concat(unowned).ToDictionary(p => p.Id);
		var versionIds = itemRequests.Where(e => e.MusicalVersionId is not null).Select(e => e.MusicalVersionId!.Value)
			.Concat(additionRequests.Where(e => e.MusicalVersionId is not null).Select(e => e.MusicalVersionId!.Value))
			.Distinct().ToList();
		var versions = await db.MusicalVersions.AsNoTracking()
			.Include(v => v.Arrangement)
			.Where(v => versionIds.Contains(v.Id))
			.ToDictionaryAsync(v => v.Id, token);
		var additionSongIds = additionRequests.Where(e => e.SongId is not null).Select(e => e.SongId!.Value)
			.Distinct().ToList();
		var knownSongs = (await db.Songs.AsNoTracking()
			.Where(s => additionSongIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(token)).ToHashSet();

		var entries = new List<PlannedEntry>();
		var claimed = new HashSet<Guid>();
		// Entries run in plan order so new occurrences append in the order
		// the programme was sung, then the added songs in request order.
		foreach (var item in plannedItems)
		{
			var request = itemRequests.First(e => e.ProgrammeItemId == item.Id);
			if (request.Outcome!.Equals(OutcomeSkipped, StringComparison.OrdinalIgnoreCase))
				continue;
			Performance? existing;
			if (request.PerformanceId is { } adoptedId)
			{
				// Adopting stays within the event, takes an unclaimed row of the
				// same song, and must not displace one linked to a different
				// entry of this very revision.
				if (!adoptableById.TryGetValue(adoptedId, out existing) || !claimed.Add(adoptedId)
					|| existing.SongId != item.SongId
					|| (existing.ProgrammeItemId is { } linked && linked != item.Id && plannedById.ContainsKey(linked))
					|| owned.Any(p => p.Id != adoptedId && p.ProgrammeItemId == item.Id))
					return Invalid();
			}
			else
			{
				existing = owned.FirstOrDefault(p => p.ProgrammeItemId == item.Id);
				if (existing is not null && !claimed.Add(existing.Id))
					return Invalid();
			}
			Guid? arrangementId = item.ArrangementId;
			Guid? versionId = item.MusicalVersionId;
			if (request.MusicalVersionId is { } correctedId)
			{
				if (!versions.TryGetValue(correctedId, out var corrected)
					|| corrected.Arrangement?.SongId != item.SongId)
					return (null, Results.Problem(statusCode: 400, title: VersionMismatchMessage));
				arrangementId = corrected.ArrangementId;
				versionId = corrected.Id;
			}
			entries.Add(new PlannedEntry
			{
				SongId = item.SongId,
				ArrangementId = arrangementId,
				MusicalVersionId = versionId,
				ProgrammeItemId = item.Id,
				Existing = existing,
				RequestedRowVersion = request.RowVersion,
				RetryKey = $"programme-item:{item.Id:N}",
			});
		}
		foreach (var request in additionRequests)
		{
			if (request.SongId is not { } songId || !knownSongs.Contains(songId))
				return (null, Results.Problem(statusCode: 404, title: PerformanceEndpoints.SongNotFoundMessage));
			Guid? arrangementId = null;
			Guid? versionId = null;
			if (request.MusicalVersionId is { } chosenId)
			{
				if (!versions.TryGetValue(chosenId, out var chosen) || chosen.Arrangement?.SongId != songId)
					return (null, Results.Problem(statusCode: 404, title: PerformanceEndpoints.FassungPasstNichtMessage));
				arrangementId = chosen.ArrangementId;
				versionId = chosen.Id;
			}
			var retryKey = $"programme-addition:{(request.ClientKey ?? Guid.CreateVersion7()):N}";
			Performance? existing;
			if (request.PerformanceId is { } adoptedId)
			{
				if (!adoptableById.TryGetValue(adoptedId, out existing) || existing.ProgrammeItemId is not null
					|| existing.SongId != songId || !claimed.Add(adoptedId))
					return Invalid();
			}
			else
			{
				existing = request.ClientKey is null
					? null
					: owned.FirstOrDefault(p => p.IdempotencyKey == retryKey && p.ProgrammeItemId is null);
				if (existing is not null && !claimed.Add(existing.Id))
					return Invalid();
			}
			entries.Add(new PlannedEntry
			{
				SongId = songId,
				ArrangementId = arrangementId,
				MusicalVersionId = versionId,
				Existing = existing,
				RequestedRowVersion = request.RowVersion,
				RetryKey = retryKey,
			});
		}
		return (new Plan(entries, claimed), null);

		static (Plan?, IResult?) Invalid()
			=> (null, Results.Problem(statusCode: 400, title: InvalidMessage));
	}

	private static bool IsOutcome(string? outcome)
		=> outcome is not null
			&& (outcome.Equals(OutcomeSung, StringComparison.OrdinalIgnoreCase)
				|| outcome.Equals(OutcomeSkipped, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// True only when the event date is certainly after today at the date's
	/// own precision: a later year, a later month of this year or a later day
	/// of this month. An unknown or coarser date cannot honestly be called
	/// future, and today itself is allowed (an evening concert).
	/// </summary>
	private static bool IsDefinitelyFuture(ChoirEvent choirEvent, DateOnly today)
	{
		if (choirEvent.DateYear is not { } year)
			return false;
		if (year != today.Year)
			return year > today.Year;
		if (choirEvent.DateMonth is not { } month)
			return false;
		if (month != today.Month)
			return month > today.Month;
		return choirEvent.DateDay is { } day && day > today.Day;
	}

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

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
}
