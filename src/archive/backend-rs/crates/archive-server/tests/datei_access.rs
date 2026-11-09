mod support;
use serde_json::json;
use std::sync::{Arc, atomic::Ordering};
use support::{App, catalogue::*, storage::FakeStorage};

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn member_gets_sas_json_not_proxy_and_unknown_trash_parents_never_mint() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut member = app.member().await;
	let id = lied(&app, "Notenlied", json!({})).await;
	let file = datei(&app, id, false, "noten", "application/pdf").await;
	let path = format!("/api/dateien/{file}/access");
	assert_eq!(app.client().get(&path).await.0, 401);
	assert!(storage.calls.lock().unwrap().is_empty());
	let (status, body) = member.get(&path).await;
	assert_eq!(status, 200);
	assert_eq!(body["dateiId"], file.to_string());
	assert_eq!(body["kind"], "noten");
	assert_eq!(body["contentType"], "application/pdf");
	assert_eq!(body["sizeBytes"], 128);
	assert_eq!(body["fileName"], "datei.pdf");
	assert!(body["playbackDateiId"].is_null());
	assert!(body["expiresAt"].is_string());
	assert!(
		body["viewUrl"]
			.as_str()
			.unwrap()
			.starts_with("https://private.storage.test/")
	);
	assert!(body["downloadUrl"].as_str().unwrap().contains("attachment"));
	assert_eq!(body.as_object().unwrap().len(), 10);
	storage.calls.lock().unwrap().clear();
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(member.get(&path).await.0, 404);
	assert!(storage.calls.lock().unwrap().is_empty());
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=NULL WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(file)
		.execute(&app.pool)
		.await
		.unwrap();
	let (status, body) = member.get(&path).await;
	assert_eq!(status, 404);
	assert_eq!(body["title"], "Die Datei wurde nicht gefunden.");
	assert!(storage.calls.lock().unwrap().is_empty());
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn auftritt_recording_member_access_is_denied_even_when_published_editor_original_allowed() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut member = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let event = event(&app).await;
	let original = datei(&app, event, true, "aufnahme", "video/mp4").await;
	recording(&app, event, original, None, "video").await;
	let path = format!("/api/dateien/{original}/access");
	assert_eq!(member.get(&path).await.0, 404);
	assert!(storage.calls.lock().unwrap().is_empty());
	assert_eq!(editor.get(&path).await.0, 200);
	let doc = datei(&app, event, true, "sonstiges", "application/pdf").await;
	assert_eq!(
		member.get(&format!("/api/dateien/{doc}/access")).await.0,
		200
	);
	storage.calls.lock().unwrap().clear();
	sqlx::query(r#"UPDATE events SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.get(&path).await.0, 404);
	assert_eq!(
		member.get(&format!("/api/dateien/{doc}/access")).await.0,
		404
	);
	assert!(storage.calls.lock().unwrap().is_empty());
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn storage_read_failure_is_exact_german_502() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut member = app.member().await;
	let id = lied(&app, "Ausfall", json!({})).await;
	let file = datei(&app, id, false, "noten", "application/pdf").await;
	storage.fail.store(true, Ordering::SeqCst);
	let (status, body) = member.get(&format!("/api/dateien/{file}/access")).await;
	assert_eq!(status, 502);
	assert_eq!(body["title"], "Speicherdienst nicht erreichbar.");
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL read policy"]
async fn unavailable_playback_copy_falls_back_to_original_and_membership_is_reread() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut member = app.member().await;
	let id = lied(&app, "Video", json!({})).await;
	let original = datei(&app, id, false, "aufnahme", "video/quicktime").await;
	let copy = datei(&app, id, false, "aufnahme", "video/mp4").await;
	video_copy(&app, original, copy, 1).await;
	let path = format!("/api/dateien/{original}/access");
	let (_, body) = member.get(&path).await;
	assert!(body["playbackDateiId"].is_null());
	assert_eq!(body["contentType"], "video/quicktime");
	sqlx::query(r#"UPDATE video_jobs SET "Status"=2 WHERE "DateiId"=$1"#)
		.bind(original)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(copy)
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, body) = member.get(&path).await;
	assert!(body["playbackDateiId"].is_null());
	assert_eq!(body["contentType"], "video/quicktime");
	storage.calls.lock().unwrap().clear();
	sqlx::query(r#"UPDATE "AspNetUsers" SET "LockoutEnabled"=true,"LockoutEnd"=now()+interval '1 day' WHERE "NormalizedEmail"='MITGLIED@LIEDERTAFEL.TEST'"#).execute(&app.pool).await.unwrap();
	assert_eq!(member.get(&path).await.0, 401);
	assert!(storage.calls.lock().unwrap().is_empty());
	app.close().await;
}
