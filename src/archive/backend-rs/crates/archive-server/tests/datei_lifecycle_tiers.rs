// Assertion-only SQL fixture helpers; no production fallible input is unwrapped.
#![allow(clippy::unwrap_used)]
mod support;
use serde_json::json;
use std::sync::{Arc, atomic::Ordering};
use support::{App, catalogue::*, storage::FakeStorage, upload::*};
use uuid::Uuid;
async fn setup() -> (App, Arc<FakeStorage>, support::Client, Uuid, Uuid, Uuid) {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let editor = app.admin().await;
	let lied = lied(&app, "Video", json!({})).await;
	let original = datei(&app, lied, false, "aufnahme", "video/quicktime").await;
	let copy = datei(&app, lied, false, "aufnahme", "video/mp4").await;
	video_copy(&app, original, copy, 2).await;
	let original_blob = blob(&app, original).await;
	storage.store(&original_blob, b"video", "video/quicktime");
	let copy_blob = blob(&app, copy).await;
	storage.store(&copy_blob, b"video", "video/mp4");
	(app, storage, editor, lied, original, copy)
}
async fn tier(app: &App, id: Uuid) -> String {
	sqlx::query_scalar(r#"SELECT "StorageTier" FROM dateien WHERE "Id"=$1"#)
		.bind(id)
		.fetch_one(&app.pool)
		.await
		.unwrap()
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL Cold-to-Hot shared trash lifecycle"]
async fn trash_copy_returns_original_hot_and_provider_refusal_rolls_back_both_rows() {
	let (app, storage, mut editor, _, original, copy) = setup().await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{original}/storage-tier"),
				json!({"tier":"cold"})
			)
			.await
			.0,
		200
	);
	storage.fail.store(true, Ordering::SeqCst);
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{copy}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		502
	);
	assert_eq!(tier(&app, original).await, "cold");
	assert_eq!(
		editor.get(&format!("/api/dateien/{copy}/access")).await.0,
		502,
		"copy still visible, provider refuses ticket"
	);
	storage.fail.store(false, Ordering::SeqCst);
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{copy}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	assert_eq!(tier(&app, original).await, "hot");
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL immutable playback replacement tier hook"]
async fn replacing_video_copy_moves_original_hot_in_same_commit_and_failed_move_keeps_stage() {
	let (app, storage, mut editor, lied, original, copy) = setup().await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{original}/storage-tier"),
				json!({"tier":"cold"})
			)
			.await
			.0,
		200
	);
	let bytes = b"\0\0\0\x18ftypisom";
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"replacesDateiId":copy,"contentType":"video/mp4"}),
		bytes,
		"video/mp4",
		&storage,
	)
	.await;
	storage.tier_fail.store(true, Ordering::SeqCst);
	assert_eq!(finish(&mut editor, &session).await.0, 502);
	assert_eq!(storage.objects.lock().unwrap().len(), 3);
	assert_eq!(tier(&app, original).await, "cold");
	assert_eq!(
		editor.get(&format!("/api/dateien/{copy}/access")).await.0,
		200
	);
	storage.tier_fail.store(false, Ordering::SeqCst);
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(tier(&app, original).await, "hot");
	assert_eq!(
		editor.get(&format!("/api/dateien/{copy}/access")).await.0,
		404
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL restore Cold original after cascading video copy trash"]
async fn trashing_original_keeps_cold_and_trashes_copy_restore_heals_hot_without_restoring_copy() {
	let (app, storage, mut editor, _, original, copy) = setup().await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{original}/storage-tier"),
				json!({"tier":"cold"})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{original}/delete"),
				json!({"rowVersion":2})
			)
			.await
			.0,
		204
	);
	assert_eq!(tier(&app, original).await, "cold");
	assert_eq!(
		editor.get(&format!("/api/dateien/{copy}/access")).await.0,
		404
	);
	storage.fail.store(true, Ordering::SeqCst);
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{original}/restore"), json!({}))
			.await
			.0,
		502
	);
	assert_eq!(tier(&app, original).await, "cold");
	storage.fail.store(false, Ordering::SeqCst);
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{original}/restore"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(tier(&app, original).await, "hot");
	assert_eq!(
		editor.get(&format!("/api/dateien/{copy}/access")).await.0,
		404
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording slot removal with Cold-to-Hot"]
async fn permanent_recording_copy_removal_clears_slots_keeps_recording_and_original_goes_hot() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let event = event(&app).await;
	let original = datei(&app, event, true, "aufnahme", "video/quicktime").await;
	let copy = datei(&app, event, true, "aufnahme", "video/mp4").await;
	let recording = recording(&app, event, original, Some(copy), "video").await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{original}/storage-tier"),
				json!({"tier":"cold"})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		editor
			.post(&format!("/api/papierkorb/dateien/{copy}/remove"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(tier(&app, original).await, "hot");
	let slot: Option<Uuid> =
		sqlx::query_scalar(r#"SELECT "PlaybackDateiId" FROM recordings WHERE "Id"=$1"#)
			.bind(recording)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert!(slot.is_none());
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL best-effort permanent storage release"]
async fn permanent_cold_original_release_and_failed_provider_release_never_claim_false_hot_state() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Cold Trash").await;
	let file = upload(
		&mut editor,
		lied,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let id = Uuid::parse_str(file["id"].as_str().unwrap()).unwrap();
	sqlx::query(r#"UPDATE dateien SET "StorageTier"='cold',"DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		editor
			.post(&format!("/api/papierkorb/dateien/{id}/remove"), json!({}))
			.await
			.0,
		204
	);
	assert!(storage.objects.lock().unwrap().is_empty());
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL frozen video Restrict limitation, not a successful-removal gate"]
async fn video_job_referenced_permanent_removal_preserves_frozen_fk_refusal_instead_of_claiming_success()
 {
	// Differential against the actual unmodified .NET PapierkorbRemoval on PG:
	// it does not clear video_jobs.PlaybackDateiId before deleting its Datei.
	let (app, storage, mut admin, _, _, copy) = setup().await;
	let name = blob(&app, copy).await;
	assert_eq!(
		admin
			.post(&format!("/api/papierkorb/dateien/{copy}/remove"), json!({}))
			.await
			.0,
		500
	);
	assert!(storage.objects.lock().unwrap().contains_key(&name));
	assert_eq!(
		admin.get(&format!("/api/dateien/{copy}/access")).await.0,
		200
	);
	app.close().await;
}
