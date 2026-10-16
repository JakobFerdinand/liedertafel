using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

/// <summary>
/// The archive's A2UI component catalog on the server (ARC-022-4): which
/// components exist, which record id each is built from, and which of them the
/// member chat can resolve. The browser registers the same four components and
/// nothing else (no basic catalog). The model supplies record ids and a
/// component name only; the server resolves every id against what a member may
/// see, builds the component properties itself and drops everything else
/// before anything is streamed or stored. Neither side ever carries a URL: the
/// browser derives its links from the ids.
/// </summary>
public static class A2uiCatalog
{
	/// <summary>Identifier of the archive catalog; surfaces naming any other catalog are refused.</summary>
	public const string CatalogId = "urn:liedertafel:archiv:a2ui:katalog:v1";

	/// <summary>A2UI protocol version of every message.</summary>
	public const string ProtocolVersion = "v0.9";

	/// <summary>AG-UI custom event carrying the validated A2UI messages of one answer.</summary>
	public const string EventName = "archive.a2ui";

	/// <summary>Name of the tool the agent shows surfaces with.</summary>
	public const string ToolName = "zeige_oberflaeche";

	/// <summary>Surfaces (cards) shown with one answer, however many calls ask for them.</summary>
	public const int MaxSurfacesPerAnswer = 4;

	/// <summary>Requests of one tool call that are looked at; the rest counts as dropped.</summary>
	internal const int MaxRequestsPerCall = 12;

	/// <summary>Songs one <c>Quellen</c> list shows.</summary>
	internal const int MaxSources = 10;

	private const int MaxTitleChars = 200;

	private const int MaxLineChars = 160;

	/// <summary>The one component of every surface.</summary>
	internal const string RootId = "root";

	internal static IReadOnlyList<A2uiComponent> Components { get; } =
	[
		new("LiedKarte", A2uiArgument.LiedId,
			"Karte zu einem einzelnen veröffentlichten Lied (Titel, Komponist, Textdichter, Textanfang) mit Verweis ins Archiv.",
			ResolveSongCardAsync),
		new("AuftrittKarte", A2uiArgument.AuftrittId,
			"Karte zu einem veröffentlichten Auftritt (Titel, Art, Datum, Ort) mit Verweis ins Archiv.",
			ResolveEventCardAsync),
		new("Quellen", A2uiArgument.LiedIds,
			"Liste von bis zu 10 veröffentlichten Liedern als Verweise ins Archiv.",
			ResolveSourcesAsync),
		// Defined and validated, but not yet resolvable in the member chat: the
		// event tools (ARC-022-1) bring its resolver with their own visibility rules.
		new("ProgrammListe", A2uiArgument.AuftrittId, "Programm eines Auftritts.", null),
	];

	/// <summary>Short German description of the components a run's tool offers.</summary>
	internal static string OfferedComponentsDescription() => string.Join(" ",
		Components.Where(c => c.Resolve is not null).Select(c => $"{c.Name} ({c.Argument.ToWire()}): {c.Description}"));

	// ---- show: validate the model's request, resolve, build the messages ----

	/// <summary>
	/// Validates the model's request against the catalog, resolves each valid
	/// one against what a member may see and records the resulting surfaces
	/// on the run. Returns the JSON the model gets back: what was shown, and
	/// how many requests were not, without saying which or why.
	/// </summary>
	internal static async Task<string> ShowAsync(
		ArchiveDbContext db, IReadOnlyList<SurfaceRequest>? requests, ChatRunState state, CancellationToken token)
	{
		var shown = new List<object>();
		var dropped = 0;
		var list = requests ?? [];
		dropped += Math.Max(0, list.Count - MaxRequestsPerCall);
		foreach (var request in list.Take(MaxRequestsPerCall))
		{
			if (state.SurfaceCount >= MaxSurfacesPerAnswer)
			{
				dropped++;
				continue;
			}
			var props = await ResolveAsync(db, request, token);
			if (props is null || !state.ShownKeys.Add(ShownKey(props)))
			{
				dropped++;
				continue;
			}
			state.AddSurface(BuildSurface($"s{state.SurfaceCount + 1}", props));
			shown.Add(new { komponente = StringOf(props["component"]) });
		}
		return JsonSerializer.Serialize(new
		{
			angezeigt = shown,
			verworfen = dropped,
			// Never promise a surface that does not exist: the model would
			// describe a card the member cannot see.
			hinweis = shown.Count > 0
				? "Die Oberfläche erscheint unter deiner Antwort. Beschreibe sie kurz im Text und wiederhole nicht ihren Inhalt."
				: "Es wurde keine Oberfläche angezeigt. Antworte nur mit Text und erwähne keine Karte.",
		});
	}

	/// <summary>One surface: its creation and its single root component.</summary>
	private static (JsonObject Create, JsonObject Update) BuildSurface(string surfaceId, JsonObject component) => (
		new JsonObject
		{
			["version"] = ProtocolVersion,
			["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = CatalogId },
		},
		new JsonObject
		{
			["version"] = ProtocolVersion,
			["updateComponents"] = new JsonObject
			{
				["surfaceId"] = surfaceId,
				["components"] = new JsonArray(component),
			},
		});

	/// <summary>
	/// Resolves one request to the component's wire properties, or null when
	/// the request is not valid for the catalog or names nothing a member may
	/// see. Every refusal looks the same from outside.
	/// </summary>
	internal static async Task<JsonObject?> ResolveAsync(ArchiveDbContext db, SurfaceRequest request, CancellationToken token)
	{
		var definition = Components.FirstOrDefault(c => string.Equals(c.Name, request.Komponente, StringComparison.Ordinal));
		if (definition?.Resolve is not { } resolve || !request.UsesOnly(definition.Argument))
			return null;
		var props = await resolve(db, request, token);
		if (props is null)
			return null;
		// The component name first, like every A2UI component on the wire.
		var ordered = new JsonObject { ["id"] = RootId, ["component"] = definition.Name };
		foreach (var (key, value) in props.ToList())
		{
			props.Remove(key);
			ordered[key] = value;
		}
		return ordered;
	}

	private static string ShownKey(JsonObject props)
	{
		var ids = new StringBuilder(StringOf(props["component"]));
		foreach (var key in new[] { "liedId", "auftrittId" })
			ids.Append('|').Append(StringOf(props[key]));
		if (props["quellen"] is JsonArray sources)
			ids.Append('|').Append(string.Join(',', sources.Select(s => StringOf(s?["liedId"]))));
		return ids.ToString();
	}

	// ---- re-check on load ----

	/// <summary>
	/// Re-validates stored A2UI messages against the catalog and today's
	/// visibility: only well-formed surfaces of this catalog survive, each
	/// rebuilt from the records it names, so a card for a record a member may
	/// no longer see is gone and nothing stored is passed through as it is.
	/// Unreadable input yields nothing.
	/// </summary>
	internal static async Task<List<JsonObject>> RecheckAsync(ArchiveDbContext db, string? stored, CancellationToken token)
	{
		var result = new List<JsonObject>();
		if (string.IsNullOrWhiteSpace(stored))
			return result;
		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(stored);
		}
		catch (JsonException)
		{
			return result;
		}
		if (parsed is not JsonArray messages)
			return result;

		var surfaces = new List<(string Id, JsonObject? Component)>();
		foreach (var message in messages)
		{
			if (message is not JsonObject obj || obj.Count != 2 || StringOf(obj["version"]) != ProtocolVersion)
				continue;
			if (obj["createSurface"] is JsonObject create
				&& StringOf(create["catalogId"]) == CatalogId && StringOf(create["surfaceId"]) is { } surfaceId)
				surfaces.Add((surfaceId, null));
			else if (obj["updateComponents"] is JsonObject update && StringOf(update["surfaceId"]) is { } updateId
				&& update["components"] is JsonArray { Count: 1 } components && components[0] is JsonObject root
				&& surfaces.FindIndex(s => s.Id == updateId && s.Component is null) is var index and >= 0)
				surfaces[index] = (updateId, root);
		}

		var state = new ChatRunState();
		foreach (var (_, component) in surfaces)
		{
			if (state.SurfaceCount >= MaxSurfacesPerAnswer || component is null)
				continue;
			var request = RequestFromStored(component);
			if (request is null || await ResolveAsync(db, request, token) is not { } props
				|| !state.ShownKeys.Add(ShownKey(props)))
				continue;
			state.AddSurface(BuildSurface($"s{state.SurfaceCount + 1}", props));
		}
		return state.Surfaces;
	}

	/// <summary>The request a stored component stands for: its name and the record ids it names.</summary>
	private static SurfaceRequest? RequestFromStored(JsonObject component)
	{
		var name = StringOf(component["component"]);
		if (StringOf(component["id"]) != RootId || name is null)
			return null;
		var definition = Components.FirstOrDefault(c => c.Name == name);
		if (definition is null)
			return null;
		// Only the id the component is built from counts; anything else the
		// stored object carries (labels, links, extra properties) is ignored.
		return definition.Argument switch
		{
			A2uiArgument.LiedId => new SurfaceRequest(name, LiedId: StringOf(component["liedId"])),
			A2uiArgument.AuftrittId => new SurfaceRequest(name, AuftrittId: StringOf(component["auftrittId"])),
			A2uiArgument.LiedIds => new SurfaceRequest(name,
				LiedIds: (component["quellen"] as JsonArray)?.Select(q => StringOf((q as JsonObject)?["liedId"]) ?? string.Empty).ToList()),
			_ => null,
		};
	}

	/// <summary>A JSON string value, or null for anything else (never throws on a foreign JSON type).</summary>
	private static string? StringOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	// ---- resolvers (published records only) ----

	private static async Task<JsonObject?> ResolveSongCardAsync(ArchiveDbContext db, SurfaceRequest request, CancellationToken token)
	{
		if (!Guid.TryParse(request.LiedId, out var id))
			return null;
		var song = await db.Songs.AsNoTracking()
			.Where(s => s.Id == id && s.PublishedAt != null)
			.Select(s => new { s.Id, s.Title, s.Composer, s.Lyricist, s.Lyrics })
			.FirstOrDefaultAsync(token);
		if (song is null)
			return null;
		var card = new JsonObject { ["liedId"] = song.Id.ToString(), ["titel"] = Clip(song.Title, MaxTitleChars) };
		SetIfPresent(card, "komponist", song.Composer);
		SetIfPresent(card, "textdichter", song.Lyricist);
		SetIfPresent(card, "textanfang", Beginning(song.Lyrics));
		return card;
	}

	private static async Task<JsonObject?> ResolveEventCardAsync(ArchiveDbContext db, SurfaceRequest request, CancellationToken token)
	{
		if (!Guid.TryParse(request.AuftrittId, out var id))
			return null;
		var entry = await db.Events.AsNoTracking()
			.Where(e => e.Id == id && e.PublishedAt != null)
			.Select(e => new { e.Id, e.Title, e.Kind, e.Venue, e.DateYear, e.DateMonth, e.DateDay, e.DateApproximate })
			.FirstOrDefaultAsync(token);
		if (entry is null)
			return null;
		var card = new JsonObject
		{
			["auftrittId"] = entry.Id.ToString(),
			["titel"] = Clip(entry.Title, MaxTitleChars),
			["art"] = EventKinds.Known.Contains(entry.Kind) ? entry.Kind : "other",
			["datum"] = EventDate.Display(entry.DateYear, entry.DateMonth, entry.DateDay, entry.DateApproximate),
		};
		SetIfPresent(card, "ort", entry.Venue);
		return card;
	}

	private static async Task<JsonObject?> ResolveSourcesAsync(ArchiveDbContext db, SurfaceRequest request, CancellationToken token)
	{
		var ids = (request.LiedIds ?? [])
			.Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
			.OfType<Guid>().Distinct().Take(MaxSources).ToList();
		if (ids.Count == 0)
			return null;
		var songs = await db.Songs.AsNoTracking()
			.Where(s => ids.Contains(s.Id) && s.PublishedAt != null)
			.Select(s => new { s.Id, s.Title, s.Composer })
			.ToListAsync(token);
		if (songs.Count == 0)
			return null;
		var sources = new JsonArray();
		foreach (var id in ids)
		{
			var song = songs.FirstOrDefault(s => s.Id == id);
			if (song is null)
				continue;
			var entry = new JsonObject { ["liedId"] = song.Id.ToString(), ["titel"] = Clip(song.Title, MaxTitleChars) };
			SetIfPresent(entry, "komponist", song.Composer);
			sources.Add(entry);
		}
		return new JsonObject { ["quellen"] = sources };
	}

	private static void SetIfPresent(JsonObject target, string key, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
			target[key] = Clip(value.Trim(), MaxTitleChars);
	}

	private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

	/// <summary>The first lines of the lyrics as a short teaser, cut at a word.</summary>
	private static string? Beginning(string? lyrics)
	{
		if (string.IsNullOrWhiteSpace(lyrics))
			return null;
		var lines = lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2);
		var text = string.Join(" / ", lines);
		if (text.Length <= MaxLineChars)
			return text;
		var cut = text.LastIndexOf(' ', MaxLineChars - 1);
		return text[..(cut > MaxLineChars / 2 ? cut : MaxLineChars - 1)].TrimEnd(' ', ',', ';', '/') + "…";
	}

	// ---- the tool ----

	/// <summary>Creates the <c>zeige_oberflaeche</c> tool of one run on the request's database context.</summary>
	internal static AITool CreateShowSurfaceTool(ArchiveDbContext db, ChatRunState state) =>
		AIFunctionFactory.Create(
			async ([Description("Die anzuzeigenden Komponenten, höchstens 4; jede mit ihrem Namen und genau der ID, die sie braucht.")]
				List<SurfaceRequest> komponenten, CancellationToken cancellationToken = default)
				=> await ShowAsync(db, komponenten, state, cancellationToken),
			name: ToolName,
			description: "Zeigt dem Mitglied unter deiner Antwort Archiv-Komponenten an. Übergib ausschließlich IDs aus "
				+ "Werkzeugergebnissen dieses Gesprächs; Texte, Adressen und Verweise kannst du nicht vorgeben. "
				+ "Verfügbar: " + OfferedComponentsDescription());
}

/// <summary>
/// One request of the model to show a component: its name and the one record
/// id (or list of ids) it is built from. Which field a component takes is
/// fixed by the catalog; a request carrying any other id is not valid.
/// </summary>
public sealed record SurfaceRequest(
	[property: Description("Name der Komponente.")] string? Komponente,
	[property: Description("ID des Liedes (für LiedKarte).")] string? LiedId = null,
	[property: Description("IDs der Lieder (für Quellen), höchstens 10.")] List<string>? LiedIds = null,
	[property: Description("ID des Auftritts (für AuftrittKarte).")] string? AuftrittId = null)
{
	/// <summary>
	/// True when the request carries the given id field and no other. A field
	/// left empty counts as absent: models that must fill every field of a
	/// schema fill the unused ones with null, an empty string or an empty list.
	/// </summary>
	internal bool UsesOnly(A2uiArgument argument) =>
		Present(LiedId) == (argument == A2uiArgument.LiedId)
		&& (LiedIds is { Count: > 0 }) == (argument == A2uiArgument.LiedIds)
		&& Present(AuftrittId) == (argument == A2uiArgument.AuftrittId);

	private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>The id a catalog component is built from.</summary>
internal enum A2uiArgument
{
	LiedId,
	LiedIds,
	AuftrittId,
}

internal static class A2uiArgumentExtensions
{
	public static string ToWire(this A2uiArgument argument) => argument switch
	{
		A2uiArgument.LiedId => "liedId",
		A2uiArgument.LiedIds => "liedIds",
		_ => "auftrittId",
	};
}

/// <summary>
/// A catalog component: its name, its argument and, when the member chat can
/// show it, the resolver that turns the request into the wire properties from
/// what a member may see.
/// </summary>
internal sealed record A2uiComponent(
	string Name, A2uiArgument Argument, string Description,
	Func<ArchiveDbContext, SurfaceRequest, CancellationToken, Task<JsonObject?>>? Resolve);
