using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Provenance;

/// <summary>
/// ARC-013-1 configuration: when an automated (regex / AI) write applies
/// immediately above the confidence threshold and when it becomes a
/// proposal. `sicher` (default) applies only certainly-read values;
/// `unsicher` also applies uncertain ones; `never` turns every automated
/// write into a proposal. Locked fields always become proposals.
/// </summary>
public sealed class ProvenanceOptions
{
	public const string SectionName = "Archive:Provenance";

	/// <summary>How much confidence an automated write needs to apply itself.</summary>
	public string AutoApplyConfidence { get; set; } = "sicher";

	public bool AutoApplies(ProvenanceConfidence confidence) => AutoApplyConfidence switch
	{
		"unsicher" => true,
		"never" => false,
		_ => confidence == ProvenanceConfidence.Sicher,
	};
}

/// <summary>
/// The one automated write path of ARC-013-1 (used by endpoint diagnostics,
/// proposal handlers and the later regex/AI feature slices): applies a
/// catalogued field only when the field is unlocked and the confidence
/// reaches the configured threshold; otherwise it creates an open proposal
/// with the target's row version at proposal time.
/// </summary>
public sealed class AutomatedFieldWriter(
	ArchiveDbContext db,
	CatalogueWriteService writes,
	IOptions<ProvenanceOptions> options,
	TimeProvider time)
{
	public enum AutomatedWriteResult
	{
		/// <summary>The value applied itself (above threshold, field unlocked).</summary>
		Applied = 0,

		/// <summary>The field already carried this value; nothing was written.</summary>
		Unchanged = 1,

		/// <summary>An open proposal was created instead (locked field or below threshold).</summary>
		Proposed = 2,

		/// <summary>The write was refused (unknown entity/field, invalid value, save conflict).</summary>
		Refused = 3,
	}

	public sealed record FieldWriteRequest(
		string EntityType,
		Guid EntityId,
		string Field,
		string? Value,
		ProvenanceSource Source,
		ProvenanceConfidence Confidence,
		string? Model,
		string? PromptVersion,
		string Reason,
		string? SourceDescription);

	public sealed record AutomatedWriteOutcome(
		AutomatedWriteResult Result,
		Guid? ProposalId,
		string? ErrorTitle,
		string? AppliedValue,
		string? ProposedValue);

	private const string ProposalReasonDefault = "Automatische Auswertung: Wert unter dem Schwellenwert oder Feld gesperrt.";

	/// <summary>
	/// Runs one automated field write. An unknown field or value is refused;
	/// a value that equals the field's current value changes nothing; a
	/// locked field or a value below the configured threshold becomes an
	/// open proposal; everything else applies through the shared write
	/// service with full provenance.
	/// </summary>
	public async Task<AutomatedWriteOutcome> WriteFieldAsync(
		FieldWriteRequest request, Guid? automationActorAccountId, CancellationToken token)
	{
		if (!FieldCatalog.IsKnownField(request.EntityType, request.Field))
			return Refuse(CatalogueWriteService.UnknownFieldMessage);
		// Identity and visibility never flow through this field path: they
		// can only ever arrive as explicit proposals (server-enforced).
		var entity = await writes.LoadEntityAsync(request.EntityType, request.EntityId, token);
		if (entity is null)
			return Refuse("Das Ziel des Schreibzugriffs wurde nicht gefunden.");
		var current = CatalogueWriteService.CurrentFieldValue(entity, request.Field);
		if (string.Equals(current, request.Value, StringComparison.Ordinal))
			return new(AutomatedWriteResult.Unchanged, null, null, null, null);
		var provenance = await db.FieldProvenance
			.FirstOrDefaultAsync(p => p.EntityType == request.EntityType
				&& p.EntityId == request.EntityId && p.Field == request.Field, token);
		if (provenance?.Locked is true
			|| !options.Value.AutoApplies(request.Confidence))
			return await ProposeFieldSuggestionAsync(request, automationActorAccountId, CatalogueWriteService.RowVersionOf(entity), token);
		var actor = new WriteActor(automationActorAccountId ?? entityActorFallback(entity), time.GetUtcNow());
		var write = new AutomatedWrite(request.Source, request.Confidence, request.Model, request.PromptVersion);
		var outcome = await writes.WriteFieldAsync(entity, request.Field, request.Value, actor, write, token);
		if (!outcome.IsSaved)
			return new(AutomatedWriteResult.Refused, null, outcome.Title, null, null);
		return new(AutomatedWriteResult.Applied, null, null, request.Value, null);
	}

	private static Guid entityActorFallback(object entity) => entity switch
	{
		Catalogue.Song song => song.CreatedByAccountId,
		Catalogue.Arrangement arrangement => arrangement.CreatedByAccountId,
		Catalogue.MusicalVersion version => version.CreatedByAccountId,
		ArchiveAsset asset => asset.CreatedByAccountId,
		Events.ChoirEvent choirEvent => choirEvent.CreatedByAccountId,
		_ => Guid.Empty,
	};

	/// <summary>
	/// Creates the open field-suggestion proposal for one locked field or a
	/// value below the threshold, naming the target's row version at
	/// proposal time so acceptance can detect a stale target.
	/// </summary>
	private async Task<AutomatedWriteOutcome> ProposeFieldSuggestionAsync(
		FieldWriteRequest request, Guid? automationActorAccountId, uint targetRowVersion, CancellationToken token)
	{
		var proposal = new Proposal
		{
			Kind = ProposalKind.FieldSuggestion,
			TargetEntityType = request.EntityType,
			TargetEntityId = request.EntityId,
			Payload = JsonSerializer.Serialize(new { field = request.Field, value = request.Value }),
			Reason = request.Reason,
			Source = request.Source == ProvenanceSource.Human ? ProvenanceSource.Regex : request.Source,
			Confidence = request.Confidence,
			Model = request.Model,
			PromptVersion = request.PromptVersion,
			SourceDescription = request.SourceDescription,
			TargetRowVersion = targetRowVersion,
			Status = ProposalStatus.Open,
			CreatedAt = time.GetUtcNow(),
			CreatedByAccountId = automationActorAccountId,
		};
		db.Proposals.Add(proposal);
		await db.SaveChangesAsync(token);
		return new(AutomatedWriteResult.Proposed, proposal.Id, null, null, request.Value);
	}

	private static AutomatedWriteOutcome Refuse(string title) =>
		new(AutomatedWriteResult.Refused, null, title, null, null);
}
