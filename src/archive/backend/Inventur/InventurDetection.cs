using Archive.Backend.Assets;
using Archive.Backend.Recordings;

namespace Archive.Backend.Inventur;

/// <summary>One file's leading-byte result before any catalogue interpretation.</summary>
public sealed record InventurFileFinding(
	string? ContentType,
	bool? Playable,
	bool IsPdf,
	string[] Flags);

/// <summary>
/// #98 Quellen-Inventur: file-kind detection from leading bytes. It reuses the
/// archive's own sniffs where they exist — <see cref="RecordingFormats"/>
/// (ARC-030) for audio/video containers and the MIDI header of
/// <see cref="DateiKindRules"/> — and adds narrow magic checks for containers
/// without an archive rule (OLE, ZIP, text-like). A real codec (H.264, MP3
/// parameters) needs a decoder and stays with the maintainer run.
/// </summary>
public static class InventurDetection
{
	public const string ShortcutFlag = "shortcut";
	public const string MidiFlag = "midi";
	public const string OleCompoundFlag = "oleCompoundFile";
	public const string ZipContainerFlag = "zipContainer";
	public const string TextLikeFlag = "textLike";
	public const string UnknownFlag = "unknown";
	public const string UnresolvedDuplicateFlag = "unresolvedDuplicateCandidate";
	public const string SameSizeDuplicateFlag = "sameSizeDuplicateCandidate";

	/// <summary>OLE compound file magic (BIFF .xls, .cap) — contents stay opaque here.</summary>
	private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

	/// <summary>Leading bytes the sniffs read (enough for MP4 brands, WebM, OLE).</summary>
	public const int HeaderBytes = 64;

	private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm", ".mpd", ".3gp", ".m4v"];

	public static InventurFileFinding Classify(string fileName, byte[] header)
	{
		var span = header.AsSpan();
		var flags = new List<string>();
		var extension = Path.GetExtension(fileName);
		string? contentType = null;
		bool? playable = null;

		if (span.StartsWith("%PDF-"u8))
		{
			// Same content gate as DateiKinds.Noten uploads.
			return new InventurFileFinding(UploadEndpoints.PdfContentType, null, IsPdf: true, []);
		}
		if (span.StartsWith("MThd"u8))
		{
			// Same MIDI gate as Übe-Dateien in DateiKindRules.MatchesMedia.
			contentType = UploadEndpoints.MidiContentType;
			flags.Add(MidiFlag);
		}
		else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
		{
			// A Drive shortcut exported as an Internet shortcut file; the
			// recorded target resolves the same-folder-twice ambiguity.
			flags.Add(ShortcutFlag);
		}
		else if (span.StartsWith(new byte[] { 0x4C, 0x00, 0x00, 0x00 }) && extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			// Windows shortcut; its binary target format stays out of scope.
			flags.Add(ShortcutFlag);
		}
		else
		{
			// ARC-030 container sniff; a video-sounding extension keeps the
			// video candidate, everything else is treated as audio.
			var kind = IsVideoExtension(extension) ? RecordingKinds.Video : RecordingKinds.Audio;
			var detected = RecordingFormats.Detect(span, kind);
			if (detected.ContentType != RecordingFormats.UnknownContentType)
			{
				contentType = detected.ContentType;
				playable = detected.Playable;
			}
		}

		if (contentType is null && !flags.Contains(ShortcutFlag))
		{
			// Containers without an archive rule stay facts, not guesses.
			if (span.Length >= OleMagic.Length && span[..OleMagic.Length].SequenceEqual(OleMagic))
				flags.Add(OleCompoundFlag);
			else if (span.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
				flags.Add(ZipContainerFlag);
			else if (IsTextLike(span))
				flags.Add(TextLikeFlag);
			else
				flags.Add(UnknownFlag);
		}

		return new InventurFileFinding(contentType, playable, IsPdf: false, [.. flags]);
	}

	/// <summary>No zero byte inside the first bytes: a text document candidate (XML, .capx, .txt).</summary>
	private static bool IsTextLike(ReadOnlySpan<byte> header) => header.Length > 0 && header.IndexOf((byte)0) < 0;

	/// <summary>Container types that may carry video; every other path is audio first.</summary>
	public static bool IsVideoExtension(string? extension) =>
		extension is not null && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Strips the copy suffix a Drive export gives same-name files
	/// ("<code> (1)</code>"); the group key for duplicate-name detection.
	/// </summary>
	public static string DuplicateStem(string fileName)
	{
		var stem = Path.GetFileNameWithoutExtension(fileName);
		var open = stem.LastIndexOf('(');
		while (open >= 0 && stem.EndsWith(")", StringComparison.Ordinal))
		{
			var digits = stem[(open + 1)..^1];
			if (digits.Length > 0 && digits.All(char.IsAsciiDigit))
				stem = stem[..open].TrimEnd();
			else
				break;
		}
		return stem;
	}

	/// <summary>The URL value of an Internet-shortcut export, null when the file does not record one.</summary>
	public static string? ShortcutLinkTarget(string text)
	{
		foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
		{
			var trimmed = line.Trim();
			if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
				return trimmed[4..].Trim();
		}
		return null;
	}
}
