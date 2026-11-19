use super::{Duration, expiry_grace};

#[derive(serde::Deserialize)]
struct Oracle {
	rows: Vec<Row>,
}
#[derive(serde::Deserialize)]
struct Row {
	input: Option<String>,
	ticks: Option<i64>,
}

#[test]
fn exact_ticks_and_rejections_match_frozen_expiry_grace_binding() {
	let oracle: Oracle = serde_json::from_str(include_str!(
		"../../../../tests/fixtures/expiry-grace-bcl.json"
	))
	.unwrap();
	let mut mismatches = Vec::new();
	for Row { input, ticks } in oracle.rows {
		let duration = input
			.as_deref()
			.map_or(Some(Duration::hours(1)), expiry_grace);
		let actual = duration.map(|d| {
			let whole = d.num_seconds();
			whole * 10_000_000 + i64::from(d.subsec_nanos()) / 100
		});
		if actual != ticks {
			mismatches.push((input, ticks, actual));
		}
	}
	assert!(mismatches.is_empty(), "{mismatches:#?}");
}
