using Archive.Backend.Assets;

namespace Archive.Backend.Lieder;

/// <summary>
/// What an upload of one <see cref="DateiKinds"/> value must look like: the
/// accepted content types, the magic-byte gate and the German rejection text.
/// Tickets add their kind by registering one rule in <see cref="DateiKindRules"/>;
/// the shared session/finalize code in <see cref="DateiUploads"/> stays untouched.
/// </summary>
public sealed record DateiKindRule(
	IReadOnlyList<string> ContentTypes,
	string InvalidMessage,
	Func<byte[]?, bool> HasValidHeader);

public static class DateiKindRules
{
	public const string InvalidPdfMessage = "Die Datei ist kein gültiges PDF.";

	public const string UnsupportedKindMessage = "Dieser Dateityp kann noch nicht hochgeladen werden.";

	private static readonly Dictionary<string, DateiKindRule> Rules = new(StringComparer.Ordinal)
	{
		// Noten: PDF only, demanded by content type and the "%PDF-" prefix.
		// #167 registers uebe (audio, MIDI), aufnahme and sonstiges here.
		[DateiKinds.Noten] = new DateiKindRule(
			[AssetEndpoints.PdfContentType],
			InvalidPdfMessage,
			header => header is not null && header.AsSpan().StartsWith("%PDF-"u8)),
	};

	public static DateiKindRule? For(string kind) => Rules.GetValueOrDefault(kind);
}
