//! Process-level checks: the actual binary cold-starts like the apphost
//! resource will (Development demands OTLP, the origin serves with the
//! handed-in `PORT`), and a SIGTERM drains within the 30-second budget.

// See tests/http_contract.rs: integration-test binaries deny the
// production lints (expect/panic) via clippy.toml allowances absent here.
#![allow(clippy::expect_used, clippy::panic, clippy::unwrap_used)]

use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

/// One free loopback port, taken from the OS and released just before the
/// binary binds it (same window the Aspire resource bridges).
fn free_port() -> u16 {
	std::net::TcpListener::bind(("127.0.0.1", 0))
		.expect("ephemeral listener")
		.local_addr()
		.expect("ephemeral addr")
		.port()
}

fn temporary_export_root(name: &str) -> std::path::PathBuf {
	let root = std::env::temp_dir().join(format!("archive-server-process-{name}"));
	std::fs::create_dir_all(root.join("system/status")).expect("fixture dirs");
	std::fs::write(
		root.join("index.html"),
		"<html lang=\"de-AT\">Hauptseite</html>",
	)
	.expect("index");
	std::fs::write(
		root.join("system/status/index.html"),
		"<html lang=\"de-AT\">Systemstatus</html>",
	)
	.expect("status page");
	std::fs::write(
		root.join("404.html"),
		"<html lang=\"de-AT\">Seite nicht gefunden</html>",
	)
	.expect("404 page");
	root
}

/// A minimal HTTP client: every wave-1 handshake is plain GET, so no HTTP
/// library is added to keep the runtime deps lean. Reads by content length
/// (a premature half-close could beat the server's first service ticks, so
/// the request never ends early).
async fn http_get(port: u16, path: &str) -> Result<(String, String), std::io::Error> {
	let read_deadline = Duration::from_secs(5);
	let stream = tokio::net::TcpStream::connect(("127.0.0.1", port)).await?;
	stream.set_nodelay(true).expect("nodelay on loopback");
	let (mut reader, mut writer) = tokio::io::split(stream);
	writer
		.write_all(
			format!("GET {path} HTTP/1.1\r\nhost: test\r\nconnection: close\r\n\r\n").as_bytes(),
		)
		.await?;
	writer.flush().await?;

	let mut raw = Vec::new();
	let mut buffer = [0u8; 4096];
	loop {
		let header_end = raw
			.windows(4)
			.rposition(|window| window == b"\r\n\r\n")
			.map(|head| head + 4);
		if let Some(header_end) = header_end {
			let head_text = String::from_utf8_lossy(&raw[..header_end]);
			let content_length = head_text
				.lines()
				.find_map(|line| line.strip_prefix("content-length: "))
				.and_then(|value| value.parse::<usize>().ok());
			if content_length
				.map(|body| raw.len() - header_end >= body)
				.unwrap_or(true)
			{
				break;
			}
		}
		let read = tokio::time::timeout(read_deadline, reader.read(&mut buffer)).await;
		let Ok(chunks) = read else {
			return Err(std::io::Error::new(
				std::io::ErrorKind::TimedOut,
				format!("reading {path} exceeded {read_deadline:?}"),
			));
		};
		let length = chunks?;
		if length == 0 {
			break;
		}
		raw.extend_from_slice(&buffer[..length]);
	}

	let text = String::from_utf8_lossy(&raw);
	let (head, body) = text
		.split_once("\r\n\r\n")
		.unwrap_or_else(|| panic!("no header/body split for {path}: {text}"));
	Ok((head.to_string(), body.to_string()))
}

fn status_of(head: &str) -> u16 {
	head.split('\r')
		.next()
		.and_then(|line| line.split(' ').nth(1))
		.and_then(|code| code.parse().ok())
		.unwrap_or_else(|| panic!("status line unreadable: {head}"))
}

/// Waits until the origin actually serves (a bare TCP connect succeeds while
/// the tokio boot is still rolling up the accept loop, so readiness is a
/// real `/alive` round trip).
async fn wait_until_ready(port: u16) {
	for attempt in 0..100 {
		if http_get(port, "/alive").await.is_ok() {
			return;
		}
		tokio::time::sleep(Duration::from_millis(50)).await;
		if attempt == 99 {
			panic!("origin never became ready on {port}");
		}
	}
}

#[tokio::test]
async fn development_without_otlp_endpoint_refuses_to_start() {
	let port = free_port();

	let outcome = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
		.env("ARCHIVE_ENVIRONMENT", "Development")
		.env("PORT", port.to_string())
		// No OTEL_EXPORTER_OTLP_ENDPOINT — the same protection the C# host has.
		.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.output()
		.await
		.expect("process ran");

	assert!(!outcome.status.success(), "Development demands OTLP");
	let stderr = String::from_utf8_lossy(&outcome.stderr);
	assert!(
		stderr.contains("Development requires OTLP configuration"),
		"the German-free C# message stays operator-visible: {stderr}"
	);
}

#[tokio::test]
async fn production_auth_requires_configured_rp_origins_and_keyless_azure_mail_before_listening() {
	for missing in [
		"Authentication__PasskeyRelyingPartyId",
		"Authentication__PasskeyOrigins__0",
		"Mail__Provider",
	] {
		let output = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
			.env("ARCHIVE_ENVIRONMENT", "Production")
			.env("PORT", free_port().to_string())
			.env(
				"ConnectionStrings__archive-db",
				"postgres://archive_runtime:private-auth-marker@127.0.0.1:1/archive",
			)
			.env(
				"Authentication__PasskeyRelyingPartyId",
				"archive.example.test",
			)
			.env(
				"Authentication__PasskeyOrigins__0",
				"https://archive.example.test",
			)
			.env("Mail__Provider", "Azure")
			.env(
				"Mail__AzureEndpoint",
				"https://synthetic.communication.azure.com",
			)
			.env_remove(missing)
			.output()
			.await
			.unwrap();
		assert!(
			!output.status.success(),
			"missing {missing} must fail startup"
		);
		assert!(!String::from_utf8_lossy(&output.stderr).contains("private-auth-marker"));
	}
}

#[tokio::test]
async fn configured_production_liveness_does_not_connect_to_database_or_identity() {
	let port = free_port();
	let mut child = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
		.env("ARCHIVE_ENVIRONMENT", "Production")
		.env("PORT", port.to_string())
		.env(
			"ConnectionStrings__archive-db",
			"postgres://archive_runtime:private-auth-marker@127.0.0.1:1/archive",
		)
		.env(
			"Authentication__PasskeyRelyingPartyId",
			"archive.example.test",
		)
		.env(
			"Authentication__PasskeyOrigins__0",
			"https://archive.example.test",
		)
		.env("Mail__Provider", "Azure")
		.env(
			"Mail__AzureEndpoint",
			"https://synthetic.communication.azure.com",
		)
		.env_remove("Mail__AzureConnectionString")
		.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.kill_on_drop(true)
		.spawn()
		.unwrap();
	wait_until_ready(port).await;
	assert_eq!(
		status_of(&http_get(port, "/api/build").await.unwrap().0),
		200
	);
	std::process::Command::new("kill")
		.arg("-TERM")
		.arg(child.id().unwrap().to_string())
		.status()
		.unwrap();
	assert!(
		tokio::time::timeout(Duration::from_secs(10), child.wait())
			.await
			.unwrap()
			.unwrap()
			.success()
	);
}

#[tokio::test]
async fn cold_start_serves_export_build_and_liveness_then_drains_on_sigterm() {
	let port = free_port();
	let root: std::path::PathBuf = temporary_export_root("cold-start");
	let otlp = free_port(); // unreachable collector: exporters batch and retry, startup stays clean

	let mut child = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
		.env("ARCHIVE_ENVIRONMENT", "Development")
		.env("PORT", port.to_string())
		.env("ARCHIVE_STATIC_ROOT", &root)
		.env(
			"OTEL_EXPORTER_OTLP_ENDPOINT",
			format!("http://127.0.0.1:{otlp}"),
		)
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.kill_on_drop(true)
		.spawn()
		.expect("binary spawns");

	wait_until_ready(port).await;

	let (head, body) = http_get(port, "/api/build").await.expect("build request");
	assert_eq!(status_of(&head), 200);
	assert!(
		body.contains("\"application\":\"Liedertafel Archiv\""),
		"build payload shape: {body}"
	);
	assert!(body.contains("\"development\":true"), "dev flag: {body}");
	assert!(
		head.contains("cache-control: no-cache, no-store"),
		"probe cache policy: {head}"
	);

	let (alive_head, alive_body) = http_get(port, "/alive").await.expect("readiness request");
	assert_eq!(status_of(&alive_head), 200);
	assert_eq!(alive_body, "Healthy");

	let (home_head, home_body) = http_get(port, "/").await.expect("static request");
	assert_eq!(status_of(&home_head), 200);
	assert!(
		home_body.contains("Hauptseite"),
		"the export renders: {home_body}"
	);

	let (status_head, _) = http_get(port, "/system/status/").await.expect("deep link");
	assert_eq!(
		status_of(&status_head),
		200,
		"deep links render from directories"
	);

	// SIGTERM drains within the 30-second shutdown budget, exit code 0. The
	// standard `kill` utility (resolves via PATH, like the apphost process
	// lifecycle does) performs the same signal delivery std/tokio have no
	// SIGTERM API for — and C FFI is forbidden in this workspace.
	std::process::Command::new("kill")
		.arg("-TERM")
		.arg(child.id().expect("origin pid").to_string())
		.status()
		.expect("SIGTERM handed to the origin process")
		.success()
		.then_some(())
		.expect("the origin accepted the signal");
	let drained = tokio::time::timeout(Duration::from_secs(10), child.wait()).await;
	let status = drained
		.expect("drain did not finish within the test budget")
		.expect("waited status");
	assert!(
		status.success(),
		"clean drain after SIGTERM, got {status:?}"
	);
}
