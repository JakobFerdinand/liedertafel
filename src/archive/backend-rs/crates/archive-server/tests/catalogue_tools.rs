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
