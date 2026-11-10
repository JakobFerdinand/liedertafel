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
