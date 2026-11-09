mod support;
use serde_json::{Value, json};
use support::App;
use support::catalogue::{datei, lied as seed_lied, titles};
use uuid::Uuid;

async fn seed(app: &App, title: &str, fields: Value) -> Uuid {
	seed_lied(app, title, fields).await
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn list_searches_and_filters_by_besetzung_anlass_and_all_dateitypen() {
	let app = App::new().await;
	let mut member = app.member().await;
	let mond = seed(
		&app,
		"Der Mond ist aufgegangen",
		json!({"voiceConfiguration":"TTBB","occasion":"Abendlied"}),
	)
	.await;
	seed(
		&app,
		"Über den Wolken",
		json!({"voiceConfiguration":"SATB","occasion":"Frühling"}),
	)
	.await;
	seed(&app, "Ohne Angaben", json!({})).await;
	datei(&app, mond, false, "noten", "application/pdf").await;
	for (q, expected) in [
		(
			"",
			vec![
				"Der Mond ist aufgegangen",
				"Ohne Angaben",
				"Über den Wolken",
			],
		),
		("?q=ueber", vec!["Über den Wolken"]),
		("?q=mond", vec!["Der Mond ist aufgegangen"]),
		("?besetzung=ttbb", vec!["Der Mond ist aufgegangen"]),
		("?anlass=fruehling", vec!["Über den Wolken"]),
		("?dateiTyp=noten", vec!["Der Mond ist aufgegangen"]),
		("?dateiTyp=uebe", vec![]),
		("?besetzung=TTBB&anlass=Fr%C3%BChling", vec![]),
		(
			"?voiceConfiguration=ttbb&occasion=abendlied",
			vec!["Der Mond ist aufgegangen"],
		),
		("?dateiTyp=noten&dateiTyp=uebe", vec![]),
	] {
		let (status, body) = member.get(&format!("/api/lieder{q}")).await;
		assert_eq!(status, 200, "{body}");
		assert_eq!(titles(&body), expected, "{q}");
	}
	let (_, body) = member
		.get("/api/lieder?q=mond&dateiTyp=%20NOTEN%20&dateiTyp=noten")
		.await;
	assert_eq!(
		body["options"],
		json!({"voiceConfigurations":["SATB","TTBB"],"occasions":["Abendlied","Frühling"]})
	);
	assert_eq!(body["filters"]["dateiTypen"], json!(["noten"]));
	assert_eq!(body["lieder"][0]["dateiTypen"], json!(["noten"]));
	assert_eq!(body["lieder"][0]["matchIn"], json!(["im Titel"]));
	for (q, title) in [
		(format!("?q={}", "a".repeat(201)), "Die Suche ist zu lang."),
		("?dateiTyp=video".into(), "Unbekannter Dateityp."),
	] {
		let (status, body) = member.get(&format!("/api/lieder{q}")).await;
		assert_eq!(status, 400);
		assert_eq!(body["title"], title);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn list_pages_by_twenty_and_option_folding_is_global_and_deterministic() {
	let app = App::new().await;
	let mut member = app.member().await;
	for i in 0..21 {
		seed(
			&app,
			&format!("Lied {i:02}"),
			json!({"voiceConfiguration":if i%2==0 {"Männer"} else {"Maenner"},"occasion":" "}),
		)
		.await;
	}
	let (_, first) = member.get("/api/lieder").await;
	assert_eq!(first["total"], 21);
	assert_eq!(first["lieder"].as_array().unwrap().len(), 20);
	let (_, second) = member.get("/api/lieder?page=2").await;
	assert_eq!(titles(&second), ["Lied 20"]);
	assert_eq!(
		second["options"],
		json!({"voiceConfigurations":["Maenner"],"occasions":[]})
	);
	assert_eq!(member.get("/api/lieder?page=-1").await.1["page"], 1);
	assert_eq!(member.get("/api/lieder?page=0").await.1["page"], 1);
	assert_eq!(
		member.get("/api/lieder?page=2147483647").await.1["lieder"]
			.as_array()
			.unwrap()
			.len(),
		20,
		"executed .NET unchecked paging oracle"
	);
	assert_eq!(member.get("/api/lieder?page=x").await.0, 400);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn detail_lists_visible_dateien_in_kind_partitur_voice_creation_order() {
	let app = App::new().await;
	let mut member = app.member().await;
	let id = seed(&app, "Dateienlied", json!({})).await;
	let audio = datei(&app, id, false, "aufnahme", "audio/mpeg").await;
	let bass = datei(&app, id, false, "noten", "application/pdf").await;
	let full = datei(&app, id, false, "noten", "application/pdf").await;
	sqlx::query(r#"UPDATE dateien SET "Stimme"='Bass 2' WHERE "Id"=$1"#)
		.bind(bass)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE dateien SET "IsPartitur"=true WHERE "Id"=$1"#)
		.bind(full)
		.execute(&app.pool)
		.await
		.unwrap();
	let trashed = datei(&app, id, false, "noten", "application/pdf").await;
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(trashed)
		.execute(&app.pool)
		.await
		.unwrap();
	let (status, body) = member.get(&format!("/api/lieder/{id}")).await;
	assert_eq!(status, 200);
	let files = body["lied"]["dateien"].as_array().unwrap();
	assert_eq!(
		files
			.iter()
			.map(|d| d["id"].as_str().unwrap())
			.collect::<Vec<_>>(),
		[full.to_string(), bass.to_string(), audio.to_string()]
	);
	assert_eq!(files[0]["storageTier"], "hot");
	assert!(files[0]["tierChangedAt"].is_null());
	assert!(files[0]["recordedAtEvent"].is_null());
	assert_eq!(files[0]["isPartitur"], true);
	assert_eq!(
		files[0].as_object().unwrap().len(),
		13,
		"exact Summary wire shape"
	);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	let (status, body) = member.get(&format!("/api/lieder/{id}")).await;
	assert_eq!(status, 404);
	assert_eq!(body["title"], "Das Lied wurde nicht gefunden.");
	assert_eq!(member.get("/api/lieder").await.1["total"], 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL read shapes"]
async fn recording_event_link_contains_visible_event_and_precision_aware_display_only() {
	let app = App::new().await;
	let mut member = app.member().await;
	let id = seed(&app, "Aufnahme", json!({})).await;
	let file = datei(&app, id, false, "aufnahme", "audio/mpeg").await;
	let event = support::catalogue::event(&app).await;
	sqlx::query(r#"UPDATE dateien SET "RecordedAtEventId"=$2 WHERE "Id"=$1"#)
		.bind(file)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	for (year, month, day, approx, display) in [
		(Some(2026), Some(11), Some(9), false, "9. November 2026"),
		(Some(1906), Some(3), None, true, "um März 1906"),
		(Some(1906), None, None, true, "um 1906"),
		(None, None, None, true, "Datum unbekannt"),
	] {
		sqlx::query(r#"UPDATE events SET "DateYear"=$2,"DateMonth"=$3,"DateDay"=$4,"DateApproximate"=$5 WHERE "Id"=$1"#).bind(event).bind(year).bind(month).bind(day).bind(approx).execute(&app.pool).await.unwrap();
		let (_, body) = member.get(&format!("/api/lieder/{id}")).await;
		assert_eq!(
			body["lied"]["dateien"][0]["recordedAtEvent"],
			json!({"id":event,"title":"Herbstkonzert","dateDisplay":display})
		);
	}
	sqlx::query(r#"UPDATE events SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	let (_, body) = member.get(&format!("/api/lieder/{id}")).await;
	assert!(body["lied"]["dateien"][0]["recordedAtEvent"].is_null());
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL ordering oracle"]
async fn stimmen_use_dotnet_ordinal_ignore_case_without_expanding_sz_or_collapsing_null() {
	let app = App::new().await;
	let mut member = app.member().await;
	let id = seed(&app, "Stimmen", json!({})).await;
	for voice in [Some("ß"), Some("SS"), Some(""), None] {
		let d = datei(&app, id, false, "uebe", "audio/mpeg").await;
		sqlx::query(r#"UPDATE dateien SET "Stimme"=$2 WHERE "Id"=$1"#)
			.bind(d)
			.bind(voice)
			.execute(&app.pool)
			.await
			.unwrap();
	}
	let (_, body) = member.get(&format!("/api/lieder/{id}")).await;
	assert_eq!(
		body["lied"]["dateien"]
			.as_array()
			.unwrap()
			.iter()
			.map(|d| d["stimme"].clone())
			.collect::<Vec<_>>(),
		[Value::Null, json!(""), json!("SS"), json!("ß")]
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn member_reads_full_lied_immediately_and_list_omits_lyrics() {
	let keys = |v: &Value| {
		let mut keys = v.as_object().unwrap().keys().cloned().collect::<Vec<_>>();
		keys.sort();
		keys
	};
	let app = App::new().await;
	let mut member = app.member().await;
	let id = seed(&app, "Der Mond ist aufgegangen", json!({"composer":"J. A. P. Schulz","arranger":"Max Mustermann","lyricist":"Matthias Claudius","voiceConfiguration":"TTBB","musicalKey":"F-Dur","language":"Deutsch","lyrics":"Der Mond ist aufgegangen","occasion":"Abendlied"})).await;
	let (status, body) = member.get(&format!("/api/lieder/{id}")).await;
	assert_eq!(status, 200, "{body}");
	let lied = &body["lied"];
	assert_eq!(lied["id"], id.to_string());
	assert_eq!(lied["arranger"], "Max Mustermann");
	assert_eq!(lied["musicalKey"], "F-Dur");
	assert_eq!(lied["lyricist"], "Matthias Claudius");
	assert_eq!(lied["voiceConfiguration"], "TTBB");
	assert_eq!(lied["occasion"], "Abendlied");
	assert_eq!(lied["lyrics"], "Der Mond ist aufgegangen");
	assert_eq!(lied["dateien"], json!([]));
	assert_eq!(lied["rowVersion"], 1);
	assert!(lied["createdAt"].is_string() && lied["updatedAt"].is_string());
	assert_eq!(
		keys(lied),
		[
			"arranger",
			"composer",
			"createdAt",
			"dateien",
			"id",
			"language",
			"lyricist",
			"lyrics",
			"musicalKey",
			"occasion",
			"rowVersion",
			"title",
			"updatedAt",
			"voiceConfiguration"
		]
	);
	let (status, list) = member.get("/api/lieder").await;
	assert_eq!(status, 200);
	assert_eq!(list["total"], 1);
	assert_eq!(list["query"], Value::Null);
	assert_eq!(list["pageSize"], 20);
	assert_eq!(list["lieder"][0]["id"], id.to_string());
	assert!(list["lieder"][0].get("lyrics").is_none());
	assert_eq!(
		keys(&list),
		[
			"filters", "lieder", "options", "page", "pageSize", "query", "total"
		]
	);
	assert_eq!(
		keys(&list["lieder"][0]),
		[
			"arranger",
			"composer",
			"dateiTypen",
			"id",
			"language",
			"lyricist",
			"matchIn",
			"musicalKey",
			"occasion",
			"title",
			"voiceConfiguration"
		]
	);
	assert_eq!(list["lieder"][0]["matchIn"], json!([]));
	assert_eq!(app.client().get("/api/lieder").await.0, 401);
	app.close().await;
}
