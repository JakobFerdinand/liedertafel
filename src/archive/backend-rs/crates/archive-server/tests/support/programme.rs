#![allow(dead_code)] // Shared external HTTP fixture for independent programme suites.
use archive_server::{
	Origin, Settings, ai,
	auth::{Auth, AuthConfig},
	reading,
};
use axum::{
	body::Body,
	http::{Request, StatusCode},
};
use http_body_util::BodyExt;
use serde_json::{Value, json};
use std::sync::Arc;
use tower::ServiceExt;

pub fn configure(
	app: &mut super::App,
	script: ai::Script,
	reading: reading::Options,
	options: ai::Options,
) -> ai::Gateway {
	configure_with_shutdown(app, script, reading, options).0
}
pub fn configure_with_shutdown(
	app: &mut super::App,
	script: ai::Script,
	reading: reading::Options,
	options: ai::Options,
) -> (ai::Gateway, tokio_util::sync::CancellationToken) {
	let (_, rx) = tokio::sync::watch::channel(options);
	let ledger = ai::Ledger::new(
		Arc::new(ai::PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let gateway = ai::Gateway::scripted(ledger.clone(), script);
	let mut origin = Origin::new(Settings::new(true, Default::default(), 0));
	origin.reading_enabled = reading.enabled;
	origin.reading = reading;
	origin.ai = Some(ai::Services {
		gateway: gateway.clone(),
		ledger,
	});
	let shutdown = origin.shutdown.clone();
	let auth = Auth::new(
		app.pool.clone(),
		AuthConfig::development(),
		app.mail.clone(),
		app.time.clone(),
	)
	.unwrap();
	app.router = archive_server::build_router(Arc::new(origin.with_auth(auth)));
	(gateway, shutdown)
}
pub fn enabled() -> reading::Options {
	reading::Options {
		enabled: true,
		..Default::default()
	}
}
pub fn photo() -> Vec<u8> {
	let image = image::RgbImage::from_pixel(80, 120, image::Rgb([40, 80, 120]));
	let mut bytes = vec![];
	image::codecs::jpeg::JpegEncoder::new(&mut bytes)
		.encode_image(&image)
		.unwrap();
	bytes
}
pub fn body(fields: &[(&str, &str)], files: &[(&str, &[u8])]) -> Vec<u8> {
	let mut bytes = vec![];
	for (key, value) in fields {
		bytes.extend_from_slice(
			format!(
				"--programme231\r\nContent-Disposition: form-data; name=\"{key}\"\r\n\r\n{value}\r\n"
			)
			.as_bytes(),
		);
	}
	for (key, file) in files {
		bytes.extend_from_slice(format!("--programme231\r\nContent-Disposition: form-data; name=\"{key}\"; filename=\"p.bin\"\r\nContent-Type: application/octet-stream\r\n\r\n").as_bytes());
		bytes.extend_from_slice(file);
		bytes.extend_from_slice(b"\r\n");
	}
	bytes.extend_from_slice(b"--programme231--\r\n");
	bytes
}
pub async fn post(client: &super::Client, file: &[u8]) -> (StatusCode, Value) {
	wire(
		client,
		"multipart/form-data; boundary=programme231",
		body(
			&[
				("entwurfId", "44444444-4444-4444-4444-444444444444"),
				("anfrageId", "2"),
			],
			&[("datei", file)],
		),
	)
	.await
}
pub async fn wire(
	client: &super::Client,
	content_type: &str,
	body: Vec<u8>,
) -> (StatusCode, Value) {
	let req = Request::builder()
		.method("POST")
		.uri("/api/auftritt-lesungen")
		.header(
			"cookie",
			client
				.cookies
				.iter()
				.map(|(k, v)| format!("{k}={v}"))
				.collect::<Vec<_>>()
				.join("; "),
		)
		.header("X-CSRF-TOKEN", &client.token)
		.header("content-type", content_type)
		.body(Body::from(body))
		.unwrap();
	let response = client.router.clone().oneshot(req).await.unwrap();
	let status = response.status();
	assert_eq!(response.headers().get("cache-control").unwrap(), "no-store");
	let data = response.into_body().collect().await.unwrap().to_bytes();
	(
		status,
		if data.is_empty() {
			Value::Null
		} else {
			serde_json::from_slice(&data).unwrap()
		},
	)
}
pub fn answer(text: &str) -> Vec<ai::ScriptStep> {
	vec![ai::ScriptStep::Update(ai::Update {
		text: text.into(),
		usage: ai::Usage::reported(100, 20),
		..Default::default()
	})]
}
pub fn custom(transcription: &str, fields: Value) -> ai::Script {
	ai::Script::new(vec![answer(transcription), answer(&fields.to_string())])
}
pub fn text_pdf(text: &str) -> Vec<u8> {
	let escaped = text
		.replace('\\', "\\\\")
		.replace('(', "\\(")
		.replace(')', "\\)")
		.replace('\n', ") Tj 0 -14 Td (");
	let content = format!("BT /F1 12 Tf 30 750 Td ({escaped}) Tj ET");
	let objects=["<< /Type /Catalog /Pages 2 0 R >>".to_owned(),"<< /Type /Pages /Count 1 /Kids [3 0 R] >>".into(),"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>".into(),"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".into(),format!("<< /Length {} >>\nstream\n{content}\nendstream",content.len())];
	let mut bytes = b"%PDF-1.4\n".to_vec();
	let mut offsets = vec![0];
	for (i, object) in objects.iter().enumerate() {
		offsets.push(bytes.len());
		bytes.extend_from_slice(format!("{} 0 obj\n{object}\nendobj\n", i + 1).as_bytes());
	}
	let xref = bytes.len();
	bytes.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", offsets.len()).as_bytes());
	for offset in offsets.iter().skip(1) {
		bytes.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
	}
	bytes.extend_from_slice(
		format!(
			"trailer << /Size {} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF",
			offsets.len()
		)
		.as_bytes(),
	);
	bytes
}
pub fn field(name: &str, value: &str, quote: &str) -> Value {
	json!({"feld":name,"wert":value,"sicherheit":"sicher","zitat":quote})
}
