// Production credential selection against a local refusing identity endpoint.
// No fake returns a token; PATH contains only refusing az/azd fixture commands.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
#[path = "support/ai.rs"]
mod ai;
use archive_server::ai::*;
use axum::{
	Router,
	extract::RawQuery,
	http::{HeaderMap, StatusCode},
	routing::get,
};
use std::{
	os::unix::fs::PermissionsExt,
	sync::{Arc, Mutex},
};

#[tokio::test]
async fn managed_identity_is_trimmed_once_and_blank_uses_only_offline_developer_tools() {
	let _ = rustls::crypto::ring::default_provider().install_default();
	if std::env::var("ARCHIVE_228_IDENTITY_CHILD").as_deref() == Ok("offline") {
		let log = std::env::var("ARCHIVE_228_IDENTITY_QUERY_LOG").unwrap();
		let h = ai::Harness::new();
		h.options.send_modify(|o| {
			o.models
				.insert("gpt-6-luna".into(), Price::new("1", "4").unwrap());
		});
		let gateway = Gateway::azure(
			h.ledger,
			AzureConfig {
				endpoint: "https://127.0.0.1:1".into(),
				deployment: "gpt-6-luna".into(),
			},
		)
		.unwrap();
		tokio::time::sleep(std::time::Duration::from_millis(20)).await;
		assert!(
			!std::path::Path::new(&log).exists(),
			"construction obtained an identity"
		);
		let mut request = Request::new(vec![Message::text("user", "Frage")]);
		request.operation = Some(Operation::start("chat", None));
		assert!(
			gateway
				.start(request, false)
				.unwrap()
				.response()
				.await
				.is_err()
		);
		assert!(gateway.drain().await);
		return;
	}
	for client_id in [
		None,
		Some(""),
		Some(" \t "),
		Some("11111111-1111-1111-1111-111111111111"),
		Some("  11111111-1111-1111-1111-111111111111  "),
	] {
		let directory = std::path::PathBuf::from("/tmp/opencode")
			.join(format!("228-identity-{}", uuid::Uuid::new_v4()));
		std::fs::create_dir(&directory).unwrap();
		std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700)).unwrap();
		let command_log = directory.join("commands");
		let query_log = directory.join("queries");
		for name in ["az", "azd"] {
			let path = directory.join(name);
			std::fs::write(
				&path,
				format!(
					"#!/bin/sh\nprintf '%s\\n' '{name}' >> \"$ARCHIVE_228_IDENTITY_COMMAND_LOG\"\nexit 1\n"
				),
			)
			.unwrap();
			std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o700)).unwrap();
		}
		let queries = Arc::new(Mutex::new(Vec::new()));
		let capture = queries.clone();
		let file = query_log.clone();
		let router = Router::new().route(
			"/identity",
			get(move |headers: HeaderMap, RawQuery(query): RawQuery| {
				let capture = capture.clone();
				let file = file.clone();
				async move {
					assert_eq!(headers["x-identity-header"], "offline-identity-header-228");
					capture.lock().unwrap().push(query.unwrap_or_default());
					std::fs::write(file, b"local identity requested").unwrap();
					(StatusCode::BAD_REQUEST, "synthetic refusal; no token")
				}
			}),
		);
		let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
		let endpoint = format!("http://{}/identity", listener.local_addr().unwrap());
		let server = tokio::spawn(async move {
			axum::serve(listener, router).await.unwrap();
		});
		let mut child = tokio::process::Command::new(std::env::current_exe().unwrap());
		child.args([
			"--exact",
			"managed_identity_is_trimmed_once_and_blank_uses_only_offline_developer_tools",
			"--nocapture",
		]);
		child
			.env("ARCHIVE_228_IDENTITY_CHILD", "offline")
			.env("IDENTITY_ENDPOINT", endpoint)
			.env("IDENTITY_HEADER", "offline-identity-header-228")
			.env("PATH", &directory)
			.env("ARCHIVE_228_IDENTITY_COMMAND_LOG", &command_log)
			.env("ARCHIVE_228_IDENTITY_QUERY_LOG", &query_log)
			.env_remove("AZURE_CLIENT_ID");
		if let Some(id) = client_id {
			child.env("AZURE_CLIENT_ID", id);
		}
		let output = tokio::time::timeout(
			std::time::Duration::from_secs(20),
			child.kill_on_drop(true).output(),
		)
		.await
		.unwrap()
		.unwrap();
		assert!(
			output.status.success(),
			"{}",
			String::from_utf8_lossy(&output.stderr)
		);
		let queries = queries.lock().unwrap().clone();
		if client_id.is_some_and(|v| !v.trim().is_empty()) {
			assert_eq!(queries.len(), 1);
			let pairs = url::form_urlencoded::parse(queries[0].as_bytes()).collect::<Vec<_>>();
			assert_eq!(
				pairs
					.iter()
					.filter(|(k, _)| k == "client_id")
					.map(|(_, v)| v.as_ref())
					.collect::<Vec<_>>(),
				vec!["11111111-1111-1111-1111-111111111111"]
			);
			assert!(!command_log.exists(), "no managed-identity fallback to CLI");
		} else {
			assert!(queries.is_empty());
			let commands = std::fs::read_to_string(&command_log).unwrap();
			assert!(commands.lines().any(|v| v == "az"));
			assert!(commands.lines().all(|v| matches!(v, "az" | "azd")));
		}
		server.abort();
		let _ = server.await;
	}
}
