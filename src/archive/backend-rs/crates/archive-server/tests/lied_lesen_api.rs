#![allow(clippy::unwrap_used, clippy::expect_used)] // Authenticated HTTP assertions and generated fixtures.
mod support;
use archive_server::{
	Origin, Settings,
	auth::{Auth, AuthConfig},
};
use axum::{body::Body, http::Request};
use http_body_util::BodyExt;
use serde_json::{Value, json};
use std::sync::Arc;
use support::lesung;
use tower::ServiceExt;

fn configure(app: &mut support::App) {
	let mut origin = Origin::new(Settings::new(true, Default::default(), 0)).with_auth(
		Auth::new(
			app.pool.clone(),
			AuthConfig::development(),
			app.mail.clone(),
			app.time.clone(),
		)
		.unwrap(),
	);
	origin.reading_enabled = true;
	app.router = archive_server::build_router(Arc::new(origin));
}
async fn post(
	client: &support::Client,
	fields: &[(&str, &str)],
	bytes: &[u8],
) -> (axum::http::StatusCode, Value) {
	let mut body = Vec::new();
	for (key, value) in fields {
		body.extend_from_slice(
			format!(
				"--lesen230\r\nContent-Disposition: form-data; name=\"{key}\"\r\n\r\n{value}\r\n"
			)
			.as_bytes(),
		);
	}
	body.extend_from_slice(b"--lesen230\r\nContent-Disposition: form-data; name=\"datei\"; filename=\"a.pdf\"\r\nContent-Type: application/octet-stream\r\n\r\n");
	body.extend_from_slice(bytes);
	body.extend_from_slice(b"\r\n--lesen230--\r\n");
	raw(client, "multipart/form-data; boundary=lesen230", body).await
}
async fn raw(
	client: &support::Client,
	content: &str,
	body: Vec<u8>,
) -> (axum::http::StatusCode, Value) {
	let response = client
		.router
		.clone()
		.oneshot(
			Request::post("/api/lied-lesungen")
				.header("content-type", content)
				.header("X-CSRF-TOKEN", &client.token)
				.header(
					"cookie",
					client
						.cookies
						.iter()
						.map(|(k, v)| format!("{k}={v}"))
						.collect::<Vec<_>>()
						.join("; "),
				)
				.body(Body::from(body))
				.unwrap(),
		)
		.await
		.unwrap();
	let status = response.status();
	let bytes = response.into_body().collect().await.unwrap().to_bytes();
	(status, serde_json::from_slice(&bytes).unwrap())
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; form parser/native transport envelope and binding order"]
async fn whole_form_envelope_is_not_the_larger_reading_file_cap() {
	let mut app = support::App::new_scoped("lesen230").await;
	configure(&mut app);
	let client = app.admin().await;
	let (status, body) = post(
		&client,
		&[("entwurfId", "invalid"), ("anfrageId", "0")],
		&vec![b'x'; 30_000_000],
	)
	.await;
	assert_eq!(status, 413, "{body}");
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL + pinned native PDFium; scripted delivery below Gateway"]
async fn scanned_pdf_yields_only_form_suggestions_and_echoes_identity() {
	let mut app = support::App::new_scoped("lesen230").await;
	configure(&mut app);
	let client = app.admin().await;
	let (status, body) = post(
		&client,
		&[
			("entwurfId", "11111111-1111-1111-1111-111111111111"),
			("anfrageId", "3"),
		],
		&support::reading::scan(1),
	)
	.await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(body["entwurfId"], "11111111-1111-1111-1111-111111111111");
	assert_eq!(body["anfrageId"], 3);
	assert!(
		body["felder"]
			.as_array()
			.unwrap()
			.contains(&json!({"feld":"title","wert":"Waldfahrt","sicher":true}))
	);
	assert!(
		body["felder"]
			.as_array()
			.unwrap()
			.contains(&json!({"feld":"composer","wert":"Franz Schubert","sicher":true}))
	);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM lieder")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 0);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM lied_entwurf_lesungen")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; full requested scan batch must fit"]
async fn oversized_three_page_input_is_refused_not_silently_reduced_to_one_page() {
	let mut app = support::App::new_scoped("lesen230").await;
	let script = archive_server::reading::scripted_scan_reader();
	let mut options = archive_server::ai::Options::shipped(true);
	options.max_input_tokens = 4000;
	lesung::configure(
		&mut app,
		script.clone(),
		options,
		archive_server::reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let client = app.admin().await;
	let (status, _, body) = lesung::post(
		&client,
		&[
			("entwurfId", "11111111-1111-1111-1111-111111111111"),
			("anfrageId", "1"),
		],
		&[("datei", &support::reading::scan(3))],
	)
	.await;
	assert_eq!(status, 422, "{body}");
	assert!(script.requests().is_empty());
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; embedded text, analyzer priority, uncertain/quote/duplicate fields"]
async fn text_pdf_uses_the_analyzer_and_keeps_only_verified_requested_form_fields() {
	let mut app = support::App::new_scoped("lesen230").await;
	let answer = json!({"felder":[
	{"feld":"title","wert":"Ave Maria","sicherheit":" unsicher ","zitat":"Ave Maria"},
	{"feld":"composer","wert":"Invented composer","sicherheit":"sicher","zitat":"Musik: Anton Bruckner"},
	{"feld":"language","wert":"Latein","sicherheit":"sicher","zitat":"steht nirgends im Text"},
	{"feld":"voice_configuration","wert":"","sicherheit":"sicher","zitat":"Ave Maria"},
	{"feld":"voice_configuration","wert":"TTBB","sicherheit":"sicher","zitat":"Ave Maria"},
	{"feld":"voice_label","wert":"Tenor I","sicherheit":"sicher","zitat":"Tenor I"},
	{"feld":"lyrics","wert":"Maria voll der Gnade gegruesst seist du\nErfundene zweite Zeile","sicherheit":"sicher","zitat":"Maria voll der Gnade gegruesst seist du"}
	]});
	let script =
		archive_server::ai::Script::new(vec![vec![archive_server::ai::ScriptStep::Update(
			archive_server::ai::Update {
				text: answer.to_string(),
				usage: archive_server::ai::Usage::reported(100, 20),
				..Default::default()
			},
		)]]);
	lesung::configure(
		&mut app,
		script.clone(),
		archive_server::ai::Options::shipped(true),
		archive_server::reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let client = app.admin().await;
	let pdf = lesung::text_pdf(&[
		"Ave Maria",
		"Musik: Anton Bruckner",
		"Satz: Franz Bearbeiter",
		"Text: Anna Beispiel",
		"G-Dur",
		"Tenor I",
		"Maria voll der Gnade gegruesst seist du",
	]);
	let (status, headers, body) = lesung::post(
		&client,
		&[
			("entwurfId", "11111111-1111-1111-1111-111111111111"),
			("anfrageId", "3"),
		],
		&[("datei", &pdf)],
	)
	.await;
	assert_eq!(status, 200, "{body}");
	assert_eq!(headers["cache-control"], "no-store");
	assert_eq!(
		body["felder"],
		json!([
 {"feld":"title","wert":"Ave Maria","sicher":false},
 {"feld":"composer","wert":"Anton Bruckner","sicher":true},
 {"feld":"arranger","wert":"Franz Bearbeiter","sicher":true},
 {"feld":"lyricist","wert":"Anna Beispiel","sicher":true},
 {"feld":"musicalKey","wert":"G-Dur","sicher":true}])
	);
	assert_eq!(
		script.requests().len(),
		1,
		"embedded text must not transcribe images"
	);
	assert!(script.requests()[0].messages.iter().flat_map(|m|&m.contents).any(|c|matches!(c,archive_server::ai::Content::Text(t) if t.starts_with("Gesuchte Felder: title, voice_configuration, language, lyrics\n"))));
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; current editor membership, CSRF, form identity/defaults/validation order"]
async fn identity_form_order_roles_csrf_and_first_file_follow_the_frozen_endpoint() {
	let mut app = support::App::new_scoped("lesen230").await;
	let options = archive_server::reading::Options {
		enabled: false,
		max_pdf_bytes: 10,
		..Default::default()
	};
	lesung::configure(
		&mut app,
		archive_server::reading::scripted_scan_reader(),
		archive_server::ai::Options::shipped(true),
		options,
		Default::default(),
	);
	let mut editor = app.admin().await;
	let member = app.member().await;
	let mut anonymous = app.client();
	anonymous.csrf().await;
	let fields = [
		("entwurfId", "11111111-1111-1111-1111-111111111111"),
		("anfrageId", "1"),
	];
	assert_eq!(lesung::post(&anonymous, &fields, &[]).await.0, 401);
	assert_eq!(lesung::post(&member, &fields, &[]).await.0, 403);
	let token = std::mem::take(&mut editor.token);
	assert_eq!(lesung::post(&editor, &fields, &[]).await.0, 400);
	editor.token = token;
	assert_eq!(
		lesung::raw(&editor, "application/json", b"{}".to_vec())
			.await
			.0,
		400
	);
	assert_eq!(
		lesung::post(
			&editor,
			&[("entwurfId", "invalid"), ("anfrageId", "1")],
			&[]
		)
		.await
		.0,
		400
	);
	for number in [
		"",
		"0",
		"-1",
		"2147483648",
		"1.0",
		"1e0",
		"1_0",
		"\u{a0}1",
		"1\u{a0}",
		"++1",
	] {
		assert_eq!(
			lesung::post(
				&editor,
				&[("entwurfId", fields[0].1), ("anfrageId", number)],
				&[]
			)
			.await
			.0,
			400,
			"{number:?}"
		);
	}
	for number in ["1", " +0001 ", "\t2147483647\r\n", "1\0"] {
		assert_eq!(
			lesung::post(
				&editor,
				&[
					("ENTWURFID", " {11111111-1111-1111-1111-111111111111} "),
					("ANFRAGEID", number)
				],
				&[]
			)
			.await
			.0,
			409,
			"{number:?}"
		);
	}
	assert_eq!(
		lesung::post(
			&editor,
			&[
				("entwurfId", fields[0].1),
				("anfrageId", ""),
				("ANFRAGEID", "1")
			],
			&[]
		)
		.await
		.0,
		409
	);
	assert_eq!(
		lesung::post(
			&editor,
			&[
				("entwurfId", fields[0].1),
				("anfrageId", "1"),
				("ANFRAGEID", "2")
			],
			&[]
		)
		.await
		.0,
		400
	);
	// Disabled precedes missing/signature/length; enabling restores domain checks.
	assert_eq!(lesung::post(&editor, &fields, &[]).await.0, 409);
	lesung::configure(
		&mut app,
		archive_server::reading::scripted_scan_reader(),
		archive_server::ai::Options::shipped(true),
		archive_server::reading::Options {
			enabled: true,
			max_pdf_bytes: 10,
			..Default::default()
		},
		Default::default(),
	);
	editor.router = app.router.clone();
	assert_eq!(
		lesung::raw(
			&editor,
			"application/x-www-form-urlencoded",
			b"entwurfId=11111111-1111-1111-1111-111111111111&anfrageId=1".to_vec()
		)
		.await
		.0,
		422
	);
	for files in [
		vec![],
		vec![("datei", b"".as_slice())],
		vec![("datei", b"not pdf".as_slice())],
		vec![("datei", b"%PDF-012345".as_slice())],
		vec![
			("DATEI", b"bad".as_slice()),
			("datei", b"%PDF-1".as_slice()),
		],
	] {
		assert_eq!(lesung::post(&editor, &fields, &files).await.0, 422);
	}
	// Membership is re-read, not cached by a previous successful request/session.
	sqlx::query(r#"DELETE FROM "AspNetUserRoles" WHERE "UserId"=(SELECT "Id" FROM "AspNetUsers" WHERE "Email"='verwaltung@liedertafel.test')"#).execute(&app.pool).await.unwrap();
	sqlx::query(r#"INSERT INTO "AspNetUserRoles" SELECT u."Id",r."Id" FROM "AspNetUsers" u CROSS JOIN "AspNetRoles" r WHERE u."Email"='verwaltung@liedertafel.test' AND r."Name"='Member'"#).execute(&app.pool).await.unwrap();
	assert_eq!(lesung::post(&editor, &fields, &[]).await.0, 403);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; actual .NET150 form observations' encoded-NUL distinction"]
async fn urlencoded_null_is_rejected_by_form_decoding_but_multipart_keeps_int32_legacy_tail() {
	let mut app = support::App::new_scoped("lesen230").await;
	configure(&mut app);
	let client = app.admin().await;
	let (status, _, body) = lesung::raw(
		&client,
		"application/x-www-form-urlencoded",
		b"entwurfId=11111111-1111-1111-1111-111111111111&anfrageId=1%00".to_vec(),
	)
	.await;
	assert_eq!(status, 400, "{body}");
	for number in ["1\0", "1 \0", "1\t\0"] {
		assert_eq!(
			lesung::post(
				&client,
				&[
					("entwurfId", "11111111-1111-1111-1111-111111111111"),
					("anfrageId", number)
				],
				&[]
			)
			.await
			.0,
			422
		);
	}
	for number in ["1\0 ", "1\0\t"] {
		assert_eq!(
			lesung::post(
				&client,
				&[
					("entwurfId", "11111111-1111-1111-1111-111111111111"),
					("anfrageId", number)
				],
				&[]
			)
			.await
			.0,
			400
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; frozen form boundary/header bounds before identity/domain validation"]
async fn multipart_boundary_and_section_header_limits_are_enforced() {
	let mut app = support::App::new_scoped("lesen230").await;
	configure(&mut app);
	let client = app.admin().await;
	let fields = [
		("entwurfId", "11111111-1111-1111-1111-111111111111"),
		("anfrageId", "1"),
	];
	for length in [128, 129] {
		let boundary = "b".repeat(length);
		let body = String::from_utf8(lesung::multipart(&fields, &[]))
			.unwrap()
			.replace("lesen230", &boundary)
			.into_bytes();
		assert_eq!(
			lesung::raw(
				&client,
				&format!("multipart/form-data; boundary=\"{boundary}\""),
				body
			)
			.await
			.0,
			if length == 128 { 422 } else { 400 }
		);
	}
	let body = String::from_utf8(lesung::multipart(&fields, &[]))
		.unwrap()
		.replacen(
			"Content-Disposition:",
			&format!(
				"{}Content-Disposition:",
				(0..16)
					.map(|i| format!("X-Header-{i}: fixture\r\n"))
					.collect::<String>()
			),
			1,
		)
		.into_bytes();
	assert_eq!(
		lesung::raw(&client, "multipart/form-data; boundary=lesen230", body)
			.await
			.0,
		400
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; accepted actual BCL139-case Guid-string matrix at the form HTTP seam"]
async fn required_form_guid_uses_the_shared_clr_string_grammar_not_typed_json() {
	let mut app = support::App::new_scoped("lesen230").await;
	configure(&mut app);
	let client = app.admin().await;
	let rows: Vec<(String, Option<String>)> =
		serde_json::from_str(include_str!("../../../tests/fixtures/guid-bcl.json")).unwrap();
	for (raw, expected) in rows {
		assert_eq!(
			lesung::post(&client, &[("entwurfId", &raw), ("anfrageId", "1")], &[])
				.await
				.0,
			if expected.is_some() { 422 } else { 400 },
			"{raw:?}"
		);
	}
	app.close().await;
}
