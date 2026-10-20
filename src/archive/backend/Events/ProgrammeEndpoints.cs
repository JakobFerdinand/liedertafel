using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Recordings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

/// <summary>
/// The full ordered Programm; <c>RowVersion</c> is required once a programme exists.
/// </summary>
public sealed record SaveProgrammeItemsRequest(List<ProgrammeItemRequest>? Items, uint? RowVersion);

/// <summary>
/// One requested programme entry; the array order defines the position. An
/// entry names a Lied.
/// </summary>
public sealed record ProgrammeItemRequest(Guid? Id, Guid? LiedId, string? Note);

/// <summary>
/// Programm API (#160): an Auftritt has one editable Programm, which is also
/// the record of what was sung. Saving the full ordered list publishes it;
/// there are no revisions, drafts or confirmation, and a saved Auftritt is
/// visible to every member. Mutations require the
/// Editor or Administrator role, CSRF and antiforgery validation; members
/// receive 403 and unauthenticated or revoked callers receive 401.
/// </summary>
public static class ProgrammeEndpoints
{
	public const string ConcurrencyMessage = "Das Programm wurde zwischenzeitlich geändert.";

	public const string MissingLiedMessage = "Ein Programmpunkt verweist auf ein nicht vorhandenes Lied.";

	public const string NoteTooLongMessage = "Die Notiz ist zu lang.";

	public const string InvalidItemMessage = "Ein Programmpunkt ist ungültig.";

	/// <summary>Field maximum for the per-entry note.</summary>
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
				.Include(p => p.Items)
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
				// The first save creates the programme; rowVersion is ignored then.
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
			// Stable item IDs: entries with a known item ID update in place,
			// the rest are created fresh and all rows renumber 1..n per array
			// order. The snapshot keeps the freshly added entries (EF fixes
			// them into the collection) out of the removal pass below.
			var existingItems = programme.Items.ToList();
			var matchById = existingItems.ToDictionary(i => i.Id);
			// Entries absent from the request are removed (full replacement).
			var requestedIds = resolved
				.Where(i => i.RequestedId is not null)
				.Select(i => i.RequestedId!.Value)
				.ToHashSet();
			var orphans = existingItems.Where(i => !requestedIds.Contains(i.Id)).ToList();
			// An entry with Zeitmarken is never removed behind the editor's
			// back: the marks have to go first.
			if (await RecordingPassages.BlockedMessageAsync(db, orphans.Select(i => i.Id).ToList(), token) is { } blocked)
				return Results.Problem(statusCode: 409, title: blocked);
			foreach (var orphan in orphans)
				db.ProgrammeItems.Remove(orphan);
			// Two-phase renumber: landing the final 1..n positions in one
			// pass is a dependency cycle for the relational command batch —
			// two swapped surviving rows each wait for the other's unique
			// (ProgrammeId, Position) slot, so EF throws before any SQL runs.
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
						ProgrammeId = programme.Id,
						LiedId = entry.LiedId,
						Note = entry.Note,
					};
					db.ProgrammeItems.Add(item);
				}
				else
				{
					item.LiedId = entry.LiedId;
					item.Note = entry.Note;
				}
				item.Position = ++parkedPosition;
				touched.Add(item);
			}
			// Attribution and the concurrency bump commit with the item
			// changes in one save; the event row stays untouched.
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
			var embed = await LoadDetailEmbedAsync(db, eventId, token);
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
				.Include(p => p.Items)
				.ToListAsync(token);
			var today = EventDate.ViennaToday(time.GetUtcNow());
			var upcoming = new List<ProgrammeRow>();
			foreach (var programme in rows)
			{
				if (programme.Items.Count == 0 || !EventDate.IsUpcoming(
					programme.Event.DateYear, programme.Event.DateMonth, programme.Event.DateDay, today))
					continue;
				upcoming.Add(new ProgrammeRow(
					programme.Event.Id, programme.Event.Kind, programme.Event.Title,
					programme.Event.Venue, programme.Event.StartTime,
					programme.Event.DateYear, programme.Event.DateMonth,
					programme.Event.DateDay, programme.Event.DateApproximate,
					programme.UpdatedAt, programme.Items.Count));
			}
			upcoming.Sort(CompareUpcoming);
			return Results.Ok(new { programmes = upcoming.Select(ProgrammeListItem) });
		});
	}

	/// <summary>
	/// The programme embed for the event detail: the same for members and
	/// editors (the caller has already applied the event's visibility). When no
	/// programme exists yet, the embed is null, so the frontend shows the
	/// "anlegen" affordance.
	/// </summary>
	public static async Task<object?> LoadDetailEmbedAsync(
		ArchiveDbContext db, Guid eventId, CancellationToken token)
	{
		var programme = await db.Programmes.AsNoTracking()
			.Include(p => p.Items).ThenInclude(i => i.Lied)
			.FirstOrDefaultAsync(p => p.EventId == eventId, token);
		return programme is null ? null : Embed(programme);
	}

	private static object Embed(EventProgramme programme) => new
	{
		id = programme.Id,
		rowVersion = programme.RowVersion,
		updatedAt = programme.UpdatedAt,
		items = programme.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).Select(ItemEmbed).ToList(),
	};

	private static object ItemEmbed(ProgrammeItem item) => new
	{
		id = item.Id,
		position = item.Position,
		liedId = item.LiedId,
		liedTitle = item.Lied?.Title,
		voiceConfiguration = item.Lied?.VoiceConfiguration,
		musicalKey = item.Lied?.MusicalKey,
		note = item.Note,
	};

	/// <summary>
	/// Validates every requested entry against the Lieder before any row is
	/// written. Returns the resolved entries in request order or the German
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
		var liedIds = requested
			.Where(i => i.LiedId is not null)
			.Select(i => i.LiedId!.Value)
			.Distinct()
			.ToList();
		var knownLieder = (await db.Lieder.AsNoTracking()
			.Where(l => liedIds.Contains(l.Id)).Select(l => l.Id).ToListAsync(token)).ToHashSet();
		var resolved = new List<ResolvedItem>(requested.Count);
		foreach (var entry in requested)
		{
			var note = entry.Note?.Trim();
			if (note is { Length: > NoteMaxLength })
				return ([], Results.Problem(statusCode: 400, title: NoteTooLongMessage));
			var cleanNote = string.IsNullOrEmpty(note) ? null : note;
			if (entry.LiedId is not { } liedId || !knownLieder.Contains(liedId))
				return ([], Results.Problem(statusCode: 400, title: MissingLiedMessage));
			resolved.Add(new ResolvedItem(entry.Id, liedId, cleanNote));
		}
		return (resolved, null);
	}

	private sealed record ResolvedItem(Guid? RequestedId, Guid LiedId, string? Note);

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
		updatedAt = row.UpdatedAt,
		itemCount = row.ItemCount,
	};

	private sealed record ProgrammeRow(Guid EventId, string Kind, string Title, string? Venue,
		string? StartTime, int? DateYear, int? DateMonth, int? DateDay, bool DateApproximate,
		DateTimeOffset UpdatedAt, int ItemCount);

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
