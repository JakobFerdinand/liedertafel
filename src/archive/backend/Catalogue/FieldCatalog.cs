using Archive.Backend.Events;
using Archive.Backend.Lieder;

namespace Archive.Backend.Catalogue;

/// <summary>
/// One editable free-text field of a Lied: its stored name, the limit, how to
/// read and write it and where the request record carries it. A
/// <see cref="Label"/> marks the fields scan reading may fill automatically.
/// </summary>
public sealed record LiedField(
	string Name, string? Label, int MaxLength,
	Func<Lied, string?> Get, Action<Lied, string?> Set, Func<LiedFieldValues, string?> From);

/// <summary>One text field of an event that scan reading may fill, with its patch.</summary>
public sealed record EventField(
	string Name, string Label, Func<ChoirEvent, string?> Get, Func<string?, PatchEventRequest> ToPatch);

/// <summary>
/// The single definition of every catalogue text field. Adding a field to a
/// Lied or an event means adding one entry here; the editor endpoints, the
/// automated writer and its field vocabulary all read from these tables.
/// </summary>
public static class FieldCatalog
{
	public const string EntityTypeLied = "lied";
	public const string EntityTypeEvent = "event";

	public static readonly IReadOnlyList<LiedField> LiedFields =
	[
		new("composer", "Komponist", LiedEndpoints.FieldMaxLength, l => l.Composer, (l, v) => l.Composer = v, f => f.Composer),
		new("arranger", "Bearbeiter", LiedEndpoints.FieldMaxLength, l => l.Arranger, (l, v) => l.Arranger = v, f => f.Arranger),
		new("lyricist", "Textdichter", LiedEndpoints.FieldMaxLength, l => l.Lyricist, (l, v) => l.Lyricist = v, f => f.Lyricist),
		new("voice_configuration", null, LiedEndpoints.FieldMaxLength, l => l.VoiceConfiguration, (l, v) => l.VoiceConfiguration = v, f => f.VoiceConfiguration),
		new("musical_key", "Tonart", LiedEndpoints.FieldMaxLength, l => l.MusicalKey, (l, v) => l.MusicalKey = v, f => f.MusicalKey),
		new("language", null, LiedEndpoints.FieldMaxLength, l => l.Language, (l, v) => l.Language = v, f => f.Language),
		new("lyrics", "Liedtext", LiedEndpoints.LyricsMaxLength, l => l.Lyrics, (l, v) => l.Lyrics = v, f => f.Lyrics),
		new("occasion", null, LiedEndpoints.FieldMaxLength, l => l.Occasion, (l, v) => l.Occasion = v, f => f.Occasion),
	];

	private static readonly PatchEventRequest EmptyPatch = new(
		Title: null, Kind: null, Venue: null, Date: null, StartTime: null,
		Notes: null, SourceNote: null, ExpectedRowVersion: null);

	public static readonly IReadOnlyList<EventField> EventFields =
	[
		new("notes", "Notizen", e => e.Notes, v => EmptyPatch with { Notes = v }),
		new("source_note", "Quellenvermerk", e => e.SourceNote, v => EmptyPatch with { SourceNote = v }),
	];

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
