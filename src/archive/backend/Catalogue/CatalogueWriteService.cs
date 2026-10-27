using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
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

	/// <summary>
	/// Loads the entity an automated field write points at (tracked, so
	/// the caller can write through it). Returns null for unknown ids or an
	/// unknown entity type.
	/// </summary>
	public async Task<object?> LoadEntityAsync(string entityType, Guid entityId, CancellationToken token)
		=> entityType switch
		{
			FieldCatalog.EntityTypeLied => await Lieder.LiedQueries.Visible(db)
				.FirstOrDefaultAsync(l => l.Id == entityId, token),
			FieldCatalog.EntityTypeEvent => await Events.EventQueries.Visible(db)
				.FirstOrDefaultAsync(e => e.Id == entityId, token),
			_ => null,
		};

	/// <summary>
	/// The current value of one catalogued field in its joined-string form
	/// (list fields newline-joined); null is an unset field.
	/// </summary>
	public static string? CurrentFieldValue(object entity, string field) => entity switch
	{
		Lieder.Lied lied when FieldCatalog.AutomatedLiedField(field) is { } liedField => liedField.Get(lied),
		ChoirEvent choirEvent when FieldCatalog.AutomatedEventField(field) is { } eventField => eventField.Get(choirEvent),
		_ => throw new InvalidOperationException($"Unbekanntes Feld {field} in CurrentFieldValue."),
	};

	/// <summary>
	/// Writes exactly one catalogued field with the given value through the
	/// ordinary patch path of its entity; a null value clears the field.
	/// </summary>
	public async Task<WriteOutcome> WriteFieldAsync(
		object entity, string field, string? value, WriteActor actor, bool automated, CancellationToken token)
	{
		switch (entity)
		{
			case Lieder.Lied lied when FieldCatalog.AutomatedLiedField(field) is { } liedField:
				// An automated write clears nothing it was not given: null is an empty value here.
				return await PatchLiedAsync(lied, null, [(liedField, value ?? string.Empty)], null, actor, automated, token);
			case ChoirEvent choirEvent when FieldCatalog.AutomatedEventField(field) is { } eventField:
				return await PatchEventAsync(choirEvent, eventField.ToPatch(value), actor, automated, token);
			default:
				return WriteOutcome.Invalid(UnknownFieldMessage);
		}
	}

	// ---- Lieder ------------------------------------------------------------

	/// <summary>
	/// Creates a Lied: title required, every field trimmed and bounded like a
	/// patch. The Lied is attributed to the actor.
	/// </summary>
	public async Task<(WriteOutcome Outcome, Lieder.Lied? Lied)> CreateLiedAsync(
		string? rawTitle, LiedFieldValues fields, WriteActor actor, CancellationToken token)
	{
		var title = rawTitle?.Trim();
		if (string.IsNullOrEmpty(title))
			return (WriteOutcome.Invalid(Lieder.LiedEndpoints.TitleRequiredMessage), null);
		if (title.Length > Lieder.LiedEndpoints.FieldMaxLength)
			return (WriteOutcome.Invalid(Lieder.LiedEndpoints.FieldTooLongMessage), null);
		var lied = new Lieder.Lied
		{
			Title = title,
			CreatedAt = actor.Now,
			CreatedByAccountId = actor.AccountId,
			UpdatedAt = actor.Now,
			UpdatedByAccountId = actor.AccountId,
			RowVersion = 1,
		};
		if (ApplyLiedFields(lied, Changes(fields)) is { } invalid)
			return (invalid, null);
		db.Lieder.Add(lied);
		await db.SaveChangesAsync(token);
		return (WriteOutcome.Saved(), lied);
	}

	/// <summary>
	/// Patches a Lied: a null title or field stays, an empty field clears.
	/// Values are trimmed and bounded; an automated write leaves the editor
	/// attribution of the Lied untouched.
	/// </summary>
	public Task<WriteOutcome> PatchLiedAsync(
		Lieder.Lied lied, string? rawTitle, LiedFieldValues fields, uint? expectedRowVersion,
		WriteActor actor, bool automated, CancellationToken token)
		=> PatchLiedAsync(lied, rawTitle, Changes(fields), expectedRowVersion, actor, automated, token);

	private async Task<WriteOutcome> PatchLiedAsync(
		Lieder.Lied lied, string? rawTitle, IReadOnlyList<(LiedField Field, string? Value)> changes,
		uint? expectedRowVersion, WriteActor actor, bool automated, CancellationToken token)
	{
		if (expectedRowVersion is { } expected && expected != lied.RowVersion)
			return WriteOutcome.Stale(Lieder.LiedEndpoints.ConcurrencyMessage);
		string? title = null;
		if (rawTitle is not null)
		{
			title = rawTitle.Trim();
			if (title.Length == 0)
				return WriteOutcome.Invalid(Lieder.LiedEndpoints.TitleRequiredMessage);
			if (title.Length > Lieder.LiedEndpoints.FieldMaxLength)
				return WriteOutcome.Invalid(Lieder.LiedEndpoints.FieldTooLongMessage);
		}
		if (ApplyLiedFields(lied, changes) is { } invalid)
			return invalid;
		if (title is not null)
			lied.Title = title;
		lied.UpdatedAt = actor.Now;
		if (!automated)
			lied.UpdatedByAccountId = actor.AccountId;
		lied.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(Lieder.LiedEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	private static IReadOnlyList<(LiedField Field, string? Value)> Changes(LiedFieldValues values)
		=> [.. FieldCatalog.LiedFields.Select(f => (f, values.Value(f)))];

	/// <summary>Validates every given field first, then applies them; null leaves a value, empty clears it.</summary>
	private static WriteOutcome? ApplyLiedFields(Lieder.Lied lied, IReadOnlyList<(LiedField Field, string? Value)> changes)
	{
		if (changes.Any(c => c.Value?.Trim().Length > c.Field.MaxLength))
			return WriteOutcome.Invalid(Lieder.LiedEndpoints.FieldTooLongMessage);
		foreach (var (field, value) in changes)
		{
			if (value?.Trim() is { } trimmed)
				field.Set(lied, trimmed.Length == 0 ? null : trimmed);
		}
		return null;
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
