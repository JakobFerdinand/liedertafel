using Archive.Backend.Assets;
using Archive.Backend.Recordings;

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
	Func<byte[]?, string, bool> HasValidHeader);

public static class DateiKindRules
{
	public const string InvalidPdfMessage = "Die Datei ist kein gültiges PDF.";

	public const string InvalidUebeMessage = "Die Datei ist keine gültige Audio- oder MIDI-Datei.";

	public const string InvalidAufnahmeMessage = "Die Datei ist keine gültige Audio- oder Videoaufnahme.";

	public const string InvalidSonstigesMessage = "Die Datei passt nicht zu ihrem Dateityp.";

	public const string UnsupportedKindMessage = "Dieser Dateityp kann noch nicht hochgeladen werden.";

	public const string UnsupportedContentTypeMessage = "Dieser Dateityp ist für diese Art nicht erlaubt.";

	private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

	private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

	private const string PptxType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

	private static readonly string[] AudioTypes =
		[AssetEndpoints.Mp3ContentType, "audio/mp4", "audio/x-m4a", "audio/wav", "audio/ogg"];

	private static readonly string[] MidiTypes = [AssetEndpoints.MidiContentType, AssetEndpoints.XMidiContentType];

	private static readonly string[] ImageTypes =
		[AssetEndpoints.JpegContentType, AssetEndpoints.PngContentType, AssetEndpoints.WebpContentType];

	private static readonly string[] ZipDocumentTypes = [DocxType, XlsxType, PptxType];

	/// <summary>Leading bytes the header gate may look at (enough for MP4 brands and WebM).</summary>
	public const int HeaderBytes = RecordingFormats.HeaderBytes;

	private static readonly Dictionary<string, DateiKindRule> Rules = new(StringComparer.Ordinal)
	{
		// Noten: PDF only, demanded by content type and the "%PDF-" prefix.
		[DateiKinds.Noten] = new DateiKindRule(
			[AssetEndpoints.PdfContentType],
			InvalidPdfMessage,
			(header, _) => IsPdf(header)),
		// Übe-Datei: audio or MIDI, recognised from the leading bytes.
		[DateiKinds.Uebe] = new DateiKindRule(
			[.. AudioTypes, .. MidiTypes],
			InvalidUebeMessage,
			(header, type) => MatchesMedia(header, type, video: false)),
		// Aufnahme: audio or video of the choir.
		[DateiKinds.Aufnahme] = new DateiKindRule(
			[.. AudioTypes, "audio/webm", "video/mp4", "video/webm"],
			InvalidAufnahmeMessage,
			(header, type) => MatchesMedia(header, type, video: type.StartsWith("video/", StringComparison.Ordinal))),
		// Sonstiges: a short whitelist of document and image types, downloaded not played.
		[DateiKinds.Sonstiges] = new DateiKindRule(
			[AssetEndpoints.PdfContentType, .. ImageTypes, "text/plain", .. ZipDocumentTypes],
			InvalidSonstigesMessage,
			MatchesDocument),
	};

	public static DateiKindRule? For(string kind) => Rules.GetValueOrDefault(kind);

	private static bool IsPdf(byte[]? header) => header is not null && header.AsSpan().StartsWith("%PDF-"u8);

	private static bool MatchesMedia(byte[]? header, string contentType, bool video)
	{
		if (header is null || header.Length == 0)
			return false;
		if (MidiTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
			return header.AsSpan().StartsWith("MThd"u8);
		var detected = RecordingFormats.Detect(header, video ? RecordingKinds.Video : RecordingKinds.Audio);
		return Subtype(detected.ContentType) == Subtype(contentType) && Subtype(contentType) != "";
	}

	/// <summary>The container family of a media type; "x-m4a" and "mp4" are the same family.</summary>
	private static string Subtype(string contentType)
	{
		var subtype = contentType[(contentType.IndexOf('/') + 1)..].ToLowerInvariant();
		return subtype switch
		{
			"x-m4a" => "mp4",
			"octet-stream" => "",
			_ => subtype,
		};
	}

	private static bool MatchesDocument(byte[]? header, string contentType)
	{
		if (header is null)
			return false;
		var span = header.AsSpan();
		return contentType.ToLowerInvariant() switch
		{
			AssetEndpoints.PdfContentType => IsPdf(header),
			AssetEndpoints.JpegContentType => span.Length >= 3 && span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF,
			AssetEndpoints.PngContentType => span.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
			AssetEndpoints.WebpContentType => span.Length >= 12 && span.StartsWith("RIFF"u8) && span.Slice(8, 4).SequenceEqual("WEBP"u8),
			"text/plain" => span.IndexOf((byte)0) < 0,
			_ => span.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
		};
	}
}
