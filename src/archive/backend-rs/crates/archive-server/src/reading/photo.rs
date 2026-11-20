//! Phone photo adapter: source rotations only (mirrored EXIF is NOT auto-oriented).
use super::{RenderedPage, programme::ProgrammError};
use crate::extraction::pdf;
use image::{ImageDecoder, metadata::Orientation};
use tokio_util::sync::CancellationToken;
pub fn downscale(
	bytes: &[u8],
	long_side: i32,
	quality: u8,
	cancel: &CancellationToken,
) -> Result<RenderedPage, ProgrammError> {
	let unreadable = || ProgrammError::Material(super::worker::UNREADABLE);
	if cancel.is_cancelled() {
		return Err(pdf::Error::Cancelled.into());
	}
	let reader = image::ImageReader::new(std::io::Cursor::new(bytes))
		.with_guessed_format()
		.map_err(|_| unreadable())?;
	let mut decoder = reader.into_decoder().map_err(|_| unreadable())?;
	let orientation = decoder.orientation().unwrap_or(Orientation::NoTransforms);
	let decoded = image::DynamicImage::from_decoder(decoder).map_err(|_| unreadable())?;
	let upright = match orientation {
		Orientation::Rotate90 => decoded.rotate90(),
		Orientation::Rotate180 => decoded.rotate180(),
		Orientation::Rotate270 => decoded.rotate270(),
		_ => decoded,
	};
	let scale = (f64::from(long_side) / f64::from(upright.width().max(upright.height()))).min(1.0);
	let width = (f64::from(upright.width()) * scale)
		.round_ties_even()
		.max(1.0) as u32;
	let height = (f64::from(upright.height()) * scale)
		.round_ties_even()
		.max(1.0) as u32;
	// Skia decodes/resizes premultiplied pixels and its JPEG drops alpha onto
	// black. Dropping straight PNG/WebP alpha would reveal invisible RGB instead.
	let mut premultiplied = upright.into_rgba8();
	for pixel in premultiplied.pixels_mut() {
		let alpha = u16::from(pixel.0[3]);
		for channel in &mut pixel.0[..3] {
			*channel = ((u16::from(*channel) * alpha + 127) / 255) as u8;
		}
	}
	let image = image::DynamicImage::ImageRgba8(premultiplied)
		.resize_exact(width, height, image::imageops::FilterType::Triangle)
		.into_rgb8();
	let mut jpeg = vec![];
	image::codecs::jpeg::JpegEncoder::new_with_quality(&mut jpeg, quality)
		.encode_image(&image)
		.map_err(|_| unreadable())?;
	if cancel.is_cancelled() {
		return Err(pdf::Error::Cancelled.into());
	}
	Ok(RenderedPage {
		page_number: 1,
		bytes: jpeg,
		content_type: "image/jpeg".into(),
	})
}
