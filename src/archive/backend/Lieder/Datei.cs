using Archive.Backend.Events;

namespace Archive.Backend.Lieder;

/// <summary>Stored <see cref="Datei.Kind"/> values; the database check constraint lists the same set.</summary>
public static class DateiKinds
{
	/// <summary>Noten: a PDF, either the Partitur or one Stimme.</summary>
	public const string Noten = "noten";

	/// <summary>Übe-Datei: audio or MIDI for rehearsing (#167).</summary>
	public const string Uebe = "uebe";

	/// <summary>Aufnahme: audio or video of the choir singing (#167, #171).</summary>
	public const string Aufnahme = "aufnahme";

	public const string Sonstiges = "sonstiges";

	public static readonly IReadOnlyList<string> All = [Noten, Uebe, Aufnahme, Sonstiges];

	public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// A Datei (GLOSSARY) of exactly one Lied or one Auftritt. Replacing a Datei
/// replaces it, there is no revision history: the replaced Datei is soft-deleted
/// (<see cref="DeletedAt"/>) and points at its successor. One table holds
/// every Datei kind, for a Lied or an Auftritt, live or in the trash.
/// </summary>
public sealed class Datei
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	/// <summary>Owning Lied; exactly one of <see cref="LiedId"/> and <see cref="EventId"/> is set.</summary>
	public Guid? LiedId { get; set; }

	public Lied? Lied { get; set; }

	/// <summary>Owning Auftritt (#171); null for Dateien of a Lied.</summary>
	public Guid? EventId { get; set; }

	public ChoirEvent? Event { get; set; }

	/// <summary>One of <see cref="DateiKinds"/>.</summary>
	public required string Kind { get; set; }

	/// <summary>Noten only: the Partitur (all Stimmen together) rather than one Stimme.</summary>
	public bool IsPartitur { get; set; }

	/// <summary>The Stimme of Noten or an Übe-Datei; free text, "Alle" allowed, null when none.</summary>
	public string? Stimme { get; set; }

	public string? Description { get; set; }

	/// <summary>An Aufnahme of a Lied naming the Auftritt it came from (#167).</summary>
	public Guid? RecordedAtEventId { get; set; }

	public ChoirEvent? RecordedAtEvent { get; set; }

	public required string BlobName { get; set; }

	public required string ContentType { get; set; }

	public long SizeBytes { get; set; }

	public string? OriginalFileName { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	/// <summary>Trash (#168): set when the Datei was deleted or replaced; the Datei is hidden from members.</summary>
	public DateTimeOffset? DeletedAt { get; set; }

	public Guid? DeletedByAccountId { get; set; }

	/// <summary>Trash (#168): the Datei that replaced this one, when it left through "Ersetzen".</summary>
	public Guid? ReplacedByDateiId { get; set; }

	/// <summary>
	/// #148: storage tier of the blob — Hot by default; Cold only for a
	/// preservation original whose members' playback comes from a separate,
	/// live playback Datei. An object serving both roles stays online. The
	/// column is the editor state's source of truth; the provider tier is
	/// moved by the editor's storage-tier action.
	/// </summary>
	public string StorageTier { get; set; } = Assets.AssetStorageTiers.Hot;

	/// <summary>#148: when the tier last changed; null while never moved.</summary>
	public DateTimeOffset? TierChangedAt { get; set; }

	public uint RowVersion { get; set; }
}
