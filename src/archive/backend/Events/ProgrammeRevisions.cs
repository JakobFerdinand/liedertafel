namespace Archive.Backend.Events;

/// <summary>
/// ARC-027 revision lifecycle: an editor starts a working draft from the
/// current published revision and publishes the replacement explicitly;
/// members keep seeing the last published revision until that moment.
/// Older published revisions remain frozen history (no unpublish, no
/// revision delete — ARC-040 later adds trash to both states).
/// </summary>
public static class ProgrammeRevisions
{
	/// <summary>
	/// The earliest instant a working draft may be published: the moment
	/// the newest published revision appeared (or the aggregate
	/// creation before the first publication). A draft written before that
	/// moment would publish against an outdated base revision — the client
	/// reloads and redoes the edit instead.
	/// </summary>
	public static DateTimeOffset EarliestAcceptedDraftMoment(EventProgramme programme)
		=> ProgrammeVisibility.NewestPublished(programme)?.PublishedAt
			?? programme.CreatedAt;
}
