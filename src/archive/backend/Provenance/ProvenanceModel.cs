namespace Archive.Backend.Provenance;

/// <summary>
/// Who wrote a catalogue field last (ARC-013-1). Human writes lock a field;
/// automated (regex / AI) writes never touch a locked field and only apply
/// above the configured confidence threshold.
/// </summary>
public enum ProvenanceSource
{
	Human = 0,
	Regex = 1,
	Ai = 2,
}

/// <summary>
/// The two-step confidence the provenance keeps per field: sicher (certain)
/// and unsicher (uncertain). The auto-apply threshold compares against it.
/// </summary>
public enum ProvenanceConfidence
{
	Sicher = 0,
	Unsicher = 1,
}

/// <summary>
/// ARC-013-1: latest provenance of one catalogue field, stored per
/// (entity type, entity id, field). A human edit or a revert writes a row
/// with source human and locks the field; automated writers refuse locked
/// fields and turn their change into a proposal instead. The members below
/// are set by the write service on every change.
/// </summary>
public sealed class FieldProvenance
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Closed set: song, arrangement, musical_version, asset, event.</summary>
	public string EntityType { get; set; } = string.Empty;

	public Guid EntityId { get; set; }

	public string Field { get; set; } = string.Empty;

	public ProvenanceSource Source { get; set; }

	public ProvenanceConfidence Confidence { get; set; }

	/// <summary>Model key for AI writes; null for human and regex sources.</summary>
	public string? Model { get; set; }

	/// <summary>Prompt version for AI writes; null otherwise.</summary>
	public string? PromptVersion { get; set; }

	/// <summary>The value before this write; null when the field was unset.</summary>
	public string? PreviousValue { get; set; }

	public DateTimeOffset ChangedAt { get; set; }

	/// <summary>The acting editor, or null for an automated write.</summary>
	public Guid? ActorAccountId { get; set; }

	/// <summary>
	/// Human edits and reverts lock the field: later automated runs never
	/// overwrite it and only offer their change as a proposal.
	/// </summary>
	public bool Locked { get; set; }
}

/// <summary>
/// ARC-013-1: one open machine suggestion awaiting an editor decision.
/// Accepting applies the change through the shared write service; every
/// identity or visibility change arrives only as a proposal.
/// </summary>
public enum ProposalKind
{
	/// <summary>One field of one record (payload: field and value).</summary>
	FieldSuggestion = 0,

	/// <summary>A whole new song with its initial arrangement and version.</summary>
	SongCreation = 1,

	/// <summary>Publish an existing song (identity/visibility: proposal only).</summary>
	SongPublication = 2,

	/// <summary>Delete a song or event (proposal only; ARC-039/040 own the trash).</summary>
	SongDeletion = 3,

	/// <summary>Merge duplicate songs (proposal only; ARC-046 owns merges).</summary>
	SongMerge = 4,

	/// <summary>Anything in member administration (proposal only by design).</summary>
	MemberAdministration = 5,

	/// <summary>Publish an existing event (identity/visibility: proposal only).</summary>
	EventPublication = 6,

	/// <summary>Delete an event (proposal only; ARC-040 owns the trash).</summary>
	EventDeletion = 7,
}

public enum ProposalStatus
{
	Open = 0,
	Accepted = 1,
	Rejected = 2,
}

public sealed class Proposal
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public required ProposalKind Kind { get; set; }

	/// <summary>Target of the suggestion; null when it has none yet (song creation).</summary>
	public string? TargetEntityType { get; set; }

	public Guid? TargetEntityId { get; set; }

	/// <summary>Typed JSON payload; the shape travels with the proposal kind.</summary>
	public required string Payload { get; set; }

	/// <summary>German reason shown in the queue, e.g. who read it from where.</summary>
	public required string Reason { get; set; }

	public required ProvenanceSource Source { get; set; }

	public required ProvenanceConfidence Confidence { get; set; }

	public string? Model { get; set; }

	public string? PromptVersion { get; set; }

	/// <summary>Short German description naming the source document or run.</summary>
	public string? SourceDescription { get; set; }

	/// <summary>Row version of the target at proposal time; nil when there is no target.</summary>
	public uint? TargetRowVersion { get; set; }

	public ProposalStatus Status { get; set; }

	public required DateTimeOffset CreatedAt { get; set; }

	/// <summary>The editor or account that caused the proposal, null for automated runs.</summary>
	public Guid? CreatedByAccountId { get; set; }

	public DateTimeOffset? DecidedAt { get; set; }

	public Guid? DecidedByAccountId { get; set; }
}

/// <summary>
/// The closed field vocabulary of this slice: which per-record fields may be
/// written (and carry provenance), their German display names for the queue
/// and the badge, and which keys stay proposal-only or outside the catalogue.
/// </summary>
public static class FieldCatalog
{
	public const string EntityTypeSong = "song";
	public const string EntityTypeArrangement = "arrangement";
	public const string EntityTypeMusicalVersion = "musical_version";
	public const string EntityTypeAsset = "asset";
	public const string EntityTypeEvent = "event";

	public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Fields =
		new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
		{
			[EntityTypeSong] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["title"] = "Titel",
				["composer"] = "Komponist",
				["lyricist"] = "Textdichter",
				["lyrics"] = "Liedtext",
				["language"] = "Sprache",
				["occasion"] = "Anlass",
				["alternate_titles"] = "Andere Titel",
				["tags"] = "Schlagwörter",
			},
			[EntityTypeArrangement] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["label"] = "Bezeichnung",
				["arranger"] = "Arrangeur",
				["voice_configuration"] = "Stimmverteilung",
				["accompaniment"] = "Begleitung",
			},
			[EntityTypeMusicalVersion] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["label"] = "Bezeichnung",
				["creator"] = "Ersteller",
				["musical_key"] = "Tonart",
			},
			[EntityTypeAsset] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["description"] = "Beschreibung",
				["voice_label"] = "Stimme",
			},
			[EntityTypeEvent] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["notes"] = "Notizen",
				["source_note"] = "Quellenvermerk",
			},
		};

	public static IReadOnlySet<string> EntityTypes { get; } =
		new HashSet<string>(Fields.Keys, StringComparer.Ordinal);

	public static bool IsEntityType(string entityType)
		=> Fields.ContainsKey(entityType);

	public static bool IsKnownField(string entityType, string field)
		=> Fields.TryGetValue(entityType, out var fields) && fields.ContainsKey(field);

	/// <summary>German display name of a field for the queue and badge tooltips.</summary>
	public static string Display(string entityType, string field)
		=> Fields.TryGetValue(entityType, out var fields) && fields.TryGetValue(field, out var name)
			? name
			: field;

	/// <summary>
	/// Multi-value fields are stored in provenance and proposals as a
	/// newline-joined string; empty is the empty list, never a single blank entry.
	/// </summary>
	public static bool IsListField(string field)
		=> field is "alternate_titles" or "tags";

	public static IReadOnlyList<string> SplitList(string? value)
		=> value is null or "" ? [] : value.Split('\n');

	public static string JoinList(IEnumerable<string> values)
		=> string.Join('\n', values);
}
