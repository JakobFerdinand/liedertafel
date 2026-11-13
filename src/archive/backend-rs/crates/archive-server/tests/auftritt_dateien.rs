mod support;
use serde_json::json;
use std::sync::Arc;
use support::{App, storage::FakeStorage, upload::*};
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL shared event-owned upload protocol"]
async fn seeded_event_owns_documents_photos_and_originals_without_event_or_recording_write_endpoints()
 {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let event = support::catalogue::event(&app).await;
	for (kind, content, bytes, expected) in [
		(
			"sonstiges",
			"image/png",
			b"\x89PNG\r\n\x1a\nsynthetic".as_slice(),
			"image/png",
		),
		(
			"aufnahme",
			"video/x-custom",
			b"\0\0\0\x14ftypqt  ",
			"video/quicktime",
		),
	] {
		let session = stage(
			&mut editor,
			event,
			true,
			json!({"kind":kind,"contentType":content}),
			bytes,
			content,
			&storage,
		)
		.await;
		let (s, b) = finish(&mut editor, &session).await;
		assert_eq!(s, 200, "{b}");
		assert_eq!(b["datei"]["contentType"], expected);
		let access = member
			.get(&format!(
				"/api/dateien/{}/access",
				b["datei"]["id"].as_str().unwrap()
			))
			.await;
		assert_eq!(access.0, if kind == "aufnahme" { 404 } else { 200 });
	}
	for kind in ["noten", "uebe"] {
		assert_eq!(
			editor
				.post(
					&format!("/api/events/{event}/dateien/upload-session"),
					json!({"kind":kind})
				)
				.await
				.0,
			422
		);
	}
	app.close().await;
}

// Full #225 translations of frozen AuftrittDateienTests facts 1–3. Recording
// attachment/publication/validated-playback facts 4–6 remain #227.
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL full event/document orchestration"]
async fn editor_creates_auftritt_uploads_document_photo_replaces_and_any_editor_edits_metadata() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let mut second = app.sign_in("redaktion@liedertafel.test").await;
	let (s, b) = editor
		.post(
			"/api/events",
			json!({"kind":"concert","title":"Frühjahrskonzert"}),
		)
		.await;
	assert_eq!(s, 201, "{b}");
	let event = uuid::Uuid::parse_str(b["event"]["id"].as_str().unwrap()).unwrap();
	let mut files = Vec::new();
	for (content, bytes, name, art) in [
		(
			"application/pdf",
			b"%PDF-1.4\nsynthetic".as_slice(),
			"programm.pdf",
			"document",
		),
		(
			"image/png",
			b"\x89PNG\r\n\x1a\nsynthetic".as_slice(),
			"chor.png",
			"photo",
		),
	] {
		let session = stage(
			&mut editor,
			event,
			true,
			json!({"kind":"sonstiges","contentType":content,"fileName":name,"description":" Vorne "}),
			bytes,
			content,
			&storage,
		)
		.await;
		let (s, b) = finish(&mut editor, &session).await;
		assert_eq!(s, 200, "{b}");
		let id = b["datei"]["id"].as_str().unwrap().to_owned();
		files.push(id.clone());
		assert_eq!(
			member.get(&format!("/api/dateien/{id}/access")).await.0,
			200
		);
		let detail = member.get(&format!("/api/events/{event}")).await.1;
		let doc = detail["event"]["documents"]
			.as_array()
			.unwrap()
			.iter()
			.find(|d| d["id"] == id)
			.unwrap();
		assert_eq!(doc["art"], art);
		assert_eq!(doc["fileName"], name);
		assert_eq!(doc["description"], "Vorne");
	}
	let old = &files[0];
	let path = format!("/api/dateien/{old}");
	let (s, b) = second
		.request(
			"PATCH",
			&path,
			json!({"description":"  Rückseite  ","rowVersion":1}),
		)
		.await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["datei"]["description"], "Rückseite");
	assert_eq!(b["datei"]["rowVersion"], 2);
	for key in [
		"kind",
		"isPartitur",
		"stimme",
		"recordedAtEvent",
		"storageTier",
		"tierChangedAt",
	] {
		assert!(b["datei"].get(key).is_some(), "{key}");
	}
	assert!(b["datei"].get("art").is_none());
	for body in [
		json!({}),
		json!({"description":null}),
		json!({"description":" Rückseite ","rowVersion":"2"}),
	] {
		let (s, b) = second.request("PATCH", &path, body).await;
		assert_eq!(s, 200, "{b}");
		assert_eq!(b["datei"]["rowVersion"], 2);
	}
	assert_eq!(
		second
			.request("PATCH", &path, json!({"rowVersion":1}))
			.await
			.0,
		409
	);
	let (s, b) = second
		.request(
			"PATCH",
			&path,
			json!({"description":"x".repeat(501),"rowVersion":1}),
		)
		.await;
	assert_eq!(s, 400);
	assert_eq!(b["title"], "Die Beschreibung ist zu lang.");
	let (s, b) = second
		.request("PATCH", &path, json!({"description":" "}))
		.await;
	assert_eq!(s, 200);
	assert!(b["datei"]["description"].is_null());
	assert_eq!(b["datei"]["rowVersion"], 3);
	assert_eq!(
		member
			.request("PATCH", &path, json!({"description":"bad"}))
			.await
			.0,
		403
	);
	let session=stage(&mut second,event,true,json!({"kind":"sonstiges","replacesDateiId":old,"contentType":"application/pdf","fileName":"ersatz.pdf"}),b"%PDF-1.4\nnew","application/pdf",&storage).await;
	let (s, b) = finish(&mut second, &session).await;
	assert_eq!(s, 200, "{b}");
	let new = b["datei"]["id"].as_str().unwrap();
	assert_ne!(new, old);
	assert_eq!(
		member.get(&format!("/api/dateien/{old}/access")).await.0,
		404
	);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"description":"gone"}))
			.await
			.0,
		404
	);
	let detail = member.get(&format!("/api/events/{event}")).await.1;
	assert_eq!(detail["event"]["documents"].as_array().unwrap().len(), 2);
	assert!(
		detail["event"]["documents"]
			.as_array()
			.unwrap()
			.iter()
			.all(|d| d["id"] != *old)
	);
	for kind in ["noten", "uebe"] {
		assert_eq!(
			editor
				.post(
					&format!("/api/events/{event}/dateien/upload-session"),
					json!({"kind":kind})
				)
				.await
				.0,
			422
		);
	}
	let other = support::catalogue::event(&app).await;
	assert_eq!(
		editor
			.post(
				&format!("/api/events/{other}/dateien/upload-session"),
				json!({"kind":"sonstiges","replacesDateiId":new})
			)
			.await
			.0,
		404
	);
	let lied = support::catalogue::lied(&app, "Lied", json!({})).await;
	let lied_file =
		support::catalogue::datei(&app, lied, false, "sonstiges", "application/pdf").await;
	let recording = support::catalogue::datei(&app, event, true, "aufnahme", "audio/mp4").await;
	for id in [lied_file, recording] {
		assert_eq!(
			editor
				.request(
					"PATCH",
					&format!("/api/dateien/{id}"),
					json!({"description":"no"})
				)
				.await
				.0,
			404
		);
	}
	// A Lied-owned Aufnahme naming this event is not an event document.
	sqlx::query(r#"UPDATE dateien SET "RecordedAtEventId"=$2 WHERE "Id"=$1"#)
		.bind(lied_file)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		member.get(&format!("/api/events/{event}")).await.1["event"]["documents"]
			.as_array()
			.unwrap()
			.len(),
		2
	);
	assert_eq!(
		editor
			.post(
				&format!("/api/events/{event}/delete"),
				json!({"rowVersion":0})
			)
			.await
			.0,
		204
	);
	assert_eq!(
		editor
			.request("PATCH", &format!("/api/dateien/{new}"), json!({}))
			.await
			.0,
		404
	);
	app.close().await;
}
