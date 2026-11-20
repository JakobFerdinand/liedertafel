mod support;
use serde_json::json;
use support::{catalogue as fixture, recordings::*, upload};
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL #227 playback reattach Cold→Hot provider-first rollback"]
async fn reattaching_playback_returns_cold_original_hot_and_provider_refusal_rolls_back_all_sql() {
	let mut s = Scenario::new().await;
	let original = s.revision.clone();
	s.replace().await;
	let old_copy = s.revision.clone();
	let tier = format!("/api/dateien/{}/storage-tier", original.as_str().unwrap());
	assert_eq!(
		s.editor
			.post(&tier, json!({"tier":"cold","rowVersion":1}))
			.await
			.0,
		200
	);
	let session = upload::stage(
		&mut s.editor,
		s.event,
		true,
		json!({"kind":"aufnahme","contentType":"video/mp4"}),
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	let (_, file) = upload::finish(&mut s.editor, &session).await;
	let path = format!("/api/recordings/{}/dateien/playback", s.recording);
	let body = json!({"dateiId":file["datei"]["id"]});
	s.storage
		.tier_fail
		.store(true, std::sync::atomic::Ordering::SeqCst);
	let (status, b) = s.editor.request("PUT", &path, body.clone()).await;
	assert_eq!(status, 502, "{b}");
	assert_eq!(s.view(false).await["playbackDateiId"], old_copy);
	let live: bool = sqlx::query_scalar(r#"SELECT "DeletedAt" IS NULL FROM dateien WHERE "Id"=$1"#)
		.bind(uuid::Uuid::parse_str(old_copy.as_str().unwrap()).unwrap())
		.fetch_one(&s.app.pool)
		.await
		.unwrap();
	assert!(live);
	s.storage
		.tier_fail
		.store(false, std::sync::atomic::Ordering::SeqCst);
	let (status, b) = s.editor.request("PUT", &path, body).await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(
		b["recording"]["editor"]["original"]["file"]["storageTier"],
		"hot"
	);
	assert_eq!(b["recording"]["playback"]["dateiId"], file["datei"]["id"]);
	let original_id = uuid::Uuid::parse_str(original.as_str().unwrap()).unwrap();
	let blob = upload::blob(&s.app, original_id).await;
	assert!(
		s.storage
			.calls
			.lock()
			.unwrap()
			.contains(&("hot".into(), blob))
	);
	let old = uuid::Uuid::parse_str(old_copy.as_str().unwrap()).unwrap();
	let replacement: Option<uuid::Uuid> = sqlx::query_scalar(
		r#"SELECT "ReplacedByDateiId" FROM dateien WHERE "Id"=$1 AND "DeletedAt" IS NOT NULL"#,
	)
	.bind(old)
	.fetch_one(&s.app.pool)
	.await
	.unwrap();
	assert_eq!(
		replacement,
		Some(uuid::Uuid::parse_str(file["datei"]["id"].as_str().unwrap()).unwrap())
	);
	s.app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL seeded #233 completed copies integrate without implementing video workers"]
async fn completed_automatic_copy_fills_only_empty_slot_and_cannot_be_manually_attached() {
	let mut s = Scenario::new().await;
	let original = fixture::datei(&s.app, s.event, true, "aufnahme", "video/quicktime").await;
	let copy = fixture::datei(&s.app, s.event, true, "aufnahme", "video/mp4").await;
	fixture::video_copy(&s.app, original, copy, 2).await;
	let id = create(&mut s.editor, s.event, "Auto Kamera", "video").await;
	let path = format!("/api/recordings/{id}/dateien");
	let (status, b) = s
		.editor
		.request(
			"PUT",
			&format!("{path}/original"),
			json!({"dateiId":original}),
		)
		.await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["recording"]["playback"]["dateiId"], copy.to_string());
	assert_eq!(
		b["recording"]["editor"]["playbackCopy"]["file"]["dateiId"],
		copy.to_string()
	);
	assert_eq!(
		s.editor
			.request(
				"PUT",
				&format!("/api/recordings/{}/dateien/playback", s.recording),
				json!({"dateiId":copy})
			)
			.await
			.0,
		404
	);
	assert_eq!(
		s.editor
			.request(
				"PATCH",
				&format!("/api/recordings/{id}"),
				json!({"durationSeconds":100})
			)
			.await
			.0,
		200
	);
	let (status, b) = attach(
		&mut s.editor,
		s.event,
		id,
		"original",
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	assert_eq!(status, 200, "{b}");
	assert!(b["recording"]["durationSeconds"].is_null());
	assert_eq!(b["recording"]["playback"]["source"], "original");
	assert!(b["recording"]["editor"]["playbackCopy"]["file"].is_null());
	// Frozen video-job FK refusal remains inherited #224: no worker/removal fix here.
	s.app.close().await;
}
