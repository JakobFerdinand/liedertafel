namespace Archive.Backend.Catalogue;

/// <summary>
/// The closed field vocabulary of the automated field writer: which
/// per-record fields scan reading may fill, with their German display names.
/// </summary>
public static class FieldCatalog
{
	public const string EntityTypeLied = "lied";
	public const string EntityTypeSong = "song";
	public const string EntityTypeArrangement = "arrangement";
	public const string EntityTypeMusicalVersion = "musical_version";
	public const string EntityTypeAsset = "asset";
	public const string EntityTypeEvent = "event";

	public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Fields =
		new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
		{
			[EntityTypeLied] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["composer"] = "Komponist",
				["lyricist"] = "Textdichter",
				["arranger"] = "Bearbeiter",
				["musical_key"] = "Tonart",
				["lyrics"] = "Liedtext",
			},
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

	public static bool IsKnownField(string entityType, string field)
		=> Fields.TryGetValue(entityType, out var fields) && fields.ContainsKey(field);

	/// <summary>
	/// Multi-value fields travel through the field writer as a newline-joined
	/// string; empty is the empty list, never a single blank entry.
	/// </summary>
	public static bool IsListField(string field)
		=> field is "alternate_titles" or "tags";

	public static IReadOnlyList<string> SplitList(string? value)
		=> value is null or "" ? [] : value.Split('\n');

	public static string JoinList(IEnumerable<string> values)
		=> string.Join('\n', values);
}
