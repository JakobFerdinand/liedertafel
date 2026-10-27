using Archive.Backend.Events;
using Archive.Backend.Lieder;

namespace Archive.Backend.Catalogue;

/// <summary>
/// One editable free-text field of a Lied. Everything a field has lands on
/// its catalog entry: the stored <paramref name="Name"/> (the vocabulary of
/// the automated writer and the reading prompt), the <paramref name="Label"/>
/// — non-null means scan reading may fill it automatically — the
/// <paramref name="SearchLabel"/> the member search names when a Treffer
/// stands here (non-null means searchable), the <paramref name="MaxLength"/>,
/// whether <paramref name="InDraft"/> joins the Lied-Entwurf form (the title
/// is that form's own extra field), how the request values are read and how
/// the value is read and written on a <see cref="Lied"/> and on a
/// <see cref="LiedEntwurf"/>. Adding a field to a Lied means adding one entry
/// here; the request records, the Entwurf, the search and the reading all
/// derive from these entries.
/// </summary>
public sealed record LiedField(
	string Name, string? Label, string? SearchLabel, string? Prompt, int MaxLength, bool InDraft,
	Func<Lied, string?> Get, Action<Lied, string?> Set,
	Func<LiedEntwurf, string> GetDraft, Action<LiedEntwurf, string> SetDraft)
{
	/// <summary>The camelCase name the browser and the request record carry.</summary>
	public string Form => FieldCatalog.FormOf(Name);

	/// <summary>The PascalCase property name on the Lied entities.</summary>
	public string EntityProperty => FieldCatalog.EntityPropertyOf(Name);
}

/// <summary>One text field of an event that scan reading may fill, with its patch.</summary>
public sealed record EventField(
	string Name, string Label, Func<ChoirEvent, string?> Get, Func<string?, PatchEventRequest> ToPatch);

/// <summary>
/// The single definition of every catalogue text field. Adding a field to a
/// Lied or an event means adding one entry here; the editor endpoints, the
/// Entwurf, the search, the automated writer and its field vocabulary all
/// read from these tables.
/// </summary>
public static class FieldCatalog
{
	public const string EntityTypeLied = "lied";
	public const string EntityTypeEvent = "event";

	public const int FieldMaxLength = 200;

	public const int LyricsMaxLength = 5000;

	public static readonly IReadOnlyList<LiedField> LiedFields =
	[
		new("composer", "Komponist", "beim Komponisten", "Komponist (Musik von), nur der Name.",
			FieldMaxLength, InDraft: true,
			l => l.Composer, (l, v) => l.Composer = v, e => e.Composer, (e, v) => e.Composer = v),
		new("arranger", "Bearbeiter", "beim Bearbeiter", "Arrangeur (Satz von), nur der Name.",
			FieldMaxLength, InDraft: true,
			l => l.Arranger, (l, v) => l.Arranger = v, e => e.Arranger, (e, v) => e.Arranger = v),
		new("lyricist", "Textdichter", "beim Texter", "Textdichter (Worte von), nur der Name.",
			FieldMaxLength, InDraft: true,
			l => l.Lyricist, (l, v) => l.Lyricist = v, e => e.Lyricist, (e, v) => e.Lyricist = v),
		new("voice_configuration", null, null, "Besetzung, zum Beispiel \"Männerchor a cappella\" oder \"TTBB\".",
			FieldMaxLength, InDraft: true,
			l => l.VoiceConfiguration, (l, v) => l.VoiceConfiguration = v, e => e.VoiceConfiguration, (e, v) => e.VoiceConfiguration = v),
		new("musical_key", "Tonart", null, "Tonart, zum Beispiel \"G-Dur\" oder \"d-Moll\".",
			FieldMaxLength, InDraft: true,
			l => l.MusicalKey, (l, v) => l.MusicalKey = v, e => e.MusicalKey, (e, v) => e.MusicalKey = v),
		new("language", null, null, "Sprache des Liedtexts, zum Beispiel \"Deutsch\" oder \"Latein\".",
			FieldMaxLength, InDraft: true,
			l => l.Language, (l, v) => l.Language = v, e => e.Language, (e, v) => e.Language = v),
		new("lyrics", "Liedtext", "im Liedtext",
			"der gesungene Liedtext mit allen Strophen, Zeilen durch Zeilenumbrüche getrennt, ohne Titel,\n  Namen, Tempo- oder Stimmangaben.",
			LyricsMaxLength, InDraft: true,
			l => l.Lyrics, (l, v) => l.Lyrics = v, e => e.Lyrics, (e, v) => e.Lyrics = v),
		// The Anlass is no draft field and carries no prompt: the Anlegen form
		// holds no value for it, so the draft accessors are never called.
		new("occasion", null, null, null, FieldMaxLength, InDraft: false,
			l => l.Occasion, (l, v) => l.Occasion = v, e => string.Empty, (_, _) => { }),
	];

	private static readonly PatchEventRequest EmptyPatch = new(
		Title: null, Kind: null, Venue: null, Date: null, StartTime: null,
		Notes: null, SourceNote: null, ExpectedRowVersion: null);

	public static readonly IReadOnlyList<EventField> EventFields =
	[
		new("notes", "Notizen", e => e.Notes, v => EmptyPatch with { Notes = v }),
		new("source_note", "Quellenvermerk", e => e.SourceNote, v => EmptyPatch with { SourceNote = v }),
	];

	/// <summary>The catalogued Lied field of a stored name; throws for a name the catalog does not know.</summary>
	public static LiedField LiedField(string name) => LiedFields.First(f => f.Name == name);

	/// <summary>The Lied fields the Anlegen form carries.</summary>
	public static IEnumerable<LiedField> LiedDraftFields => LiedFields.Where(f => f.InDraft);

	/// <summary>The Lied fields a word may match in.</summary>
	public static IEnumerable<LiedField> LiedSearchFields => LiedFields.Where(f => f.SearchLabel is not null);

	/// <summary>The Lied fields scan reading may fill automatically.</summary>
	public static IEnumerable<LiedField> LiedAutomatedFields => LiedFields.Where(f => f.Label is not null);

	/// <summary>
	/// The camelCase form names the Lied-Entwurf carries: the title is the
	/// form's own extra field, then the catalog's draft fields.
	/// </summary>
	public static IReadOnlyList<string> DraftFormNames =>
		["title", .. LiedDraftFields.Select(f => f.Form)];

	/// <summary>voice_configuration becomes voiceConfiguration, and so on for every storage name.</summary>
	public static string FormOf(string name)
	{
		var parts = name.Split('_');
		return parts[0] + string.Concat(parts.Skip(1)
			.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
	}

	public static string EntityPropertyOf(string name)
	{
		var camel = FormOf(name);
		return char.ToUpperInvariant(camel[0]) + camel[1..];
	}

	/// <summary>The Lied field scan reading may fill under this name, or null.</summary>
	public static LiedField? AutomatedLiedField(string field)
		=> LiedFields.FirstOrDefault(f => f.Label is not null && f.Name == field);

	public static EventField? AutomatedEventField(string field)
		=> EventFields.FirstOrDefault(f => f.Name == field);

	public static bool IsKnownField(string entityType, string field) => entityType switch
	{
		EntityTypeLied => AutomatedLiedField(field) is not null,
		EntityTypeEvent => AutomatedEventField(field) is not null,
		_ => false,
	};
}
