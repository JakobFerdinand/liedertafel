#![allow(clippy::unwrap_used)] // Frozen pure-probe assertion seam.
use archive_server::videos::Probe;
use archive_server::videos::{Action, Plan};

#[test]
fn parses_an_iphone_hevc_probe_with_rotation() {
	let probe = Probe::from_json(r#"{"streams":[{"codec_type":"video","codec_name":"hevc","width":2160,"height":3840,"side_data_list":[{"rotation":-90}]},{"codec_type":"audio","codec_name":"aac"}]}"#).unwrap().unwrap();
	assert_eq!(probe.video_codec.as_deref(), Some("hevc"));
	assert_eq!(probe.audio_codec.as_deref(), Some("aac"));
	assert_eq!((probe.width, probe.height), (3840, 2160));
}

#[test]
fn the_probe_facts_decide_the_copy() {
	for (container, video, audio, width, height, expected) in [
		("video/mp4", "h264", Some("aac"), 1920, 1080, Action::Keep),
		("video/mp4", "h264", Some("aac"), 1280, 720, Action::Keep),
		("video/mp4", "h264", Some("aac"), 1080, 1920, Action::Keep),
		("video/webm", "vp9", None, 1280, 720, Action::Keep),
		("video/webm", "vp8", Some("opus"), 1920, 1080, Action::Keep),
		("video/mp4", "hevc", Some("aac"), 1080, 1920, Action::Copy),
		("video/mp4", "h264", Some("aac"), 3840, 2160, Action::Copy),
		("video/mp4", "h264", Some("aac"), 1080, 1921, Action::Copy),
		("video/webm", "vp9", Some("opus"), 1080, 1920, Action::Keep),
		(
			"video/quicktime",
			"h264",
			Some("aac"),
			1920,
			1080,
			Action::Copy,
		),
		(
			"video/x-matroska",
			"h264",
			Some("aac"),
			1280,
			720,
			Action::Copy,
		),
		(
			"video/x-msvideo",
			"mpeg2video",
			Some("mp2"),
			720,
			576,
			Action::Copy,
		),
		("video/3gpp", "h263", Some("amr_nb"), 176, 144, Action::Copy),
		("video/mp4", "h264", Some("mp3"), 1280, 720, Action::Copy),
		("video/webm", "av1", None, 1280, 720, Action::Keep),
		("video/webm", "vp9", Some("vorbis"), 1280, 720, Action::Keep),
	] {
		assert_eq!(
			Plan::for_probe(
				container,
				&Probe {
					video_codec: Some(video.into()),
					audio_codec: audio.map(str::to_owned),
					width,
					height
				}
			)
			.action,
			expected
		);
	}
}

#[test]
fn parses_a_video_without_audio_stream() {
	let p = Probe::from_json(
		r#"{"streams":[{"codec_type":"video","codec_name":"vp9","width":640,"height":360}]}"#,
	)
	.unwrap()
	.unwrap();
	assert_eq!(p.video_codec.as_deref(), Some("vp9"));
	assert_eq!(p.audio_codec, None);
	assert_eq!((p.width, p.height), (640, 360));
}
#[test]
fn a_json_without_video_stream_answers_null() {
	assert_eq!(
		Probe::from_json(r#"{"streams":[{"codec_type":"audio","codec_name":"aac"}]}"#).unwrap(),
		None
	);
}
fn plan(video: &str, w: i32, h: i32) -> Plan {
	Plan::for_probe(
		video,
		&Probe {
			video_codec: Some("h264".into()),
			audio_codec: Some("aac".into()),
			width: w,
			height: h,
		},
	)
}
#[test]
fn a_4k_original_scales_down_to_the_bounding_square() {
	let p = plan("video/mp4", 3840, 2160);
	assert_eq!(
		(p.action, p.scale_width, p.scale_height),
		(Action::Copy, 1920, 1080)
	);
	let p = plan("video/quicktime", 2160, 3840);
	assert_eq!((p.scale_width, p.scale_height), (1080, 1920));
}
#[test]
fn scaling_stays_even_and_never_upscales() {
	let p = plan("video/mp4", 3839, 2161);
	assert_eq!(p.action, Action::Copy);
	assert_eq!((p.scale_width % 2, p.scale_height % 2), (0, 0));
	assert!((1918..=1920).contains(&p.scale_width));
	assert!((1079..=1081).contains(&p.scale_height));
	let p = plan("video/quicktime", 176, 144);
	assert_eq!((p.scale_width, p.scale_height), (176, 144));
}
#[test]
fn keeping_the_original_needs_no_scale_arguments() {
	let p = plan("video/mp4", 1920, 1080);
	assert_eq!(
		(p.action, p.scale_width, p.scale_height),
		(Action::Keep, 0, 0)
	);
}
#[test]
fn only_video_aufnahmen_are_video_material() {
	for (kind, content, expected) in [
		("aufnahme", "video/quicktime", true),
		("aufnahme", "video/mp4", true),
		("aufnahme", "audio/mp4", false),
		("noten", "video/mp4", false),
		("aufnahme", "application/octet-stream", false),
	] {
		assert_eq!(archive_server::videos::is_material(kind, content), expected);
	}
}
#[test]
fn only_browser_listed_containers_may_stay_the_original() {
	for (content, expected) in [
		("video/mp4", true),
		("video/webm", true),
		("video/quicktime", false),
		("video/x-matroska", false),
		("video/3gpp", false),
	] {
		assert_eq!(Plan::is_playable_container(content), expected);
	}
}
