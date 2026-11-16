//! Current jsonb/visibility SQL; historical EF add/down/up has its explicit
//! frozen-head equivalent in historical_baseline, not an old-schema upgrade.
mod support;
use archive_server::ai::{Script, ScriptStep, Update};
use serde_json::{Value, json};
use support::{App, catalogue::lied, chat::*};
use uuid::Uuid;

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn current_jsonb_roundtrip_containment_and_fresh_visibility_recheck_rebuild_ids_not_labels() {
	let mut app = App::new_scoped("chat232_jsonb").await;
	let first = lied(&app, "Erster Titel", json!({"composer":"Änderbar"})).await;
	let second = lied(&app, "Zweiter Titel", json!({})).await;
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool(
				"zeige_oberflaeche",
				json!({"komponenten":[{"komponente":"Quellen","liedIds":[first,second]}]}),
			)],
			vec![ScriptStep::Update(Update::text("Text bleibt."))],
		]),
	);
	let mut member = app.member().await;
	let thread = Uuid::new_v4();
	let events = run(&mut member, &thread.to_string(), "Quellenkarte").await;
	let expected = &events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"];
	let column: String = sqlx::query_scalar("SELECT data_type FROM information_schema.columns WHERE table_name='chat_messages' AND column_name='A2uiJson'").fetch_one(&app.pool).await.unwrap();
	assert_eq!(column, "jsonb");
	let (id,stored,kind): (Uuid,Value,String) = sqlx::query_as(r#"SELECT "Id","A2uiJson",jsonb_typeof("A2uiJson") FROM chat_messages WHERE "ThreadId"=$1 AND "Role"='assistant'"#).bind(thread).fetch_one(&app.pool).await.unwrap();
	assert_eq!(kind, "array");
	assert_eq!(stored, *expected);
	let contained: i64 = sqlx::query_scalar(r#"SELECT count(*) FROM chat_messages WHERE "ThreadId"=$1 AND "A2uiJson" @> $2::jsonb"#).bind(thread).bind(json!([{"updateComponents":{"components":[{"component":"Quellen","quellen":[{"liedId":first}]}]}}])).fetch_one(&app.pool).await.unwrap();
	assert_eq!(contained, 1);
	let calls = harness.script.requests().len();
	let mut tampered = stored;
	tampered[1]["updateComponents"]["components"][0]["quellen"][0]["titel"] =
		json!("FALSCHE GESPEICHERTE DATEN");
	tampered[1]["updateComponents"]["components"][0]["url"] = json!("https://invalid.test/private");
	sqlx::query(r#"UPDATE chat_messages SET "A2uiJson"=$2 WHERE "Id"=$1"#)
		.bind(id)
		.bind(tampered)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE lieder SET "Title"='Aktueller Titel' WHERE "Id"=$1"#)
		.bind(first)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(second)
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, restored) = member.get(&format!("/api/chat/thread/{thread}")).await;
	let answer = restored["messages"]
		.as_array()
		.unwrap()
		.iter()
		.find(|m| m["role"] == "assistant")
		.unwrap();
	assert_eq!(answer["content"], "Text bleibt.");
	assert_eq!(
		answer["a2ui"][1]["updateComponents"]["components"][0]["quellen"],
		json!([{"liedId":first,"titel":"Aktueller Titel","komponist":"Änderbar"}])
	);
	assert!(!restored.to_string().contains("invalid.test"));
	assert!(!restored.to_string().contains("FALSCHE"));
	assert_eq!(
		harness.script.requests().len(),
		calls,
		"GET/recheck never calls provider"
	);
	// Mixed stored replay, not model invocation: current visible ID survives;
	// nonexistent, unsupported URL/Button, and duplicate-ID surfaces do not.
	let mut mixed = Vec::new();
	for (index,root) in [json!({"id":"root","component":"LiedKarte","liedId":first,"titel":"Gefälscht"}),json!({"id":"root","component":"LiedKarte","liedId":Uuid::new_v4(),"titel":"Geheime Probe"}),json!({"id":"root","component":"Button","label":"Klick","href":"https://boese.example"}),json!({"id":"root","component":"LiedKarte","liedId":first,"titel":"Gefälscht","href":"https://boese.example"})].into_iter().enumerate() {
		let surface=format!("s{}",index+1);
		mixed.push(json!({"version":"v0.9","createSurface":{"surfaceId":surface,"catalogId":"urn:liedertafel:archiv:a2ui:katalog:v1"}}));
		mixed.push(json!({"version":"v0.9","updateComponents":{"surfaceId":surface,"components":[root]}}));
	}
	sqlx::query(r#"UPDATE chat_messages SET "A2uiJson"=$2 WHERE "Id"=$1"#)
		.bind(id)
		.bind(json!(mixed))
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, history) = member.get(&format!("/api/chat/thread/{thread}")).await;
	let answer = history["messages"]
		.as_array()
		.unwrap()
		.iter()
		.find(|m| m["role"] == "assistant")
		.unwrap();
	assert_eq!(answer["content"], "Text bleibt.");
	assert_eq!(answer["a2ui"].as_array().unwrap().len(), 2);
	assert_eq!(
		answer["a2ui"][1]["updateComponents"]["components"][0]["titel"],
		"Aktueller Titel"
	);
	for forbidden in ["boese.example", "Gefälscht", "Geheime Probe"] {
		assert!(!history.to_string().contains(forbidden));
	}
	assert_eq!(harness.script.requests().len(), calls);
	// Unreadable application shape is legitimate jsonb, never a whole-thread error.
	for garbage in [
		json!("not an array"),
		json!({"garbage":true}),
		json!([{"version":"unknown"}]),
		json!([1, 2, 3]),
		json!([null]),
	] {
		sqlx::query(r#"UPDATE chat_messages SET "A2uiJson"=$2 WHERE "Id"=$1"#)
			.bind(id)
			.bind(garbage)
			.execute(&app.pool)
			.await
			.unwrap();
		let (status, history) = member.get(&format!("/api/chat/thread/{thread}")).await;
		assert_eq!(status, 200);
		assert!(
			history["messages"]
				.as_array()
				.unwrap()
				.iter()
				.find(|m| m["role"] == "assistant")
				.unwrap()["a2ui"]
				.is_null()
		);
	}
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}
