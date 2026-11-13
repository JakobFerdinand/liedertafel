// Actual frozen BCL helper observations, including exhaustive scalar normalization.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Pure differential assertions.
use archive_server::reading::{ReadField, clean_value, quote_is_verified};
use serde_json::Value;
fn oracle() -> Value {
	serde_json::from_str(include_str!("fixtures/229-reading-quotes.json")).unwrap()
}
#[test]
fn shared_reading_normalization_matches_all_actual_frozen_unicode_scalars() {
	let data = oracle();
	let changes: std::collections::HashMap<u32, String> = data["scalarChanges"]
		.as_object()
		.unwrap()
		.iter()
		.map(|(point, value)| (point.parse().unwrap(), value.as_str().unwrap().to_owned()))
		.collect();
	let mut count = 0;
	for point in 0..=0x10ffff {
		let Some(c) = char::from_u32(point) else {
			continue;
		};
		let source = format!("x{c}x");
		let expected = changes.get(&point).unwrap_or(&source);
		assert_eq!(
			&archive_server::reading::normalize_reading_text(&source),
			expected,
			"U+{point:04X}"
		);
		count += 1;
	}
	assert_eq!(count, data["scalarCount"].as_u64().unwrap());
}
#[test]
fn frozen_quote_and_clean_rules_preserve_unicode_trust_and_utf16_bounds() {
	let data = oracle();
	for row in data["quotes"].as_array().unwrap() {
		let field = row["field"].as_str().unwrap();
		let value = row["value"].as_str().unwrap();
		let read = ReadField {
			field: Some(field.into()),
			value: Some(value.into()),
			certainty: Some("sicher".into()),
			quote: Some(row["quote"].as_str().unwrap().into()),
		};
		assert_eq!(
			quote_is_verified(&read, value, row["text"].as_str().unwrap()),
			row["accepted"].as_bool().unwrap(),
			"{}",
			row["name"]
		);
	}
	for row in data["cleans"].as_array().unwrap() {
		assert_eq!(
			clean_value(row["field"].as_str().unwrap(), row["raw"].as_str()).as_deref(),
			row["value"].as_str()
		);
	}
}
#[test]
fn frozen_sigma_quote_is_accepted_without_context_sensitive_final_sigma() {
	let read = ReadField {
		field: Some("composer".into()),
		value: Some("Οσ".into()),
		certainty: Some("sicher".into()),
		quote: Some("Von ΟΣ".into()),
	};
	assert!(quote_is_verified(&read, "Οσ", "Von ΟΣ"));
}
