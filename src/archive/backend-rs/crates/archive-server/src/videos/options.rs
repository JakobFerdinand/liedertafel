/// Frozen Archive:Video bounds; queue mechanics use the accepted finite engine.
#[derive(Clone)]
pub struct Options {
	pub max_input_bytes: u64,
	pub ffmpeg_path: String,
	pub ffprobe_path: String,
	pub queue: crate::extraction::Options,
}
impl Default for Options {
	fn default() -> Self {
		Self {
			max_input_bytes: 10 * 1024 * 1024 * 1024,
			ffmpeg_path: "ffmpeg".into(),
			ffprobe_path: "ffprobe".into(),
			queue: crate::extraction::Options {
				max_attempts: 3,
				stale_running_after: chrono::Duration::hours(1),
				time_budget: chrono::Duration::hours(8),
				redisplay_after: chrono::Duration::hours(1),
				max_dispatch_per_run: 100,
				visibility_backoff: chrono::Duration::minutes(1),
				batch_size: 1,
				receive_visibility: chrono::Duration::hours(8),
				max_messages_per_run: 10,
				drain_budget: chrono::Duration::hours(10),
				queue_name: "archive-videos".into(),
				..Default::default()
			},
		}
	}
}
impl Options {
	pub fn from_env() -> Self {
		let mut result = Self::default();
		macro_rules! number {
			($name:literal,$target:expr,$wide:expr) => {
				if let Ok(v) = std::env::var(concat!("Archive__Video__", $name))
					&& let Some(v) = crate::reading::configuration_integer(&v, $wide)
					&& v > 0
				{
					$target = v as _;
				}
			};
		}
		number!("MaxInputBytes", result.max_input_bytes, true);
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
		result.queue.batch_size = result.queue.batch_size.min(32);
		macro_rules! duration {
			($name:literal,$field:ident) => {
				if let Ok(v) = std::env::var(concat!("Archive__Video__", $name))
					&& let Some(v) =
						crate::storage::parse_lifetime(&v).filter(|v| *v > chrono::Duration::zero())
				{
					result.queue.$field = v;
				}
			};
		}
		duration!("TimeBudget", time_budget);
		duration!("StaleRunningAfter", stale_running_after);
		duration!("RedisplayAfter", redisplay_after);
		duration!("VisibilityBackoff", visibility_backoff);
		duration!("ReceiveVisibility", receive_visibility);
		duration!("DrainBudget", drain_budget);
		for (name, target) in [
			("QueueName", &mut result.queue.queue_name),
			("FfmpegPath", &mut result.ffmpeg_path),
			("FfprobePath", &mut result.ffprobe_path),
		] {
			if let Ok(v) = std::env::var(format!("Archive__Video__{name}"))
				&& !v.trim().is_empty()
			{
				*target = v.trim().into();
			}
		}
		result
	}
}
