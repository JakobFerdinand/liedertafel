//! Model-free stored-Datei text extraction. Durable state lives in the canonical
//! baseline tables; queue messages are at-least-once notifications, not leases.
pub mod command;
mod endpoints;
pub mod pdf;
pub mod score;
mod service;
mod worker;
pub(crate) use endpoints::routes;
pub use service::{Hook, Job, dispatch, enqueue};
pub use worker::Worker;

#[derive(Clone)]
pub struct Options {
	pub max_pdf_bytes: u64,
	pub max_pdf_pages: i32,
	pub max_text_characters: usize,
	pub max_attempts: i32,
	pub stale_running_after: chrono::Duration,
	pub time_budget: chrono::Duration,
	pub redisplay_after: chrono::Duration,
	pub max_dispatch_per_run: i64,
	pub visibility_backoff: chrono::Duration,
	pub batch_size: usize,
	pub receive_visibility: chrono::Duration,
	pub max_messages_per_run: usize,
	pub drain_budget: chrono::Duration,
	pub queue_name: String,
}
impl Default for Options {
	fn default() -> Self {
		Self {
			max_pdf_bytes: 67_108_864,
			max_pdf_pages: 500,
			max_text_characters: 100_000,
			max_attempts: 5,
			stale_running_after: chrono::Duration::minutes(15),
			time_budget: chrono::Duration::minutes(2),
			redisplay_after: chrono::Duration::minutes(10),
			max_dispatch_per_run: 200,
			visibility_backoff: chrono::Duration::seconds(30),
			batch_size: 8,
			receive_visibility: chrono::Duration::minutes(10),
			max_messages_per_run: 500,
			drain_budget: chrono::Duration::minutes(10),
			queue_name: "archive-extraction".into(),
		}
	}
}
impl Options {
	pub fn from_env() -> Self {
		let mut result = Self::default();
		macro_rules! number {
			($name:literal,$field:ident) => {
				if let Ok(v) = std::env::var(concat!("Archive__Extraction__", $name)) {
					if let Ok(v) = v.parse() {
						if v > 0 {
							result.$field = v;
						}
					}
				}
			};
		}
		number!("MaxPdfBytes", max_pdf_bytes);
		number!("MaxPdfPages", max_pdf_pages);
		number!("MaxTextCharacters", max_text_characters);
		number!("MaxAttempts", max_attempts);
		number!("MaxDispatchPerRun", max_dispatch_per_run);
		number!("BatchSize", batch_size);
		number!("MaxMessagesPerRun", max_messages_per_run);
		result.batch_size = result.batch_size.min(8);
		macro_rules! duration {
			($name:literal,$field:ident) => {
				if let Ok(v) = std::env::var(concat!("Archive__Extraction__", $name)) {
					if let Some(v) =
						crate::storage::parse_lifetime(&v).filter(|v| *v > chrono::Duration::zero())
					{
						result.$field = v;
					}
				}
			};
		}
		duration!("StaleRunningAfter", stale_running_after);
		duration!("TimeBudget", time_budget);
		duration!("RedisplayAfter", redisplay_after);
		duration!("VisibilityBackoff", visibility_backoff);
		duration!("ReceiveVisibility", receive_visibility);
		duration!("DrainBudget", drain_budget);
		if let Ok(name) = std::env::var("Archive__Extraction__QueueName")
			&& !name.trim().is_empty()
		{
			result.queue_name = name.trim().into();
		}
		result
	}
	pub fn visibility_fit_batch_size(&self) -> usize {
		let ticks = |value: chrono::Duration| {
			i128::from(value.num_seconds()) * 10_000_000 + i128::from(value.subsec_nanos()) / 100
		};
		let fit = (ticks(self.receive_visibility) - 300_000_000) / ticks(self.time_budget).max(1);
		usize::try_from(fit.max(1))
			.unwrap_or(usize::MAX)
			.min(self.batch_size)
	}
}
