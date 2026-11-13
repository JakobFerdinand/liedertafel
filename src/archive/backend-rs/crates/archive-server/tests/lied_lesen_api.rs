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
