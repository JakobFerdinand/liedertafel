#![allow(clippy::unwrap_used, clippy::expect_used)] // Offline provider-boundary oracle assertions.
#[path = "support/ai.rs"]
mod support;
use archive_server::{ai::*, reading::*};
#[tokio::test]
async fn actual_frozen_agent_schema_and_output_bounds_reach_the_budgeted_provider() {
	let h = support::Harness::new();
	let script = Script::new(vec![vec![ScriptStep::Update(Update {
		text: "{\"felder\":[]}".into(),
		usage: Usage::reported(100, 20),
		..Default::default()
	})]]);
	let gateway = Gateway::scripted(h.ledger, script.clone());
	let model = ScanReadingModel::new(gateway.clone(), archive_server::reading::Options::default());
	model
		.read_fields(
			"Waldfahrt\nVon Franz Schubert",
			&["composer", "lyrics"],
			None,
			&Default::default(),
		)
		.await
		.unwrap();
	let request = &script.requests()[0];
	let schema: serde_json::Value = serde_json::from_str(
		request
			.options
			.response_format
			.as_ref()
			.unwrap()
			.schema
			.get(),
	)
	.unwrap();
	assert_eq!(
		schema,
		serde_json::from_str::<serde_json::Value>(include_str!("fixtures/229-scan-schema.json"))
			.unwrap()
	);
	assert_eq!(
		request.options.instructions.as_deref(),
		Some(include_str!("fixtures/229-scan-fields.txt").trim_end())
	);
	assert_eq!(request.options.max_output_tokens, Some(3000));
	assert_eq!(FIELDS_VERSION, "scan-fields-v3");
	assert_eq!(
		fields_instructions()
			.lines()
			.filter(|l| l.starts_with("- "))
			.count(),
		9
	);
	assert!(gateway.drain().await);
}
#[test]
fn quote_confidence_values_and_utf16_caps_match_the_frozen_scan_rules() {
	let read = |field: &str, value: &str, quote: &str| ReadField {
		field: Some(field.into()),
		value: Some(value.into()),
		certainty: Some("sicher".into()),
		quote: Some(quote.into()),
	};
	for (field, value, quote, text, expected) in [
		(
			"composer",
			"Franz Schubert",
			"Musik: Franz Schubert",
			"MUSIK: Franz\n Schubert",
			true,
		),
		(
			"composer",
			"Hans Erfunden",
			"Musik: Hans Erfunden",
			"Musik: Franz Schubert",
			false,
		),
		(
			"lyricist",
			"Wilhelm Müller",
			"Und alle stimmen mit ein",
			"Und alle stimmen mit ein",
			false,
		),
		(
			"lyrics",
			"Im Wald da singen die Vögel\nErfundene Strophe",
			"Im Wald da singen die Vögel",
			"Im Wald da singen die Vögel",
			false,
		),
		(
			"lyrics",
			"Im Wald da singen die Vögel\nUnd alle stimmen mit ein",
			"Im Wald da singen die Vögel",
			"Im Wald da singen die Vögel\nUnd alle stimmen mit ein",
			true,
		),
		(
			"musical_key",
			"Es-Dur",
			"Tonart Es-Dur",
			"Tonart Es-Dur",
			true,
		),
		(
			"musical_key",
			"G-Dur",
			"Im Wald da singen die Vögel",
			"Im Wald da singen die Vögel",
			false,
		),
	] {
		assert_eq!(
			quote_is_verified(&read(field, value, quote), value, text),
			expected,
			"{field}"
		);
	}
	assert!(clean_value("composer", Some(&"😀".repeat(76))).is_none());
	assert!(clean_value("composer", Some(&"😀".repeat(75))).is_some());
	assert!(clean_value("composer", Some("zwei\nZeilen")).is_none());
	assert!(clean_value("lyrics", Some(&"x".repeat(3501))).is_none());
	assert_eq!(bound_text("A😀B", 3), "A😀");
	assert_eq!(
		strip_page_markers("=== Seite 1 ===\r\nText\n=== Seite 2 ===\nMehr"),
		"Text\nMehr"
	);
}
