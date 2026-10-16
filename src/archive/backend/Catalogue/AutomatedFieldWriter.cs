using Archive.Backend.Assets;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Catalogue;

/// <summary>
/// The one automated write path (scan reading): writes a certain,
/// quote-verified value into a catalogued field only while that field is
/// still empty, and drops it otherwise. It never overwrites a value an editor
/// entered and keeps no record of where a value came from; the caller
/// decides what is certain and verified.
/// </summary>
public sealed class AutomatedFieldWriter(
	ArchiveDbContext db,
	CatalogueWriteService writes,
	TimeProvider time)
{
	public enum AutomatedWriteResult
	{
		/// <summary>The field was empty and now holds the value.</summary>
		Applied = 0,

		/// <summary>The field already holds a value; nothing was written.</summary>
		Skipped = 1,

		/// <summary>The write was refused (unknown entity/field, invalid value, save conflict).</summary>
		Refused = 2,
	}

	public sealed record FieldWriteRequest(string EntityType, Guid EntityId, string Field, string Value);

	public sealed record AutomatedWriteOutcome(AutomatedWriteResult Result, string? ErrorTitle);

	/// <summary>
	/// Runs one automated field write against the stored state of the
	/// target: an unknown field or target is refused, a field that holds a
	/// value is skipped, an empty field is filled through the shared write
	/// service. A save that loses a race with an editor is refused.
	/// </summary>
	public async Task<AutomatedWriteOutcome> WriteFieldAsync(
		FieldWriteRequest request, Guid? automationActorAccountId, CancellationToken token)
	{
		if (!FieldCatalog.IsKnownField(request.EntityType, request.Field))
			return Refuse(CatalogueWriteService.UnknownFieldMessage);
		var entity = await writes.LoadEntityAsync(request.EntityType, request.EntityId, token);
		if (entity is null)
			return Refuse("Das Ziel des Schreibzugriffs wurde nicht gefunden.");
		// The tracked entity can be older than the row: compare against what
		// is stored, so a value an editor saved meanwhile is never replaced.
		await db.Entry(entity).ReloadAsync(token);
		if (!string.IsNullOrWhiteSpace(CatalogueWriteService.CurrentFieldValue(entity, request.Field)))
			return new(AutomatedWriteResult.Skipped, null);
		var actor = new WriteActor(automationActorAccountId ?? CreatorOf(entity), time.GetUtcNow());
		var outcome = await writes.WriteFieldAsync(entity, request.Field, request.Value, actor, automated: true, token);
		return outcome.IsSaved
			? new(AutomatedWriteResult.Applied, null)
			: new(AutomatedWriteResult.Refused, outcome.Title);
	}

	private static Guid CreatorOf(object entity) => entity switch
	{
		Song song => song.CreatedByAccountId,
		Arrangement arrangement => arrangement.CreatedByAccountId,
		MusicalVersion version => version.CreatedByAccountId,
		ArchiveAsset asset => asset.CreatedByAccountId,
		Events.ChoirEvent choirEvent => choirEvent.CreatedByAccountId,
		_ => Guid.Empty,
	};

	private static AutomatedWriteOutcome Refuse(string title) => new(AutomatedWriteResult.Refused, title);
}
