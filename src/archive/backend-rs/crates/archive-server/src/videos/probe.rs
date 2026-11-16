use serde_json::Value;

/// ffprobe facts use displayed (rotation-adjusted), not coded dimensions.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Probe {
	pub video_codec: Option<String>,
	pub audio_codec: Option<String>,
	pub width: i32,
	pub height: i32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Action {
	Keep,
	Copy,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Plan {
	pub action: Action,
	pub scale_width: i32,
	pub scale_height: i32,
}
impl Plan {
	pub fn is_playable_container(content_type: &str) -> bool {
		crate::catalogue::queries::is_playable(content_type, "video")
	}
	pub fn for_probe(content_type: &str, probe: &Probe) -> Self {
		let contains = |values: &[&str], value: Option<&str>| {
			value.is_some_and(|value| values.iter().any(|v| v.eq_ignore_ascii_case(value)))
		};
		if crate::catalogue::queries::is_playable(content_type, "video")
			&& contains(&["h264", "vp8", "vp9", "av1"], probe.video_codec.as_deref())
			&& (probe.audio_codec.is_none()
				|| contains(&["aac", "opus", "vorbis"], probe.audio_codec.as_deref()))
			&& probe.width.max(probe.height) <= 1920
		{
			return Self {
				action: Action::Keep,
				scale_width: 0,
				scale_height: 0,
			};
		}
		let factor = (1920.0 / f64::from(probe.width.max(1)))
			.min(1920.0 / f64::from(probe.height.max(1)))
			.min(1.0);
		let even = |value: i32| {
			let floor = (f64::from(value) * factor).floor() as i32;
			floor - floor % 2
		};
		Self {
			action: Action::Copy,
			scale_width: even(probe.width),
			scale_height: even(probe.height),
		}
	}
}
impl Probe {
	pub fn from_json(input: &str) -> Result<Option<Self>, serde_json::Error> {
		let value: Value = serde_json::from_str(input)?;
		let Some(streams) = value.get("streams").and_then(Value::as_array) else {
			return Ok(None);
		};
		let mut result = Self {
			video_codec: None,
			audio_codec: None,
			width: 0,
			height: 0,
		};
		for stream in streams {
			match stream.get("codec_type").and_then(Value::as_str) {
				Some("video") if result.video_codec.is_none() => {
					result.video_codec = stream
						.get("codec_name")
						.and_then(Value::as_str)
						.map(str::to_owned);
					let dimension = |name| {
						stream
							.get(name)
							.and_then(Value::as_i64)
							.and_then(|n| i32::try_from(n).ok())
							.unwrap_or(0)
					};
					(result.width, result.height) = (dimension("width"), dimension("height"));
					if stream
						.get("side_data_list")
						.and_then(Value::as_array)
						.is_some_and(|data| {
							data.iter().any(|entry| {
								entry
									.get("rotation")
									.and_then(Value::as_f64)
									.is_some_and(|r| {
										let r = r.abs() % 180.0;
										r > 45.0 && r < 135.0
									})
							})
						}) {
						std::mem::swap(&mut result.width, &mut result.height);
					}
				}
				Some("audio") if result.audio_codec.is_none() => {
					result.audio_codec = stream
						.get("codec_name")
						.and_then(Value::as_str)
						.map(str::to_owned)
				}
				_ => {}
			}
		}
		Ok(result.video_codec.is_some().then_some(result))
	}
}
