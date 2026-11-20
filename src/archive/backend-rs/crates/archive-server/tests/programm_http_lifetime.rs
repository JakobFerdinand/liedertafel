#![allow(clippy::unwrap_used, clippy::expect_used)] // Actual socket + SQL lifecycle assertions.
mod support;
use archive_server::{ai, reading};
use std::{sync::Arc, time::Duration};
use support::programme::*;
use tokio::io::AsyncWriteExt;
async fn settled(app: &support::App) -> Vec<(String, i64, i64, i64, i64)> {
	tokio::time::timeout(Duration::from_secs(5),async {
		loop {
			let rows:Vec<(String,i64,i64,i64,i64)>=sqlx::query_as(r#"SELECT "Feature","Calls"::bigint,"ReservedMicroEur"::bigint,"InputTokens"::bigint,"OutputTokens"::bigint FROM ai_usage_entries ORDER BY "Feature""#).fetch_all(&app.pool).await.unwrap();
			if rows.len()==2 && rows.iter().all(|r|r.2==0) {break rows;}
			tokio::task::yield_now().await;
		}
	}).await.expect("HTTP cancellation must settle without waiting for global drain")
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; actual TCP disconnect and host signal, real gateway/settlement"]
async fn programme_is_always_request_bound_and_host_cancellation_precedes_graceful_wait() {
	let mut app = support::App::new_scoped("programme_http231").await;
	let mut client = app.admin().await;
	for host_shutdown in [false, true] {
		let entered = Arc::new(tokio::sync::Notify::new());
		let called = entered.clone();
		let script = ai::Script::handler(move |request| {
			if request
				.options
				.instructions
				.as_deref()
				.is_some_and(|s| s.contains("PROGRAMMTEXT-ANFANG"))
			{
				called.notify_one();
				vec![ai::ScriptStep::Hold]
			} else {
				answer("Konzert\n1. Ave Maria")
			}
		});
		let (gateway, shutdown) =
			configure_with_shutdown(&mut app, script, enabled(), ai::Options::shipped(true));
		client.router = app.router.clone();
		let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
		let address = listener.local_addr().unwrap();
		let signal = shutdown.clone();
		let router = app.router.clone();
		let server = tokio::spawn(async move {
			axum::serve(listener, router)
				.with_graceful_shutdown(async move { signal.cancelled().await })
				.await
				.unwrap()
		});
		let body = body(
			&[
				("entwurfId", "44444444-4444-4444-4444-444444444444"),
				("anfrageId", "2"),
			],
			&[("datei", &photo())],
		);
		let cookie = client
			.cookies
			.iter()
			.map(|(k, v)| format!("{k}={v}"))
			.collect::<Vec<_>>()
			.join("; ");
		let header = format!(
			"POST /api/auftritt-lesungen HTTP/1.1\r\nHost: localhost\r\nCookie: {cookie}\r\nX-CSRF-TOKEN: {}\r\nContent-Type: multipart/form-data; boundary=programme231\r\nContent-Length: {}\r\n\r\n",
			client.token,
			body.len()
		);
		let mut socket = tokio::net::TcpStream::connect(address).await.unwrap();
		socket.write_all(header.as_bytes()).await.unwrap();
		socket.write_all(&body).await.unwrap();
		tokio::time::timeout(Duration::from_secs(5), entered.notified())
			.await
			.unwrap();
		if host_shutdown {
			shutdown.cancel();
		} else {
			drop(socket);
		}
		let rows = settled(&app).await;
		let trans = rows
			.iter()
			.find(|r| r.0 == "programm-transcription")
			.unwrap();
		let fields = rows.iter().find(|r| r.0 == "programm-fields").unwrap();
		assert_eq!((trans.1, trans.3, trans.4), (1, 100, 20));
		assert_eq!(fields.1, 1);
		assert_eq!(
			fields.4,
			i64::from(reading::Options::default().fields_max_output_tokens)
		);
		shutdown.cancel();
		tokio::time::timeout(Duration::from_secs(5), server)
			.await
			.expect("no shutdown/drain circular wait")
			.unwrap();
		assert!(gateway.drain().await);
		assert_eq!(
			client.get("/api/events").await.1["events"],
			serde_json::json!([])
		);
		assert_eq!(
			client.get("/api/auftritt-entwuerfe").await.1["entwuerfe"],
			serde_json::json!([])
		);
		// Fixture reset only; second independent actual-HTTP scenario uses new calls.
		sqlx::query("DELETE FROM ai_usage_entries")
			.execute(&app.pool)
			.await
			.unwrap();
	}
	app.close().await;
}
