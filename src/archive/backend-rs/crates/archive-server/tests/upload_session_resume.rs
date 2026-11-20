mod support;
use archive_server::catalogue::uploads::UploadOptions;
use serde_json::{Value, json};
use std::sync::{Arc, atomic::Ordering};
use support::{App, storage::FakeStorage, upload::*};
use uuid::Uuid;

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL upload identity"]
async fn finalize_compares_only_dimensions_present_on_both_sides_and_mismatch_stays_pending() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Identität").await;
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"fileName":"lied.pdf","sizeBytes":64}),
		&pdf(64),
		"application/octet-stream",
		&storage,
	)
	.await;
	let path = format!(
		"/api/upload-sessions/{}/finalize",
		session["uploadSessionId"].as_str().unwrap()
	);
	for body in [json!({"sizeBytes":65}), json!({"fileName":"anders.pdf"})] {
		let (s, b) = editor.post(&path, body).await;
		assert_eq!(s, 409);
		assert_eq!(
			b["title"],
			"Die Datei passt nicht zur bestehenden Uploadsitzung."
		);
	}
	assert_eq!(
		editor
			.post(&path, json!({"fileName":" lied.pdf ","sizeBytes":64}))
			.await
			.0,
		200
	);
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let path = format!(
		"/api/upload-sessions/{}/finalize",
		session["uploadSessionId"].as_str().unwrap()
	);
	assert_eq!(
		editor
			.post(&path, json!({"fileName":"anything.pdf","sizeBytes":999}))
			.await
			.0,
		200
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL declaration and reservation budgets"]
async fn initiation_budget_counts_declared_sizes_or_caps_and_stored_trash_but_is_scoped_to_owner() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.configure_uploads(
		storage.clone(),
		UploadOptions {
			max_bytes: 2048,
			collection_bytes: 4096,
			..Default::default()
		},
		vec![],
		vec![],
	);
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Budget").await;
	let path = format!("/api/lieder/{lied}/dateien/upload-session");
	for body in [
		json!({"isPartitur":true,"sizeBytes":1024}),
		json!({"isPartitur":true}),
		json!({"isPartitur":true,"sizeBytes":1024}),
	] {
		let (s, b) = editor.post(&path, body).await;
		assert_eq!(s, 201, "{b}");
		assert_eq!(b["maxBytes"], 2048);
		assert_eq!(b["blockBytes"], 8 * 1024 * 1024);
	}
	assert_eq!(
		editor
			.post(&path, json!({"isPartitur":true,"sizeBytes":1}))
			.await
			.0,
		413
	);
	let other = create(&mut editor, "Anderes Budget").await;
	let file = upload(
		&mut editor,
		other,
		json!({"isPartitur":true}),
		&pdf(1024),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{}/delete", file["id"].as_str().unwrap()),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	let path = format!("/api/lieder/{other}/dateien/upload-session");
	assert_eq!(
		editor
			.post(&path, json!({"isPartitur":true,"sizeBytes":2048}))
			.await
			.0,
		201
	);
	assert_eq!(
		editor
			.post(&path, json!({"isPartitur":true,"sizeBytes":1025}))
			.await
			.0,
		413
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL upload guards"]
async fn session_creation_validates_owner_kind_voice_lengths_size_and_role_without_provider_calls()
{
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.configure_uploads(
		storage.clone(),
		UploadOptions {
			max_bytes: 2048,
			..Default::default()
		},
		vec![],
		vec![],
	);
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Validierung").await;
	let path = format!("/api/lieder/{lied}/dateien/upload-session");
	for (body, status, title) in [
		(
			json!({"isPartitur":"true","stimme":"Tenor 1"}),
			400,
			"Die Anfrage ist ungültig.",
		),
		(
			json!({"isPartitur":true,"stimme":123}),
			400,
			"Die Anfrage ist ungültig.",
		),
		(
			json!({"kind":"noten"}),
			400,
			"Bitte eine Stimme angeben oder die Datei als Partitur kennzeichnen.",
		),
		(json!({"kind":"video"}), 422, "Unbekannter Dateityp."),
		(
			json!({"kind":"uebe","stimme":"Alle","contentType":"application/pdf"}),
			422,
			"Dieser Dateityp ist für diese Art nicht erlaubt.",
		),
		(
			json!({"isPartitur":true,"fileName":"x".repeat(301)}),
			400,
			"Der Dateiname ist zu lang.",
		),
		(
			json!({"isPartitur":true,"description":"x".repeat(501)}),
			400,
			"Die Beschreibung ist zu lang.",
		),
		(
			json!({"stimme":"x".repeat(201)}),
			400,
			"Ein Feld ist zu lang.",
		),
		(
			json!({"isPartitur":true,"sizeBytes":2049}),
			413,
			"Die Datei ist zu groß.",
		),
		(
			json!({"kind":"aufnahme","recordedAtEventId":Uuid::new_v4()}),
			400,
			"Auftritt nicht gefunden.",
		),
		(
			json!({"isPartitur":true,"replacesDateiId":Uuid::new_v4()}),
			404,
			"Datei nicht gefunden.",
		),
	] {
		let (s, b) = editor.post(&path, body).await;
		assert_eq!(s.as_u16(), status, "{b}");
		assert_eq!(b["title"], title);
	}
	for kind in ["noten", "uebe", "aufnahme", "sonstiges"] {
		assert_eq!(
			member
				.post(&path, json!({"kind":kind,"isPartitur":true}))
				.await
				.0,
			403
		);
	}
	assert!(storage.calls.lock().unwrap().is_empty());
	assert_eq!(
		editor
			.post(
				&format!("/api/lieder/{}/dateien/upload-session", Uuid::new_v4()),
				json!({"isPartitur":true})
			)
			.await
			.0,
		404
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL upload terminal/foreign ownership"]
async fn unknown_foreign_and_terminal_session_actions_never_touch_provider_or_bypass_csrf() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut foreign = app.sign_in("redaktion@liedertafel.test").await;
	let lied = create(&mut editor, "Besitz").await;
	let session = open(&mut editor, lied, false, json!({"isPartitur":true})).await;
	let id = session["uploadSessionId"].as_str().unwrap();
	for (method, suffix) in [("POST", "/renew"), ("POST", "/finalize"), ("DELETE", "")] {
		let path = format!("/api/upload-sessions/{id}{suffix}");
		let count = storage.calls.lock().unwrap().len();
		let (s, b) = foreign.request(method, &path, json!({})).await;
		assert_eq!(s, 403);
		assert_eq!(
			b["title"],
			"Nur die anlegendende Person kann den Upload abschließen."
		);
		assert_eq!(storage.calls.lock().unwrap().len(), count);
		assert_eq!(
			editor
				.request(
					method,
					&format!("/api/upload-sessions/{}{suffix}", Uuid::new_v4()),
					json!({})
				)
				.await
				.0,
			404
		);
	}
	let token = std::mem::take(&mut editor.token);
	assert_eq!(
		editor
			.post(&format!("/api/upload-sessions/{id}/renew"), json!({}))
			.await
			.0,
		400
	);
	editor.token = token;
	assert_eq!(
		editor
			.request("DELETE", &format!("/api/upload-sessions/{id}"), Value::Null)
			.await
			.0,
		204
	);
	for (method, suffix) in [("POST", "/renew"), ("POST", "/finalize"), ("DELETE", "")] {
		assert_eq!(
			editor
				.request(
					method,
					&format!("/api/upload-sessions/{id}{suffix}"),
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
#[ignore = "explicit disposable PostgreSQL provider failure/save ordering"]
async fn failed_tickets_leave_saved_session_and_extended_expiry_and_finalize_provider_failures_are_retryable()
 {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Ausfall").await;
	storage.fail.store(true, Ordering::SeqCst);
	let (s, b) = editor
		.post(
			&format!("/api/lieder/{lied}/dateien/upload-session"),
			json!({"isPartitur":true}),
		)
		.await;
	assert_eq!(s, 502);
	assert_eq!(b["title"], "Speicherdienst nicht erreichbar.");
	let id: Uuid = sqlx::query_scalar("SELECT \"Id\" FROM upload_sessions")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(2);
	let (s, b) = editor
		.post(&format!("/api/upload-sessions/{id}/renew"), json!({}))
		.await;
	assert_eq!(s, 502);
	assert_eq!(b["title"], "Speicherdienst nicht erreichbar.");
	let expires: chrono::DateTime<chrono::Utc> =
		sqlx::query_scalar("SELECT \"UploadTicketExpiresAt\" FROM upload_sessions")
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert!(
		(expires - *app.time.0.lock().unwrap() - chrono::Duration::minutes(30))
			.num_milliseconds()
			.abs() < 2
	);
	storage.fail.store(false, Ordering::SeqCst);
	let (_, renew) = editor
		.post(&format!("/api/upload-sessions/{id}/renew"), json!({}))
		.await;
	storage.store(
		renew["blobName"].as_str().unwrap(),
		&pdf(64),
		"application/pdf",
	);
	storage.fail.store(true, Ordering::SeqCst);
	assert_eq!(
		editor
			.post(&format!("/api/upload-sessions/{id}/finalize"), json!({}))
			.await
			.0,
		502
	);
	storage.fail.store(false, Ordering::SeqCst);
	assert_eq!(
		editor
			.post(&format!("/api/upload-sessions/{id}/finalize"), json!({}))
			.await
			.0,
		200
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL finalized budget and terminal validation"]
async fn expired_missing_oversize_invalid_headers_and_xml_type_have_distinct_terminal_rules() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.configure_uploads(
		storage.clone(),
		UploadOptions {
			max_bytes: 100,
			collection_bytes: 150,
			..Default::default()
		},
		vec![],
		vec![],
	);
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Endzustände").await;
	let missing = open(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"sizeBytes":1}),
	)
	.await;
	assert_eq!(finish(&mut editor, &missing).await.0, 409);
	for (data, content, status) in [
		(pdf(101), "application/pdf", 413),
		(b"not PDF".to_vec(), "application/pdf", 422),
		(pdf(64), "application/xml", 422),
	] {
		let session = stage(
			&mut editor,
			lied,
			false,
			json!({"isPartitur":true,"sizeBytes":1}),
			&data,
			content,
			&storage,
		)
		.await;
		assert_eq!(finish(&mut editor, &session).await.0, status);
		assert_eq!(finish(&mut editor, &session).await.0, 409);
	}
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"sizeBytes":1}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let id = Uuid::parse_str(session["uploadSessionId"].as_str().unwrap()).unwrap();
	sqlx::query(
		r#"UPDATE upload_sessions SET "UploadTicketExpiresAt"=now()-interval '1 minute' WHERE "Id"=$1"#,
	)
	.bind(id)
	.execute(&app.pool)
	.await
	.unwrap();
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 409);
	assert_eq!(b["title"], "Der Uploadzeitraum ist abgelaufen.");
	assert_eq!(
		editor
			.post(&format!("/api/upload-sessions/{id}/renew"), json!({}))
			.await
			.0,
		409
	);
	let first = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"sizeBytes":1}),
		&pdf(100),
		"application/pdf",
		&storage,
	)
	.await;
	let second = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"sizeBytes":1}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(finish(&mut editor, &first).await.0, 200);
	assert_eq!(finish(&mut editor, &second).await.0, 413);
	app.close().await;
}
