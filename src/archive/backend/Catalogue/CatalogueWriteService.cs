using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Catalogue;

/// <summary>Result statuses the shared write service reports to its callers.</summary>
public enum WriteOutcomeStatus
{
	/// <summary>The change is saved.</summary>
	Saved = 0,

	/// <summary>The request does not pass validation; endpoints answer 400.</summary>
	Invalid = 1,

	/// <summary>
	/// The edit is stale: a supplied expected row version did not match, or
	/// the save lost a concurrency race. Endpoints answer 409; reloading helps.
	/// </summary>
	Stale = 2,
}

/// <summary>
/// Outcome of a write through the shared write service. Validation and
/// attribution live here alone, so the endpoint lambdas and the automated
/// writer cannot drift.
/// </summary>
public sealed record WriteOutcome(WriteOutcomeStatus Status, string? Title)
{
	public static WriteOutcome Saved() => new(WriteOutcomeStatus.Saved, null);

	public static WriteOutcome Invalid(string title) => new(WriteOutcomeStatus.Invalid, title);

	public static WriteOutcome Stale(string title) => new(WriteOutcomeStatus.Stale, title);

	public bool IsSaved => Status == WriteOutcomeStatus.Saved;

	/// <summary>The HTTP status this outcome maps to in an editor endpoint.</summary>
	public int HttpCode => Status == WriteOutcomeStatus.Invalid ? 400 : 409;
}

/// <summary>The acting account and the moment of the write, as the caller decided them.</summary>
public sealed record WriteActor(Guid AccountId, DateTimeOffset Now);

/// <summary>
/// The shared write service: applies Lied-field and event mutations for
/// endpoint lambdas and the automated writer alike. It owns validation, attribution and row-version bumps in
/// one save. An automated write (scan reading) keeps the record's
/// attribution to the editor who last saved it.
/// </summary>
public sealed class CatalogueWriteService(ArchiveDbContext db)
{
	public const string UnknownFieldMessage = "Unbekanntes Feld.";

	public static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	/// <summary>
	/// Loads the entity an automated field write points at (tracked, so
	/// the caller can write through it). Returns null for unknown ids or an
	/// unknown entity type.
	/// </summary>
	public async Task<object?> LoadEntityAsync(string entityType, Guid entityId, CancellationToken token)
		=> entityType switch
		{
			FieldCatalog.EntityTypeLied => await db.Lieder
				.FirstOrDefaultAsync(l => l.Id == entityId, token),
			FieldCatalog.EntityTypeEvent => await db.Events
				.FirstOrDefaultAsync(e => e.Id == entityId, token),
			_ => null,
		};

	/// <summary>The optimistic row version of any of a target entity type.</summary>
	public static uint RowVersionOf(object entity) => entity switch
	{
		Lieder.Lied lied => lied.RowVersion,
		ChoirEvent choirEvent => choirEvent.RowVersion,
		_ => throw new InvalidOperationException(
			$"Unbekannter Eintragstyp in RowVersionOf ({entity.GetType().Name})."),
	};

	/// <summary>
	/// The current value of one catalogued field in its joined-string form
	/// (list fields newline-joined); null is an unset field.
	/// </summary>
	public static string? CurrentFieldValue(object entity, string field)
	{
		switch (entity, field)
		{
			case (Lieder.Lied lied, "composer"): return lied.Composer;
			case (Lieder.Lied lied, "lyricist"): return lied.Lyricist;
			case (Lieder.Lied lied, "arranger"): return lied.Arranger;
			case (Lieder.Lied lied, "musical_key"): return lied.MusicalKey;
			case (Lieder.Lied lied, "lyrics"): return lied.Lyrics;
			case (ChoirEvent choirEvent, "notes"): return choirEvent.Notes;
			case (ChoirEvent choirEvent, "source_note"): return choirEvent.SourceNote;
			default: throw new InvalidOperationException($"Unbekanntes Feld {field} in CurrentFieldValue.");
		}
	}

	/// <summary>
	/// Writes exactly one catalogued field with the given value through the
	/// ordinary patch path of its entity; a null value clears the field.
	/// </summary>
	public async Task<WriteOutcome> WriteFieldAsync(
		object entity, string field, string? value, WriteActor actor, bool automated, CancellationToken token)
	{
		switch (entity, field)
		{
			case (Lieder.Lied lied, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeLied, f):
				return await WriteLiedFieldAsync(lied, f, value, actor, automated, token);
			case (ChoirEvent choirEvent, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeEvent, f):
				return await PatchEventAsync(choirEvent, new PatchEventRequest(
					Title: null, Kind: null, Venue: null, Date: null, StartTime: null,
					Notes: f == "notes" ? value : null,
					SourceNote: f == "source_note" ? value : null,
					ExpectedRowVersion: null), actor, automated, token);
			default:
				return WriteOutcome.Invalid(UnknownFieldMessage);
		}
	}

	// ---- Lieder ------------------------------------------------------------

	/// <summary>
	/// Writes one Lied field (the automated reading fills only these). Values
	/// are trimmed and bounded like the editor's patch; an automated write
	/// leaves the editor attribution of the Lied untouched.
	/// </summary>
	private async Task<WriteOutcome> WriteLiedFieldAsync(
		Lieder.Lied lied, string field, string? value, WriteActor actor, bool automated, CancellationToken token)
	{
		var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
		var max = field == "lyrics" ? Lieder.LiedEndpoints.LyricsMaxLength : Lieder.LiedEndpoints.FieldMaxLength;
		if (clean is not null && clean.Length > max)
			return WriteOutcome.Invalid("Der Wert ist zu lang.");
		switch (field)
		{
			case "composer": lied.Composer = clean; break;
			case "lyricist": lied.Lyricist = clean; break;
			case "arranger": lied.Arranger = clean; break;
			case "musical_key": lied.MusicalKey = clean; break;
			case "lyrics": lied.Lyrics = clean; break;
			default: return WriteOutcome.Invalid(UnknownFieldMessage);
		}
		lied.UpdatedAt = actor.Now;
		if (!automated)
			lied.UpdatedByAccountId = actor.AccountId;
		lied.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(Lieder.LiedEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	// ---- Events ------------------------------------------------------------

	/// <summary>Applies one event patch; validation, attribution and the row-version bump live here.</summary>
	public async Task<WriteOutcome> PatchEventAsync(
		ChoirEvent choirEvent, PatchEventRequest request, WriteActor actor, bool automated, CancellationToken token)
	{
		string? title = null;
		if (request.Title is not null)
		{
			title = request.Title.Trim();
			if (title.Length == 0)
				return WriteOutcome.Invalid(EventEndpoints.TitleRequiredMessage);
			if (title.Length > EventEndpoints.TitleMaxLength)
				return WriteOutcome.Invalid(EventEndpoints.TitleTooLongMessage);
		}
		string? kind = null;
		if (request.Kind is not null)
		{
			kind = request.Kind.Trim();
			if (!EventKinds.Known.Contains(kind))
				return WriteOutcome.Invalid(EventEndpoints.KindUnknownMessage);
		}
		var venue = CleanPatch(request.Venue, EventEndpoints.VenueMaxLength, EventEndpoints.VenueTooLongMessage, out var venueError);
		if (venueError is not null) return venueError;
		var notes = CleanPatch(request.Notes, EventEndpoints.NotesMaxLength, EventEndpoints.NotesTooLongMessage, out var notesError);
		if (notesError is not null) return notesError;
		var sourceNote = CleanPatch(request.SourceNote, EventEndpoints.SourceNoteMaxLength, EventEndpoints.SourceNoteTooLongMessage, out var sourceNoteError);
		if (sourceNoteError is not null) return sourceNoteError;
		string? startTime = null;
		if (request.StartTime is not null)
		{
			var trimmed = request.StartTime.Trim();
			// Present but empty clears the optional time; otherwise it must
			// parse exactly as "HH:mm".
			if (trimmed.Length > 0 && !TimeOnly.TryParseExact(trimmed, "HH:mm",
					System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.None, out _))
				return WriteOutcome.Invalid(EventEndpoints.StartTimeInvalidMessage);
			startTime = trimmed.Length == 0 ? null : trimmed;
		}
		if (request.Date is not null
			&& !ValidateEventDate(request.Date.Year, request.Date.Month, request.Date.Day, out var dateTitle))
			return WriteOutcome.Invalid(dateTitle);
		if (request.ExpectedRowVersion is not null && request.ExpectedRowVersion != choirEvent.RowVersion)
			return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage);

		if (title is not null)
			choirEvent.Title = title;
		if (kind is not null)
			choirEvent.Kind = kind;
		if (request.Venue is not null)
			choirEvent.Venue = venue;
		if (request.StartTime is not null)
			choirEvent.StartTime = startTime;
		if (request.Notes is not null)
			choirEvent.Notes = notes;
		if (request.SourceNote is not null)
			choirEvent.SourceNote = sourceNote;
		if (request.Date is not null)
		{
			// The nested date object is an explicit full replacement; a null
			// year means unknown date.
			choirEvent.DateYear = request.Date.Year;
			choirEvent.DateMonth = request.Date.Month;
			choirEvent.DateDay = request.Date.Day;
			choirEvent.DateApproximate = request.Date.Approximate;
		}

		choirEvent.UpdatedAt = actor.Now;
		choirEvent.UpdatedByAccountId = automated ? choirEvent.CreatedByAccountId : actor.AccountId;
		choirEvent.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	/// <summary>Publishes one event; only an editor's request does this.</summary>
	public async Task<WriteOutcome> PublishEventAsync(ChoirEvent choirEvent, WriteActor actor, uint? expectedRowVersion, CancellationToken token)
	{
		if (choirEvent.PublishedAt is not null)
			return WriteOutcome.Saved();
		if (expectedRowVersion is not null && expectedRowVersion != choirEvent.RowVersion)
			return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage);
		choirEvent.PublishedAt = actor.Now;
		choirEvent.PublishedByAccountId = actor.AccountId;
		choirEvent.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	public async Task<WriteOutcome> UnpublishEventAsync(ChoirEvent choirEvent, WriteActor actor, uint? expectedRowVersion, CancellationToken token)
	{
		if (choirEvent.PublishedAt is null)
			return WriteOutcome.Saved();
		if (expectedRowVersion is not null && expectedRowVersion != choirEvent.RowVersion)
			return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage);
		choirEvent.PublishedAt = null;
		choirEvent.PublishedByAccountId = null;
		choirEvent.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	// ---- Internal helpers --------------------------------------------------

	/// <summary>Trims an optional write value for a patch; empty means clear (null), too long is an invalid write.</summary>
	private static string? CleanPatch(string? raw, int maxLength, string tooLongTitle, out WriteOutcome? error)
	{
		error = null;
		var trimmed = raw?.Trim();
		if (trimmed is not null && trimmed.Length > maxLength)
		{
			error = new WriteOutcome(WriteOutcomeStatus.Invalid, tooLongTitle);
			return null;
		}
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>Validates one explicit ARC-024 date block: only (nothing), (year), (year, month) and (year, month, day) are valid.</summary>
	private static bool ValidateEventDate(int? year, int? month, int? day, out string title)
	{
		if (month is not null && year is null)
		{
			title = EventEndpoints.IncompleteDateMessage;
			return false;
		}
		if (day is not null && month is null)
		{
			title = EventEndpoints.IncompleteDateMessage;
			return false;
		}
		if (year is not null && year is < 1800 or > 2100)
		{
			title = EventEndpoints.YearOutOfRangeMessage;
			return false;
		}
		if (month is not null && month is < 1 or > 12)
		{
			title = EventEndpoints.InvalidDateMessage;
			return false;
		}
		if (day is not null && (day is < 1 || day.Value > DateTime.DaysInMonth(year!.Value, month!.Value)))
		{
			title = EventEndpoints.InvalidDateMessage;
			return false;
		}
		title = string.Empty;
		return true;
	}
}
