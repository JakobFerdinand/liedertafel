#![allow(clippy::unwrap_used, clippy::expect_used)] // Real socket/model lifecycle assertions.
mod support;
use archive_server::{ai, reading};
use serde_json::json;
use std::{sync::Arc, time::Duration};
use support::lesung;
use tokio::io::AsyncWriteExt;
use uuid::Uuid;

async fn socket(
	app: &support::App,
	client: &support::Client,
	fields: &[(&str, &str)],
) -> (
	tokio::net::TcpStream,
	tokio_util::sync::CancellationToken,
	tokio::task::JoinHandle<()>,
) {
	let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
	let address = listener.local_addr().unwrap();
	let stop = tokio_util::sync::CancellationToken::new();
	let signal = stop.clone();
	let router = app.router.clone();
	let server = tokio::spawn(async move {
		axum::serve(
			listener,
			router.into_make_service_with_connect_info::<std::net::SocketAddr>(),
		)
		.with_graceful_shutdown(signal.cancelled_owned())
		.await
		.unwrap();
	});
	let body = lesung::multipart(fields, &[("datei", &support::reading::scan(1))]);
	let mut socket = tokio::net::TcpStream::connect(address).await.unwrap();
	socket.write_all(format!("POST /api/lied-lesungen HTTP/1.1\r\nHost: localhost\r\nContent-Type: multipart/form-data; boundary=lesen230\r\nContent-Length: {}\r\nCookie: {}\r\nX-CSRF-TOKEN: {}\r\n\r\n",body.len(),lesung::cookie(client),client.token).as_bytes()).await.unwrap();
	socket.write_all(&body).await.unwrap();
	(socket, stop, server)
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; actual HTTP disconnect and full-reservation settlement"]
async fn actual_socket_disconnect_cancels_stateless_model_and_joins_accounting() {
	let mut app = support::App::new_scoped("lifetime230").await;
	let started = Arc::new(tokio::sync::Notify::new());
	let notify = started.clone();
	let script = ai::Script::handler(move |_| {
		notify.notify_one();
		vec![ai::ScriptStep::Hold]
	});
	let origin = lesung::configure(
		&mut app,
		script,
		ai::Options::shipped(true),
		reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let editor = app.admin().await;
	let (stream, stop, server) = socket(
		&app,
		&editor,
		&[
			("entwurfId", "11111111-1111-1111-1111-111111111111"),
			("anfrageId", "1"),
		],
	)
	.await;
	tokio::time::timeout(Duration::from_secs(5), started.notified())
		.await
		.unwrap();
	drop(stream);
	origin.reading_tasks.close();
	let joined = tokio::time::timeout(Duration::from_secs(3), origin.reading_tasks.wait()).await;
	let completed = joined.is_ok();
	origin.shutdown.cancel();
	stop.cancel();
	server.await.unwrap();
	assert!(origin.ai.as_ref().unwrap().gateway.drain().await);
	assert!(
		completed,
		"actual HTTP disconnect did not stop its model producer"
	);
	let charged:serde_json::Value=sqlx::query_scalar(r#"SELECT json_build_object('reserved',sum("ReservedMicroEur"),'cost',sum("CostMicroEur"),'calls',sum("Calls"),'output',sum("OutputTokens")) FROM ai_usage_entries"#).fetch_one(&app.pool).await.unwrap();
	assert_eq!(charged["reserved"], 0);
	assert_eq!(charged["calls"], 1);
	assert_eq!(charged["output"], 5000);
	assert!(charged["cost"].as_i64().unwrap() > 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; actual abandoned tab persists/resumes complete suggestions"]
async fn actual_socket_disconnect_does_not_cancel_an_owned_persisted_reading() {
	let mut app = support::App::new_scoped("lifetime230").await;
	let started = Arc::new(tokio::sync::Notify::new());
	let notify = started.clone();
	let calls = Arc::new(std::sync::atomic::AtomicUsize::new(0));
	let counter = calls.clone();
	let script = ai::Script::handler(move |_| {
		let first = counter.fetch_add(1, std::sync::atomic::Ordering::SeqCst) == 0;
		notify.notify_one();
		vec![
			ai::ScriptStep::Delay(Duration::from_millis(200)),
			ai::ScriptStep::Update(ai::Update {
				text: if first {
					"Waldfahrt".into()
				} else {
					json!({"felder":[{"feld":"title","wert":"Waldfahrt","sicherheit":"sicher","zitat":"Waldfahrt"}]}).to_string()
				},
				usage: ai::Usage::reported(100, 20),
				..Default::default()
			}),
		]
	});
	let origin = lesung::configure(
		&mut app,
		script,
		ai::Options::shipped(true),
		reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let mut editor = app.admin().await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(editor.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Handarbeit"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let (stream, stop, server) = socket(
		&app,
		&editor,
		&[
			("entwurfId", &id.to_string()),
			("dateiId", &file.to_string()),
			("anfrageId", "1"),
		],
	)
	.await;
	tokio::time::timeout(Duration::from_secs(5), started.notified())
		.await
		.unwrap();
	drop(stream);
	origin.reading_tasks.close();
	tokio::time::timeout(Duration::from_secs(5), origin.reading_tasks.wait())
		.await
		.unwrap();
	let body = editor.get(&path).await.1;
	assert_eq!(body["entwurf"]["dateien"][0]["lesung"]["status"], "gelesen");
	assert_eq!(
		body["entwurf"]["dateien"][0]["lesung"]["felder"],
		json!([{"feld":"title","wert":"Waldfahrt","sicher":true}])
	);
	assert_eq!(body["entwurf"]["lied"]["title"], "Handarbeit");
	origin.shutdown.cancel();
	stop.cancel();
	server.await.unwrap();
	assert!(origin.ai.as_ref().unwrap().gateway.drain().await);
	assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 2);
	app.close().await;
}
