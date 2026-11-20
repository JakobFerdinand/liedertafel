//! P1: identical synthetic bodies from the actual pinned frozen HTTP oracle.
mod support;
use archive_server::ai::{Content, Script, ScriptStep, Update, Usage};
use serde_json::Value;
use support::{App, chat::*};

#[tokio::test]
#[ignore = "dedicated local PostgreSQL; actual frozen AG-UI grammar corpus"]
async fn pinned_frozen_request_grammar_validates_all_supplied_fields_before_any_paid_work() {
	let fixture: Value = serde_json::from_str(include_str!(
		"../../../tests/fixtures/232-chat-binding.json"
	))
	.unwrap();
	let media: Value = serde_json::from_str(include_str!(
		"../../../tests/fixtures/232-chat-media-base64.json"
	))
	.unwrap();
	let mut app = App::new_scoped("chat232_binding").await;
	let harness = install(
		&mut app,
		Script::handler(|_| {
			vec![
				ScriptStep::Update(Update::text("Gebundene Antwort")),
				ScriptStep::Update(Update::usage(Usage::reported(7, 14))),
			]
		}),
	);
	let mut member = app.member().await;
	for row in fixture["cases"]
		.as_array()
		.unwrap()
		.iter()
		.chain(media["cases"].as_array().unwrap())
	{
		let name = row["name"].as_str().unwrap();
		let before = harness.script.requests().len();
		let previous: (i64, i64) = sqlx::query_as(
			"SELECT (SELECT count(*) FROM ai_usage_entries),(SELECT count(*) FROM chat_threads)",
		)
		.fetch_one(&app.pool)
		.await
		.unwrap();
		let (status, _, bytes) = member
			.wire("POST", "/api/chat", row["raw"].as_str().unwrap())
			.await;
		assert_eq!(
			status.as_u16(),
			row["status"].as_u64().unwrap() as u16,
			"{name}"
		);
		let after: (i64, i64) = sqlx::query_as(
			"SELECT (SELECT count(*) FROM ai_usage_entries),(SELECT count(*) FROM chat_threads)",
		)
		.fetch_one(&app.pool)
		.await
		.unwrap();
		if status == 200 {
			assert_eq!(harness.script.requests().len(), before + 1, "{name}");
			assert_eq!(after, (previous.0 + 1, previous.1 + 1), "{name}");
			assert!(
				String::from_utf8_lossy(&bytes).contains("RUN_FINISHED"),
				"{name}"
			);
			let requests = harness.script.requests();
			assert_eq!(
				requests.last().unwrap().messages.len(),
				1,
				"{name}: browser media/history is not grounding"
			);
			assert_eq!(requests.last().unwrap().messages[0].role, "user");
			assert_eq!(requests.last().unwrap().messages[0].contents.len(), 1);
			let question = &requests.last().unwrap().messages.last().unwrap().contents[0];
			assert!(
				matches!(question,Content::Text(t) if t==row["question"].as_str().unwrap()),
				"{name}"
			);
		} else {
			assert_eq!(
				harness.script.requests().len(),
				before,
				"{name}: rejected before provider"
			);
			assert_eq!(
				after, previous,
				"{name}: rejected before ledger/thread writes"
			);
		}
	}
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}
