//! Generated, public synthetic PDFs only. No private scan inputs.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Test assertions/fixtures.
use archive_server::extraction::{
	Options,
	pdf::{Outcome, PdfEngine},
};

fn pdf(pages: &[&str]) -> Vec<u8> {
	pdf_with_mapping(pages, false)
}
fn pdf_with_mapping(pages: &[&str], mapping: bool) -> Vec<u8> {
	let mut objects = vec![
		"<< /Type /Catalog /Pages 2 0 R >>".to_owned(),
		format!(
			"<< /Type /Pages /Count {} /Kids [{}] >>",
			pages.len(),
			(0..pages.len())
				.map(|i| format!("{} 0 R", 4 + i * 2))
				.collect::<Vec<_>>()
				.join(" ")
		),
		format!(
			"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica {} >>",
			if mapping {
				format!("/ToUnicode {} 0 R", 4 + pages.len() * 2)
			} else {
				String::new()
			}
		),
	];
	for (i, text) in pages.iter().enumerate() {
		let stream = format!("BT /F1 12 Tf 30 700 Td ({text}) Tj ET");
		objects.push(format!("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents {} 0 R >>",5+i*2));
		objects.push(format!(
			"<< /Length {} >>\nstream\n{stream}\nendstream",
			stream.len()
		));
	}
	if mapping {
		let cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Synthetic226 def /CMapType 2 def 1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <D83DDE00> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
		objects.push(format!(
			"<< /Length {} >>\nstream\n{cmap}\nendstream",
			cmap.len()
		));
	}
	let mut result = b"%PDF-1.4\n".to_vec();
	let mut offsets = vec![0];
	for (i, object) in objects.iter().enumerate() {
		offsets.push(result.len());
		result.extend_from_slice(format!("{} 0 obj\n{object}\nendobj\n", i + 1).as_bytes());
	}
	let xref = result.len();
	result
		.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", offsets.len()).as_bytes());
	for offset in offsets.iter().skip(1) {
		result.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
	}
	result.extend_from_slice(
		format!(
			"trailer << /Size {} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF",
			offsets.len()
		)
		.as_bytes(),
	);
	result
}

#[test]
#[ignore = "explicit native PDFium 8125 gate"]
fn native_text_pages_characters_render_and_cancellation() {
	let engine = PdfEngine::from_env().unwrap();
	let file = std::env::temp_dir().join(format!("226-pdf-{}.pdf", uuid::Uuid::new_v4()));
	std::fs::write(&file, pdf(&["Hosianna Abendstern", "Excluded second page"])).unwrap();
	let mut options = Options {
		max_pdf_pages: 1,
		..Default::default()
	};
	assert_eq!(
		engine.extract(&file, &options, || false).unwrap(),
		Outcome::Text("Hosianna Abendstern".into())
	);
	options.max_text_characters = 4;
	assert_eq!(
		engine.extract(&file, &options, || false).unwrap(),
		Outcome::Failure(archive_server::extraction::pdf::TOO_MUCH_TEXT)
	);
	assert!(engine.extract(&file, &options, || true).is_err());
	let rendered = engine.render(&file, 0, 1000, || false).unwrap();
	assert_eq!((rendered.width(), rendered.height()), (750, 1000));
	assert!(
		rendered
			.to_rgb8()
			.pixels()
			.any(|p| p.0.iter().all(|v| *v < 100))
	);
	let checks = std::cell::Cell::new(0);
	assert!(
		engine
			.extract(&file, &Options::default(), || {
				checks.set(checks.get() + 1);
				checks.get() >= 6
			})
			.is_err()
	);
	let checks = std::cell::Cell::new(0);
	assert!(
		engine
			.render(&file, 0, 1000, || {
				checks.set(checks.get() + 1);
				checks.get() >= 3
			})
			.is_err()
	);
	std::fs::write(&file, pdf(&[""])).unwrap();
	assert_eq!(
		engine
			.extract(&file, &Options::default(), || false)
			.unwrap(),
		Outcome::NoText
	);
	std::fs::write(&file, b"not a PDF").unwrap();
	assert_eq!(
		engine
			.extract(&file, &Options::default(), || false)
			.unwrap(),
		Outcome::Failure(archive_server::extraction::pdf::CORRUPT)
	);
	// Public synthetic blank page encrypted offline with pypdf 6.12.0,
	// PdfWriter.add_blank_page(600,800), encrypt("synthetic226-not-secret",
	// algorithm="RC4-128"). No private document or real password.
	use base64::Engine;
	let encrypted = base64::engine::general_purpose::STANDARD
		.decode(include_str!("fixtures/226-encrypted-pdf.base64").trim())
		.unwrap();
	std::fs::write(&file, encrypted).unwrap();
	assert_eq!(
		engine
			.extract(&file, &Options::default(), || false)
			.unwrap(),
		Outcome::Failure(archive_server::extraction::pdf::ENCRYPTED)
	);
	std::fs::write(&file, pdf_with_mapping(&["A"], true)).unwrap();
	options.max_text_characters = 1;
	assert_eq!(
		engine.extract(&file, &options, || false).unwrap(),
		Outcome::Failure(archive_server::extraction::pdf::TOO_MUCH_TEXT)
	);
	options.max_text_characters = 2;
	assert_eq!(
		engine.extract(&file, &options, || false).unwrap(),
		Outcome::Text("😀".into())
	);
	std::fs::remove_file(file).unwrap();
}
