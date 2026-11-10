mod support;
use serde_json::json;
use std::sync::Arc;
use support::{App, storage::FakeStorage, upload::*};
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL replacement/trash HTTP"]
async fn replacing_keeps_new_visible_and_old_immutable_in_editor_trash() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Ersatzlied").await;
	let old = upload(
		&mut editor,
		lied,
		json!({"kind":"noten","stimme":"Tenor 1","fileName":"alt.pdf"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let old_id = old["id"].as_str().unwrap();
	let old_blob = blob(&app, uuid::Uuid::parse_str(old_id).unwrap()).await;
	let new = upload(
		&mut editor,
		lied,
		json!({"kind":"sonstiges","stimme":"Falsch","replacesDateiId":old_id,"fileName":"neu.pdf"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	assert_ne!(old["id"], new["id"]);
	assert_eq!(new["kind"], "noten");
	assert_eq!(new["stimme"], "Tenor 1");
	let (_, detail) = member.get(&format!("/api/lieder/{lied}")).await;
	assert_eq!(detail["lied"]["dateien"], json!([new]));
	assert_eq!(
		member.get(&format!("/api/dateien/{old_id}/access")).await.0,
		404
	);
	assert!(storage.objects.lock().unwrap().contains_key(&old_blob));
	let (status, trash) = editor.get("/api/papierkorb").await;
	assert_eq!(status, 200);
	let entry = &trash["dateien"][0];
	assert_eq!(entry["id"], old_id);
	assert_eq!(entry["fileName"], "alt.pdf");
	assert_eq!(entry["liedTitle"], "Ersatzlied");
	assert_eq!(entry["replaced"], true);
	assert!(entry["deletedBy"].is_string());
	assert_eq!(member.get("/api/papierkorb").await.0, 403);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL replacement ownership and deleted-target race"]
async fn replace_only_live_same_lied_and_finalize_after_target_deleted_abandons_staged_bytes() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Eins").await;
	let other = create(&mut editor, "Zwei").await;
	let old = upload(
		&mut editor,
		lied,
		json!({"stimme":"Tenor 1"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	for (owner, client, status) in [(lied, &mut member, 403), (other, &mut editor, 404)] {
		assert_eq!(
			client
				.post(
					&format!("/api/lieder/{owner}/dateien/upload-session"),
					json!({"replacesDateiId":old["id"]})
				)
				.await
				.0,
			status
		);
	}
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"replacesDateiId":old["id"]}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{}/delete", old["id"].as_str().unwrap()),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 409);
	assert_eq!(
		b["title"],
		"Die Datei wurde inzwischen ersetzt oder gelöscht."
	);
	assert_eq!(storage.objects.lock().unwrap().len(), 1);
	assert_eq!(
		editor
			.post(
				&format!("/api/lieder/{lied}/dateien/upload-session"),
				json!({"replacesDateiId":old["id"]})
			)
			.await
			.0,
		404
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL actual replacement shared search visibility"]
async fn replaced_noten_text_leaves_member_search_without_deleting_extraction_or_original_bytes() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = create(&mut editor, "Suchlied").await;
	let old = upload(
		&mut editor,
		lied,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	support::catalogue::text(
		&app,
		uuid::Uuid::parse_str(old["id"].as_str().unwrap()).unwrap(),
		"Nachtigall im Tannenwald",
		2,
		0,
	)
	.await;
	assert_eq!(member.get("/api/lieder?q=nachtigall").await.1["total"], 1);
	upload(
		&mut editor,
		lied,
		json!({"replacesDateiId":old["id"]}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(member.get("/api/lieder?q=nachtigall").await.1["total"], 0);
	assert_eq!(storage.objects.lock().unwrap().len(), 2);
	app.close().await;
}
