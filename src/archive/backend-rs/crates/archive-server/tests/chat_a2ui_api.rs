//! Frozen ChatApiTests.A2ui through product HTTP, real Gateway and current SQL.
mod support;
use archive_server::ai::{Script, ScriptStep, Update};
use serde_json::json;
use support::{App, catalogue::lied, chat::*};

#[tokio::test]
#[ignore = "dedicated local PostgreSQL; actual frozen HTTP teaser oracle"]
async fn teaser_includes_the_word_ending_at_the_space_at_utf16_index159() {
	let fixture: serde_json::Value = serde_json::from_str(include_str!(
		"../../../tests/fixtures/232-chat-clipping.json"
	))
	.unwrap();
	let mut app = App::new_scoped("chat232_teaser_boundary").await;
	let lyrics = format!("{} {} {}", "a".repeat(100), "b".repeat(58), "c".repeat(10));
	let id = lied(
		&app,
		"Boundary Lied",
		json!({"lyrics":lyrics,"composer":"Komponist"}),
	)
	.await;
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool(
				"zeige_oberflaeche",
				json!({"komponenten":[{"komponente":"LiedKarte","liedId":id}]}),
			)],
			vec![],
		]),
	);
	let mut member = app.member().await;
	let events = run(&mut member, "", "Boundary question").await;
	let root = &events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"][1]["updateComponents"]
		["components"][0];
	assert_eq!(
		root["textanfang"],
		fixture["cases"][0]["card"]["textanfang"]
	);
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}

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
async fn malformed_arguments_foreign_fields_removed_components_and_unresolvable_ids_finish_without_cards()
 {
	let mut app = App::new_scoped("chat232_a2ui_reject").await;
	let id = lied(&app, "Sichtbares Lied", json!({})).await;
	let cases = vec![
		json!({"komponenten":"kaputt"}),
		json!({"komponenten":{"komponente":"LiedKarte"}}),
		json!({"komponenten":[{"komponente":"LiedKarte","liedId":42}]}),
		json!({"komponenten":[{"komponente":"Quellen","liedIds":"nicht-als-liste"}]}),
		json!({"komponenten":["LiedKarte"]}),
		json!({"komponenten":[{"komponente":"LiedKarte","liedId":id,"auftrittId":uuid::Uuid::new_v4()}]}),
		json!({"komponenten":[{"komponente":"LiedKarte","liedId":id,"liedIds":[id]}]}),
		json!({"komponenten":[{"komponente":"VorschlagKarte","vorschlagId":uuid::Uuid::new_v4()}]}),
		json!({"komponenten":[{"komponente":"AenderungsVorschau","vorschlagId":uuid::Uuid::new_v4()}]}),
		json!({"komponenten":[{"komponente":"LiedKarte","liedId":uuid::Uuid::new_v4()}]}),
		json!({"komponenten":[{"komponente":"LiedKarte","liedId":"https://boese.example/lied"}]}),
		json!({"komponenten":[{"komponente":"LiedKarte"}]}),
		json!({"komponenten":[{"komponente":"basic:Text","text":"FALSCHE MODELLDATEN"}]}),
	];
	let mut member = app.member().await;
	for (i, args) in cases.into_iter().enumerate() {
		let harness = install(
			&mut app,
			Script::new(vec![
				vec![tool("zeige_oberflaeche", args)],
				vec![ScriptStep::Update(Update::text("Ich habe es angezeigt."))],
			]),
		);
		member.router = app.router.clone();
		let events = run(&mut member, "", "Zeige etwas").await;
		assert_eq!(events.last().unwrap()["type"], "RUN_FINISHED", "row {i}");
		assert!(!events.iter().any(|e| e["name"] == "archive.a2ui"));
		assert_eq!(text(&events), "Ich habe es angezeigt.");
		let requests = harness.script.requests();
		let archive_server::ai::Content::FunctionResult { result, .. } =
			&requests[1].messages.last().unwrap().contents[0]
		else {
			unreachable!()
		};
		let value: serde_json::Value = serde_json::from_str(result.raw()).unwrap();
		if i >= 5 {
			assert_eq!(value["verworfen"], 1);
			assert_eq!(value["angezeigt"], json!([]));
			assert!(
				value["hinweis"]
					.as_str()
					.unwrap()
					.contains("Antworte nur mit Text")
			);
		}
		let description = &requests[0]
			.options
			.tools
			.iter()
			.find(|t| t.name == "zeige_oberflaeche")
			.unwrap()
			.description;
		for name in ["LiedKarte", "AuftrittKarte", "Quellen", "ProgrammListe"] {
			assert!(description.contains(name));
		}
		assert!(!description.to_lowercase().contains("vorschlag"));
		assert!(!description.contains("AenderungsVorschau"));
		assert!(harness.origin.drain_readings().await);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn card_only_answer_disappears_after_deletion_and_empty_programmes_never_create_a_surface() {
	let mut app = App::new_scoped("chat232_a2ui_only").await;
	let id = lied(&app, "Nur Karte", json!({})).await;
	let event = support::catalogue::event(&app).await;
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool(
				"zeige_oberflaeche",
				json!({"komponenten":[{"komponente":"LiedKarte","liedId":id},{"komponente":"ProgrammListe","auftrittId":event}]}),
			)],
			vec![],
		]),
	);
	let mut member = app.member().await;
	let thread = uuid::Uuid::new_v4().to_string();
	let events = run(&mut member, &thread, "Nur Karte").await;
	assert_eq!(events.last().unwrap()["type"], "RUN_FINISHED");
	assert_eq!(text(&events), "");
	assert_eq!(
		events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"]
			.as_array()
			.unwrap()
			.len(),
		2
	);
	let (_, before) = member.get(&format!("/api/chat/thread/{thread}")).await;
	assert_eq!(before["messages"].as_array().unwrap().len(), 2);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, after) = member.get(&format!("/api/chat/thread/{thread}")).await;
	assert_eq!(after["messages"].as_array().unwrap().len(), 1);
	assert_eq!(after["messages"][0]["role"], "user");
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn twelve_request_inspection_and_four_surface_bound_apply_across_multiple_calls() {
	let mut app = App::new_scoped("chat232_a2ui_bounds").await;
	let mut ids = Vec::new();
	for i in 0..6 {
		ids.push(lied(&app, &format!("Karte {i}"), json!({})).await);
	}
	let mut requests = vec![json!({"komponente":"Unbekannt"}); 12];
	requests.push(json!({"komponente":"LiedKarte","liedId":ids[0]}));
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool("zeige_oberflaeche", json!({"komponenten":requests}))],
			vec![tool(
				"zeige_oberflaeche",
				json!({"komponenten":ids.iter().map(|id|json!({"komponente":"LiedKarte","liedId":id})).collect::<Vec<_>>()}),
			)],
			vec![],
		]),
	);
	let mut member = app.member().await;
	let events = run(&mut member, "", "Karten").await;
	let messages = &events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"];
	assert_eq!(messages.as_array().unwrap().len(), 8);
	for i in 0..4 {
		assert_eq!(
			messages[i * 2]["createSurface"]["surfaceId"],
			format!("s{}", i + 1)
		);
		assert_eq!(
			messages[i * 2 + 1]["updateComponents"]["components"][0]["liedId"],
			ids[i].to_string()
		);
	}
	let captured = harness.script.requests();
	let archive_server::ai::Content::FunctionResult { result, .. } =
		&captured[1].messages.last().unwrap().contents[0]
	else {
		unreachable!()
	};
	let value: serde_json::Value = serde_json::from_str(result.raw()).unwrap();
	assert_eq!(value["verworfen"], 13);
	assert_eq!(value["angezeigt"], json!([]));
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

#[tokio::test]
#[ignore = "dedicated local PostgreSQL and locked unchanged frontend parser"]
async fn two_visible_programme_entries_keep_order_optional_notes_and_all_four_strict_schemas() {
	let mut app = App::new_scoped("chat232_programme_order").await;
	let first = lied(&app, "Erstes Lied", json!({})).await;
	let second = lied(&app, "Zweites Lied", json!({})).await;
	let event = support::catalogue::event(&app).await;
	let empty = support::catalogue::event(&app).await;
	let mut other = app.admin().await;
	assert_eq!(
		other
			.request(
				"PUT",
				&format!("/api/events/{event}/programme/items"),
				json!({"items":[{"liedId":second,"note":" Erster Punkt "},{"liedId":first}]})
			)
			.await
			.0,
		200
	);
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool(
				"zeige_oberflaeche",
				json!({"komponenten":[{"komponente":"LiedKarte","liedId":first},{"komponente":"AuftrittKarte","auftrittId":event},{"komponente":"Quellen","liedIds":[first,second]},{"komponente":"ProgrammListe","auftrittId":event}]}),
			)],
			vec![ScriptStep::Update(Update::text("Gespeicherte Karten."))],
		]),
	);
	other.router = app.router.clone();
	let mut member = app.member().await;
	let thread = uuid::Uuid::new_v4().to_string();
	let events = run(&mut member, &thread, "Karten").await;
	let cards = &events.iter().find(|e| e["name"] == "archive.a2ui").unwrap()["value"];
	assert_eq!(
		cards[7]["updateComponents"]["components"][0]["punkte"],
		json!([{"position":1,"liedId":second,"titel":"Zweites Lied","notiz":"Erster Punkt"},{"position":2,"liedId":first,"titel":"Erstes Lied"}])
	);
	// Validate the real server output, not a hand-built fixture, with the exact
	// unchanged frontend parser and its locked Zod dependency.
	use std::{
		io::Write,
		process::{Command, Stdio},
	};
	let mut child = Command::new("node")
		.current_dir(std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../.."))
		.arg("tests/browser/chat-a2ui-parse.cjs")
		.stdin(Stdio::piped())
		.spawn()
		.unwrap();
	child
		.stdin
		.take()
		.unwrap()
		.write_all(cards.to_string().as_bytes())
		.unwrap();
	assert!(child.wait().unwrap().success());
	let route = format!("/api/chat/thread/{thread}");
	assert_eq!(other.get(&route).await.0, 404);
	let mut anonymous = app.client();
	assert_eq!(anonymous.get(&route).await.0, 401);
	let (status, headers, _) = member.wire("GET", &route, "").await;
	assert_eq!(status, 200);
	assert_eq!(headers["cache-control"], "no-store");
	let calls = harness.script.requests().len();
	sqlx::query(r#"UPDATE "AspNetUsers" SET "LockoutEnd"=now()+interval '1 day',"LockoutEnabled"=true WHERE "NormalizedEmail"='MITGLIED@LIEDERTAFEL.TEST'"#).execute(&app.pool).await.unwrap();
	assert_eq!(member.get(&route).await.0, 401);
	assert_eq!(harness.script.requests().len(), calls);
	assert!(harness.origin.drain_readings().await);
	// The independent parent guards must reject empty/missing and trashed event,
	// even though its saved programme still contains visible Lieder.
	sqlx::query(r#"UPDATE events SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	let requests = json!({"komponenten":[{"komponente":"ProgrammListe","auftrittId":event},{"komponente":"AuftrittKarte","auftrittId":event},{"komponente":"ProgrammListe","auftrittId":empty},{"komponente":"ProgrammListe","auftrittId":uuid::Uuid::new_v4()}]});
	let harness = install(
		&mut app,
		Script::new(vec![vec![tool("zeige_oberflaeche", requests)], vec![]]),
	);
	other.router = app.router.clone();
	let events = run(&mut other, "", "Nicht sichtbare Programme").await;
	assert_eq!(events.last().unwrap()["type"], "RUN_FINISHED");
	assert!(!events.iter().any(|e| e["name"] == "archive.a2ui"));
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}
