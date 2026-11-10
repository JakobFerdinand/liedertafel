//! Stored Noten reading and the shared scan-model boundary. Drafts/programmes
//! consume the model/renderer/verification seams later; no HTTP model lifetime
//! or draft registry is implemented here.
pub mod command;
mod endpoints;
mod fields;
mod model;
mod options;
mod renderer;
mod service;
mod worker;
pub(crate) use endpoints::routes;
pub use fields::{ReadField, clean_value, quote_is_verified};
pub use model::{
	FIELDS_VERSION, RenderedPage, ScanReadingModel, TRANSCRIPTION_VERSION, bound_text,
	fields_instructions, scripted_scan_reader, strip_page_markers,
};
pub use options::Options;
pub use renderer::PdfPageRenderer;
pub use service::{Job, dispatch, enqueue, insert};
pub use worker::{Error as ReadingError, Worker};
