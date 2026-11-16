//! Frozen ChatApiTests.A2ui through product HTTP, real Gateway and current SQL.
mod support;
use archive_server::ai::{Script, ScriptStep, Update};
use serde_json::json;
use support::{App, catalogue::lied, chat::*};

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn model_ids_resolve_server_card_data_store_jsonb_and_rebuild_on_every_load() {
	let mut app = App::new_scoped("chat232_a2ui").await;
	let id = lied(
		&app,
		"Die Waldfahrt",
		json!({"composer":"Friedrich Silcher","lyrics":"Die Sänger wandern\nDurch den Wald"}),
	)
	.await;
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool(
				"zeige_oberflaeche",
				json!({"komponenten":[{"komponente":"LiedKarte","liedId":id,"titel":"FALSCHE MODELLDATEN","url":"https://invalid.test"}]}),
			)],
			vec![ScriptStep::Update(Update::text("Eine Karte."))],
		]),
	);
	let mut member = app.member().await;
	let thread = uuid::Uuid::new_v4().to_string();
	let events = run(&mut member, &thread, "Zeige die Karte").await;
	let wire = serde_json::to_string(&events).unwrap();
	assert!(!wire.contains("zeige_oberflaeche"));
	assert!(!wire.contains("FALSCHE"));
	assert!(!wire.contains("https://"));
	let cards = &events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"];
	assert_eq!(
		cards[0]["createSurface"]["catalogId"],
		"urn:liedertafel:archiv:a2ui:katalog:v1"
	);
	let root = &cards[1]["updateComponents"]["components"][0];
	assert_eq!(root["liedId"], id.to_string());
	assert_eq!(root["titel"], "Die Waldfahrt");
	assert_eq!(root["komponist"], "Friedrich Silcher");
	assert_eq!(root["textanfang"], "Die Sänger wandern / Durch den Wald");
	let (_, history) = member.get(&format!("/api/chat/thread/{thread}")).await;
	assert_eq!(
		history["messages"]
			.as_array()
			.unwrap()
			.iter()
			.find(|m| m["role"] == "assistant")
			.unwrap()["a2ui"],
		*cards
	);
	sqlx::query(r#"UPDATE lieder SET "Title"='Neuer Titel' WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, history) = member.get(&format!("/api/chat/thread/{thread}")).await;
	assert_eq!(
		history["messages"]
			.as_array()
			.unwrap()
			.iter()
			.find(|m| m["role"] == "assistant")
			.unwrap()["a2ui"][1]["updateComponents"]["components"][0]["titel"],
		"Neuer Titel"
	);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, history) = member.get(&format!("/api/chat/thread/{thread}")).await;
	let assistant = history["messages"]
		.as_array()
		.unwrap()
		.iter()
		.find(|m| m["role"] == "assistant")
		.unwrap();
	assert_eq!(assistant["content"], "Eine Karte.");
	assert!(assistant["a2ui"].is_null());
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn four_catalog_components_bound_deduplicate_and_hide_trashed_programme_entries() {
	let mut app = App::new_scoped("chat232_catalog").await;
	let first = lied(&app, "Erstes Lied", json!({})).await;
	let second = lied(&app, "Zweites Lied", json!({})).await;
	let event = support::catalogue::event(&app).await;
	let mut admin = app.admin().await;
	assert_eq!(
		admin
			.request(
				"PUT",
				&format!("/api/events/{event}/programme/items"),
				json!({"items":[{"liedId":first,"note":" Erster Punkt "},{"liedId":second}]})
			)
			.await
			.0,
		200
	);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(second)
		.execute(&app.pool)
		.await
		.unwrap();
	let requests = json!({"komponenten":[
		{"komponente":"LiedKarte","liedId":first,"auftrittId":" ","liedIds":[]},
		{"komponente":"LiedKarte","liedId":first},
		{"komponente":"Vorschlag","liedId":first},
		{"komponente":"LiedKarte","liedId":first,"auftrittId":event},
		{"komponente":"AuftrittKarte","auftrittId":event},
		{"komponente":"Quellen","liedIds":[second,first,first]},
		{"komponente":"ProgrammListe","auftrittId":event},
		{"komponente":"LiedKarte","liedId":second},
		{"komponente":"LiedKarte","liedId":first}
	]});
	let harness = install(
		&mut app,
		Script::new(vec![vec![tool("zeige_oberflaeche", requests)], vec![]]),
	);
	let mut member = app.member().await;
	let thread = uuid::Uuid::new_v4().to_string();
	let events = run(&mut member, &thread, "Karten").await;
	let cards = &events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"];
	assert_eq!(cards.as_array().unwrap().len(), 8);
	assert_eq!(
		cards[3]["updateComponents"]["components"][0]["component"],
		"AuftrittKarte"
	);
	assert_eq!(
		cards[5]["updateComponents"]["components"][0]["quellen"],
		json!([{"liedId":first,"titel":"Erstes Lied"}])
	);
	assert_eq!(
		cards[7]["updateComponents"]["components"][0]["punkte"],
		json!([{"position":1,"liedId":first,"titel":"Erstes Lied","notiz":"Erster Punkt"}])
	);
	let captured = harness.script.requests();
	let archive_server::ai::Content::FunctionResult { result, .. } =
		&captured[1].messages.last().unwrap().contents[0]
	else {
		unreachable!()
	};
	let result: serde_json::Value = serde_json::from_str(result.raw()).unwrap();
	assert_eq!(result["angezeigt"].as_array().unwrap().len(), 4);
	assert_eq!(result["verworfen"], 5);
	let (_, history) = member.get(&format!("/api/chat/thread/{thread}")).await;
	assert_eq!(
		history["messages"]
			.as_array()
			.unwrap()
			.iter()
			.find(|m| m["role"] == "assistant")
			.unwrap()["a2ui"],
		*cards
	);
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}
