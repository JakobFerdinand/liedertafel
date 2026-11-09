mod support;
use serde_json::json;
use std::sync::Arc;
use support::{App, storage::FakeStorage};

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL finalize lifecycle"]
async fn finalize_promotes_unique_immutable_datei_deletes_staging_then_replays_without_provider() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let (_, created) = editor
		.post("/api/lieder", json!({"title":"Notenlied"}))
		.await;
	let id = created["lied"]["id"].as_str().unwrap();
	let (_, open) = editor
		.post(
			&format!("/api/lieder/{id}/dateien/upload-session"),
			json!({"kind":"noten","isPartitur":true,"fileName":"partitur.pdf","sizeBytes":64}),
		)
		.await;
	let blob = storage.calls.lock().unwrap().last().unwrap().1.clone();
	let mut bytes = vec![0; 64];
	bytes[..5].copy_from_slice(b"%PDF-");
	storage.store(&blob, &bytes, "application/octet-stream");
	let path = format!(
		"/api/upload-sessions/{}/finalize",
		open["uploadSessionId"].as_str().unwrap()
	);
	let (status, result) = editor
		.post(&path, json!({"fileName":"partitur.pdf","sizeBytes":64}))
		.await;
	assert_eq!(status, 200, "{result}");
	assert_eq!(result["datei"]["contentType"], "application/pdf");
	assert_eq!(result["datei"]["rowVersion"], 1);
	assert_eq!(result["datei"]["isPartitur"], true);
	let count = storage.calls.lock().unwrap().len();
	assert_eq!(editor.post(&path, json!({})).await.1, result);
	assert_eq!(storage.calls.lock().unwrap().len(), count);
	{
		let objects = storage.objects.lock().unwrap();
		assert!(!objects.contains_key(&blob));
		assert_eq!(objects.len(), 1);
		assert!(objects.keys().next().unwrap().starts_with(&format!(
			"dateien/{}/",
			result["datei"]["id"].as_str().unwrap()
		)));
	}
	let (_, detail) = member.get(&format!("/api/lieder/{id}")).await;
	assert_eq!(detail["lied"]["dateien"][0], result["datei"]);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL upload session protocol"]
async fn initiate_renew_after_expiry_and_cancel_preserve_owner_and_terminal_contracts() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let (_, created) = editor
		.post("/api/lieder", json!({"title":"Notenlied"}))
		.await;
	let id = created["lied"]["id"].as_str().unwrap();
	let path = format!("/api/lieder/{id}/dateien/upload-session");
	let (status, open) = editor
		.post(
			&path,
			json!({"kind":"noten","isPartitur":true,"fileName":"  noten.pdf  ","sizeBytes":64}),
		)
		.await;
	assert_eq!(status, 201, "{open}");
	assert!(open["blobName"].is_null());
	assert_eq!(open["blockBytes"], 8 * 1024 * 1024);
	let session = open["uploadSessionId"].as_str().unwrap();
	let key = uuid::Uuid::parse_str(session).unwrap();
	sqlx::query(
		r#"UPDATE upload_sessions SET "UploadTicketExpiresAt"=now()-interval '5 minutes' WHERE "Id"=$1"#,
	)
	.bind(key)
	.execute(&app.pool)
	.await
	.unwrap();
	let (status, renew) = editor
		.post(&format!("/api/upload-sessions/{session}/renew"), json!({}))
		.await;
	assert_eq!(status, 200, "{renew}");
	assert!(renew["blobName"].as_str().unwrap().starts_with("pending/"));
	assert_eq!(
		editor
			.request(
				"DELETE",
				&format!("/api/upload-sessions/{session}"),
				json!({})
			)
			.await
			.0,
		204
	);
	for operation in ["renew", "finalize"] {
		assert_eq!(
			editor
				.post(
					&format!("/api/upload-sessions/{session}/{operation}"),
					json!({})
				)
				.await
				.0,
			409
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL multiple Noten upload/list order"]
async fn editor_adds_partitur_and_two_stimmen_member_reads_correct_order_and_several_sessions_stay_open()
 {
	use support::upload::*;
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Notenlied").await;
	let a = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"fileName":"partitur.pdf"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let b = stage(
		&mut editor,
		lied,
		false,
		json!({"stimme":"Tenor 1","fileName":"tenor1.pdf"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let c = stage(
		&mut editor,
		lied,
		false,
		json!({"stimme":"Bass 2","fileName":"bass2.pdf"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	for s in [&a, &b, &c] {
		assert_eq!(finish(&mut editor, s).await.0, 200);
	}
	let (_, detail) = member.get(&format!("/api/lieder/{lied}")).await;
	assert_eq!(
		detail["lied"]["dateien"]
			.as_array()
			.unwrap()
			.iter()
			.map(|d| d["fileName"].as_str().unwrap())
			.collect::<Vec<_>>(),
		["partitur.pdf", "bass2.pdf", "tenor1.pdf"]
	);
	app.close().await;
}
