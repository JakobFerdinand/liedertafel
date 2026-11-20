// Isolated real ENV children; actual frozen binder supplies values/errors.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Offline fixture assertions.
use archive_server::reading::Options;
use serde_json::{Value, json};
fn snapshot(o: &Options) -> Value {
	json!({"MaxPdfBytes":o.max_pdf_bytes,"MaxPagesPerRequest":o.max_pages_per_request,"ImagesPerCall":o.images_per_call,"MaxImageLongSide":o.max_image_long_side,"JpegQuality":o.jpeg_quality,"EstimatedTokensPerImage":o.estimated_tokens_per_image,"TranscriptionMaxOutputTokens":o.transcription_max_output_tokens,"FieldsMaxOutputTokens":o.fields_max_output_tokens,"MaxFieldTextCharacters":o.max_field_text_characters,"MaxAttempts":o.queue.max_attempts,"MaxDispatchPerRun":o.queue.max_dispatch_per_run,"BatchSize":o.queue.batch_size,"MaxMessagesPerRun":o.queue.max_messages_per_run})
}
#[test]
fn real_reading_numeric_environment_matches_frozen_values_or_documented_invalid_default() {
	let fixture: Value =
		serde_json::from_str(include_str!("fixtures/229-reading-configuration.json")).unwrap();
	let rows = fixture["rows"].as_array().unwrap();
	for setting in ["MaxPdfBytes", "MaxPagesPerRequest"] {
		for input in ["#0x1", "0x0x1", "&h0X1", "  #0x1  "] {
			assert!(
				rows.iter().any(|row| row["name"] == setting
					&& row["input"] == input
					&& row["error"] == false),
				"missing actual valid {setting}={input}"
			);
		}
	}
	if let Ok(index) = std::env::var("ARCHIVE_229_READING_CONFIG_CASE") {
		let row = &rows[index.parse::<usize>().unwrap()];
		// Existing Rust interface keeps defaults for conversion errors whereas
		// actual BCL binding throws. Do not relabel that as equal exceptions.
		assert_eq!(
			snapshot(&Options::from_env(false)),
			row["expected"],
			"{row}"
		);
		return;
	}
	for (index, row) in rows.iter().enumerate() {
		let mut child = std::process::Command::new(std::env::current_exe().unwrap());
		child.args([
			"--exact",
			"real_reading_numeric_environment_matches_frozen_values_or_documented_invalid_default",
			"--nocapture",
		]);
		for (key, _) in std::env::vars() {
			if key
				.replace("__", ":")
				.to_ascii_lowercase()
				.starts_with("archive:reading:")
			{
				child.env_remove(key);
			}
		}
		child
			.env("ARCHIVE_229_READING_CONFIG_CASE", index.to_string())
			.env(
				format!("Archive__Reading__{}", row["name"].as_str().unwrap()),
				row["input"].as_str().unwrap(),
			);
		let output = child.output().unwrap();
		assert!(
			output.status.success(),
			"{row}: {}",
			String::from_utf8_lossy(&output.stdout)
		);
	}
}
