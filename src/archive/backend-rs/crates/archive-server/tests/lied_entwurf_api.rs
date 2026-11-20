mod support;
use serde_json::json;
use support::App;
use uuid::Uuid;
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL Angefangen persistence"]
async fn draft_roundtrips_whole_form_marks_file_choices_and_repeatable_discard_without_uploading_bytes()
 {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	let body = json!({"entwurfId":id,"lied":{"title":"Ave Maria","composer":"Bruckner","lyrics":""},"bearbeitet":["lyrics","composer"],"vorschlaege":{"title":"unsicher"},"dateien":[{"id":file,"name":"  tenor.mp3  ","sizeBytes":1234,"art":"uebe","stimme":"Tenor 1","stimmeBearbeitet":true,"lesen":{"status":"keine","anfrageId":0,"meldung":""}}]});
	let (s, b) = editor.request("PUT", &path, body.clone()).await;
	assert_eq!(s, 200, "{b}");
	let (s, b) = editor.get(&path).await;
	assert_eq!(s, 200);
	let d = &b["entwurf"];
	assert_eq!(d["lied"]["composer"], "Bruckner");
	assert_eq!(d["lied"]["lyrics"], "");
	assert_eq!(d["bearbeitet"], json!(["composer", "lyrics"]));
	assert_eq!(d["vorschlaege"]["title"], "unsicher");
	assert_eq!(d["dateien"][0]["name"], "tenor.mp3");
	assert!(d["dateien"][0]["lesung"].is_null());
	assert_eq!(d["dateien"][0]["stimmeBearbeitet"], true);
	assert_eq!(
		editor.get("/api/lied-entwuerfe").await.1["entwuerfe"][0]["dateien"],
		1
	);
	assert_eq!(member.get(&path).await.0, 403);
	for _ in 0..2 {
		assert_eq!(editor.request("DELETE", &path, json!({})).await.0, 204);
	}
	assert_eq!(editor.get(&path).await.0, 404);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL draft ownership and replacement"]
async fn draft_is_private_to_editor_whole_form_replaces_without_clobbering_kept_server_outcome() {
	let app = App::new().await;
	let mut first = app.admin().await;
	let mut other = app.sign_in("redaktion@liedertafel.test").await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let removed = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	let body = json!({"entwurfId":id,"lied":{"title":"Ave Maria","composer":"Bruckner"},"dateien":[{"id":file,"name":"a.pdf","sizeBytes":64,"art":"partitur"},{"id":removed,"name":"b.mp3","sizeBytes":3,"art":"uebe"}]});
	assert_eq!(first.request("PUT", &path, body.clone()).await.0, 200);
	assert_eq!(other.get(&path).await.0, 404);
	assert_eq!(
		other.get("/api/lied-entwuerfe").await.1["entwuerfe"],
		json!([])
	);
	assert_eq!(other.request("PUT", &path, body.clone()).await.0, 404);
	assert_eq!(other.request("DELETE", &path, json!({})).await.0, 204);
	sqlx::query(r#"INSERT INTO lied_entwurf_lesungen ("EntwurfId","DateiId","AnfrageId","Status","Meldung","Felder","UpdatedAt") VALUES ($1,$2,1,'gelesen','','[]',now())"#).bind(id).bind(file).execute(&app.pool).await.unwrap();
	let mut replacement = body;
	replacement["lied"]["composer"] = json!("");
	replacement["dateien"].as_array_mut().unwrap().pop();
	assert_eq!(first.request("PUT", &path, replacement).await.0, 200);
	let (_, read) = first.get(&path).await;
	assert_eq!(read["entwurf"]["lied"]["composer"], "");
	assert_eq!(read["entwurf"]["dateien"].as_array().unwrap().len(), 1);
	assert_eq!(read["entwurf"]["dateien"][0]["lesung"]["status"], "gelesen");
	assert_eq!(
		first
			.request(
				"PUT",
				&path,
				json!({"entwurfId":id,"lied":{"title":"Ave"},"dateien":[]})
			)
			.await
			.0,
		200
	);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM lied_entwurf_lesungen")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL draft shape bounds"]
async fn draft_validation_refuses_unknown_fields_marks_arts_duplicates_lengths_and_missing_csrf() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	let good = json!({"entwurfId":id,"lied":{"title":"Ave"},"dateien":[{"id":file,"name":"a.pdf","sizeBytes":64,"art":"partitur"}]});
	for mutation in [
		json!({"entwurfId":Uuid::new_v4()}),
		json!({"lied":{"title":"x".repeat(201)}}),
		json!({"lied":{"title":"Ave","occasion":"Konzert"}}),
		json!({"bearbeitet":["herkunft"]}),
		json!({"vorschlaege":{"title":"vielleicht"}}),
		json!({"dateien":[good["dateien"][0].clone(),good["dateien"][0].clone()]}),
		json!({"dateien":[{"id":file,"name":"a.pdf","sizeBytes":1,"art":"unbekannt"}]}),
		json!({"dateien":[{"id":file,"name":"a.pdf","sizeBytes":-1,"art":"noten"}]}),
		json!({"dateien":[{"id":file,"name":"a.pdf","sizeBytes":1,"art":"noten","lesen":{"status":"liest","anfrageId":-1}}]}),
	] {
		let mut request = good.clone();
		for (k, v) in mutation.as_object().unwrap() {
			request[k] = v.clone();
		}
		let (s, b) = editor.request("PUT", &path, request).await;
		assert_eq!(s, 400, "{b}");
	}
	editor.token.clear();
	assert_eq!(editor.request("PUT", &path, good).await.0, 400);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL own newest-first draft list and cap"]
async fn own_drafts_are_newest_first_with_counts_and_bound_at_twenty_five() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	for index in 0..25 {
		let id = Uuid::new_v4();
		let (s, b) = editor
			.request(
				"PUT",
				&format!("/api/lied-entwuerfe/{id}"),
				json!({"entwurfId":id,"lied":{"title":format!("Lied {index}")}}),
			)
			.await;
		assert_eq!(s, 200, "{b}");
		*app.time.0.lock().unwrap() += chrono::Duration::seconds(1);
	}
	let list = editor.get("/api/lied-entwuerfe").await.1;
	assert_eq!(list["entwuerfe"].as_array().unwrap().len(), 25);
	assert_eq!(list["entwuerfe"][0]["titel"], "Lied 24");
	assert_eq!(list["entwuerfe"][0]["dateien"], 0);
	let id = Uuid::new_v4();
	let (s, b) = editor
		.request(
			"PUT",
			&format!("/api/lied-entwuerfe/{id}"),
			json!({"entwurfId":id,"lied":{}}),
		)
		.await;
	assert_eq!(s, 409);
	assert_eq!(
		b["title"],
		"Es sind zu viele Einträge angefangen. Bitte erst welche verwerfen."
	);
	app.close().await;
}
