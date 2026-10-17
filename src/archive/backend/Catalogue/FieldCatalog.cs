namespace Archive.Backend.Catalogue;

/// <summary>
/// The closed field vocabulary of the automated field writer: which
/// per-record fields scan reading may fill, with their German display names.
/// </summary>
public static class FieldCatalog
{
	public const string EntityTypeLied = "lied";
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
			[EntityTypeEvent] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["notes"] = "Notizen",
				["source_note"] = "Quellenvermerk",
			},
		};

	public static bool IsKnownField(string entityType, string field)
		=> Fields.TryGetValue(entityType, out var fields) && fields.ContainsKey(field);
}
