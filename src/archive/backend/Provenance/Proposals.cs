using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Provenance;

/// <summary>
/// A typed handler for one proposal kind (ARC-013-1). It loads the target
/// state for the freshness gate and applies the change through the shared
/// write service after an editor accepted the proposal.
/// </summary>
public interface IProposalHandler
{
	ProposalKind Kind { get; }

	/// <summary>
	/// The target state at decision time: the current row version and a
	/// value summary for the stale comparison. Null when the target is gone
	/// or the proposal has none (song creation).
	/// </summary>
	Task<ProposalTargetState?> LoadTargetAsync(ArchiveDbContext db, Proposal proposal, CancellationToken token);

	/// <summary>Applies the change through the shared write service.</summary>
	Task<WriteOutcome> ApplyAsync(Proposal proposal, WriteActor actor, CancellationToken token);
}

/// <summary>What a handler reports about a proposal's target.</summary>
public sealed record ProposalTargetState(uint RowVersion, string? CurrentValueSummary);

/// <summary>Outcome of applying one proposal: applied, or refused with a German message (the proposal stays open).</summary>
public sealed record ProposalApplyOutcome(bool Applied, string? ErrorTitle);

/// <summary>Handler of field suggestions: applies one payload field/value pair through the write service.</summary>
public sealed class FieldSuggestionHandler(CatalogueWriteService writes) : IProposalHandler
{
	public ProposalKind Kind => ProposalKind.FieldSuggestion;

	public async Task<ProposalTargetState?> LoadTargetAsync(ArchiveDbContext db, Proposal proposal, CancellationToken token)
	{
		var entity = await writes.LoadEntityAsync(proposal.TargetEntityType!, proposal.TargetEntityId!.Value, token);
		if (entity is null)
			return null;
		var payload = ProposalPayload(proposal);
		return payload.Field is null
			? null
			: new(CatalogueWriteService.RowVersionOf(entity), CurrentValueText(entity, payload.Field));
	}

	public async Task<WriteOutcome> ApplyAsync(Proposal proposal, WriteActor actor, CancellationToken token)
	{
		var entity = await writes.LoadEntityAsync(proposal.TargetEntityType!, proposal.TargetEntityId!.Value, token);
		if (entity is null)
			return WriteOutcome.Stale("Das Zielsobjekt des Vorschlags ist nicht mehr vorhanden.");
		var payload = ProposalPayload(proposal);
		if (payload.Field is null)
			return WriteOutcome.Invalid("Unbekanntes Feld.");
		// The confirming editor accepted an AI/regex value: the provenance
		// keeps the automated source and the badge stays revertible.
		var origin = new AutomatedWrite(
			proposal.Source, proposal.Confidence, proposal.Model, proposal.PromptVersion, actor.AccountId);
		return await writes.WriteFieldAsync(entity, payload.Field, payload.Value, actor, origin, token);
	}

	public static string? CurrentValueText(object entity, string field)
	{
		try
		{
			return CatalogueWriteService.CurrentFieldValue(entity, field);
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private static (string? Field, string? Value) ProposalPayload(Proposal proposal)
	{
		try
		{
			using var document = JsonDocument.Parse(proposal.Payload);
			var root = document.RootElement;
			var field = root.TryGetProperty("field", out var fieldElement) && fieldElement.ValueKind is JsonValueKind.String
				? fieldElement.GetString()
				: null;
			if (!root.TryGetProperty("value", out var valueElement))
				return (field, null);
			// A null JSON value is an explicit clear of the field.
			var value = valueElement.ValueKind is JsonValueKind.Null
				? null
				: valueElement.ValueKind is JsonValueKind.String ? valueElement.GetString() : null;
			return (field, value);
		}
		catch (JsonException)
		{
			return (null, null);
		}
	}
}

/// <summary>Handler of song-creation proposals: creates the proposed song with initial arrangement and version.</summary>
public sealed class SongCreationHandler(CatalogueWriteService writes) : IProposalHandler
{
	public ProposalKind Kind => ProposalKind.SongCreation;

	public Task<ProposalTargetState?> LoadTargetAsync(ArchiveDbContext db, Proposal proposal, CancellationToken token)
		=> Task.FromResult<ProposalTargetState?>(null);

	public async Task<WriteOutcome> ApplyAsync(Proposal proposal, WriteActor actor, CancellationToken token)
	{
		if (!PayloadToCreateRequest(proposal.Payload, out var request, out var errorTitle))
			return WriteOutcome.Invalid(errorTitle ?? "Der Vorschlag enthält keine gültigen Angaben.");
		var (outcome, _) = await writes.CreateSongAsync(request, actor, token);
		return outcome;
	}

	private static bool PayloadToCreateRequest(string payload, out CreateSongRequest request, out string? errorTitle)
	{
		errorTitle = null;
		request = new CreateSongRequest(null, null, null, null, null, null, null, null);
		try
		{
			using var document = JsonDocument.Parse(payload);
			var root = document.RootElement;
			request = new CreateSongRequest(
				Title: root.TryGetProperty("title", out var title) && title.ValueKind is JsonValueKind.String ? title.GetString() : null,
				Composer: root.TryGetProperty("composer", out var composer) && composer.ValueKind is JsonValueKind.String ? composer.GetString() : null,
				Lyricist: root.TryGetProperty("lyricist", out var lyricist) && lyricist.ValueKind is JsonValueKind.String ? lyricist.GetString() : null,
				ArrangementLabel: root.TryGetProperty("arrangementLabel", out var arrangementLabel) && arrangementLabel.ValueKind is JsonValueKind.String ? arrangementLabel.GetString() : null,
				VersionLabel: root.TryGetProperty("versionLabel", out var versionLabel) && versionLabel.ValueKind is JsonValueKind.String ? versionLabel.GetString() : null,
				Language: root.TryGetProperty("language", out var language) && language.ValueKind is JsonValueKind.String ? language.GetString() : null,
				Occasion: root.TryGetProperty("occasion", out var occasion) && occasion.ValueKind is JsonValueKind.String ? occasion.GetString() : null,
				Tags: root.TryGetProperty("tags", out var tags) && tags.ValueKind is JsonValueKind.Array
					? tags.EnumerateArray().Where(t => t.ValueKind is JsonValueKind.String).Select(t => (string?)t.GetString()).ToList()
					: null);
			return true;
		}
		catch (JsonException)
		{
			errorTitle = "Der Vorschlag enthält keine gültigen Angaben.";
			return false;
		}
	}
}

/// <summary>Handler of song-publication proposals; publication stays proposal-only for automated writers.</summary>
public sealed class SongPublicationHandler(ArchiveDbContext db, CatalogueWriteService writes) : IProposalHandler
{
	public ProposalKind Kind => ProposalKind.SongPublication;

	public async Task<ProposalTargetState?> LoadTargetAsync(ArchiveDbContext handlerDb, Proposal proposal, CancellationToken token)
	{
		var song = await handlerDb.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == proposal.TargetEntityId!.Value, token);
		if (song is null)
			return null;
		return new(song.RowVersion, song.PublishedAt is not null ? "veröffentlicht" : "unveröffentlicht");
	}

	public async Task<WriteOutcome> ApplyAsync(Proposal proposal, WriteActor actor, CancellationToken token)
	{
		var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == proposal.TargetEntityId!.Value);
		if (song is null)
			return WriteOutcome.Stale("Das Lied ist nicht mehr vorhanden.");
		return await writes.PublishSongAsync(song, actor, proposal.TargetRowVersion, token);
	}
}

/// <summary>
/// Resolves the typed handler per proposal kind; kinds without a handler
/// this slice (deletion, merge, member administration) are refused at
/// acceptance until their slices register their handlers.
/// </summary>
public sealed class ProposalHandlers(IEnumerable<IProposalHandler> handlers)
{
	public IProposalHandler? Resolve(ProposalKind kind)
		=> handlers.FirstOrDefault(handler => handler.Kind == kind);
}

/// <summary>
/// The editor decisions of the "Vorschläge" queue: accept applies through
/// the typed handler after a freshness gate, reject just records the
/// decision, and refresh re-bases a stale proposal onto the current target.
/// </summary>
public sealed class ProposalDecisions(ArchiveDbContext db, ProposalHandlers handlers, TimeProvider time)
{
	public const string AlreadyDecidedMessage = "Der Vorschlag wurde bereits entschieden.";

	public const string NoHandlerMessage = "Für diese Art von Vorschlag ist keine Anwendung vorgesehen.";

	public const string TargetMissingMessage = "Das Ziel des Vorschlags existiert nicht mehr.";

	public const string StaleTargetMessage = "Das Ziel wurde zwischenzeitlich geändert.";

	public sealed record DecisionResult(
		bool Ok,
		int HttpCode,
		string? Title,
		ProposalTargetState? TargetState = null,
		string? ProposedValue = null);

	public async Task<DecisionResult> AcceptAsync(Guid proposalId, Guid editorId, CancellationToken token)
	{
		var proposal = await db.Proposals.FirstOrDefaultAsync(p => p.Id == proposalId, token);
		if (proposal is null)
			return RefusedNotFound();
		if (proposal.Status is not ProposalStatus.Open)
			return new(false, 409, AlreadyDecidedMessage);
		var handler = handlers.Resolve(proposal.Kind);
		if (handler is null)
			return new(false, 409, NoHandlerMessage);
		ProposalTargetState? target = null;
		if (proposal.TargetEntityId is not null && proposal.TargetEntityType is not null)
		{
			target = await handler.LoadTargetAsync(db, proposal, token);
			if (target is null)
				return new(false, 409, TargetMissingMessage);
			// Stale target: show the current value beside the proposal for a
			// fresh decision instead of applying it.
			if (proposal.TargetRowVersion is not null && proposal.TargetRowVersion != target.RowVersion)
				return new(false, 409, StaleTargetMessage, target, ProposedValueOf(proposal));
		}
		var actor = new WriteActor(editorId, time.GetUtcNow());
		var outcome = await handler.ApplyAsync(proposal, actor, token);
		if (!outcome.IsSaved)
			return new(false, outcome.HttpCode, outcome.Title);
		proposal.Status = ProposalStatus.Accepted;
		proposal.DecidedAt = actor.Now;
		proposal.DecidedByAccountId = editorId;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return new(false, 409, StaleTargetMessage); }
		return Ok;
	}

	public async Task<DecisionResult> RejectAsync(Guid proposalId, Guid editorId, CancellationToken token)
	{
		var proposal = await db.Proposals.FirstOrDefaultAsync(p => p.Id == proposalId, token);
		if (proposal is null)
			return RefusedNotFound();
		if (proposal.Status is not ProposalStatus.Open)
			return new(false, 409, AlreadyDecidedMessage);
		proposal.Status = ProposalStatus.Rejected;
		proposal.DecidedAt = time.GetUtcNow();
		proposal.DecidedByAccountId = editorId;
		await db.SaveChangesAsync(token);
		return Ok;
	}

	/// <summary>
	/// Re-bases an open proposal onto the current target row version after
	/// the editor saw proposed and current value side by side.
	/// </summary>
	public async Task<DecisionResult> RefreshAsync(Guid proposalId, CancellationToken token)
	{
		var proposal = await db.Proposals.FirstOrDefaultAsync(p => p.Id == proposalId, token);
		if (proposal is null)
			return RefusedNotFound();
		if (proposal.Status is not ProposalStatus.Open)
			return new(false, 409, AlreadyDecidedMessage);
		if (proposal.TargetEntityId is null || proposal.TargetEntityType is null)
			return new(false, 409, NoHandlerMessage);
		var handler = handlers.Resolve(proposal.Kind);
		if (handler is null)
			return new(false, 409, NoHandlerMessage);
		var target = await handler.LoadTargetAsync(db, proposal, token);
		if (target is null)
			return new(false, 409, TargetMissingMessage);
		proposal.TargetRowVersion = target.RowVersion;
		await db.SaveChangesAsync(token);
		return Ok;
	}

	private DecisionResult Ok => new(true, 200, null);

	private static DecisionResult RefusedNotFound() => new(false, 404, "Der Vorschlag wurde nicht gefunden.");

	private static string? ProposedValueOf(Proposal proposal)
	{
		try
		{
			using var document = JsonDocument.Parse(proposal.Payload);
			return document.RootElement.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.String
				? value.GetString()
				: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}

/// <summary>German labels of the proposal kinds for the queue.</summary>
public static class ProposalKindText
{
	public static string Of(ProposalKind kind) => kind switch
	{
		ProposalKind.FieldSuggestion => "Feldvorschlag",
		ProposalKind.SongCreation => "Neues Lied",
		ProposalKind.SongPublication => "Veröffentlichung",
		ProposalKind.SongDeletion => "Löschen",
		ProposalKind.SongMerge => "Zusammenführen",
		ProposalKind.MemberAdministration => "Mitgliederverwaltung",
		ProposalKind.EventPublication => "Auftritt veröffentlichen",
		ProposalKind.EventDeletion => "Auftritt löschen",
		_ => kind.ToString(),
	};
}
