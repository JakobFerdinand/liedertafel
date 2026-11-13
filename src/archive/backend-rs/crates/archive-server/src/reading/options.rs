#[derive(Clone)]
pub struct Options {
	pub enabled: bool,
	pub max_pdf_bytes: u64,
	pub max_pages_per_request: i32,
	pub images_per_call: i32,
	pub max_image_long_side: i32,
	pub jpeg_quality: u8,
	pub estimated_tokens_per_image: i64,
	pub transcription_max_output_tokens: i32,
	pub fields_max_output_tokens: i32,
	pub max_field_text_characters: usize,
	pub queue: crate::extraction::Options,
}
impl Default for Options {
	fn default() -> Self {
		Self {
			enabled: false,
			max_pdf_bytes: 33_554_432,
			max_pages_per_request: 20,
			images_per_call: 10,
			max_image_long_side: 1600,
			jpeg_quality: 80,
			estimated_tokens_per_image: 2500,
			transcription_max_output_tokens: 5000,
			fields_max_output_tokens: 3000,
			max_field_text_characters: 24_000,
			queue: crate::extraction::Options {
				max_attempts: 4,
				stale_running_after: chrono::Duration::minutes(20),
				time_budget: chrono::Duration::minutes(8),
				redisplay_after: chrono::Duration::minutes(15),
				max_dispatch_per_run: 50,
				visibility_backoff: chrono::Duration::minutes(1),
				batch_size: 2,
				receive_visibility: chrono::Duration::minutes(20),
				max_messages_per_run: 50,
				drain_budget: chrono::Duration::minutes(30),
				queue_name: "archive-reading".into(),
				..Default::default()
			},
		}
	}
}
impl Options {
	pub fn from_env(development: bool) -> Self {
		let mut result = Self {
			enabled: development,
			..Default::default()
		};
		if let Ok(v) = std::env::var("Archive__Reading__Enabled") {
			result.enabled = v.trim().eq_ignore_ascii_case("true");
		}
		macro_rules! number {
			($name:literal,$target:expr,$wide:expr) => {
				if let Ok(v) = std::env::var(concat!("Archive__Reading__", $name)) {
					if let Some(v) = configuration_integer(&v, $wide) {
						if v > 0 {
							// Source widths were checked before conversion to the
							// wider/unsigned Rust storage representation.
							$target = v as _;
						}
					}
				}
			};
		}
		number!("MaxPdfBytes", result.max_pdf_bytes, true);
		number!("MaxPagesPerRequest", result.max_pages_per_request, false);
		number!("ImagesPerCall", result.images_per_call, false);
		number!("MaxImageLongSide", result.max_image_long_side, false);
		number!(
			"EstimatedTokensPerImage",
			result.estimated_tokens_per_image,
			false
		);
		number!(
			"TranscriptionMaxOutputTokens",
			result.transcription_max_output_tokens,
			false
		);
		number!(
			"FieldsMaxOutputTokens",
			result.fields_max_output_tokens,
			false
		);
		number!(
			"MaxFieldTextCharacters",
			result.max_field_text_characters,
			false
		);
		number!("MaxAttempts", result.queue.max_attempts, false);
		number!(
			"MaxDispatchPerRun",
			result.queue.max_dispatch_per_run,
			false
		);
		number!("BatchSize", result.queue.batch_size, false);
		number!(
			"MaxMessagesPerRun",
			result.queue.max_messages_per_run,
			false
		);
		if let Ok(v) = std::env::var("Archive__Reading__JpegQuality")
			&& let Some(v) = configuration_integer(&v, false)
			&& (1..=100).contains(&v)
		{
			result.jpeg_quality = v as u8;
		}
		result.images_per_call = result.images_per_call.min(10);
		result.max_image_long_side = result.max_image_long_side.min(2048);
		result.queue.batch_size = result.queue.batch_size.min(4);
		macro_rules! duration {
			($name:literal,$field:ident) => {
				if let Ok(v) = std::env::var(concat!("Archive__Reading__", $name)) {
					if let Some(v) =
						crate::storage::parse_lifetime(&v).filter(|v| *v > chrono::Duration::zero())
					{
						result.queue.$field = v;
					}
				}
			};
		}
		duration!("TimeBudget", time_budget);
		duration!("StaleRunningAfter", stale_running_after);
		duration!("RedisplayAfter", redisplay_after);
		duration!("VisibilityBackoff", visibility_backoff);
		duration!("ReceiveVisibility", receive_visibility);
		duration!("DrainBudget", drain_budget);
		if let Ok(v) = std::env::var("Archive__Reading__QueueName")
			&& !v.trim().is_empty()
		{
			result.queue.queue_name = v.trim().into();
		}
		result
	}
}

/// Frozen BaseNumberConverter trims first, then converts a signed Int32 (or
/// Int64 for PDF bytes). Its #/0x/&h hex forms use signed-width bit patterns.
/// Unconvertible values retain the pre-existing Rust default rather than the
/// frozen binder's startup exception; actual binder fixtures qualify this.
fn configuration_integer(value: &str, wide: bool) -> Option<i64> {
	let text = value.trim();
	let hexadecimal = text
		.strip_prefix('#')
		.or_else(|| text.strip_prefix("0x"))
		.or_else(|| text.strip_prefix("0X"))
		.or_else(|| text.strip_prefix("&h"))
		.or_else(|| text.strip_prefix("&H"));
	if let Some(digits) = hexadecimal {
		let bits = u64::from_str_radix(digits, 16).ok()?;
		if wide {
			Some(bits as i64)
		} else {
			u32::try_from(bits).ok().map(|bits| i64::from(bits as i32))
		}
	} else if wide {
		text.parse::<i64>().ok()
	} else {
		text.parse::<i32>().ok().map(i64::from)
	}
}
