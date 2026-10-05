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
			if (IsOneOf(brand, AudioOnlyBrands))
				return new RecordingFormat("audio/mp4", !video);
			// Only known audio/video brands count: the same container also
			// carries still images (HEIC, AVIF) and other non-media data.
			return IsOneOf(brand, MediaBrands)
				? new RecordingFormat(video ? "video/mp4" : "audio/mp4", true)
				: new RecordingFormat(UnknownContentType, false);
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

	/// <summary>ISO base media brands that mean ordinary audio/video MP4.</summary>
	private static readonly string[] MediaBrands =
		["isom", "iso2", "iso3", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "M4V ", "dash", "mmp4", "MSNV"];

	private static readonly string[] AudioOnlyBrands = ["M4A ", "M4B "];

	private static bool IsOneOf(ReadOnlySpan<byte> brand, string[] brands)
	{
		foreach (var known in brands)
		{
			if (brand.Length == 4 && brand[0] == known[0] && brand[1] == known[1]
				&& brand[2] == known[2] && brand[3] == known[3])
				return true;
		}
		return false;
	}

	/// <summary>
	/// A plausible MPEG Layer III frame header: 11 sync bits, a defined
	/// version, layer III, and bitrate and sample-rate fields that are
	/// neither free-format nor reserved. A bare <c>FF FE</c> (UTF-16 byte
	/// order mark, Layer I by its bits) and AAC/ADTS do not pass.
	/// </summary>
	private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header)
	{
		if (header.Length < 4 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
			return false;
		var version = (header[1] >> 3) & 0x03;
		var layer = (header[1] >> 1) & 0x03;
		var bitrate = header[2] >> 4;
		var sampleRate = (header[2] >> 2) & 0x03;
		return version != 0x01 && layer == 0x01 && bitrate is not (0x00 or 0x0F) && sampleRate != 0x03;
	}
}
