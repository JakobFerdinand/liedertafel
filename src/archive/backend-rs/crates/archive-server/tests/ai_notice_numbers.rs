// BCL differential fixtures and collecting logger are pure-sibling test code.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
#[path = "support/ai.rs"]
mod ai;
use archive_server::ai::*;
use serde_json::Value;
use std::{
	io::Write,
	sync::{Arc, Mutex},
};

#[derive(Clone, Default)]
struct Capture(Arc<Mutex<Vec<u8>>>);
impl Write for Capture {
	fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
		self.0.lock().unwrap().extend_from_slice(bytes);
		Ok(bytes.len())
	}
	fn flush(&mut self) -> std::io::Result<()> {
		Ok(())
	}
}
fn captured<R>(f: impl FnOnce() -> R) -> (R, String) {
	let capture = Capture::default();
	let writer = capture.clone();
	let subscriber = tracing_subscriber::fmt()
		.without_time()
		.with_ansi(false)
		.with_target(false)
		.with_writer(move || writer.clone())
		.finish();
	let result = tracing::subscriber::with_default(subscriber, f);
	let text = String::from_utf8(capture.0.lock().unwrap().clone()).unwrap();
	(result, text)
}
fn observations() -> Vec<Value> {
	include_str!("../../../tests/fixtures/228-ai-numbers.jsonl")
		.lines()
		.map(|l| serde_json::from_str(l).unwrap())
		.collect()
}
#[test]
fn feature_breakdown_uses_actual_bcl_decimal_two_place_ties() {
	for case in observations().iter().filter(|v| v["kind"] == "format") {
		let entry = MonthEntry {
			feature: "chat".into(),
			model: "test".into(),
			calls: 1,
			input_tokens: 0,
			output_tokens: 0,
			cost_micro_eur: case["micro"].as_i64().unwrap(),
			reserved_micro_eur: 0,
		};
		assert_eq!(
			Notices::breakdown(&[entry]),
			format!("chat {} EUR", case["expected"].as_str().unwrap()),
			"{}",
			case["value"]
		);
	}
}
#[test]
fn threshold_and_cap_warning_share_the_bcl_formatter() {
	for cap in [false, true] {
		let (_, log) = captured(|| {
			tokio::runtime::Builder::new_current_thread()
				.enable_all()
				.build()
				.unwrap()
				.block_on(async {
					let h = ai::Harness::new();
					h.options.send_modify(|o| {
						if cap {
							o.monthly_cap_eur = "0.005".parse().unwrap();
						} else {
							o.notice_thresholds_eur = "0.005".into();
						}
					});
					let reservation = h.ledger.reserve(h.call()).await.unwrap();
					assert!(
						h.ledger
							.settle(reservation, Usage::reported(25000, 0))
							.await
					);
					Notices::new(h.ledger).review().await.unwrap();
				})
		});
		assert!(
			log.contains(if cap {
				"KI-Budget: Monatsbudget erreicht (2026-10: 0.03 von 0.01 EUR)"
			} else {
				"KI-Budget: Schwellwert 0.01 EUR im Monat 2026-10 erreicht (0.03 von 15.00 EUR): chat 0.03 EUR"
			}),
			"{log}"
		);
	}
}
