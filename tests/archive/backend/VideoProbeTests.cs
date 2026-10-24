using Archive.Backend.Lieder;
using Archive.Backend.Recordings;
using Archive.Backend.Videos;
using Xunit;

namespace Archive.Backend.Tests;

/// <summary>
/// #161 Entscheidungsschicht: aus den ffprobe-Fakten und dem gespeicherten
/// Inhaltstyp folgt, ob eine Aufnahme eine MP4-Abspielkopie braucht. Schon
/// abspielbare Dateien bis 1080p bleiben unangetastet; HEVC, QuickTime,
/// Matroska, AVI und alles über 1080p wird kopiert. Der Anzeigequerschnitt
/// (Rotation) zählt, und Skalierung läuft nie hoch.
/// </summary>
public sealed class VideoProbeTests
{
	[Fact]
	public void ParsesAnIphoneHevcProbeWithRotation()
	{
		var probe = VideoProbe.FromFfprobeJson("""
			{
			  "streams": [
			    {
			      "index": 0,
			      "codec_name": "hevc",
			      "codec_type": "video",
			      "width": 2160,
			      "height": 3840,
			      "avg_frame_rate": "30000/1001",
			      "side_data_list": [
			        {
			          "side_data_type": "Display Matrix",
			          "displaymatrix": "input display matrix",
			          "rotation": -90
			        }
			      ]
			    },
			    {
			      "index": 1,
			      "codec_name": "aac",
			      "codec_type": "audio",
			      "sample_rate": "44100"
			    }
			  ],
			  "format": {
			    "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
			    "duration": "122.000000"
			  }
			}
			""");

		Assert.Equal("hevc", probe.VideoCodec);
		Assert.Equal("aac", probe.AudioCodec);
		// Die Anzeige dreht die Porträtaufnahme: die angezeigten Masse tauschen
		// die kodierten (2160 breit kodiert, 3840 hoch; gedreht andersherum).
		Assert.Equal(3840, probe.Width);
		Assert.Equal(2160, probe.Height);
		Assert.True(probe.HasAudio);
	}

	[Fact]
	public void ParsesAVideoWithoutAudioStream()
	{
		var probe = VideoProbe.FromFfprobeJson("""
			{
			  "streams": [
			    {
			      "index": 0,
			      "codec_name": "vp9",
			      "codec_type": "video",
			      "width": 640,
			      "height": 360
			    }
			  ],
			  "format": { "format_name": "matroska,webm" }
			}
			""");

		Assert.Equal("vp9", probe.VideoCodec);
		Assert.Null(probe.AudioCodec);
		Assert.False(probe.HasAudio);
		Assert.Equal(640, probe.Width);
		Assert.Equal(360, probe.Height);
	}

	[Fact]
	public void AJsonWithoutVideoStreamAnswersNull()
	{
		var probe = VideoProbe.FromFfprobeJson("""
			{
			  "streams": [
			    { "index": 0, "codec_name": "aac", "codec_type": "audio" }
			  ],
			  "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" }
			}
			""");

		Assert.Null(probe);
	}

	[Theory]
	// Schon abspielbar: MP4 mit H.264 und AAC, bis 1080p.
	[InlineData("video/mp4", "h264", "aac", 1920, 1080, VideoAction.Keep)]
	[InlineData("video/mp4", "h264", "aac", 1280, 720, VideoAction.Keep)]
	// Porträt bis 1080p bleibt auch ohne Kopie.
	[InlineData("video/mp4", "h264", "aac", 1080, 1920, VideoAction.Keep)]
	// Ohne Ton spielt es auch so.
	[InlineData("video/webm", "vp9", null, 1280, 720, VideoAction.Keep)]
	[InlineData("video/webm", "vp8", "opus", 1920, 1080, VideoAction.Keep)]
	// HEVC spielt der Browser nicht immer: Kopie trotz gutem Container.
	[InlineData("video/mp4", "hevc", "aac", 1080, 1920, VideoAction.Copy)]
	// Über 1080p: Herabskalieren, nie Hochskalieren.
	[InlineData("video/mp4", "h264", "aac", 3840, 2160, VideoAction.Copy)]
	[InlineData("video/mp4", "h264", "aac", 1080, 1921, VideoAction.Copy)]
	[InlineData("video/webm", "vp9", "opus", 1080, 1920, VideoAction.Keep)]
	// Nicht abspielbare Container: QuickTime, Matroska und AVI immer.
	[InlineData("video/quicktime", "h264", "aac", 1920, 1080, VideoAction.Copy)]
	[InlineData("video/x-matroska", "h264", "aac", 1280, 720, VideoAction.Copy)]
	[InlineData("video/x-msvideo", "mpeg2video", "mp2", 720, 576, VideoAction.Copy)]
	// Auto-Kameras älteren Standards.
	[InlineData("video/3gpp", "h263", "amr_nb", 176, 144, VideoAction.Copy)]
	// MP3-Ton im MP4 verdient auch die Abspielkopie: AAC ist verlässlich.
	[InlineData("video/mp4", "h264", "mp3", 1280, 720, VideoAction.Copy)]
	// AV1 spielt der Browser; AAC/Opus/Vorbis heißt Ton.
	[InlineData("video/webm", "av1", null, 1280, 720, VideoAction.Keep)]
	[InlineData("video/webm", "vp9", "vorbis", 1280, 720, VideoAction.Keep)]
	public void TheProbeFactsDecideTheCopy(string contentType, string videoCodec,
		string? audioCodec, int width, int height, VideoAction expected)
	{
		var probe = new VideoProbe(videoCodec, audioCodec, width, height);

		var plan = VideoCopyPlan.Plan(contentType, probe);

		Assert.Equal(expected, plan.Action);
	}

	[Fact]
	public void A4KOrientalScalesDownToThe1080pBoundingBox()
	{
		var plan = VideoCopyPlan.Plan("video/mp4", new VideoProbe("h264", "aac", 3840, 2160));

		Assert.Equal(VideoAction.Copy, plan.Action);
		Assert.Equal(1920, plan.ScaleWidth);
		Assert.Equal(1080, plan.ScaleHeight);
		// Porträt: dieselbe Regel entlang der Anzeige.
		var portrait = VideoCopyPlan.Plan("video/mp4", new VideoProbe("hevc", "aac", 2160, 3840));
		Assert.Equal(1080, portrait.ScaleWidth);
		Assert.Equal(1920, portrait.ScaleHeight);
	}

	[Fact]
	public void ScalingStaysEvenForTheVideoEncoderAndNeverUpscales()
	{
		// Ungerade Querschnitt knapp über 1080p: Herabskalieren auf gerade Werte.
		var plan = VideoCopyPlan.Plan("video/mp4", new VideoProbe("h264", "aac", 3839, 2161));
		Assert.Equal(VideoAction.Copy, plan.Action);
		Assert.Equal(0, plan.ScaleWidth % 2);
		Assert.Equal(0, plan.ScaleHeight % 2);
		Assert.InRange(plan.ScaleWidth, 1918, 1920);
		Assert.InRange(plan.ScaleHeight, 1079, 1081);
		Assert.True(plan.ScaleWidth < 3839 && plan.ScaleHeight < 2161);

		// Eine Korngrenze nie vergrößern.
		var small = VideoCopyPlan.Plan("video/quicktime", new VideoProbe("h263", "amr_nb", 176, 144));
		Assert.Equal(176, small.ScaleWidth);
		Assert.Equal(144, small.ScaleHeight);
	}

	[Fact]
	public void KeepingTheOriginalNeedsNoScaleArguments()
	{
		var plan = VideoCopyPlan.Plan("video/mp4", new VideoProbe("h264", "aac", 1920, 1080));

		Assert.Equal(VideoAction.Keep, plan.Action);
		Assert.Equal(0, plan.ScaleWidth);
		Assert.Equal(0, plan.ScaleHeight);
	}

	[Theory]
	[InlineData(DateiKinds.Aufnahme, "video/quicktime", true)]
	[InlineData(DateiKinds.Aufnahme, "video/mp4", true)]
	[InlineData(DateiKinds.Aufnahme, "audio/mp4", false)]
	[InlineData(DateiKinds.Noten, "video/mp4", false)]
	[InlineData(DateiKinds.Aufnahme, "application/octet-stream", false)]
	public void OnlyVideoAufnahmenAreVideoMaterial(string kind, string contentType, bool expected)
	{
		Assert.Equal(expected, VideoService.IsVideoMaterial(kind, contentType));
	}

	[Theory]
	[InlineData("video/mp4", true)]
	[InlineData("video/webm", true)]
	// Was finalize aufgenommen hat, kopiert der Job immer: HEVC-MP4, QuickTime.
	[InlineData("video/quicktime", false)]
	[InlineData("video/x-matroska", false)]
	[InlineData("video/3gpp", false)]
	public void OnlyBrowserListedContainersMayStayTheOriginal(string contentType, bool playable)
	{
		Assert.Equal(playable, RecordingFormats.IsPlayable(contentType, RecordingKinds.Video));
	}
}
