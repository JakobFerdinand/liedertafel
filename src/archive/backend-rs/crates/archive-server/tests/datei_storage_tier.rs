// Delivery-fake assertions run outside #[test] bodies; failures must fail the gate.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::{Value, json};
use std::sync::{Arc, atomic::Ordering};
use support::{App, catalogue::*, storage::FakeStorage};
use uuid::Uuid;
async fn video(app: &App) -> (Uuid, Uuid, Uuid) {
	let lied = lied(app, "Videolied", json!({})).await;
	let original = datei(app, lied, false, "aufnahme", "video/quicktime").await;
	let copy = datei(app, lied, false, "aufnahme", "video/mp4").await;
	video_copy(app, original, copy, 2).await;
	(lied, original, copy)
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP/provider gate"]
async fn editor_moves_lied_video_original_cold_hot_and_member_plays_copy_downloads_original() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut member = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let (lied, original, copy) = video(&app).await;
	let path = format!("/api/dateien/{original}/storage-tier");
	assert_eq!(
		member
			.post(&path, json!({"tier":"cold","rowVersion":1}))
			.await
			.0,
		403
	);
	assert!(storage.calls.lock().unwrap().is_empty());
	let (status, body) = editor
		.post(&path, json!({"tier":" COLD ","rowVersion":1}))
		.await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(body["datei"]["storageTier"], "cold");
	assert_eq!(body["datei"]["rowVersion"], 2);
	assert!(body["datei"]["tierChangedAt"].is_string());
	assert_eq!(storage.calls.lock().unwrap()[0].0, "cold");
	let (_, body) = member.get(&format!("/api/lieder/{lied}")).await;
	let files = body["lied"]["dateien"].as_array().unwrap();
	assert_eq!(files.len(), 1);
	assert_eq!(files[0]["id"], original.to_string());
	assert_eq!(files[0]["storageTier"], "cold");
	let (_, body) = member.get(&format!("/api/dateien/{original}/access")).await;
	assert_eq!(body["playbackDateiId"], copy.to_string());
	assert_eq!(body["contentType"], "video/mp4");
	assert!(
		body["viewUrl"]
			.as_str()
			.unwrap()
			.contains(&copy.to_string())
	);
	assert!(
		body["downloadUrl"]
			.as_str()
			.unwrap()
			.contains(&original.to_string())
	);
	let (status, body) = editor
		.post(&path, json!({"tier":"hot","rowVersion":2}))
		.await;
	assert_eq!(status, 200);
	assert_eq!(body["datei"]["storageTier"], "hot");
	assert_eq!(body["datei"]["rowVersion"], 3);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP/provider gate"]
async fn recording_policy_requires_every_recording_to_have_live_validated_playback() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let _ = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let event = event(&app).await;
	let original = datei(&app, event, true, "aufnahme", "video/quicktime").await;
	let copy = datei(&app, event, true, "aufnahme", "video/mp4").await;
	let recording = recording(&app, event, original, Some(copy), "video").await;
	let path = format!("/api/dateien/{original}/storage-tier");
	sqlx::query(r#"UPDATE dateien SET "ContentType"='video/quicktime' WHERE "Id"=$1"#)
		.bind(copy)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({"tier":"cold"})).await.0, 422);
	sqlx::query(r#"UPDATE dateien SET "ContentType"='video/mp4',"DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(copy)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({"tier":"cold"})).await.0, 422);
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=NULL WHERE "Id"=$1"#)
		.bind(copy)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE recordings SET "PlaybackDateiId"=NULL WHERE "Id"=$1"#)
		.bind(recording)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({"tier":"cold"})).await.0, 422);
	assert!(storage.calls.lock().unwrap().is_empty());
	sqlx::query(r#"UPDATE recordings SET "PlaybackDateiId"=$2 WHERE "Id"=$1"#)
		.bind(recording)
		.bind(copy)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({"tier":"cold"})).await.0, 200);
	// /api/recordings/{id}/access is #227, not fabricated in this wave.
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP/provider gate"]
async fn no_separate_copy_unknown_tier_missing_trash_stale_and_csrf_are_refused_before_provider() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let _ = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let lied = lied(&app, "Tonspur", json!({})).await;
	let file = datei(&app, lied, false, "aufnahme", "audio/mpeg").await;
	let path = format!("/api/dateien/{file}/storage-tier");
	let (status, body) = editor.post(&path, json!({"tier":"cold"})).await;
	assert_eq!(status, 422);
	assert_eq!(
		body["title"],
		"Das Original kann erst ins Cold Storage verschoben werden, wenn eine abspielbare Abspielkopie für Mitglieder bereitsteht."
	);
	for body in [json!({"tier":"archive"}), json!({}), json!({"tier":null})] {
		assert_eq!(editor.post(&path, body).await.0, 400);
	}
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{}/storage-tier", Uuid::new_v4()),
				json!({"tier":"cold"})
			)
			.await
			.0,
		404
	);
	assert_eq!(
		editor
			.post(&path, json!({"tier":"cold","rowVersion":99}))
			.await
			.0,
		409
	);
	let saved = editor.token.clone();
	editor.token = "forged".into();
	assert_eq!(editor.post(&path, json!({"tier":"hot"})).await.0, 400);
	editor.token = saved;
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(file)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({"tier":"cold"})).await.0, 404);
	assert!(storage.calls.lock().unwrap().is_empty());
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP/provider gate"]
async fn same_tier_is_no_call_no_write_and_provider_failure_keeps_row_honest() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let _ = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let (_, file, _) = video(&app).await;
	let path = format!("/api/dateien/{file}/storage-tier");
	let (status, body) = editor
		.post(&path, json!({"tier":"hot","rowVersion":1}))
		.await;
	assert_eq!(status, 200);
	assert_eq!(body["datei"]["rowVersion"], 1);
	assert_eq!(body["datei"]["tierChangedAt"], Value::Null);
	assert!(storage.calls.lock().unwrap().is_empty());
	storage.fail.store(true, Ordering::SeqCst);
	let (status, body) = editor.post(&path, json!({"tier":"cold"})).await;
	assert_eq!(status, 502);
	assert_eq!(body["title"], "Speicherdienst nicht erreichbar.");
	let (_, body) = editor.get(&format!("/api/dateien/{file}/access")).await;
	assert_eq!(body["status"], 502);
	let stored: (String, i64, Option<chrono::DateTime<chrono::Utc>>) = sqlx::query_as(
		r#"SELECT "StorageTier","RowVersion","TierChangedAt" FROM dateien WHERE "Id"=$1"#,
	)
	.bind(file)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_eq!(stored, ("hot".into(), 1, None));
	app.close().await;
}

struct RacingStorage {
	pool: sqlx::PgPool,
	barrier: tokio::sync::Barrier,
}
impl archive_server::storage::Storage for RacingStorage {
	fn read_ticket<'a>(
		&'a self,
		_: &'a str,
		_: chrono::Duration,
		_: bool,
		_: Option<&'a str>,
	) -> futures::future::BoxFuture<'a, Result<String, archive_server::storage::StorageError>> {
		Box::pin(async { unreachable!("tier-only delivery fake") })
	}
	fn set_tier<'a>(
		&'a self,
		blob: &'a str,
		_: &'a str,
	) -> futures::future::BoxFuture<'a, Result<(), archive_server::storage::StorageError>> {
		Box::pin(async move {
			let row: (String, i64) = sqlx::query_as(
				r#"SELECT "StorageTier","RowVersion" FROM dateien WHERE "BlobName"=$1"#,
			)
			.bind(blob)
			.fetch_one(&self.pool)
			.await
			.unwrap();
			assert_eq!(
				row,
				("hot".into(), 1),
				"provider must be called before any persisted transition"
			);
			tokio::time::timeout(std::time::Duration::from_secs(5), self.barrier.wait())
				.await
				.unwrap();
			Ok(())
		})
	}
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL replica race"]
async fn concurrent_replicas_call_provider_before_cas_and_exactly_one_claims_transition() {
	let mut app = App::new().await;
	let storage = Arc::new(RacingStorage {
		pool: app.pool.clone(),
		barrier: tokio::sync::Barrier::new(2),
	});
	app.with_storage(storage.clone());
	let _ = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let mut admin = app.sign_in("verwaltung@liedertafel.test").await;
	// Independent Origin/Auth instances over the same real PostgreSQL database.
	let auth = archive_server::auth::Auth::new(
		app.pool.clone(),
		archive_server::auth::AuthConfig::development(),
		app.mail.clone(),
		app.time.clone(),
	)
	.unwrap();
	admin.router = archive_server::build_router(Arc::new(
		archive_server::Origin::new(archive_server::Settings::new(
			true,
			std::path::PathBuf::new(),
			0,
		))
		.with_auth(auth)
		.with_storage(storage, chrono::Duration::minutes(15)),
	));
	let (_, file, _) = video(&app).await;
	let path = format!("/api/dateien/{file}/storage-tier");
	let (a, b) = tokio::join!(
		editor.post(&path, json!({"tier":"cold","rowVersion":1})),
		admin.post(&path, json!({"tier":"cold","rowVersion":1}))
	);
	let mut statuses = [a.0.as_u16(), b.0.as_u16()];
	statuses.sort();
	assert_eq!(statuses, [200, 409]);
	let stored: (String, i64) =
		sqlx::query_as(r#"SELECT "StorageTier","RowVersion" FROM dateien WHERE "Id"=$1"#)
			.bind(file)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(stored, ("cold".into(), 2));
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL save failure"]
async fn failed_database_save_after_provider_remains_honestly_retryable() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let _ = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let (_, file, _) = video(&app).await;
	let path = format!("/api/dateien/{file}/storage-tier");
	sqlx::raw_sql("CREATE FUNCTION refuse_tier() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic-save-failure'; END $$; CREATE TRIGGER refuse_tier BEFORE UPDATE ON dateien FOR EACH ROW EXECUTE FUNCTION refuse_tier();").execute(&app.pool).await.unwrap();
	assert_eq!(
		editor
			.post(&path, json!({"tier":"cold","rowVersion":1}))
			.await
			.0,
		500
	);
	assert_eq!(storage.calls.lock().unwrap().len(), 1);
	sqlx::raw_sql("DROP TRIGGER refuse_tier ON dateien; DROP FUNCTION refuse_tier();")
		.execute(&app.pool)
		.await
		.unwrap();
	let stored: (String, i64) =
		sqlx::query_as(r#"SELECT "StorageTier","RowVersion" FROM dateien WHERE "Id"=$1"#)
			.bind(file)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(stored, ("hot".into(), 1));
	assert_eq!(
		editor
			.post(&path, json!({"tier":"cold","rowVersion":1}))
			.await
			.0,
		200
	);
	assert_eq!(storage.calls.lock().unwrap().len(), 2);
	app.close().await;
}
