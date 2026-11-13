//! Suggestions only. No catalogue writer or stored-reading state is reachable.
use super::{PdfPageRenderer, ScanReadingModel, clean_value, quote_is_verified};
use crate::{
	ai,
	extraction::{
		self,
		pdf::{self, Spool},
		score,
	},
};
use std::collections::HashMap;
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

const FIELDS: [(&str, &str); 8] = [
	("title", "title"),
	("composer", "composer"),
	("arranger", "arranger"),
	("lyricist", "lyricist"),
	("voice_configuration", "voiceConfiguration"),
	("musical_key", "musicalKey"),
	("language", "language"),
	("lyrics", "lyrics"),
];
#[derive(Clone, serde::Serialize)]
pub struct DraftSuggestion {
	#[serde(rename = "feld")]
	pub field: String,
	#[serde(rename = "wert")]
	pub value: String,
	#[serde(rename = "sicher")]
	pub sure: bool,
}
#[derive(Debug, thiserror::Error)]
pub enum DraftError {
	#[error("Die angefragten Seiten überschreiten die Eingabegrenze.")]
	InputTooLarge,
	#[error("Dokument nicht lesbar.")]
	Material(&'static str),
	#[error("PDF-Verarbeitung gescheitert.")]
	Pdf(#[from] pdf::Error),
	#[error("KI-Lesung gescheitert.")]
	Ai(#[from] ai::Error),
}
pub struct DraftReader {
	pub model: ScanReadingModel,
	pub extraction: extraction::Options,
}
impl DraftReader {
	pub async fn read(
		&self,
		bytes: &[u8],
		actor: Option<Uuid>,
		cancel: &CancellationToken,
	) -> Result<Vec<DraftSuggestion>, DraftError> {
		if bytes.len() as u64 > self.model.options.max_pdf_bytes {
			return Err(DraftError::Material(super::worker::TOO_LARGE));
		}
		let spool =
			Spool::from_bytes(bytes, &std::env::temp_dir(), || cancel.is_cancelled()).await?;
		let renderer = PdfPageRenderer::from_env()?;
		let engine = renderer.0.clone();
		let source = spool.clone();
		let token = cancel.clone();
		let extraction = self.extraction.clone();
		let outcome = tokio::task::spawn_blocking(move || {
			engine.extract(&source.0, &extraction, || token.is_cancelled())
		})
		.await
		.map_err(|_| pdf::Error::Io)??;
		let text = match outcome {
			pdf::Outcome::Failure(reason) => return Err(DraftError::Material(reason)),
			pdf::Outcome::Text(text) => text,
			pdf::Outcome::NoText => {
				let count = 3
					.min(self.model.options.images_per_call)
					.min(renderer.page_count(spool.clone(), cancel.clone()).await?);
				if count <= 0 {
					return Ok(vec![]);
				}
				// Include the shared prompt/image estimator BEFORE native rendering.
				if self.model.batch_size(1, count)? != count {
					// A draft asks for its first three pages as ONE call. Do not use
					// the worker's adaptive batching to silently omit requested pages.
					return Err(DraftError::InputTooLarge);
				}
				let pages = renderer
					.render(
						spool,
						1,
						count,
						self.model.options.max_image_long_side,
						self.model.options.jpeg_quality,
						cancel.clone(),
					)
					.await
					.map_err(|e| match e {
						pdf::Error::Io => DraftError::Material(super::worker::UNREADABLE),
						_ => DraftError::Pdf(e),
					})?;
				if pages.is_empty() {
					return Ok(vec![]);
				}
				self.model.transcribe(&pages, actor, cancel).await?
			}
		};
		if text.trim().is_empty() {
			return Ok(vec![]);
		}
		let facts = score::analyze(Some(&text)).facts;
		let mut found = HashMap::new();
		for (field, value) in [
			("composer", facts.composer),
			("lyricist", facts.lyricist),
			("arranger", facts.arranger),
			("musical_key", facts.musical_key),
		] {
			if let Some(value) = value {
				found.insert(
					field,
					DraftSuggestion {
						field: form(field).into(),
						value,
						sure: true,
					},
				);
			}
		}
		let wanted = FIELDS
			.iter()
			.map(|(f, _)| *f)
			.filter(|f| !found.contains_key(f))
			.collect::<Vec<_>>();
		let mut seen = std::collections::HashSet::new();
		for read in self
			.model
			.read_fields(&text, &wanted, actor, cancel)
			.await?
		{
			let Some(field) = read.field.as_deref() else {
				continue;
			};
			if !wanted.contains(&field) || !seen.insert(field.to_owned()) {
				continue;
			}
			let Some(value) = clean_value(field, read.value.as_deref()) else {
				continue;
			};
			if !quote_is_verified(&read, &value, &text) {
				continue;
			}
			found.insert(
				FIELDS
					.iter()
					.find(|(f, _)| *f == field)
					.map(|(f, _)| *f)
					.unwrap_or_default(),
				DraftSuggestion {
					field: form(field).into(),
					value,
					sure: read
						.certainty
						.as_deref()
						.is_some_and(|s| s.trim().eq_ignore_ascii_case("sicher")),
				},
			);
		}
		Ok(FIELDS.iter().filter_map(|(f, _)| found.remove(f)).collect())
	}
}
fn form(field: &str) -> &str {
	FIELDS
		.iter()
		.find(|(f, _)| *f == field)
		.map(|(_, f)| *f)
		.unwrap_or_default()
}
