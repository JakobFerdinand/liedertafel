#![allow(clippy::unwrap_used, clippy::expect_used)] // Raw HTTP parity assertions.
mod support;
use archive_server::{ai, reading};
use serde_json::json;
use support::programme::*;

#[tokio::test]
#[ignore = "explicit real PostgreSQL; programme HTTP + gateway + ledger"]
async fn photo_suggests_headers_matches_catalogue_echoes_identity_without_creating_records() {
	let mut app = support::App::new_scoped("programme231").await;
	let ave = support::catalogue::lied(&app, "Ave Maria", json!({})).await;
	let gateway = configure(
		&mut app,
		reading::scripted_scan_reader(),
		enabled(),
		ai::Options::shipped(true),
	);
	let client = app.admin().await;
	let (status, body) = post(&client, &photo()).await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(body["entwurfId"], "44444444-4444-4444-4444-444444444444");
	assert_eq!(body["anfrageId"], 2);
	assert_eq!(
		body["felder"][0],
		json!({"feld":"title","wert":"Sängerfest der Liedertafel Mining","sicher":true})
	);
	assert_eq!(body["programm"][0]["lied"]["id"], ave.to_string());
	assert_eq!(body["programm"][0]["lied"]["sicher"], true);
	assert_eq!(body["programm"][2]["lied"], json!(null));
	let mut member = app.member().await;
	assert_eq!(member.get("/api/events").await.1["events"], json!([]));
	assert_eq!(
		member.get("/api/lieder").await.1["lieder"]
			.as_array()
			.unwrap()
			.len(),
		1
	);
	assert!(gateway.drain().await);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL + pinned PDFium; embedded text first and bounded programme scans"]
async fn text_pdf_uses_one_fields_call_and_scan_uses_its_first_four_pages_with_exact_model_contract()
 {
	use archive_server::ai::Content;
	let mut app = support::App::new_scoped("programme231").await;
	let script = reading::scripted_scan_reader();
	let mut options = enabled();
	options.images_per_call = 10;
	options.max_image_long_side = 400;
	let gateway = configure(
		&mut app,
		script.clone(),
		options,
		ai::Options::shipped(true),
	);
	let client = app.admin().await;
	let (status,body)=post(&client,&text_pdf("Fruehjahrskonzert der Liedertafel Mining\nSamstag, 12. Mai 1951\nOrt: Gasthaus zur Post, Mining\n1. Ave Maria\n2. Der Mond ist aufgegangen\n3. Waldfahrt")).await;
	assert_eq!(status, 200, "{body}");
	let fields = body["felder"].as_array().unwrap();
	for (name, want) in [
		("title", "Fruehjahrskonzert der Liedertafel Mining"),
		("kind", "concert"),
		("jahr", "1951"),
		("monat", "5"),
		("tag", "12"),
		("venue", "Gasthaus zur Post, Mining"),
	] {
		assert!(
			fields
				.iter()
				.any(|f| f["feld"] == name && f["wert"] == want),
			"{body}"
		);
	}
	assert_eq!(script.requests().len(), 1);
	let (status, body) = post(&client, &support::reading::scan(8)).await;
	assert_eq!(status, 200, "{body}");
	let requests = script.requests();
	assert_eq!(requests.len(), 3);
	let pages = requests[1]
		.messages
		.iter()
		.flat_map(|m| &m.contents)
		.filter(|c| matches!(c, Content::ImageUrl(_)))
		.count();
	assert_eq!(pages, 4);
	let oracle: serde_json::Value = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-oracle.json"
	))
	.unwrap();
	for (request, row) in [(&requests[0], &oracle[0]), (&requests[1], &oracle[1])] {
		assert_eq!(
			request.options.instructions.as_deref(),
			row["instructions"].as_str()
		);
		assert_eq!(
			request.options.max_output_tokens.map(i64::from),
			row["maxOutputTokens"].as_i64()
		);
	}
	assert_eq!(
		serde_json::from_str::<serde_json::Value>(
			requests[0]
				.options
				.response_format
				.as_ref()
				.unwrap()
				.schema
				.get()
		)
		.unwrap(),
		oracle[0]["schema"]
	);
	let accounts: Vec<(String, Option<uuid::Uuid>)> =
		sqlx::query_as(r#"SELECT "Feature","AccountId" FROM ai_usage_entries"#)
			.fetch_all(&app.pool)
			.await
			.unwrap();
	assert_eq!(
		accounts
			.iter()
			.filter(|(f, _)| f == "programm-fields")
			.count(),
		2
	);
	assert_eq!(
		accounts
			.iter()
			.filter(|(f, _)| f == "programm-transcription")
			.count(),
		1
	);
	assert!(accounts.iter().all(|(_, a)| a.is_some()));
	assert!(gateway.drain().await);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; frozen quote, duplicate-field, uncertainty, calendar and entry limits"]
async fn invented_values_drop_uncertain_values_stay_and_header_dependencies_and_sixty_accepted_entries_hold()
 {
	let mut app = support::App::new_scoped("programme231").await;
	let ave = support::catalogue::lied(&app, "Ave Maria", json!({})).await;
	let mut entries = vec![
		json!({"titel":"Erfunden","sicherheit":"sicher","zitat":"Steht nicht im Programm"});
		65
	];
	entries.extend(
		(0..65).map(
			|_| json!({"titel":"1. Ave Maria","sicherheit":"unsicher","zitat":"1. Ave Maria"}),
		),
	);
	let fields = json!({"felder":[
		field("title","Erfunden","Konzert"),field("title","Konzert","Konzert"),
		field("kind","concert","Konzert"),field("year","2000","29. Februar 2000"),
		field("month","2","29. Februar 2000"),field("day","29","29. Februar 2000"),
		field("approximate","ja","um 2000"),field("venue","Mining","Ort: Mining"),
		field("composer","Erfunden","Konzert")],"punkte":entries});
	let gateway = configure(
		&mut app,
		custom(
			"Konzert\n29. Februar 2000\num 2000\nOrt: Mining\n1. Ave Maria",
			fields,
		),
		enabled(),
		ai::Options::shipped(true),
	);
	let mut client = app.admin().await;
	let (status, body) = post(&client, &photo()).await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(
		body["felder"]
			.as_array()
			.unwrap()
			.iter()
			.map(|f| f["feld"].as_str().unwrap())
			.collect::<Vec<_>>(),
		vec!["kind", "jahr", "monat", "tag", "unsicher", "venue"]
	);
	assert_eq!(body["programm"].as_array().unwrap().len(), 60);
	assert!(
		body["programm"]
			.as_array()
			.unwrap()
			.iter()
			.all(|p| p["text"] == "Ave Maria"
				&& p["sicher"] == false
				&& p["lied"]["id"] == ave.to_string()
				&& p["lied"]["sicher"] == true)
	);
	assert!(gateway.drain().await);
	for (text, fields, wanted) in [
		(
			"𝟙05.2000",
			vec![
				field("year", "2000", "𝟙05.2000"),
				field("month", "5", "𝟙05.2000"),
			],
			vec!["jahr", "monat"],
		),
		(
			"𝟙12.05.2000",
			vec![
				field("year", "2000", "𝟙12.05.2000"),
				field("month", "5", "𝟙12.05.2000"),
				field("day", "12", "𝟙12.05.2000"),
			],
			vec!["jahr", "monat", "tag"],
		),
		(
			"Konzert",
			vec![field("kind", "con\u{ad}cert", "Konzert")],
			vec![],
		),
		(
			"31. April 2001",
			vec![
				field("year", "2001", "31. April 2001"),
				field("month", "4", "31. April 2001"),
				field("day", "31", "31. April 2001"),
			],
			vec!["jahr", "monat"],
		),
		(
			"29. Februar 1900",
			vec![
				field("year", "1900", "29. Februar 1900"),
				field("month", "2", "29. Februar 1900"),
				field("day", "29", "29. Februar 1900"),
			],
			vec!["jahr", "monat"],
		),
		(
			"12. Mai 1951",
			vec![
				field("year", "1950", "12. Mai 1951"),
				field("month", "5", "12. Mai 1951"),
				field("day", "12", "12. Mai 1951"),
			],
			vec![],
		),
		(
			"11. Mai 1951",
			vec![
				field("year", "1951", "11. Mai 1951"),
				field("month", "5", "11. Mai 1951"),
				field("day", "1", "11. Mai 1951"),
			],
			vec!["jahr", "monat"],
		),
		(
			"Dezember 1938",
			vec![
				field("year", "1938", "Dezember 1938"),
				field("month", "5", "Dezember 1938"),
				field("day", "12", "Dezember 1938"),
			],
			vec!["jahr"],
		),
		(
			"um 1950",
			vec![
				field("year", "1950", "um 1950"),
				field("approximate", "ja", "um 1950"),
			],
			vec!["jahr", "unsicher"],
		),
	] {
		let gateway = configure(
			&mut app,
			custom(text, json!({"felder":fields,"punkte":null})),
			enabled(),
			ai::Options::shipped(true),
		);
		client.router = app.router.clone();
		let (status, body) = post(&client, &photo()).await;
		assert_eq!(status, 200, "{body}");
		assert_eq!(
			body["felder"]
				.as_array()
				.unwrap()
				.iter()
				.map(|f| f["feld"].as_str().unwrap())
				.collect::<Vec<_>>(),
			wanted,
			"{body}"
		);
		assert!(gateway.drain().await);
	}
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; actual frozen JPEG/PNG/WebP and EXIF/rounding controls"]
async fn accepted_photos_become_bounded_jpeg_with_only_the_source_exif_rotations() {
	use base64::Engine;
	let mut app = support::App::new_scoped("programme231").await;
	let oracle: serde_json::Value = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-oracle.json"
	))
	.unwrap();
	let script = reading::scripted_scan_reader();
	let mut options = enabled();
	options.max_image_long_side = 10;
	let gateway = configure(
		&mut app,
		script.clone(),
		options,
		ai::Options::shipped(true),
	);
	let client = app.admin().await;
	for row in oracle
		.as_array()
		.unwrap()
		.iter()
		.filter(|r| r["kind"] == "photo")
	{
		let bytes = base64::engine::general_purpose::STANDARD
			.decode(row["bytes"].as_str().unwrap())
			.unwrap();
		let (status, body) = post(&client, &bytes).await;
		assert_eq!(status, 200, "{body}");
		let requests = script.requests();
		let encoded = requests[requests.len() - 2]
			.messages
			.iter()
			.flat_map(|m| &m.contents)
			.find_map(|c| {
				if let ai::Content::ImageUrl(url) = c {
					Some(url)
				} else {
					None
				}
			})
			.unwrap();
		let jpeg = base64::engine::general_purpose::STANDARD
			.decode(encoded.strip_prefix("data:image/jpeg;base64,").unwrap())
			.unwrap();
		let image = image::load_from_memory(&jpeg).unwrap().to_rgb8();
		assert_eq!(
			u64::from(image.width()),
			row["width"].as_u64().unwrap(),
			"format={} orientation={}",
			row["format"],
			row["origin"]
		);
		assert_eq!(u64::from(image.height()), row["height"].as_u64().unwrap());
		for (field, pixel) in [
			("topLeft", image.get_pixel(0, 0)),
			(
				"bottomRight",
				image.get_pixel(image.width() - 1, image.height() - 1),
			),
		] {
			let expected = base64::engine::general_purpose::STANDARD
				.decode(row[field].as_str().unwrap())
				.unwrap();
			// Separate JPEG implementations need not have byte-identical quantization.
			for (found, want) in pixel.0.into_iter().zip(expected) {
				assert!(
					found.abs_diff(want) <= 32,
					"{field}: format={} orientation={}",
					row["format"],
					row["origin"]
				);
			}
		}
	}
	assert!(gateway.drain().await);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; auth/CSRF/binding/Enabled/material/budget/input failure order"]
async fn validation_and_spending_refusals_leave_the_form_manual_and_never_invoke_delivery() {
	let mut app = support::App::new_scoped("programme231").await;
	let script = reading::scripted_scan_reader();
	let gateway = configure(
		&mut app,
		script.clone(),
		reading::Options::default(),
		ai::Options::shipped(true),
	);
	let mut client = app.admin().await;
	let member = app.member().await;
	assert_eq!(post(&member, &photo()).await.0, 403);
	let saved = client.token.clone();
	client.token.clear();
	assert_eq!(post(&client, &photo()).await.0, 400);
	client.token = saved;
	assert_eq!(
		wire(&client, "application/json", b"{}".to_vec()).await.0,
		400
	);
	let bad = body(&[("entwurfId", "kein-guid"), ("anfrageId", "1")], &[]);
	assert_eq!(
		wire(&client, "multipart/form-data; boundary=programme231", bad)
			.await
			.0,
		400
	);
	assert_eq!(post(&client, &[]).await.0, 409);
	assert!(script.requests().is_empty());
	assert!(gateway.drain().await);
	let ids = [
		("entwurfId", "44444444-4444-4444-4444-444444444444"),
		("anfrageId", "2"),
	];
	for (reading, ai, want, bytes) in [
		(
			enabled(),
			ai::Options::shipped(true),
			422,
			b"kein programm".to_vec(),
		),
		(
			reading::Options {
				max_pdf_bytes: 10,
				..enabled()
			},
			ai::Options::shipped(true),
			422,
			photo(),
		),
		(
			enabled(),
			ai::Options {
				monthly_cap_eur: rust_decimal::Decimal::new(1, 6),
				..ai::Options::shipped(true)
			},
			429,
			photo(),
		),
		(
			enabled(),
			ai::Options {
				max_input_tokens: 10,
				..ai::Options::shipped(true)
			},
			422,
			photo(),
		),
	] {
		let script = reading::scripted_scan_reader();
		let gateway = configure(&mut app, script.clone(), reading, ai);
		client.router = app.router.clone();
		let (status, body) = post(&client, &bytes).await;
		assert_eq!(status.as_u16(), want, "{body}");
		assert!(script.requests().is_empty());
		assert!(gateway.drain().await);
	}
	let script = reading::scripted_scan_reader();
	let gateway = configure(
		&mut app,
		script.clone(),
		enabled(),
		ai::Options::shipped(true),
	);
	client.router = app.router.clone();
	assert_eq!(
		wire(
			&client,
			"application/x-www-form-urlencoded",
			b"entwurfId=44444444444444444444444444444444&anfrageId=%2B0002".to_vec()
		)
		.await
		.0,
		422
	);
	for number in ["0", "-1", "2147483648", "1.0", "1e0", "1_0", "\u{a0}2"] {
		assert_eq!(
			wire(
				&client,
				"multipart/form-data; boundary=programme231",
				body(
					&[("entwurfId", ids[0].1), ("anfrageId", number)],
					&[("datei", &photo())]
				)
			)
			.await
			.0,
			400,
			"{number}"
		);
	}
	let joined = body(
		&[
			("entwurfId", ids[0].1),
			("ANFRAGEID", "2"),
			("anfrageId", "3"),
		],
		&[("datei", &photo())],
	);
	assert_eq!(
		wire(
			&client,
			"multipart/form-data; boundary=programme231",
			joined
		)
		.await
		.0,
		400
	);
	let first = body(&ids, &[("DATEI", b"kein programm"), ("datei", &photo())]);
	assert_eq!(
		wire(&client, "multipart/form-data; boundary=programme231", first)
			.await
			.0,
		422
	);
	assert!(script.requests().is_empty());
	assert!(gateway.drain().await);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; actual BCL normalization exceptions become safe reader failures"]
async fn invalid_normalization_is_a_failed_read_not_a_silent_match_or_quote_repair() {
	let mut app = support::App::new_scoped("programme231").await;
	let fields = json!({"felder":[],"punkte":[]});
	let gateway = configure(
		&mut app,
		custom("Konzert\u{fffe}", fields),
		enabled(),
		ai::Options::shipped(true),
	);
	let client = app.admin().await;
	let (status, body) = post(&client, &photo()).await;
	assert_eq!(status, 502, "{body}");
	assert_eq!(
		body["title"],
		"Das Lesen ist gescheitert. Bitte erneut versuchen."
	);
	assert!(!body.to_string().contains("Konzert"));
	assert!(gateway.drain().await);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; source all-Lieder projection includes saved trashed titles"]
async fn programme_matches_saved_trash_without_changing_member_visibility_or_writing_catalogue() {
	let mut app = support::App::new_scoped("programme231").await;
	let trashed = support::catalogue::lied(&app, "Ave Maria", json!({})).await;
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now(),"DeletedByAccountId"=$2 WHERE "Id"=$1"#)
		.bind(trashed)
		.bind(uuid::Uuid::nil())
		.execute(&app.pool)
		.await
		.unwrap();
	let gateway = configure(
		&mut app,
		reading::scripted_scan_reader(),
		enabled(),
		ai::Options::shipped(true),
	);
	let mut client = app.admin().await;
	let (status, body) = post(&client, &photo()).await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(body["programm"][0]["lied"]["id"], trashed.to_string());
	assert_eq!(body["programm"][0]["lied"]["sicher"], true);
	assert_eq!(client.get("/api/lieder").await.1["lieder"], json!([]));
	let live = support::catalogue::lied(&app, "Ave Maria", json!({})).await;
	let (status, body) = post(&client, &photo()).await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(body["programm"][0]["lied"], json!(null));
	let ids = body["programm"][0]["kandidaten"]
		.as_array()
		.unwrap()
		.iter()
		.map(|v| v["id"].as_str().unwrap())
		.collect::<Vec<_>>();
	assert!(
		ids.contains(&trashed.to_string().as_str()) && ids.contains(&live.to_string().as_str())
	);
	assert_eq!(
		client.get("/api/lieder").await.1["lieder"]
			.as_array()
			.unwrap()
			.len(),
		1
	);
	assert!(gateway.drain().await);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; source regex numbers are UTF-16/BMP decimal digits"]
async fn entry_numbering_keeps_supplementary_digits_but_strips_bmp_decimal_digits() {
	let mut app = support::App::new_scoped("programme231").await;
	let script = custom(
		"Konzert\n𝟙. Ave Maria\n１. Ave Maria",
		json!({"felder":[],"punkte":[{"titel":"𝟙. Ave Maria","sicherheit":"sicher","zitat":"𝟙. Ave Maria"},{"titel":"１. Ave Maria","sicherheit":"sicher","zitat":"１. Ave Maria"}]}),
	);
	let gateway = configure(&mut app, script, enabled(), ai::Options::shipped(true));
	let client = app.admin().await;
	let (status, body) = post(&client, &photo()).await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(body["programm"][0]["text"], "𝟙. Ave Maria");
	assert_eq!(body["programm"][1]["text"], "Ave Maria");
	assert!(gateway.drain().await);
	app.close().await;
}
