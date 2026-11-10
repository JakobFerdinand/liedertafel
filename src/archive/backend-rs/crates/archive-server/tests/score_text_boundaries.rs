#![allow(clippy::unwrap_used)] // Frozen external oracle/behavior assertions.
use archive_server::extraction::score::analyze;
use sha2::{Digest, Sha256};

#[test]
fn frozen_analyzer_boundary_and_utf16_observations() {
	let mut failures = Vec::new();
	let mut count = 0;
	for line in include_str!("fixtures/226-score-bcl.jsonl").lines() {
		let (name, text, repeat, suffix, clean_hash, facts): (
			String,
			String,
			usize,
			String,
			String,
			serde_json::Value,
		) = serde_json::from_str(line).unwrap();
		let input = text.repeat(repeat) + &suffix;
		let actual = analyze(Some(&input));
		let mut observed = serde_json::to_value(&actual.facts).unwrap();
		observed
			.as_object_mut()
			.unwrap()
			.retain(|_, v| !v.is_null());
		if observed != facts
			|| format!("{:x}", Sha256::digest(actual.clean_text.as_bytes())) != clean_hash
			|| actual.facts.is_empty() != facts.as_object().unwrap().is_empty()
		{
			failures.push((name, observed, facts));
		}
		count += 1;
	}
	assert_eq!(count, 54);
	assert!(
		failures.is_empty(),
		"frozen analyzer differences: {failures:?}"
	);
}
