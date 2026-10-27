using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Archive.Backend.Catalogue;

namespace Archive.Backend.Lieder;

/// <summary>
/// The values a request carries for a Lied: the title plus the fields of the
/// <see cref="FieldCatalog"/> under their camelCase form names, bound through
/// <see cref="LiedFieldsBinding{T}"/>. An absent or null value leaves what is
/// stored, an empty one clears it, and a key the catalog does not know lands
/// in <see cref="Unknown"/> — the endpoint rejects such a request instead of
/// silently dropping what the browser sent.
/// </summary>
public abstract record LiedFieldValues
{
	private sealed record NoValues : LiedFieldValues;

	/// <summary>A request with no values at all, for a null body.</summary>
	public static LiedFieldValues Empty { get; } = new NoValues();

	/// <summary>The values by camelCase form name.</summary>
	public IReadOnlyDictionary<string, string?> Values { get; internal init; }
		= FrozenDictionary<string, string?>.Empty;

	public string? Title { get; internal init; }

	/// <summary>The patch's optimistic-concurrency token; only a patch body binds it, any other kind counts it unknown.</summary>
	public uint? RowVersion { get; internal init; }

	/// <summary>The top-level keys this request kind does not know, so a caller can reject them.</summary>
	public IReadOnlyList<string> Unknown { get; internal init; } = [];

	/// <summary>The value the catalogued field carries; null when it is absent or null.</summary>
	public string? Value(LiedField field) => Values.TryGetValue(field.Form, out var value) ? value : null;
}

[JsonConverter(typeof(CreateLiedBinding<CreateLiedRequest>))]
public sealed record CreateLiedRequest : LiedFieldValues;

[JsonConverter(typeof(PatchLiedBinding<PatchLiedRequest>))]
public sealed record PatchLiedRequest : LiedFieldValues;

[JsonConverter(typeof(EntwurfLiedBinding<EntwurfLiedBody>))]
public sealed record EntwurfLiedBody : LiedFieldValues;

/// <summary>
/// The binding behind every Lied request record: reads a flat JSON object,
/// matches each key (case-insensitively, like the browser binding it replaces)
/// against the title, the request kind's catalogued fields and — for the
/// patch — the rowVersion, and collects everything else into Unknown.
/// </summary>
public abstract class LiedFieldsBinding<T> : JsonConverter<T> where T : LiedFieldValues, new()
{
	/// <summary>Which top-level keys this request kind may carry: every catalogued field, plus the patch's rowVersion, or only the draft fields.</summary>
	protected abstract bool RowVersion { get; }

	/// <summary>The catalog fields <typeparamref name="T"/> binds: the Lied fields of the create and patch bodies or only the draft's for the Entwurf.</summary>
	protected abstract bool DraftOnly { get; }

	public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
			throw new JsonException();
		// The request body streams in partial blocks, so the object is read
		// whole into the DOM first; a token-level walk across segment
		// boundaries cannot skip an unknown value safely.
		var body = JsonElement.ParseValue(ref reader);
		var fields = DraftOnly ? FieldCatalog.LiedDraftFields : FieldCatalog.LiedFields;
		var forms = fields.ToDictionary(f => f.Form, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string?> values = new(StringComparer.Ordinal);
		string? title = null;
		uint? rowVersion = null;
		List<string>? unknown = null;
		foreach (var entry in body.EnumerateObject())
		{
			if (entry.Name.Equals("title", StringComparison.OrdinalIgnoreCase))
				title = StringOrNull(entry.Value);
			else if (RowVersion && entry.Name.Equals("rowVersion", StringComparison.OrdinalIgnoreCase))
				rowVersion = entry.Value.ValueKind switch
				{
					JsonValueKind.Null => null,
					JsonValueKind.Number when entry.Value.TryGetUInt32(out var version) => version,
					_ => throw new JsonException(),
				};
			else if (forms.TryGetValue(entry.Name, out var matched))
				values[matched.Form] = StringOrNull(entry.Value);
			else
			{
				// Keep what the browser sent so the endpoint can reject it.
				unknown ??= [];
				unknown.Add(entry.Name);
			}
		}
		return new T { Values = values, Title = title, RowVersion = rowVersion, Unknown = unknown ?? [] };
	}

	public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
		=> throw new NotSupportedException("Lied requests are only read.");

	private static string? StringOrNull(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Null => null,
		JsonValueKind.String => value.GetString(),
		_ => throw new JsonException(),
	};
}

/// <summary>Binds the create body: title plus every catalogued field.</summary>
public sealed class CreateLiedBinding<T> : LiedFieldsBinding<T> where T : LiedFieldValues, new()
{
	protected override bool RowVersion => false;

	protected override bool DraftOnly => false;
}

/// <summary>Binds the patch body: title plus every catalogued field plus the rowVersion.</summary>
public sealed class PatchLiedBinding<T> : LiedFieldsBinding<T> where T : LiedFieldValues, new()
{
	protected override bool RowVersion => true;

	protected override bool DraftOnly => false;
}

/// <summary>Binds the Entwurf's Lied body: the title plus only the draft fields of the catalog.</summary>
public sealed class EntwurfLiedBinding<T> : LiedFieldsBinding<T> where T : LiedFieldValues, new()
{
	protected override bool RowVersion => false;

	protected override bool DraftOnly => true;
}
