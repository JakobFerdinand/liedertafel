//! Stored Noten reading, Lied draft suggestions and their shared scan boundary.
//! HTTP consumers keep request/owned-draft lifetimes separate and use Gateway.
pub mod command;
mod draft;
mod draft_endpoints;
mod draft_registry;
pub use draft_registry::DraftReadingRegistry;
mod endpoints;
mod fields;
pub(crate) mod http;
mod model;
mod options;
mod photo;
pub mod programme;
mod programme_endpoints;
mod programme_script;
mod renderer;
mod service;
mod worker;
pub(crate) fn routes() -> Vec<(
	&'static str,
	axum::routing::MethodRouter<std::sync::Arc<crate::Origin>>,
)> {
	let mut routes = endpoints::routes();
	routes.extend(draft_endpoints::routes());
	routes
}
pub use draft::{DraftError, DraftReader, DraftSuggestion};
pub(crate) use service::insert_handoff;

/// One reading Queue selection for the origin, extraction handoff and finite
/// reader. Whitespace-normalized Reading URI falls back to Extraction's URI.
pub fn select_queue(
	options: &Options,
	development: bool,
	clock: std::sync::Arc<dyn crate::auth::Clock>,
) -> Result<crate::queue::AzureQueue, crate::queue::QueueError> {
	crate::queue::AzureQueue::select(
		std::env::var("ConnectionStrings__archive-queues")
			.ok()
			.as_deref(),
		std::env::var("Archive__Reading__QueueServiceUri")
			.ok()
			.filter(|s| !s.trim().is_empty())
			.or_else(|| std::env::var("Archive__Extraction__QueueServiceUri").ok())
			.as_deref(),
		&options.queue.queue_name,
		development,
		clock,
	)
}
pub use fields::normalize as normalize_reading_text;
pub use fields::{ReadField, clean_value, quote_is_verified};
pub use model::{
	FIELDS_VERSION, RenderedPage, ScanReadingModel, TRANSCRIPTION_VERSION, bound_text,
	fields_instructions, scripted_scan_reader, scripted_scan_reader_with_programme_transcription,
	strip_page_markers,
};
pub use options::Options;
pub use renderer::PdfPageRenderer;
pub use service::{Job, dispatch, enqueue, insert};
pub use worker::{Error as ReadingError, Worker};
