mod support;
use serde_json::json;
use std::sync::Arc;
use support::{App, storage::FakeStorage, upload::*};
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL owner/file recovery"]
async fn owner_first_restore_preserves_individual_trash_and_successor_then_admin_removes_rows_and_bytes()
 {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Papierkorb").await;
	let first = upload(
		&mut editor,
		lied,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let file = first["id"].as_str().unwrap();
	let second = upload(
		&mut editor,
		lied,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{file}/delete"),
				json!({"rowVersion":99})
			)
			.await
			.0,
		409
	);
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{file}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	assert_eq!(
		editor
			.post(
				&format!("/api/lieder/{lied}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	assert_eq!(member.get(&format!("/api/lieder/{lied}")).await.0, 404);
	let (s, b) = editor
		.post(&format!("/api/dateien/{file}/restore"), json!({}))
		.await;
	assert_eq!(s, 409, "{b}");
	assert_eq!(
		b["title"],
		"Der übergeordnete Eintrag ist noch im Papierkorb."
	);
	assert_eq!(
		editor
			.post(&format!("/api/lieder/{lied}/restore"), json!({}))
			.await
			.0,
		204
	);
	let (_, detail) = member.get(&format!("/api/lieder/{lied}")).await;
	assert_eq!(detail["lied"]["dateien"], json!([second]));
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{file}/restore"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(
		member.get(&format!("/api/lieder/{lied}")).await.1["lied"]["dateien"]
			.as_array()
			.unwrap()
			.len(),
		2
	);
	assert_eq!(
		member
			.post(&format!("/api/papierkorb/lieder/{lied}/remove"), json!({}))
			.await
			.0,
		403
	);
	assert_eq!(
		editor
			.post(&format!("/api/papierkorb/lieder/{lied}/remove"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(member.get(&format!("/api/lieder/{lied}")).await.0, 404);
	assert!(storage.objects.lock().unwrap().is_empty());
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL seven-day restore boundary"]
async fn restore_refuses_at_retention_boundary_but_succeeds_just_before_and_live_restore_is_idempotent()
 {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Frist").await;
	let path = format!("/api/lieder/{lied}/restore");
	let now = *app.time.0.lock().unwrap();
	let expired = now - chrono::Duration::days(7) - chrono::Duration::seconds(1);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=$2 WHERE "Id"=$1"#)
		.bind(lied)
		.bind(expired)
		.execute(&app.pool)
		.await
		.unwrap();
	let (s, b) = editor.post(&path, json!({})).await;
	assert_eq!(s, 409);
	assert_eq!(b["title"], "Die Aufbewahrungsfrist ist abgelaufen.");
	assert_eq!(
		b["detail"],
		"Einträge bleiben 7 Tage im Papierkorb und werden danach endgültig entfernt."
	);
	let before = now - chrono::Duration::days(7) + chrono::Duration::seconds(1);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=$2 WHERE "Id"=$1"#)
		.bind(lied)
		.bind(before)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({})).await.0, 204);
	let version = editor.get(&format!("/api/lieder/{lied}")).await.1["lied"]["rowVersion"].clone();
	assert_eq!(editor.post(&path, json!({})).await.0, 204);
	assert_eq!(
		editor.get(&format!("/api/lieder/{lied}")).await.1["lied"]["rowVersion"],
		version
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL shared event trash rules"]
async fn seeded_event_delete_restore_preserves_programme_recording_passage_and_individually_trashed_file_waits_for_owner()
 {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage);
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let event = support::catalogue::event(&app).await;
	let lied = create(&mut editor, "Gespielt").await;
	let original =
		support::catalogue::datei(&app, event, true, "sonstiges", "application/pdf").await;
	let recording = support::catalogue::recording(&app, event, original, None, "audio").await;
	let programme = uuid::Uuid::new_v4();
	let item = uuid::Uuid::new_v4();
	sqlx::query(r#"INSERT INTO programmes ("Id","EventId","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,now(),now(),$3,$3,1)"#).bind(programme).bind(event).bind(uuid::Uuid::nil()).execute(&app.pool).await.unwrap();
	sqlx::query(
		r#"INSERT INTO programme_items ("Id","ProgrammeId","Position","LiedId") VALUES ($1,$2,0,$3)"#,
	)
	.bind(item)
	.bind(programme)
	.bind(lied)
	.execute(&app.pool)
	.await
	.unwrap();
	sqlx::query(r#"INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion","PlaybackDateiId") VALUES ($1,$2,$3,$4,1,2,now(),now(),$5,$5,1,$6)"#).bind(uuid::Uuid::new_v4()).bind(recording).bind(item).bind(event).bind(uuid::Uuid::nil()).bind(original).execute(&app.pool).await.unwrap();
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{original}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
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
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{original}/restore"), json!({}))
			.await
			.0,
		409
	);
	assert_eq!(
		member
			.get(&format!("/api/dateien/{original}/access"))
			.await
			.0,
		404
	);
	let trash = editor.get("/api/papierkorb").await.1;
	assert_eq!(trash["auftritte"][0]["id"], event.to_string());
	assert_eq!(trash["dateien"][0]["eventTitle"], "Herbstkonzert");
	assert_eq!(
		editor
			.post(&format!("/api/events/{event}/restore"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{original}/restore"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(
		member
			.get(&format!("/api/dateien/{original}/access"))
			.await
			.0,
		200
	);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM recording_passages")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	assert_eq!(
		editor
			.post(
				&format!("/api/papierkorb/auftritte/{event}/remove"),
				json!({})
			)
			.await
			.0,
		204
	);
	for table in [
		"events",
		"recordings",
		"recording_passages",
		"programmes",
		"programme_items",
		"dateien",
	] {
		let count: i64 =
			sqlx::query_scalar(sqlx::AssertSqlSafe(format!("SELECT count(*) FROM {table}")))
				.fetch_one(&app.pool)
				.await
				.unwrap();
		assert_eq!(count, 0, "{table}");
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL restored replacement visibility"]
async fn restoring_replaced_datei_brings_both_back_and_keeps_replaced_attribution() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Beide").await;
	let old = upload(
		&mut editor,
		lied,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	upload(
		&mut editor,
		lied,
		json!({"replacesDateiId":old["id"]}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{}/restore", old["id"].as_str().unwrap()),
				json!({})
			)
			.await
			.0,
		204
	);
	assert_eq!(
		editor.get(&format!("/api/lieder/{lied}")).await.1["lied"]["dateien"]
			.as_array()
			.unwrap()
			.len(),
		2
	);
	app.close().await;
}
