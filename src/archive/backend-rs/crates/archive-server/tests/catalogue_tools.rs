//! Frozen CatalogueToolsTests. Execute actual tools through HTTP/provider capture.
mod support;
use serde_json::json;
use support::{App, catalogue::*, chat::invoke};

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn details_include_live_completed_cleaned_score_facts_bounded_deduplicated_text_and_fields() {
	let mut app = App::new_scoped("chat232_details").await;
	let id=lied(&app,"Waldfahrt",json!({"composer":"Silcher","voiceConfiguration":"TTBB","musicalKey":"G-Dur","language":"Deutsch","occasion":"Wandern","lyrics":"Liedtext"})).await;
	for _ in 0..2 {
		let file = datei(&app, id, false, "noten", "application/pdf").await;
		text(
			&app,
			file,
			"Tenor I\nG-Dur\nMusik: Silcher\nDie Sänger wandern im Wald",
			2,
			0,
		)
		.await;
	}
	let other = datei(&app, id, false, "sonstiges", "application/pdf").await;
	text(&app, other, "PRIVATE_FREMDE_NOTIZ", 2, 0).await;
	let value = invoke(&mut app, "lied_details", json!({"liedId":id})).await;
	assert_eq!(value["lieder"][0]["title"], "Waldfahrt");
	assert_eq!(value["besetzung"], "TTBB");
	assert_eq!(value["tonart"], "G-Dur");
	assert_eq!(value["sprache"], "Deutsch");
	assert_eq!(value["anlass"], "Wandern");
	let files = value["dateien"].as_array().unwrap();
	assert_eq!(files.len(), 3);
	let notes: Vec<_> = files.iter().filter(|v| v["art"] == "noten").collect();
	assert_eq!(
		notes.iter().filter(|v| v["notenText"].is_string()).count(),
		1
	);
	assert!(
		notes
			.iter()
			.all(|v| v["notenFakten"]["musicalKey"] == "G-Dur")
	);
	assert!(!value.to_string().contains("PRIVATE_FREMDE_NOTIZ"));
	assert!(!value.to_string().contains("BlobName"));
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn history_separates_planned_from_sung_keeps_honest_dates_and_published_timestamp_state() {
	let mut scenario = support::recordings::Scenario::new().await;
	scenario.add(0, 12.5, 40.0).await;
	scenario.publish().await;
	sqlx::query(r#"UPDATE events SET "Title"='Sängerfest',"DateYear"=1950,"DateMonth"=NULL,"DateDay"=NULL,"DateApproximate"=true WHERE "Id"=$1"#).bind(scenario.event).execute(&scenario.app.pool).await.unwrap();
	let future = event(&scenario.app).await;
	sqlx::query(r#"UPDATE events SET "Title"='Kommendes Fest',"DateYear"=2099 WHERE "Id"=$1"#)
		.bind(future)
		.execute(&scenario.app.pool)
		.await
		.unwrap();
	let id = scenario.lieder[0];
	assert_eq!(
		scenario
			.editor
			.request(
				"PUT",
				&format!("/api/events/{future}/programme/items"),
				json!({"items":[{"liedId":id}]})
			)
			.await
			.0,
		200
	);
	let recorded = event(&scenario.app).await;
	sqlx::query(r#"UPDATE events SET "Title"='Nur Lied-Aufnahme',"DateYear"=1960 WHERE "Id"=$1"#)
		.bind(recorded)
		.execute(&scenario.app.pool)
		.await
		.unwrap();
	let audio = datei(&scenario.app, id, false, "aufnahme", "audio/mpeg").await;
	sqlx::query(r#"UPDATE dateien SET "RecordedAtEventId"=$2,"OriginalFileName"='Konzertmitschnitt.mp3' WHERE "Id"=$1"#).bind(audio).bind(recorded).execute(&scenario.app.pool).await.unwrap();
	let payload = invoke(&mut scenario.app, "lied_details", json!({"liedId":id})).await;
	let past = payload["gesungen"].as_array().unwrap();
	assert_eq!(past.len(), 2);
	assert_eq!(past[0]["auftritt"], "Nur Lied-Aufnahme");
	assert_eq!(past[0]["aufnahmen"], json!(["Konzertmitschnitt.mp3"]));
	assert_eq!(past[1]["auftritt"], "Sängerfest");
	assert_eq!(past[1]["datum"], "um 1950");
	assert_eq!(past[1]["zeitmarken"][0]["start"], 12.5);
	assert_eq!(past[1]["zeitmarken"][0]["status"], "current");
	assert_eq!(payload["geplant"][0]["auftritt"], "Kommendes Fest");
	// Publication is rechecked independently of the actual programme appearance.
	sqlx::query(r#"UPDATE recordings SET "PublishedAt"=NULL WHERE "Id"=$1"#)
		.bind(scenario.recording)
		.execute(&scenario.app.pool)
		.await
		.unwrap();
	let payload = invoke(&mut scenario.app, "lied_details", json!({"liedId":id})).await;
	assert_eq!(payload["gesungen"][1]["zeitmarken"], json!([]));
	scenario.app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn actual_search_adapter_pages_counts_clamps_limits_narrows_and_handles_past_end() {
	let mut app = App::new_scoped("chat232_tool_pages").await;
	for i in 0..54 {
		lied(&app, &format!("Lied {i:02}"), json!({})).await;
	}
	for (page, expected, count, more, next, limit) in [
		(0, 1, 10, true, Some(2), false),
		(1, 1, 10, true, Some(2), false),
		(2, 2, 10, true, Some(3), false),
		(5, 5, 10, true, None, true),
	] {
		let value = invoke(
			&mut app,
			"catalogue_search",
			json!({"query":"","page":page}),
		)
		.await;
		assert_eq!(value["totalCount"], 54);
		assert_eq!(value["page"], expected);
		assert_eq!(value["lieder"].as_array().unwrap().len(), count);
		assert_eq!(value["hasMore"], more);
		assert_eq!(value["nextPage"], json!(next));
		assert_eq!(value["limitReached"], limit);
		assert_eq!(
			value["lieder"][0]["title"],
			format!("Lied {:02}", (expected - 1) * 10)
		);
	}
	let maximum = invoke(
		&mut app,
		"catalogue_search",
		json!({"query":"","page":i32::MAX}),
	)
	.await;
	assert_eq!(
		maximum,
		json!({"lieder":[],"error":"Höchstens 5 Seiten abrufbar. Bitte die Suche eingrenzen.","limitReached":true})
	);
	let narrow = invoke(
		&mut app,
		"catalogue_search",
		json!({"query":"Lied 53","page":1}),
	)
	.await;
	assert_eq!(narrow["totalCount"], 1);
	assert_eq!(narrow["hasMore"], false);
	assert_eq!(narrow["nextPage"], json!(null));
	let end = invoke(
		&mut app,
		"catalogue_search",
		json!({"query":"Lied 53","page":2}),
	)
	.await;
	assert_eq!(end["totalCount"], 1);
	assert_eq!(end["lieder"], json!([]));
	assert_eq!(end["limitReached"], false);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn actual_adapter_missing_details_and_completed_live_noten_only_match() {
	let mut app = App::new_scoped("chat232_tool_notes").await;
	let empty = invoke(&mut app, "catalogue_search", json!({"query":""})).await;
	assert_eq!(empty["totalCount"], 0);
	assert_eq!(empty["lieder"], json!([]));
	for id in ["", "not-a-guid", "00000000-0000-0000-0000-000000000000"] {
		assert_eq!(
			invoke(&mut app, "lied_details", json!({"liedId":id})).await,
			json!({})
		);
	}
	let id = lied(&app, "Notenfund", json!({})).await;
	let score = datei(&app, id, false, "noten", "application/pdf").await;
	text(&app, score, "Seltener Suchbegriff", 2, 0).await;
	let incomplete = lied(&app, "Noch nicht fertig", json!({})).await;
	let score = datei(&app, incomplete, false, "noten", "application/pdf").await;
	text(&app, score, "Seltener Suchbegriff", 1, 0).await;
	let value = invoke(
		&mut app,
		"catalogue_search",
		json!({"query":"Seltener Suchbegriff"}),
	)
	.await;
	assert_eq!(value["totalCount"], 1);
	assert_eq!(value["lieder"][0]["id"], id.to_string());
	assert_eq!(value["lieder"][0]["trefferIn"], json!(["in den Noten"]));
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		invoke(&mut app, "lied_details", json!({"liedId":id})).await,
		json!({})
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn tool_results_keep_frozen_default_json_escaping_and_meaningful_nulls() {
	use archive_server::ai::{Content, Script};
	use support::chat::*;
	let mut app = App::new_scoped("chat232_tool_json").await;
	lied(&app, "Sänger <Wald> 😀", json!({})).await;
	let harness = install(
		&mut app,
		Script::new(vec![
			vec![tool("catalogue_search", json!({"query":""}))],
			vec![],
		]),
	);
	let mut member = app.member().await;
	run(&mut member, "", "Frage").await;
	let requests = harness.script.requests();
	let Content::FunctionResult { result, .. } = &requests[1].messages.last().unwrap().contents[0]
	else {
		unreachable!()
	};
	assert!(
		result
			.raw()
			.contains("S\\u00E4nger \\u003CWald\\u003E \\uD83D\\uDE00"),
		"actual frozen JsonSerializer tool spelling"
	);
	let value: serde_json::Value = serde_json::from_str(result.raw()).unwrap();
	assert_eq!(value["nextPage"], json!(null));
	assert!(
		value["lieder"][0]
			.as_object()
			.unwrap()
			.contains_key("composer")
	);
	assert!(value["lieder"][0]["composer"].is_null());
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}
