mod support;
use serde_json::json;
use std::sync::Arc;
use support::{App, storage::FakeStorage, upload::*};

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL kind gates"]
async fn audio_midi_camera_and_whitelisted_documents_finalize_with_unchanged_member_tickets() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Dateiarten").await;
	for (kind, content, bytes) in [
		("uebe", "audio/mpeg", b"ID3synthetic".as_slice()),
		("uebe", "audio/midi", b"MThdsynthetic"),
		("aufnahme", "video/mp4", b"\0\0\0\x18ftypisom"),
		("aufnahme", "video/quicktime", b"\0\0\0\x14ftypqt  "),
		("sonstiges", "text/plain", b"Wohlauf, Gesellen\n"),
		(
			"sonstiges",
			"application/vnd.openxmlformats-officedocument.wordprocessingml.document",
			b"PK\x03\x04synthetic",
		),
		("sonstiges", "image/png", b"\x89PNG\r\n\x1a\nsynthetic"),
		("sonstiges", "image/jpeg", b"\xff\xd8\xffsynthetic"),
		("sonstiges", "image/webp", b"RIFFxxxxWEBPsynthetic"),
	] {
		let file = upload(
			&mut editor,
			lied,
			json!({"kind":kind,"contentType":content,"stimme":"  Solo Bariton  "}),
			bytes,
			content,
			&storage,
		)
		.await;
		assert_eq!(file["contentType"], content);
		assert_eq!(
			file["stimme"],
			if kind == "uebe" {
				json!("Solo Bariton")
			} else {
				serde_json::Value::Null
			}
		);
		let (s, access) = member
			.get(&format!(
				"/api/dateien/{}/access",
				file["id"].as_str().unwrap()
			))
			.await;
		assert_eq!(s, 200);
		assert_eq!(access["contentType"], content);
		assert_ne!(access["viewUrl"], access["downloadUrl"]);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL magic-byte and explicit content type rejection"]
async fn fake_media_families_and_documents_are_terminally_rejected_with_kind_specific_messages() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Falsche Dateien").await;
	for (kind, content, bytes, title) in [
		(
			"uebe",
			"audio/midi",
			b"ID3".as_slice(),
			"Die Datei ist keine gültige Audio- oder MIDI-Datei.",
		),
		(
			"uebe",
			"audio/mpeg",
			b"Kein Audio",
			"Die Datei ist keine gültige Audio- oder MIDI-Datei.",
		),
		(
			"aufnahme",
			"video/mp4",
			b"fLaC",
			"Die Datei ist keine gültige Audio- oder Videoaufnahme.",
		),
		(
			"sonstiges",
			"application/pdf",
			b"<script>x</script>",
			"Die Datei passt nicht zu ihrem Dateityp.",
		),
		(
			"sonstiges",
			"image/png",
			b"not PNG",
			"Die Datei passt nicht zu ihrem Dateityp.",
		),
		(
			"sonstiges",
			"text/plain",
			b"nul\0text",
			"Die Datei passt nicht zu ihrem Dateityp.",
		),
	] {
		let session = stage(
			&mut editor,
			lied,
			false,
			json!({"kind":kind,"stimme":"Alle","contentType":content}),
			bytes,
			content,
			&storage,
		)
		.await;
		let (s, b) = finish(&mut editor, &session).await;
		assert_eq!(s, 422, "{b}");
		assert_eq!(b["title"], title);
		assert_eq!(finish(&mut editor, &session).await.0, 409);
	}
	assert!(storage.objects.lock().unwrap().is_empty());
	assert_eq!(
		editor
			.post(
				&format!("/api/lieder/{lied}/dateien/upload-session"),
				json!({"kind":"sonstiges","contentType":"text/html"})
			)
			.await
			.0,
		422
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL Aufnahme recorded-at attribution"]
async fn aufnahme_names_visible_seeded_event_and_replacement_preserves_original_event_identity() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Aufnahme").await;
	let event = support::catalogue::event(&app).await;
	let old = upload(
		&mut editor,
		lied,
		json!({"kind":"aufnahme","contentType":"audio/mpeg","recordedAtEventId":event}),
		b"ID3",
		"audio/mpeg",
		&storage,
	)
	.await;
	let replaced=upload(&mut editor,lied,json!({"replacesDateiId":old["id"],"contentType":"audio/mpeg","recordedAtEventId":uuid::Uuid::new_v4()}),b"ID3","audio/mpeg",&storage).await;
	let (_, detail) = editor.get(&format!("/api/lieder/{lied}")).await;
	assert_eq!(detail["lied"]["dateien"][0]["id"], replaced["id"]);
	assert_eq!(
		detail["lied"]["dateien"][0]["recordedAtEvent"]["id"],
		event.to_string()
	);
	assert!(replaced["recordedAtEvent"].is_null());
	// The old link is inherited on replacement, even after the event is trashed.
	assert_eq!(
		editor
			.post(
				&format!("/api/events/{event}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"replacesDateiId":replaced["id"],"contentType":"audio/mpeg"}),
		b"ID3",
		"audio/mpeg",
		&storage,
	)
	.await;
	assert_eq!(finish(&mut editor, &session).await.0, 200);
	app.close().await;
}
