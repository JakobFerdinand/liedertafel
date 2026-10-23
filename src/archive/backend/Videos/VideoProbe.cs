using System.Text.Json;
using Archive.Backend.Recordings;

namespace Archive.Backend.Videos;

/// <summary>
/// ffprobe facts of one video file (#161). Width and height are the DISPLAYED
/// size: rotation side data of the video stream swaps the coded dimensions.
/// </summary>
/// <param name="VideoCodec">
/// <c>codec_name</c> of the first video stream ("h264", "hevc", "vp9" …); null
/// without a video stream.
/// </param>
/// <param name="AudioCodec">
/// <c>codec_name</c> of the first audio stream, or null when the video has no audio.
/// </param>
public sealed record VideoProbe(string? VideoCodec, string? AudioCodec, int Width, int Height)
{
	/// <summary>Codec names browsers can decode jointly with the archive's containers (#161).</summary>
	public static readonly IReadOnlyList<string> PlayableVideoCodecs = ["h264", "vp8", "vp9", "av1"];

	/// <summary>Browsers handle AAC, Opus and Vorbis audio failurelessly.</summary>
	public static readonly IReadOnlyList<string> PlayableAudioCodecs = ["aac", "opus", "vorbis"];

	public bool HasAudio => AudioCodec is not null;

	/// <summary>
	/// Largest dimension an already playable original may have (a portrait
	/// video's 1920 corresponds to the canonical landscape 1920x1080).
	/// </summary>
	public const int MaxPlayableDimension = 1920;

	/// <summary>
	/// Parses ffprobe's <c>-print_format json -show_streams -show_format</c>
	/// output; null when no video stream exists (also the answer for corrupt
	/// material that still managed to open).
	/// </summary>
	public static VideoProbe? FromFfprobeJson(string json)
	{
		using var document = JsonDocument.Parse(json);
		if (!document.RootElement.TryGetProperty("streams", out var streams)
			|| streams.ValueKind != JsonValueKind.Array)
			return null;
		string? videoCodec = null;
		string? audioCodec = null;
		var width = 0;
		var height = 0;
		foreach (var stream in streams.EnumerateArray())
		{
			if (stream.TryGetProperty("codec_type", out var type))
			{
				if (type.ValueEquals("video") && videoCodec is null)
				{
					if (stream.TryGetProperty("codec_name", out var codec))
						videoCodec = codec.GetString();
					(width, height) = DisplaySize(stream);
				}
				else if (type.ValueEquals("audio") && audioCodec is null)
				{
					if (stream.TryGetProperty("codec_name", out var codec))
						audioCodec = codec.GetString();
				}
			}
		}
		return videoCodec is null ? null : new VideoProbe(videoCodec, audioCodec, width, height);
	}

	/// <summary>
	/// The displayed size: the coded stream dimensions swap when the Display
	/// Matrix side data carries a sideways rotation.
	/// </summary>
	private static (int Width, int Height) DisplaySize(JsonElement stream)
	{
		var (width, height) = (ReadInt(stream, "width"), ReadInt(stream, "height"));
		if (!stream.TryGetProperty("side_data_list", out var sideData)
			|| sideData.ValueKind != JsonValueKind.Array)
			return (width, height);
		foreach (var entry in sideData.EnumerateArray())
		{
			if (entry.TryGetProperty("rotation", out var rotation)
				&& rotation.ValueKind == JsonValueKind.Number
				&& Math.Abs(rotation.GetDouble()) % 180 is > 45 and < 135)
				return (height, width);
		}
		return (width, height);
	}

	private static int ReadInt(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: 0;
}

/// <summary>
/// The decision for one video Datei (#161): with the finalize-recorded
/// container type (browsers play MP4 and WebM) and the ffprobe facts, an
/// already-playable original up to 1080p-equivalent stays a single object —
/// storage policy §5 forbids duplicates just to fill a slot — everything else
/// becomes an MP4 H.264/AAC copy scaled down to the 1920x1920 bounding box
/// (never up), or fails deterministically without a video stream.
/// </summary>
public sealed record VideoPlan(VideoAction Action, int ScaleWidth, int ScaleHeight)
{
	public bool NeedsCopy => Action == VideoAction.Copy;
}

public static class VideoCopyPlan
{
	/// <summary>The deterministic failure for bytes with no video stream; the reason text lives on the worker.</summary>
	public static readonly VideoPlan Fail = new(VideoAction.Fail, 0, 0);

	public static VideoPlan Plan(string contentType, VideoProbe probe)
	{
		// Container, codec or size short of the browsers' joint set: copy.
		var maxDimension = Math.Max(probe.Width, probe.Height);
		if (!IsPlayableContainer(contentType)
			|| probe.VideoCodec is null
			|| !VideoProbe.PlayableVideoCodecs.Contains(probe.VideoCodec, StringComparer.OrdinalIgnoreCase)
			|| (probe.HasAudio
				&& !VideoProbe.PlayableAudioCodecs.Contains(probe.AudioCodec!, StringComparer.OrdinalIgnoreCase))
			|| maxDimension > VideoProbe.MaxPlayableDimension)
			return scaled(probe);
		return new VideoPlan(VideoAction.Keep, 0, 0);
	}

	/// <summary>The same rule the playback slots already apply to event recordings.</summary>
	public static bool IsPlayableContainer(string contentType) =>
		RecordingFormats.IsPlayable(contentType, RecordingKinds.Video);

	private static VideoPlan scaled(VideoProbe probe)
	{
		// Fit into the bounding box, never upscale, then even: h264 yuv420p.
		var factor = Math.Min(
			Math.Min((double)VideoProbe.MaxPlayableDimension / Math.Max(probe.Width, 1),
				(double)VideoProbe.MaxPlayableDimension / Math.Max(probe.Height, 1)),
			1.0);
		return new VideoPlan(VideoAction.Copy, Even(probe.Width * factor), Even(probe.Height * factor));
	}

	private static int Even(double value)
	{
		var floored = (int)Math.Floor(value);
		return floored % 2 == 0 ? floored : floored - 1;
	}
}
