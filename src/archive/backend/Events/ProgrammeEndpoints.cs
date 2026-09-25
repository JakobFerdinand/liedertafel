using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

public sealed record SaveProgrammeItemsRequest(List<ProgrammeItemRequest>? Items, uint RowVersion);

/// <summary>One requested programme entry; the array order defines the position.</summary>
public sealed record ProgrammeItemRequest(Guid? Id, Guid? SongId, Guid? MusicalVersionId, string? Note);

public sealed record PublishProgrammeRequest(uint RowVersion);

/// <summary>
/// Programme API (ARC-026): editors order songs for an event's appearance
/// and explicitly publish the revision members should follow. Reads use the
/// shared database decision: members see only the newest published revision
/// of a member-visible event (see <see cref="ProgrammeVisibility"/> and
/// <see cref="EventVisibility"/>), editors/administrators also the working
/// draft. All mutations require the Editor or Administrator role from that
/// decision, CSRF and antiforgery validation; members receive 403 and
/// unauthenticated or revoked callers receive 401.
/// </summary>
public static class ProgrammeEndpoints
{
	public const string ConcurrencyMessage = "Der Programmentwurf wurde zwischenzeitlich geändert.";

	public const string AlreadyPublishedMessage = "Das Programm wurde bereits veröffentlicht.";

	public const string EmptyProgrammeMessage = "Das Programm enthält keine Lieder.";

	public const string UnavailableVersionsMessage = "Das Programm enthält nicht verfügbare Liedfassungen.";

	public const string MissingVersionMessage = "Ein Programmpunkt verweist auf eine nicht vorhandene Liedfassung.";

	public const string NoteTooLongMessage = "Die Notiz ist zu lang.";

	public const string InvalidItemMessage = "Ein Programmpunkt ist ungültig.";

	/// <summary>Field maximum for the per-entry note (ARC-026 schema).</summary>
	public const int NoteMaxLength = 500;

	public static void MapProgrammeEndpoints(this WebApplication app)
	{
		app.MapPut("/api/events/{eventId}/programme/items", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid eventId, CancellationToken token,
			SaveProgrammeItemsRequest? body) =>
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
			var programme = await db.Programmes
				.Include(p => p.Revisions).ThenInclude(r => r.Items)
				.FirstOrDefaultAsync(p => p.EventId == eventId, token);
			// Stale edits answer before any validation or write: the client
			// must reload the fresh state and redo the change.
			if (programme is not null && body?.RowVersion != programme.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			// Full ordered replacement is all-or-nothing: validate every
			// requested entry against the catalogue before touching a row.
			var (resolved, validationError) = await ResolveItemsAsync(db, body?.Items ?? [], token);
			if (validationError is not null)
				return validationError;
			var now = time.GetUtcNow();
			if (programme is null)
			{
				// The first save creates the programme and its first draft
				// revision implicitly; rowVersion is ignored on creation.
				programme = new EventProgramme
				{
					EventId = eventId,
					CreatedAt = now,
					CreatedByAccountId = decision!.AccountId,
					UpdatedAt = now,
					UpdatedByAccountId = decision.AccountId,
				};
				db.Programmes.Add(programme);
			}
			var working = ProgrammeVisibility.WorkingDraft(programme);
			if (working is null)
			{
				// Post-publication editing: a fresh draft revision (next
				// number) seeded empty, so published revisions stay frozen.
				working = new ProgrammeRevision
				{
					ProgrammeId = programme.Id,
					Number = programme.Revisions.Count == 0
						? 1 : programme.Revisions.Max(r => r.Number) + 1,
					CreatedAt = now,
					CreatedByAccountId = decision!.AccountId,
				};
				db.ProgrammeRevisions.Add(working);
			}
			// Stable item IDs within one working revision: entries with a
			// known item ID update in place, the rest are created fresh and
			// all rows renumber 1..n per array order. The snapshot keeps the
			// freshly added entries (EF fixes them into the collection) out
			// of the removal pass below.
			var existingItems = working.Items.ToList();
			var matchById = existingItems.ToDictionary(i => i.Id);
			// Entries absent from the request are removed (full replacement).
			var requestedIds = resolved
				.Where(i => i.RequestedId is not null)
				.Select(i => i.RequestedId!.Value)
				.ToHashSet();
			foreach (var orphan in existingItems.Where(i => !requestedIds.Contains(i.Id)))
				db.ProgrammeItems.Remove(orphan);
			// Two-phase renumber: landing the final 1..n positions in one
			// pass is a dependency cycle for the relational command batch —
			// two swapped surviving rows each wait for the other's unique
			// (RevisionId, Position) slot, so EF throws before any SQL runs.
			// Phase 1 parks every entry strictly above all current positions,
			// phase 2 lands the final order. The parking base is also raised
			// to the requested count when it grows: otherwise phase 2's
			// targets (1..n) would overlap phase 1's parked rows and the
			// provider's statement order could still trip the unique index.
			var touched = new List<ProgrammeItem>(resolved.Count);
			var parkedPosition = Math.Max(existingItems.Count, resolved.Count);
			foreach (var entry in resolved)
			{
				ProgrammeItem? item = entry.RequestedId is { } id
					&& matchById.TryGetValue(id, out var match) ? match : null;
				if (item is null)
				{
					item = new ProgrammeItem
					{
						RevisionId = working.Id,
						SongId = entry.SongId,
						ArrangementId = entry.ArrangementId,
						MusicalVersionId = entry.MusicalVersionId,
						Note = entry.Note,
					};
					db.ProgrammeItems.Add(item);
				}
				else
				{
					item.SongId = entry.SongId;
					item.ArrangementId = entry.ArrangementId;
					item.MusicalVersionId = entry.MusicalVersionId;
					item.Note = entry.Note;
				}
				item.Position = ++parkedPosition;
				touched.Add(item);
			}
			// Programme attribution and the concurrency bump commit with the
			// revision/item changes in one save; the event row stays untouched.
			programme.UpdatedAt = now;
			programme.UpdatedByAccountId = decision!.AccountId;
			programme.RowVersion++;
			// One explicit transaction keeps the transient non-contiguous
			// positions unobservable on PostgreSQL; only phase 1 can hit the
			// concurrency conflict (the token bump happens there) — a failure
			// rolls the whole renumber back.
			await using var transaction = await db.Database.BeginTransactionAsync(token);
			try
			{
				await db.SaveChangesAsync(token);
				// Phase 2: the final contiguous positions 1..n in request order.
				var finalPosition = 1;
				foreach (var item in touched)
					item.Position = finalPosition++;
				await db.SaveChangesAsync(token);
				await transaction.CommitAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			var embed = await LoadDetailEmbedAsync(db, IsEditor(decision!), eventId, token);
			return Results.Ok(new { programme = embed });
		}).DisableAntiforgery();

		app.MapPost("/api/events/{eventId}/programme/publish", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid eventId, CancellationToken token,
			PublishProgrammeRequest? body) =>
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
			var programme = await db.Programmes
				.Include(p => p.Revisions).ThenInclude(r => r.Items)
				.FirstOrDefaultAsync(p => p.EventId == eventId, token);
			// Without a programme (nothing saved yet) or a working draft
			// (everything already published) there is nothing to publish;
			// ARC-026 has no unpublish, superseding means a newer revision.
			if (programme is null)
				return Results.Problem(statusCode: 409, title: AlreadyPublishedMessage);
			var working = ProgrammeVisibility.WorkingDraft(programme);
			if (working is null)
				return Results.Problem(statusCode: 409, title: AlreadyPublishedMessage);
			if (body?.RowVersion != programme.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			if (working.Items.Count == 0)
				return Results.Problem(statusCode: 400, title: EmptyProgrammeMessage);
			// Draft selections were allowed while drafting; only member-
			// visible, still existing chains may be published. The referenced
			// chains are resolved directly from the catalogue so the check
			// never depends on the working draft's loaded navigation.
			var itemVersionIds = working.Items.Select(i => i.MusicalVersionId).ToList();
			var availableVersions = await db.MusicalVersions.AsNoTracking()
				.Include(v => v.Arrangement).ThenInclude(a => a.Song)
				.Where(v => itemVersionIds.Contains(v.Id) && v.Arrangement.Song.PublishedAt != null)
				.Select(v => v.Id)
				.ToListAsync(token);
			if (working.Items.Any(item => !availableVersions.Contains(item.MusicalVersionId)))
				return Results.Problem(statusCode: 409, title: UnavailableVersionsMessage);
			// The publication stamp and the programme bump commit together in
			// one save. Nothing else is written: no performance records, the
			// event row (and its own publish stamps) stays untouched — merely
			// passing the event date never marks songs performed.
			working.PublishedAt = time.GetUtcNow();
			working.PublishedByAccountId = decision!.AccountId;
			programme.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			var embed = await LoadDetailEmbedAsync(db, IsEditor(decision!), eventId, token);
			return Results.Ok(new { programme = embed });
		}).DisableAntiforgery();

		app.MapGet("/api/programmes", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			// One materialized set so InMemory tests and PostgreSQL agree:
			// the precision-aware upcoming filter and sorting run in C#
			// (unpaginated archive scale).
			var rows = await db.Programmes.AsNoTracking()
				.Include(p => p.Event)
				.Include(p => p.Revisions).ThenInclude(r => r.Items)
				.ToListAsync(token);
			var today = ViennaToday(time.GetUtcNow());
			var upcoming = new List<ProgrammeRow>();
			foreach (var programme in rows)
			{
				if (!isEditor && !EventVisibility.IsMemberVisible(programme.Event))
					continue;
				var published = ProgrammeVisibility.NewestPublished(programme);
				if (published is null || !IsUpcoming(programme.Event, today))
					continue;
				upcoming.Add(new ProgrammeRow(
					programme.Event.Id, programme.Event.Kind, programme.Event.Title,
					programme.Event.Venue, programme.Event.StartTime,
					programme.Event.DateYear, programme.Event.DateMonth,
					programme.Event.DateDay, programme.Event.DateApproximate,
					published.PublishedAt!.Value, published.Items.Count));
			}
			upcoming.Sort(CompareUpcoming);
			return Results.Ok(new { programmes = upcoming.Select(ProgrammeListItem) });
		});
	}

	/// <summary>
	/// Role-filtered programme embed for the event detail (ARC-026): members
	/// receive only the newest published revision, editors additionally the
	/// working draft. Before the first publication members (and editors)
	/// receive null — an absent programme is indistinguishable from no
	/// programme, so the frontend shows the "anlegen" affordance.
	/// </summary>
	public static async Task<object?> LoadDetailEmbedAsync(
		ArchiveDbContext db, bool isEditor, Guid eventId, CancellationToken token)
	{
		var programme = await db.Programmes.AsNoTracking()
			.Include(p => p.Revisions).ThenInclude(r => r.Items)
			.ThenInclude(i => i.MusicalVersion).ThenInclude(v => v!.Arrangement).ThenInclude(a => a.Song)
			.FirstOrDefaultAsync(p => p.EventId == eventId, token);
		if (programme is null)
			return null;
		var published = ProgrammeVisibility.NewestPublished(programme);
		var working = isEditor ? ProgrammeVisibility.WorkingDraft(programme) : null;
		if (!isEditor && published is null)
			return null;
		return Embed(programme, working, published);
	}

	private static object Embed(EventProgramme programme, ProgrammeRevision? working, ProgrammeRevision? published) => new
	{
		id = programme.Id,
		rowVersion = programme.RowVersion,
		working = working is null ? null : WorkingEmbed(programme, working),
		published = published is null ? null : PublishedEmbed(published),
	};

	private static object WorkingEmbed(EventProgramme programme, ProgrammeRevision revision) => new
	{
		id = revision.Id,
		number = revision.Number,
		// The working embed's updatedAt tracks the programme aggregate (it
		// bumps on every programme edit next to rowVersion), not the frozen
		// revision creation instant.
		updatedAt = programme.UpdatedAt,
		items = OrderedItems(revision),
	};

	private static object PublishedEmbed(ProgrammeRevision revision) => new
	{
		id = revision.Id,
		number = revision.Number,
		publishedAt = revision.PublishedAt,
		items = OrderedItems(revision),
	};

	private static IEnumerable<object> OrderedItems(ProgrammeRevision revision)
		=> revision.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).Select(ItemEmbed);

	private static object ItemEmbed(ProgrammeItem item) => new
	{
		id = item.Id,
		position = item.Position,
		songId = item.SongId,
		arrangementId = item.ArrangementId,
		musicalVersionId = item.MusicalVersionId,
		// Display fields come from the referenced chain; unknown values stay
		// null instead of inventing "unbekannt" placeholders (ARC-026).
		songTitle = item.MusicalVersion?.Arrangement?.Song?.Title,
		arrangementLabel = item.MusicalVersion?.Arrangement?.Label,
		voiceConfiguration = item.MusicalVersion?.Arrangement?.VoiceConfiguration,
		musicalVersionLabel = item.MusicalVersion?.Label,
		musicalKey = item.MusicalVersion?.MusicalKey,
		note = item.Note,
	};

	/// <summary>
	/// Validates every requested entry against the catalogue before any row
	/// is written: the musical version must exist with its arrangement and
	/// song, and the song must match the version's arrangement parent.
	/// Returns the resolved entries in request order or the German
	/// ProblemDetails error.
	/// </summary>
	private static async Task<(List<ResolvedItem> Items, IResult? Error)> ResolveItemsAsync(
		ArchiveDbContext db, List<ProgrammeItemRequest> requested, CancellationToken token)
	{
		// A null entry or a duplicated item id would silently overwrite an
		// entry and break the 1..n contiguity — rejected before any write.
		if (requested.Any(entry => entry is null))
			return ([], Results.Problem(statusCode: 400, title: InvalidItemMessage));
		if (requested.Where(i => i.Id is not null)
			.GroupBy(i => i.Id!.Value).Any(group => group.Count() > 1))
			return ([], Results.Problem(statusCode: 400, title: InvalidItemMessage));
		var versionIds = requested
			.Where(i => i.MusicalVersionId is not null)
			.Select(i => i.MusicalVersionId!.Value)
			.Distinct()
			.ToList();
		var versions = await db.MusicalVersions.AsNoTracking()
			.Include(v => v.Arrangement).ThenInclude(a => a.Song)
			.Where(v => versionIds.Contains(v.Id))
			.ToDictionaryAsync(v => v.Id, token);
		var resolved = new List<ResolvedItem>(requested.Count);
		foreach (var entry in requested)
		{
			if (entry.SongId is not { } songId
				|| !versions.TryGetValue(entry.MusicalVersionId ?? Guid.Empty, out var version)
				|| version.Arrangement?.Song is null
				|| version.Arrangement.SongId != songId)
				return ([], Results.Problem(statusCode: 400, title: MissingVersionMessage));
			var note = entry.Note?.Trim();
			if (note is { Length: > NoteMaxLength })
				return ([], Results.Problem(statusCode: 400, title: NoteTooLongMessage));
			resolved.Add(new ResolvedItem(entry.Id, songId, version.ArrangementId, version.Id,
				string.IsNullOrEmpty(note) ? null : note));
		}
		return (resolved, null);
	}

	private sealed record ResolvedItem(
		Guid? RequestedId, Guid SongId, Guid ArrangementId, Guid MusicalVersionId, string? Note);

	/// <summary>
	/// Honest precision-aware ARC-026 "upcoming" decision: exact dates count
	/// from today on, month precision from the current month on, year
	/// precision from the current year on, and unknown dates always count as
	/// upcoming (they cannot honestly be called past).
	/// </summary>
	private static bool IsUpcoming(ChoirEvent choirEvent, DateOnly today)
	{
		if (choirEvent.DateYear is not { } year)
			return true;
		if (year != today.Year)
			return year > today.Year;
		if (choirEvent.DateMonth is not { } month)
			return true;
		if (month != today.Month)
			return month > today.Month;
		if (choirEvent.DateDay is not { } day)
			return true;
		return day >= today.Day;
	}

	/// <summary>
	/// The choir's calendar day (Vienna local time), falling back to UTC when
	/// the host lacks the zone definition.
	/// </summary>
	private static DateOnly ViennaToday(DateTimeOffset utcNow)
	{
		try
		{
			return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
				utcNow.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna")));
		}
		catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
		{
			return DateOnly.FromDateTime(utcNow.UtcDateTime);
		}
	}

	/// <summary>
	/// Upcoming order, soonest first: known dates ascending by their known
	/// components (coarser precision sorts first within the same year);
	/// unknown dates sort last, by id.
	/// </summary>
	private static int CompareUpcoming(ProgrammeRow left, ProgrammeRow right)
	{
		if (left.DateYear is null || right.DateYear is null)
		{
			if (left.DateYear == right.DateYear)
				return left.EventId.CompareTo(right.EventId);
			return left.DateYear is null ? 1 : -1;
		}
		var yearOrder = left.DateYear.Value.CompareTo(right.DateYear.Value);
		if (yearOrder != 0)
			return yearOrder;
		var monthOrder = (left.DateMonth ?? 0).CompareTo(right.DateMonth ?? 0);
		if (monthOrder != 0)
			return monthOrder;
		var dayOrder = (left.DateDay ?? 0).CompareTo(right.DateDay ?? 0);
		if (dayOrder != 0)
			return dayOrder;
		return left.EventId.CompareTo(right.EventId);
	}

	private static object ProgrammeListItem(ProgrammeRow row) => new
	{
		eventId = row.EventId,
		eventTitle = row.Title,
		kind = row.Kind,
		dateDisplay = EventDate.Display(row.DateYear, row.DateMonth, row.DateDay, row.DateApproximate),
		datePrecision = EventDate.Precision(row.DateYear, row.DateMonth, row.DateDay),
		dateApproximate = row.DateApproximate,
		venue = row.Venue,
		startTime = row.StartTime,
		publishedAt = row.PublishedAt,
		itemCount = row.ItemCount,
	};

	private sealed record ProgrammeRow(Guid EventId, string Kind, string Title, string? Venue,
		string? StartTime, int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
		DateTimeOffset PublishedAt, int ItemCount);

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		// Cookie presence first for a useful signed-out state: revoked or
		// signed-out callers have no principal and get 401.
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		return (decision, null);
	}

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!IsEditor(decision))
			return (null, Results.Problem(statusCode: 403, title: EventEndpoints.ForbiddenMessage));
		return (decision, null);
	}
}
