//! Deterministic video copies; originals are immutable preservation material.
pub mod command;
mod endpoints;
mod options;
mod probe;
mod process;
mod service;
mod worker;
pub(crate) use endpoints::routes;
pub use options::Options;
pub use probe::{Action, Plan, Probe};
pub use service::{Hook, Job, dispatch, enqueue};

pub fn is_material(kind: &str, content_type: &str) -> bool {
	kind == "aufnahme"
		&& content_type
			.get(..6)
			.is_some_and(|s| s.eq_ignore_ascii_case("video/"))
}
pub fn select_queue(
	options: &Options,
	development: bool,
	clock: std::sync::Arc<dyn crate::auth::Clock>,
) -> Result<crate::queue::AzureQueue, crate::queue::QueueError> {
	crate::queue::AzureQueue::select(
		std::env::var("ConnectionStrings__archive-queues")
			.ok()
			.as_deref(),
		std::env::var("Archive__Video__QueueServiceUri")
			.ok()
			.filter(|s| !s.trim().is_empty())
			.or_else(|| std::env::var("Archive__Extraction__QueueServiceUri").ok())
			.as_deref(),
		&options.queue.queue_name,
		development,
		clock,
	)
}
