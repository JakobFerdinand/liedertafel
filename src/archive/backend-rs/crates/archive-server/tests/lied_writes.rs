mod support;
use serde_json::json;
use support::App;

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL authenticated Lied writes"]
async fn editor_creates_and_patches_flat_lied_member_sees_it_immediately() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let (status, created) = editor
		.post(
			"/api/lieder",
			json!({"Title":"  Ave Maria  ","Composer":"  Franz  ","lyrics":"Wort","occasion":"Konzert"}),
		)
		.await;
	assert_eq!(status, 201, "{created}");
	let lied = &created["lied"];
	assert_eq!(lied["title"], "Ave Maria");
	assert_eq!(lied["composer"], "Franz");
	assert_eq!(lied["rowVersion"], 1);
	let path = format!("/api/lieder/{}", lied["id"].as_str().unwrap());
	assert_eq!(member.get(&path).await.1, created);
	let (status, changed) = editor
		.request(
			"PATCH",
			&path,
			json!({"RowVersion":1,"composer":" ","lyrics":null,"arranger":"Neu"}),
		)
		.await;
	assert_eq!(status, 200, "{changed}");
	assert!(changed["lied"]["composer"].is_null());
	assert_eq!(changed["lied"]["lyrics"], "Wort");
	assert_eq!(changed["lied"]["arranger"], "Neu");
	assert_eq!(changed["lied"]["rowVersion"], 2);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"rowVersion":1,"title":"Alt"}))
			.await
			.0,
		409
	);
	assert_eq!(
		member.post("/api/lieder", json!({"title":"Nein"})).await.0,
		403
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL frozen field binding"]
async fn every_catalogued_field_binds_case_insensitively_and_unknown_fields_and_create_rowversion_are_refused()
 {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let fields = [
		"composer",
		"arranger",
		"lyricist",
		"voiceConfiguration",
		"musicalKey",
		"language",
		"lyrics",
		"occasion",
	];
	let mut request = json!({"Title":"Felder"});
	for field in fields {
		request[field.to_uppercase()] = json!(format!("{field}-Wert"));
	}
	let (s, created) = editor.post("/api/lieder", request).await;
	assert_eq!(s, 201, "{created}");
	let id = created["lied"]["id"].as_str().unwrap();
	for field in fields {
		assert_eq!(created["lied"][field], format!("{field}-Wert"));
	}
	let path = format!("/api/lieder/{id}");
	for (method, path, request) in [
		(
			"POST",
			"/api/lieder",
			json!({"title":"Fremd","instrumentation":"Trommeln"}),
		),
		(
			"POST",
			"/api/lieder",
			json!({"title":"Fremd","rowVersion":3}),
		),
		(
			"PATCH",
			path.as_str(),
			json!({"instrumentation":"Trommeln"}),
		),
	] {
		let (s, b) = editor.request(method, path, request).await;
		assert_eq!(s, 400);
		assert_eq!(b["title"], "Unbekanntes Feld.");
	}
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"RowVersion":1}))
			.await
			.0,
		200
	);
	assert_eq!(editor.request("PATCH", &path, json!({})).await.0, 200);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL Lied title/field limits"]
async fn titles_and_utf16_field_limits_trim_clear_and_missing_lied_match_frozen_writes() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	for request in [
		json!({}),
		json!({"title":" "}),
		json!({"title":"x".repeat(201)}),
		json!({"title":"A","composer":"😀".repeat(101)}),
		json!({"title":"A","lyrics":"x".repeat(5001)}),
	] {
		assert_eq!(editor.post("/api/lieder", request).await.0, 400);
	}
	let (s, b) = editor
		.post(
			"/api/lieder",
			json!({"title":"A","lyrics":"x".repeat(5000),"composer":"😀".repeat(100)}),
		)
		.await;
	assert_eq!(s, 201, "{b}");
	let id = b["lied"]["id"].as_str().unwrap();
	assert_eq!(
		editor
			.request("PATCH", &format!("/api/lieder/{id}"), json!({"title":" "}))
			.await
			.0,
		400
	);
	assert_eq!(
		editor
			.request(
				"PATCH",
				&format!("/api/lieder/{}", uuid::Uuid::new_v4()),
				json!({"title":"X"})
			)
			.await
			.0,
		404
	);
	app.close().await;
}
