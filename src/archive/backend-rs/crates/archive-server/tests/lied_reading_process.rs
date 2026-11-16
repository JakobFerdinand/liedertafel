#![allow(clippy::unwrap_used, clippy::expect_used)] // Actual product host signal/exit assertions.
use std::{process::Stdio, time::Duration};
fn port() -> u16 {
	std::net::TcpListener::bind("127.0.0.1:0")
		.unwrap()
		.local_addr()
		.unwrap()
		.port()
}
#[tokio::test]
async fn configured_model_host_reports_failed_terminal_telemetry_flush_as_nonzero() {
	let port = port();
	let otlp = super_port();
	let child = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
		.env("ARCHIVE_ENVIRONMENT", "Development")
		.env("PORT", port.to_string())
		.env(
			"OTEL_EXPORTER_OTLP_ENDPOINT",
			format!("http://127.0.0.1:{otlp}"),
		)
		.env(
			"ConnectionStrings__archive-db",
			"postgres://unavailable@127.0.0.1:1/test230_never_connected",
		)
		.env("Mail__Provider", "Smtp")
		.env("Mail__Host", "127.0.0.1")
		.env("Mail__Port", "1")
		.env("Archive__Chat__Provider", "Scripted")
		.env_remove("Archive__Chat__Endpoint")
		.env_remove("Archive__Chat__DeploymentName")
		.stdout(Stdio::null())
		.stderr(Stdio::piped())
		.kill_on_drop(true)
		.spawn()
		.unwrap();
	let client = reqwest::Client::new();
	tokio::time::timeout(Duration::from_secs(5), async {
		loop {
			if client
				.get(format!("http://127.0.0.1:{port}/api/build"))
				.send()
				.await
				.is_ok()
			{
				break;
			}
			tokio::time::sleep(Duration::from_millis(20)).await;
		}
	})
	.await
	.unwrap();
	assert!(
		std::process::Command::new("kill")
			.arg("-TERM")
			.arg(child.id().unwrap().to_string())
			.status()
			.unwrap()
			.success()
	);
	let output = tokio::time::timeout(Duration::from_secs(25), child.wait_with_output())
		.await
		.unwrap()
		.unwrap();
	assert!(
		!output.status.success(),
		"configured model host silently accepted a failed terminal OTLP flush"
	);
}
fn super_port() -> u16 {
	port()
}
