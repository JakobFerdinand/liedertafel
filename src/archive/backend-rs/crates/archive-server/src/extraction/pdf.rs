//! Explicit 8125 ABI, bounded seekable local spool, safe high-level text APIs.
//! Native calls are synchronous: cancellation rejects expired results, but does
//! not preempt PDFium parsing or bound decoded native memory (image gate #235).
use super::Options;
use pdfium_render::prelude::*;
use std::{
	path::{Path, PathBuf},
	sync::{Arc, OnceLock},
};
pub const ENCRYPTED: &str = "Das Dokument ist verschlüsselt.";
pub const CORRUPT: &str = "Das Dokument konnte nicht gelesen werden.";
pub const TOO_LARGE: &str = "Das Dokument ist zu groß für die automatische Textauswertung.";
pub const TOO_MUCH_TEXT: &str =
	"Das Dokument enthält zu viel Text für die automatische Auswertung.";

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
	Text(String),
	NoText,
	Failure(&'static str),
}
#[derive(Debug, Clone, Copy, thiserror::Error)]
pub enum Error {
	#[error("PDFium-Konfiguration nicht verfügbar.")]
	Configuration,
	#[error("PDF-Verarbeitung abgebrochen.")]
	Cancelled,
	#[error("PDF-Datei nicht verfügbar.")]
	Io,
}
#[derive(Clone)]
pub struct PdfEngine(Arc<Pdfium>);
impl PdfEngine {
	pub fn from_env() -> Result<Self, Error> {
		static ENGINE: OnceLock<Result<Arc<Pdfium>, Error>> = OnceLock::new();
		ENGINE
			.get_or_init(|| {
				let path = PathBuf::from(
					std::env::var_os("Archive__Pdfium__LibraryPath").ok_or(Error::Configuration)?,
				);
				if !path.is_absolute() {
					return Err(Error::Configuration);
				}
				let bindings = Pdfium::bind_to_library(&path).map_err(|_| Error::Configuration)?;
				Ok(Arc::new(Pdfium::new(bindings)))
			})
			.clone()
			.map(Self)
	}
	pub fn extract(
		&self,
		path: &Path,
		options: &Options,
		cancelled: impl Fn() -> bool,
	) -> Result<Outcome, Error> {
		check(&cancelled)?;
		if std::fs::metadata(path).map_err(|_| Error::Io)?.len() > options.max_pdf_bytes {
			return Ok(Outcome::Failure(TOO_LARGE));
		}
		let document = match self.0.load_pdf_from_file(path, None) {
			Ok(d) => d,
			Err(e) => return structure(e, &cancelled),
		};
		check(&cancelled)?;
		let result = (|| {
			let mut output = String::new();
			let mut units = 0usize;
			for index in 0..document.pages().len().min(options.max_pdf_pages) {
				check(&cancelled)?;
				let page = match document.pages().get(index) {
					Ok(p) => p,
					Err(e) => return structure(e, &cancelled),
				};
				let text = match page.text() {
					Ok(t) => t,
					Err(e) => return structure(e, &cancelled),
				};
				check(&cancelled)?;
				// chars() collects indices, so count BEFORE requesting the collection.
				// Each index contributes at least one UTF-16 code unit. No .all()
				// allocation of an unbounded page followed by truncate.
				let separator = usize::from(!output.is_empty());
				let count = usize::try_from(text.len()).map_err(|_| Error::Io)?;
				if count.saturating_add(separator)
					> options.max_text_characters.saturating_sub(units)
				{
					return Ok(Outcome::Failure(TOO_MUCH_TEXT));
				}
				if separator > 0 {
					output.push('\n');
					units += 1;
				}
				let mut page_units = Vec::with_capacity(count);
				for character in text.chars().iter() {
					check(&cancelled)?;
					// PDFium can expose supplementary code points as two surrogate
					// indices. unicode_char() would drop both as non-scalars.
					let value = character.unicode_value();
					if let Ok(unit) = u16::try_from(value) {
						units += 1;
						if units <= options.max_text_characters {
							page_units.push(unit);
						}
					} else if let Some(c) = char::from_u32(value) {
						let mut buffer = [0; 2];
						let encoded = c.encode_utf16(&mut buffer);
						units += encoded.len();
						if units <= options.max_text_characters {
							page_units.extend_from_slice(encoded);
						}
					}
					if units > options.max_text_characters {
						return Ok(Outcome::Failure(TOO_MUCH_TEXT));
					}
				}
				output.push_str(&String::from_utf16_lossy(&page_units));
			}
			Ok(if output.is_empty() {
				Outcome::NoText
			} else {
				Outcome::Text(output)
			})
		})();
		drop(document);
		check(&cancelled)?;
		result
	}
	/// Shared bounded rendering seam for the later reading worker, not an AI path.
	pub fn render(
		&self,
		path: &Path,
		page: i32,
		long_side: i32,
		cancelled: impl Fn() -> bool,
	) -> Result<image::DynamicImage, Error> {
		check(&cancelled)?;
		let document = self
			.0
			.load_pdf_from_file(path, None)
			.map_err(|_| Error::Io)?;
		let page = document.pages().get(page).map_err(|_| Error::Io)?;
		check(&cancelled)?;
		let long_side = long_side.clamp(1, 2048);
		let width = page.width().value;
		let height = page.height().value;
		if !width.is_finite() || !height.is_finite() || width <= 0.0 || height <= 0.0 {
			return Err(Error::Io);
		}
		let scale = long_side as f32 / width.max(height);
		let w = (width * scale).round().clamp(1.0, long_side as f32) as i32;
		let h = (height * scale).round().clamp(1.0, long_side as f32) as i32;
		let config = PdfRenderConfig::new()
			.set_target_size(w, h)
			.set_maximum_width(long_side)
			.set_maximum_height(long_side);
		let bitmap = page.render_with_config(&config).map_err(|_| Error::Io)?;
		check(&cancelled)?;
		let image = bitmap.as_image().map_err(|_| Error::Io)?;
		drop(bitmap);
		drop(page);
		drop(document);
		check(&cancelled)?;
		Ok(image)
	}
}
fn check(cancelled: &impl Fn() -> bool) -> Result<(), Error> {
	if cancelled() {
		Err(Error::Cancelled)
	} else {
		Ok(())
	}
}
fn structure(error: PdfiumError, cancelled: &impl Fn() -> bool) -> Result<Outcome, Error> {
	check(cancelled)?;
	match error {
		PdfiumError::PdfiumLibraryInternalError(PdfiumInternalError::PasswordError) => {
			Ok(Outcome::Failure(ENCRYPTED))
		}
		PdfiumError::PdfiumLibraryInternalError(
			PdfiumInternalError::FormatError
			| PdfiumInternalError::SecurityError
			| PdfiumInternalError::PageError
			| PdfiumInternalError::Unknown,
		) => Ok(Outcome::Failure(CORRUPT)),
		_ => Err(Error::Io),
	}
}
