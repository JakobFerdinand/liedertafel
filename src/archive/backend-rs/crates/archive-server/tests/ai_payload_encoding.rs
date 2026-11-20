// Frozen-fixture parsing helpers are assertion-only test code.
#![allow(clippy::unwrap_used)]
#[path = "support/ai.rs"]
mod ai;
use archive_server::ai::*;
use serde_json::{Value, json};
fn fixtures() -> Vec<Value> {
	include_str!("../../../tests/fixtures/228-ai-transport-review.jsonl")
		.lines()
		.map(|l| serde_json::from_str(l).unwrap())
		.collect()
}
#[test]
fn supported_value_payloads_and_estimates_match_actual_ai_json_utilities() {
	for case in fixtures().iter().filter(|v| v["kind"] == "payload") {
		let payload = Payload::from_value(&json!({"text":case["text"]})).unwrap();
		assert_eq!(
			payload.raw(),
			case["serialized"].as_str().unwrap(),
			"{}",
			case["name"]
		);
		for (content, field, role) in [
			(
				Content::FunctionCall {
					id: "call".into(),
					name: "f".into(),
					arguments: payload.clone(),
				},
				"argumentsEstimate",
				"assistant",
			),
			(
				Content::FunctionResult {
					id: "call".into(),
					result: payload,
				},
				"resultEstimate",
				"tool",
			),
		] {
			assert_eq!(
				estimate_text(
					&[Message {
						role: role.into(),
						contents: vec![content]
					}],
					&CallOptions::default()
				)
				.unwrap()
				.0,
				case[field].as_i64().unwrap(),
				"{} {field}",
				case["name"]
			);
		}
	}
}
#[test]
fn already_serialized_schema_and_raw_json_keep_their_spelling() {
	let raw = "{ \"text\": \"Grüß😀\u{2028}\u{2029}\u{0085}\u{E000}\" }";
	assert_eq!(Payload::json(raw).unwrap().raw(), raw);
	let case = fixtures()
		.into_iter()
		.find(|v| v["kind"] == "schema")
		.unwrap();
	let raw = case["raw"].as_str().unwrap();
	assert_eq!(Payload::json(raw).unwrap().raw(), raw);
	let options = CallOptions {
		tools: vec![Tool {
			name: case["name"].as_str().unwrap().into(),
			description: case["description"].as_str().unwrap().into(),
			schema: serde_json::from_str(raw).unwrap(),
		}],
		..Default::default()
	};
	assert_eq!(
		estimate_text(&[Message::text("user", "abc")], &options)
			.unwrap()
			.0,
		case["estimate"].as_i64().unwrap()
	);
}
#[tokio::test]
async fn frozen_twenty_seven_token_value_is_refused_at_twenty_three_before_provider_or_reservation()
{
	let case = fixtures()
		.into_iter()
		.find(|v| v["kind"] == "payload" && v["name"] == "review")
		.unwrap();
	let h = ai::Harness::new();
	h.options.send_modify(|o| o.max_input_tokens = 23);
	let script = Script::new(vec![vec![]]);
	let gateway = Gateway::scripted(h.ledger.clone(), script.clone());
	let mut request = h.request();
	request.messages = vec![Message {
		role: "tool".into(),
		contents: vec![Content::FunctionResult {
			id: "call".into(),
			result: Payload::from_value(&json!({"text":case["text"]})).unwrap(),
		}],
	}];
	assert!(matches!(
		gateway.start(request, true).unwrap().response().await,
		Err(Error::InputTooLarge {
			estimate: 27,
			bound: 23
		})
	));
	assert!(gateway.drain().await);
	assert!(script.requests().is_empty());
	assert!(h.store.rows.lock().await.is_empty());
}
