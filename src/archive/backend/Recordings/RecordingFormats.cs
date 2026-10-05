namespace Archive.Backend.Recordings;

/// <summary>What the leading bytes of a recording file say about it.</summary>
/// <param name="ContentType">Recognised media type, or the storage default.</param>
/// <param name="Playable">True when browsers commonly play this container for the recording's kind.</param>
public sealed record RecordingFormat(string ContentType, bool Playable);

/// <summary>
/// Recognises recording containers from their leading bytes (ARC-030). The
/// result decides whether a file may serve as the playback file: MP4/M4A,
/// WebM, MP3 and WAV count as playable; everything else — QuickTime,
/// Matroska, AVI, FLAC, Ogg, unknown data — is preserved as an original but
/// needs a converted playback copy.
/// This is a container check, not a decode: an MP4 with a codec the member's
/// browser lacks still passes, and the player then reports it. No audio or
/// video analysis happens here (out of scope for the archive).
/// </summary>
public static class RecordingFormats
{
	/// <summary>Leading bytes needed: the EBML header names its document type within them.</summary>
	public const int HeaderBytes = 64;

	public const string UnknownContentType = "application/octet-stream";

	private static readonly string[] PlayableVideoTypes = ["video/mp4", "video/webm"];

	private static readonly string[] PlayableAudioTypes = ["audio/mp4", "audio/webm", "audio/mpeg", "audio/wav"];

	public static RecordingFormat Detect(ReadOnlySpan<byte> header, string kind)
	{
		var video = kind == RecordingKinds.Video;
		if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
		{
			var brand = header.Slice(8, 4);
			if (brand.SequenceEqual("qt  "u8))
				return new RecordingFormat("video/quicktime", false);
			if (brand.StartsWith("3g"u8))
				return new RecordingFormat("video/3gpp", false);
			return new RecordingFormat(video ? "video/mp4" : "audio/mp4", true);
		}
		if (header.StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
		{
			return header.IndexOf("webm"u8) >= 0
				? new RecordingFormat(video ? "video/webm" : "audio/webm", true)
				: new RecordingFormat("video/x-matroska", false);
		}
		if (header.Length >= 12 && header.StartsWith("RIFF"u8))
		{
			if (header.Slice(8, 4).SequenceEqual("WAVE"u8))
				return new RecordingFormat("audio/wav", !video);
			if (header.Slice(8, 4).SequenceEqual("AVI "u8))
				return new RecordingFormat("video/x-msvideo", false);
		}
		if (header.StartsWith("ID3"u8) || IsMpegAudioFrame(header))
			return new RecordingFormat("audio/mpeg", !video);
		if (header.StartsWith("fLaC"u8))
			return new RecordingFormat("audio/flac", false);
		if (header.StartsWith("OggS"u8))
			return new RecordingFormat(video ? "video/ogg" : "audio/ogg", false);
		return new RecordingFormat(UnknownContentType, false);
	}

	/// <summary>
	/// True when a stored revision's media type is one this slice validated
	/// as playable for the recording's kind; the same rule as
	/// <see cref="Detect"/>, applied to what finalize recorded.
	/// </summary>
	public static bool IsPlayable(string contentType, string kind) =>
		(kind == RecordingKinds.Video ? PlayableVideoTypes : PlayableAudioTypes)
			.Contains(contentType, StringComparer.OrdinalIgnoreCase);

	/// <summary>MPEG audio frame sync with a real layer (layer bits 00 are AAC/ADTS, which is not MP3).</summary>
	private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header) =>
		header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0;
}
