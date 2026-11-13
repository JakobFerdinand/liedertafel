#![allow(clippy::unwrap_used, clippy::expect_used)] // Authenticated HTTP and persisted outcome assertions.
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
use uuid::Uuid;

async fn post(
	client: &support::Client,
	id: Uuid,
	file: Uuid,
	number: i32,
) -> (axum::http::StatusCode, Value) {
	let bytes = support::reading::scan(1);
	let mut body=format!("--lesen230\r\nContent-Disposition: form-data; name=\"entwurfId\"\r\n\r\n{id}\r\n--lesen230\r\nContent-Disposition: form-data; name=\"dateiId\"\r\n\r\n{file}\r\n--lesen230\r\nContent-Disposition: form-data; name=\"anfrageId\"\r\n\r\n{number}\r\n--lesen230\r\nContent-Disposition: form-data; name=\"datei\"; filename=\"a.pdf\"\r\n\r\n").into_bytes();
	body.extend(bytes);
	body.extend(b"\r\n--lesen230--\r\n");
	let response = client
		.router
		.clone()
		.oneshot(
			Request::post("/api/lied-lesungen")
				.header("content-type", "multipart/form-data; boundary=lesen230")
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
#[ignore = "explicit PostgreSQL/native PDFium; owned persisted reading and resume"]
async fn owned_reading_is_kept_for_resume_without_changing_the_form() {
	let mut app = support::App::new_scoped("draft230").await;
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
	let mut client = app.admin().await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(client.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Handgeschrieben","composer":"Eigene Angabe"},"bearbeitet":["composer"],"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let (status, answer) = post(&client, id, file, 1).await;
	assert_eq!(status, 200, "{answer}");
	let (_, body) = client.get(&path).await;
	assert_eq!(body["entwurf"]["dateien"][0]["lesung"]["status"], "gelesen");
	assert_eq!(
		body["entwurf"]["dateien"][0]["lesung"]["felder"],
		answer["felder"]
	);
	assert_eq!(body["entwurf"]["lied"]["composer"], "Eigene Angabe");
	assert_eq!(body["entwurf"]["lied"]["title"], "Handgeschrieben");
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; real committed discard cancels held delivery"]
async fn discarding_the_draft_stops_its_held_model_and_cannot_resurrect_an_outcome() {
	let mut app = support::App::new_scoped("draft230").await;
	let started = Arc::new(tokio::sync::Notify::new());
	let notify = started.clone();
	let script = archive_server::ai::Script::handler(move |_| {
		notify.notify_one();
		vec![archive_server::ai::ScriptStep::Hold]
	});
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
	let (_, rx) = tokio::sync::watch::channel(archive_server::ai::Options::shipped(true));
	let ledger = archive_server::ai::Ledger::new(
		Arc::new(archive_server::ai::PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let gateway = archive_server::ai::Gateway::scripted(ledger.clone(), script);
	origin.ai = Some(archive_server::ai::Services {
		gateway: gateway.clone(),
		ledger,
	});
	app.router = archive_server::build_router(Arc::new(origin));
	let mut operator = app.admin().await;
	let reader = app.sign_in("verwaltung@liedertafel.test").await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(operator.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let task = tokio::spawn(async move { post(&reader, id, file, 1).await });
	tokio::time::timeout(std::time::Duration::from_secs(5), started.notified())
		.await
		.unwrap();
	assert_eq!(operator.request("DELETE", &path, json!({})).await.0, 204);
	let result = tokio::time::timeout(std::time::Duration::from_secs(2), task).await;
	// Never leave a failed red test's held producer running after this assertion.
	let completed = result.is_ok();
	gateway.drain().await;
	assert!(completed, "discard left the actual model call running");
	let (status, body) = result.unwrap().unwrap();
	assert_eq!(status, 404, "{body}");
	assert_eq!(operator.get(&path).await.0, 404);
	app.close().await;
}
