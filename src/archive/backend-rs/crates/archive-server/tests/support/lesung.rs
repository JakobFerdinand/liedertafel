// Shared HTTP-only generated fixtures; each suite uses a subset.
#![allow(dead_code, clippy::unwrap_used, clippy::expect_used)]
use archive_server::{
	Origin, Settings, ai,
	auth::{Auth, AuthConfig},
	reading,
};
use axum::{
	body::Body,
	http::{HeaderMap, Request, StatusCode},
};
use http_body_util::BodyExt;
use serde_json::Value;
use std::sync::Arc;
use tower::ServiceExt;

pub fn configure(
	app: &mut super::App,
	script: ai::Script,
	options: ai::Options,
	reading: reading::Options,
	extraction: archive_server::extraction::Options,
) -> Arc<Origin> {
	let mut origin = Origin::new(Settings::new(true, Default::default(), 0)).with_auth(
		Auth::new(
			app.pool.clone(),
			AuthConfig::development(),
			app.mail.clone(),
			app.time.clone(),
		)
		.unwrap(),
	);
	origin.reading_enabled = reading.enabled;
	origin.reading = reading;
	origin.extraction = extraction;
	let (_, rx) = tokio::sync::watch::channel(options);
	let ledger = ai::Ledger::new(
		Arc::new(ai::PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	origin.ai = Some(ai::Services {
		gateway: ai::Gateway::scripted(ledger.clone(), script),
		ledger,
	});
	let origin = Arc::new(origin);
	app.router = archive_server::build_router(origin.clone());
	origin
}
pub fn multipart(fields: &[(&str, &str)], files: &[(&str, &[u8])]) -> Vec<u8> {
	let mut body = Vec::new();
	for (key, value) in fields {
		body.extend_from_slice(
			format!(
				"--lesen230\r\nContent-Disposition: form-data; name=\"{key}\"\r\n\r\n{value}\r\n"
			)
			.as_bytes(),
		);
	}
	for (key, bytes) in files {
		body.extend_from_slice(format!("--lesen230\r\nContent-Disposition: form-data; name=\"{key}\"; filename=\"not-a-pdf.txt\"\r\nContent-Type: application/octet-stream\r\n\r\n").as_bytes());
		body.extend_from_slice(bytes);
		body.extend_from_slice(b"\r\n");
	}
	body.extend_from_slice(b"--lesen230--\r\n");
	body
}
pub fn cookie(client: &super::Client) -> String {
	client
		.cookies
		.iter()
		.map(|(k, v)| format!("{k}={v}"))
		.collect::<Vec<_>>()
		.join("; ")
}
pub async fn raw(
	client: &super::Client,
	content: &str,
	body: Vec<u8>,
) -> (StatusCode, HeaderMap, Value) {
	let response = client
		.router
		.clone()
		.oneshot(
			Request::post("/api/lied-lesungen")
				.header("content-type", content)
				.header("X-CSRF-TOKEN", &client.token)
				.header("cookie", cookie(client))
				.body(Body::from(body))
				.unwrap(),
		)
		.await
		.unwrap();
	let status = response.status();
	let headers = response.headers().clone();
	let bytes = response.into_body().collect().await.unwrap().to_bytes();
	(status, headers, serde_json::from_slice(&bytes).unwrap())
}
pub async fn post(
	client: &super::Client,
	fields: &[(&str, &str)],
	files: &[(&str, &[u8])],
) -> (StatusCode, HeaderMap, Value) {
	raw(
		client,
		"multipart/form-data; boundary=lesen230",
		multipart(fields, files),
	)
	.await
}
/// Public ASCII text PDF, independent of the scan delivery script. Single page
/// with separate text operators/positions, no proprietary/native fixture inputs.
pub fn text_pdf(lines: &[&str]) -> Vec<u8> {
	let stream = format!(
		"BT /F1 12 Tf 30 700 Td {} ET",
		lines
			.iter()
			.enumerate()
			.map(|(n, line)| format!(
				"{} ({}) Tj",
				if n == 0 { "" } else { "0 -20 Td" },
				line.replace('\\', "\\\\")
					.replace('(', "\\(")
					.replace(')', "\\)")
			))
			.collect::<Vec<_>>()
			.join(" ")
	);
	let objects=["<< /Type /Catalog /Pages 2 0 R >>".into(),"<< /Type /Pages /Count 1 /Kids [4 0 R] >>".into(),"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".into(),"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>".into(),format!("<< /Length {} >>\nstream\n{stream}\nendstream",stream.len())];
	let mut result = b"%PDF-1.4\n".to_vec();
	let mut offsets = vec![0];
	for (n, object) in objects.iter().enumerate() {
		offsets.push(result.len());
		result.extend_from_slice(format!("{} 0 obj\n{object}\nendobj\n", n + 1).as_bytes());
	}
	let xref = result.len();
	result
		.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", offsets.len()).as_bytes());
	for offset in offsets.iter().skip(1) {
		result.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
	}
	result.extend_from_slice(
		format!(
			"trailer << /Size {} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF",
			offsets.len()
		)
		.as_bytes(),
	);
	result
}
