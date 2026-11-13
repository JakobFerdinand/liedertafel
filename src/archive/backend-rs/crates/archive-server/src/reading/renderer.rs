//! Thin reading adapter over the accepted shared native engine/spool. One-based
//! public page numbers; JPEGs bounded per batch and per image before allocation.
use super::model::RenderedPage;
use crate::extraction::pdf::{self, PdfEngine, Spool};
use std::sync::Arc;
#[derive(Clone)]
pub struct PdfPageRenderer(pub PdfEngine);
impl PdfPageRenderer {
	pub fn from_env() -> Result<Self, pdf::Error> {
		PdfEngine::from_env().map(Self)
	}
	pub async fn page_count(
		&self,
		spool: Arc<Spool>,
		cancel: tokio_util::sync::CancellationToken,
	) -> Result<i32, pdf::Error> {
		let engine = self.0.clone();
		tokio::task::spawn_blocking(move || engine.page_count(&spool.0, || cancel.is_cancelled()))
			.await
			.map_err(|_| pdf::Error::Io)?
	}
	pub async fn render(
		&self,
		spool: Arc<Spool>,
		first: i32,
		count: i32,
		long_side: i32,
		quality: u8,
		cancel: tokio_util::sync::CancellationToken,
	) -> Result<Vec<RenderedPage>, pdf::Error> {
		let engine = self.0.clone();
		tokio::task::spawn_blocking(move || {
			let total = engine.page_count(&spool.0, || cancel.is_cancelled())?;
			let last = total.min(first.saturating_add(count.clamp(0, 10)).saturating_sub(1));
			let mut pages = vec![];
			for page in first.max(1)..=last {
				let image = engine
					.render(&spool.0, page - 1, long_side, || cancel.is_cancelled())?
					.into_rgb8();
				let mut bytes = vec![];
				image::codecs::jpeg::JpegEncoder::new_with_quality(
					&mut bytes,
					quality.clamp(1, 100),
				)
				.encode_image(&image)
				.map_err(|_| pdf::Error::Io)?;
				if cancel.is_cancelled() {
					return Err(pdf::Error::Cancelled);
				}
				pages.push(RenderedPage {
					page_number: page,
					bytes,
					content_type: "image/jpeg".into(),
				});
			}
			Ok(pages)
		})
		.await
		.map_err(|_| pdf::Error::Io)?
	}
}
