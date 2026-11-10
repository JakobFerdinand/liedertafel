#![allow(clippy::unwrap_used, clippy::expect_used)] // Native PDFium raster/JPEG assertions.
mod support;
use archive_server::{extraction::pdf::Spool, reading::PdfPageRenderer};
#[tokio::test]
#[ignore = "explicit pinned native PDFium; generated raster page/JPEG/clamp/cancellation"]
async fn renderer_counts_and_renders_only_requested_pages_with_visible_pixels_and_exact_long_side()
{
	let cancel = tokio_util::sync::CancellationToken::new();
	let pdf = support::reading::Bytes(support::reading::scan(3));
	let spool = Spool::load(
		&pdf,
		"synthetic",
		1_000_000,
		std::path::Path::new("/tmp/opencode"),
		|| false,
	)
	.await
	.unwrap();
	let path = spool.0.clone();
	let renderer = PdfPageRenderer::from_env().unwrap();
	assert_eq!(
		renderer
			.page_count(spool.clone(), cancel.clone())
			.await
			.unwrap(),
		3
	);
	let pages = renderer
		.render(spool.clone(), 2, 8, 1000, 80, cancel.clone())
		.await
		.unwrap();
	assert_eq!(
		pages.iter().map(|p| p.page_number).collect::<Vec<_>>(),
		vec![2, 3]
	);
	for page in pages {
		assert_eq!(page.content_type, "image/jpeg");
		assert!(page.bytes.starts_with(&[0xff, 0xd8]));
		let image = image::load_from_memory(&page.bytes).unwrap().into_rgb8();
		assert_eq!(image.width().max(image.height()), 1000);
		assert!(
			image
				.pixels()
				.filter(|p| p.0.iter().all(|v| *v < 100))
				.count() > 1000
		);
	}
	assert!(
		renderer
			.render(spool.clone(), 8, 2, 1000, 80, cancel.clone())
			.await
			.unwrap()
			.is_empty()
	);
	cancel.cancel();
	assert!(
		renderer
			.render(spool.clone(), 1, 1, 1000, 80, cancel)
			.await
			.is_err()
	);
	drop(spool);
	assert!(!path.exists());
	let invalid = support::reading::Bytes(b"%PDF-1.7\n%%EOF".to_vec());
	let spool = Spool::load(
		&invalid,
		"synthetic",
		100,
		std::path::Path::new("/tmp/opencode"),
		|| false,
	)
	.await
	.unwrap();
	assert!(
		renderer
			.page_count(spool, Default::default())
			.await
			.is_err()
	);
}
