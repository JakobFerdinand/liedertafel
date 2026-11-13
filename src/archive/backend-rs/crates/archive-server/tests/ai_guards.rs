#[path = "support/ai.rs"]
mod support;
use archive_server::ai::*;
use std::{sync::atomic::Ordering, time::Duration};
use support::Harness;

#[tokio::test]
async fn sub_micro_euro_calls_round_individually_before_an_operation_is_summed() {
	let h = Harness::new();
	h.options.send_modify(|o| {
		o.models
			.insert("scripted".into(), Price::new("0.1", "0.4").unwrap());
	});
	let call = h.call();
	for _ in 0..2 {
		let reservation = h.ledger.reserve(call.clone()).await.unwrap();
		assert!(h.ledger.settle(reservation, Usage::reported(1, 0)).await);
	}
	let row = h.row().await;
	assert_eq!((row.calls, row.input_tokens, row.cost_micro_eur), (2, 2, 2));
}

#[tokio::test(start_paused = true)]
async fn a_stuck_settlement_finitely_fails_and_retains_durable_reservation() {
	let h = Harness::new();
	h.store.hanging_settlement.store(true, Ordering::SeqCst);
	let reservation = h.ledger.reserve(h.call()).await.unwrap();
	let before = tokio::time::Instant::now();
	assert!(
		!tokio::time::timeout(
			Duration::from_secs(25),
			h.ledger.settle(reservation, Usage::reported(0, 0))
		)
		.await
		.unwrap()
	);
	assert_eq!(h.store.attempts.load(Ordering::SeqCst), 4);
	assert_eq!(h.row().await.reserved_micro_eur, 900);
	assert!(before.elapsed() >= Duration::from_millis(22100));
}

#[tokio::test]
async fn all_admission_refusals_precede_provider_and_leave_no_ledger_row() {
	for case in 0..6 {
		let h = Harness::new();
		let script = Script::new(vec![vec![]]);
		let gateway = Gateway::scripted(h.ledger.clone(), script.clone());
		let mut request = h.request();
		match case {
			0 => request.operation = None,
			1 => request.options.model_id = Some("foreign".into()),
			2 => request.messages[0]
				.contents
				.push(Content::ImageUrl("data:image/png;base64,AQID".into())),
			3 => h.options.send_modify(|o| o.max_input_tokens = 1),
			4 => h.options.send_modify(|o| o.monthly_cap_eur = 0.into()),
			_ => h.store.broken.store(true, Ordering::SeqCst),
		}
		assert!(
			gateway
				.start(request, true)
				.unwrap()
				.response()
				.await
				.is_err()
		);
		assert!(gateway.drain().await);
		assert!(script.requests().is_empty());
		assert!(h.store.rows.lock().await.is_empty());
	}
}
#[tokio::test]
async fn precedence_deployment_normalization_nontext_estimate_and_caller_options_survive() {
	let h = Harness::new();
	h.options.send_modify(|o| o.max_output_tokens = 700);
	let script = Script::new(vec![vec![]]);
	let gateway = Gateway::scripted(h.ledger.clone(), script.clone());
	let mut request = h.request();
	request.options.max_output_tokens = Some(20);
	request.options.model_id = Some(" SCRIPTED ".into());
	request.operation.as_mut().unwrap().max_output_tokens = Some(300);
	request.operation.as_mut().unwrap().non_text_input_tokens = Some(5000);
	request.messages = vec![Message {
		role: "user".into(),
		contents: vec![
			Content::Text("abc".into()),
			Content::ImageUrl("data:image/png;base64,AQID".into()),
		],
	}];
	gateway
		.start(request.clone(), true)
		.unwrap()
		.response()
		.await
		.unwrap();
	let sent = script.requests();
	assert_eq!(sent[0].options.max_output_tokens, Some(300));
	assert!(sent[0].options.model_id.is_none());
	assert_eq!(request.options.max_output_tokens, Some(20));
	let row = h.row().await;
	assert_eq!(
		(row.input_tokens, row.output_tokens, row.cost_micro_eur),
		(5009, 300, 6209)
	);
	assert!(gateway.drain().await);
}
#[tokio::test]
async fn repeated_usage_is_component_max_missing_report_is_not_reported_zero() {
	for (updates, expected) in [
		(
			vec![
				Update::usage(Usage::reported(120, 5)),
				Update::usage(Usage::reported(40, 30)),
			],
			(120, 30, 240),
		),
		(vec![Update::usage(Usage::reported(0, 0))], (0, 0, 0)),
		(vec![Update::text("Antwort")], (10, 6000, 24010)),
	] {
		let h = Harness::new();
		let script = Script::new(vec![updates.into_iter().map(ScriptStep::Update).collect()]);
		let gateway = Gateway::scripted(h.ledger.clone(), script);
		gateway
			.start(h.request(), false)
			.unwrap()
			.response()
			.await
			.unwrap();
		assert!(gateway.drain().await);
		let row = h.row().await;
		assert_eq!(
			(row.input_tokens, row.output_tokens, row.cost_micro_eur),
			expected
		);
		assert_eq!(row.reserved_micro_eur, 0);
	}
}
#[tokio::test]
async fn failure_cancel_drop_and_consumer_break_are_joined_and_charge_full_reservation() {
	for case in 0..4 {
		let h = Harness::new();
		let script = Script::new(vec![vec![
			ScriptStep::Update(Update::text("Antwort")),
			if case == 0 {
				ScriptStep::Fail
			} else {
				ScriptStep::Hold
			},
		]]);
		let gateway = Gateway::scripted(h.ledger.clone(), script);
		let mut call = gateway.start(h.request(), true).unwrap();
		assert_eq!(call.next().await.unwrap().unwrap().text, "Antwort");
		match case {
			0 => assert!(call.response().await.is_err()),
			1 => {
				call.cancel();
				assert!(call.response().await.is_err());
			}
			2 => drop(call),
			_ => {
				assert!(call.finish().await.is_err());
			}
		}
		assert!(
			tokio::time::timeout(Duration::from_secs(3), gateway.drain())
				.await
				.unwrap()
		);
		let row = h.row().await;
		assert_eq!((row.cost_micro_eur, row.reserved_micro_eur), (24010, 0));
	}
}
#[tokio::test]
async fn settlement_retries_ignore_consumer_cancel_and_fail_durably_after_four_attempts() {
	for failures in [2, 4] {
		let h = Harness::new();
		h.store.fail_settlements.store(failures, Ordering::SeqCst);
		let gateway = Gateway::scripted(
			h.ledger.clone(),
			Script::new(vec![vec![
				ScriptStep::Update(Update::text("Antwort")),
				ScriptStep::Hold,
			]]),
		);
		let mut call = gateway.start(h.request(), true).unwrap();
		call.next().await.unwrap();
		drop(call);
		assert_eq!(gateway.drain().await, failures < 4);
		assert_eq!(
			h.store.attempts.load(Ordering::SeqCst),
			if failures < 4 { 3 } else { 4 }
		);
		assert_eq!(
			h.row().await.reserved_micro_eur,
			if failures < 4 { 0 } else { 24010 }
		);
	}
}
#[tokio::test]
async fn original_month_current_prices_and_unreported_fallback_are_literal_not_clipped() {
	let h = Harness::new();
	let r = h.ledger.reserve(h.call()).await.unwrap();
	assert_eq!(r.reserved_micro_eur, 900);
	*h.clock.0.lock().unwrap() = "2026-10-31T23:01:00Z".parse().unwrap();
	h.options.send_modify(|o| {
		o.models
			.insert("scripted".into(), Price::new("10", "40").unwrap());
	});
	assert!(h.ledger.settle(r, Usage::reported(100, 200)).await);
	assert_eq!(h.ledger.status().await.unwrap().spent_micro_eur, 0);
	assert_eq!(
		h.ledger.month_entries("2026-10").await.unwrap()[0].cost_micro_eur,
		9000,
		"reported usage/current prices may exceed reserve; no universal overshoot proof"
	);
	let r = h.ledger.reserve(h.call()).await.unwrap();
	h.options.send_modify(|o| o.models.clear());
	assert!(h.ledger.settle(r, Usage::reported(0, 0)).await);
	assert_eq!(h.ledger.status().await.unwrap().spent_micro_eur, 9000);
}
#[tokio::test]
async fn dormant_embedding_guard_has_no_provider_but_preserves_budget_refusal_and_accounting() {
	let h = Harness::new();
	let values = vec!["abcdef".into()];
	assert!(dormant_embedding_call(&values, None, "scripted", None).is_err());
	let spec = dormant_embedding_call(
		&values,
		Some(Operation::start("search", None)),
		"scripted",
		None,
	)
	.unwrap();
	assert_eq!((spec.estimated_input, spec.max_output), (2, 0));
	h.options.send_modify(|o| o.monthly_cap_eur = 0.into());
	assert!(h.ledger.reserve(spec.clone()).await.is_err());
	h.options.send_modify(|o| o.monthly_cap_eur = 15.into());
	let r = h.ledger.reserve(spec).await.unwrap();
	assert!(h.ledger.settle(r, Usage::reported(7, 0)).await);
	let row = h.row().await;
	assert_eq!(
		(row.input_tokens, row.output_tokens, row.cost_micro_eur),
		(7, 0, 7)
	);
}
#[test]
fn utf16_and_raw_schema_estimate_preserve_frozen_character_semantics() {
	let messages = vec![Message::text("user", "ü😀")];
	assert_eq!(
		estimate_text(&messages, &CallOptions::default()).unwrap(),
		(9, false)
	);
	let mut options = CallOptions {
		instructions: Some("abc".into()),
		..Default::default()
	};
	options.tools.push(Tool {
		name: "f".into(),
		description: "ü😀".into(),
		schema: serde_json::from_str("{ \"type\": \"object\" }").unwrap(),
	});
	assert_eq!(estimate_text(&messages, &options).unwrap(), (18, false));
}

#[test]
fn indexed_wire_tool_assembly_bounds_arguments_and_refuses_malformed_json() {
	let mut calls = ToolCalls::default();
	assert!(
		calls
			.observe(&[ToolDelta {
				index: 128,
				..Default::default()
			}])
			.is_err()
	);
	let mut calls = ToolCalls::default();
	assert!(
		calls
			.observe(&[ToolDelta {
				index: 0,
				id: Some("a".into()),
				name: Some("f".into()),
				arguments: "x".repeat(1024 * 1024 + 1)
			}])
			.is_err()
	);
	let mut calls = ToolCalls::default();
	calls
		.observe(&[ToolDelta {
			index: 0,
			id: Some("a".into()),
			name: Some("f".into()),
			arguments: "{bad".into(),
		}])
		.unwrap();
	assert!(calls.finish().is_err());
}

#[test]
fn all_three_future_live_evaluations_skip_before_provider_construction_if_any_value_is_absent() {
	for gate in [
		LiveEvaluation::Chat,
		LiveEvaluation::Scan,
		LiveEvaluation::Programm,
	] {
		assert!(!gate.configured(|_| None));
		assert!(!gate.configured(|_| Some(" \t".into())));
		for absent in gate.required() {
			assert!(!gate.configured(|key| if key == *absent {
				None
			} else {
				Some("explicit-synthetic-config".into())
			}));
		}
		assert!(gate.configured(|_| Some("explicit-synthetic-config".into())));
	}
}

#[tokio::test]
async fn missing_zero_or_negative_price_and_broken_store_refuse_before_any_write() {
	for case in 0..5 {
		let h = Harness::new();
		match case {
			0 => h.options.send_modify(|o| o.models.clear()),
			1 => h
				.options
				.send_modify(|o| o.models.get_mut("scripted").unwrap().input = 0.into()),
			2 => h
				.options
				.send_modify(|o| o.models.get_mut("scripted").unwrap().output = 0.into()),
			3 => h
				.options
				.send_modify(|o| o.models.get_mut("scripted").unwrap().input = (-1).into()),
			_ => h.store.broken.store(true, Ordering::SeqCst),
		}
		assert!(h.ledger.reserve(h.call()).await.is_err());
		assert!(!h.ledger.would_admit(&h.call()).await);
		assert!(h.store.rows.lock().await.is_empty());
	}
}
#[tokio::test]
async fn in_flight_reservations_cap_equality_and_nonmutating_hints_match_source() {
	let h = Harness::new();
	h.options
		.send_modify(|o| o.monthly_cap_eur = "0.0009".parse().unwrap());
	let first = h.ledger.reserve(h.call()).await.unwrap();
	assert_eq!(first.reserved_micro_eur, 900);
	assert!(!h.ledger.would_admit(&h.call()).await);
	assert!(h.ledger.reserve(h.call()).await.is_err());
	assert_eq!(h.store.rows.lock().await.len(), 1);
	assert!(h.ledger.settle(first, Usage::reported(50, 10)).await);
	assert_eq!(h.ledger.status().await.unwrap().spent_micro_eur, 90);
	assert!(!h.ledger.would_admit(&h.call()).await);
	let mut smaller = h.call();
	smaller.max_output = 50;
	assert!(h.ledger.would_admit(&smaller).await);
	assert_eq!(h.store.rows.lock().await.len(), 1);
}
#[tokio::test]
async fn missing_settlement_row_does_not_retry_and_failed_settlement_remains_reserved() {
	let h = Harness::new();
	let reservation = h.ledger.reserve(h.call()).await.unwrap();
	h.store.missing.store(true, Ordering::SeqCst);
	assert!(!h.ledger.settle(reservation, Usage::reported(0, 0)).await);
	assert_eq!(h.store.attempts.load(Ordering::SeqCst), 1);
	assert_eq!(h.ledger.status().await.unwrap().spent_micro_eur, 900);
}
#[tokio::test]
async fn model_free_notices_keep_below_threshold_silent_include_cap_and_read_each_feature() {
	let h = Harness::new();
	let notices = Notices::new(h.ledger.clone());
	assert_eq!(notices.review().await.unwrap().notified_micro_eur, 0);
	assert_eq!(
		notices.state().await.unwrap().thresholds_eur,
		vec![15.into()]
	);
	h.options
		.send_modify(|o| o.notice_thresholds_eur = "5,bloss,7,0,-1".into());
	assert_eq!(
		notices.state().await.unwrap().thresholds_eur,
		vec![5.into(), 7.into(), 15.into()]
	);
	let entries = vec![
		MonthEntry {
			feature: "chat".into(),
			model: "test".into(),
			calls: 3,
			input_tokens: 1000,
			output_tokens: 2000,
			cost_micro_eur: 4_200_000,
			reserved_micro_eur: 0,
		},
		MonthEntry {
			feature: "lied-lesung".into(),
			model: "test".into(),
			calls: 1,
			input_tokens: 500,
			output_tokens: 0,
			cost_micro_eur: 50_000,
			reserved_micro_eur: 100_000,
		},
	];
	assert_eq!(
		Notices::breakdown(&entries),
		"chat 4.20 EUR; lied-lesung 0.15 EUR"
	);
}
