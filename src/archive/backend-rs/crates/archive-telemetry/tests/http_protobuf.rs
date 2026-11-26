#![allow(clippy::unwrap_used, clippy::panic)] // Local synthetic collector/test failures only.

use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::process::Command;
use std::time::{Duration, Instant};

use archive_telemetry::{ACTIVITY_SOURCE, ServiceDefaults};
use opentelemetry::global;
use opentelemetry_proto::tonic::collector::{
	logs::v1::ExportLogsServiceRequest, metrics::v1::ExportMetricsServiceRequest,
	trace::v1::ExportTraceServiceRequest,
};
use opentelemetry_proto::tonic::common::v1::any_value::Value;
use opentelemetry_proto::tonic::metrics::v1::{metric, number_data_point};
use opentelemetry_proto::tonic::resource::v1::Resource;
use prost::Message;

const SECRET: &str = "synthetic-235-failure-secret";
const SERVICE: &str = "archive-test-worker";
const SPAN: &str = "verification-job";
const METRIC: &str = "archive.test.work";
const LOG: &str = "Worker test started";

// Each process owns exactly one tracing subscriber. The subprocess exercises
// the same registration and checked flush as product finite jobs, not an HTTP
// client mock. Product-image worker smoke is a separate integration check.
#[test]
fn finite_job_child() {
	let Ok(mode) = std::env::var("ARCHIVE_TEST_FINITE_MODE") else {
		return;
	};
	let result = tokio::runtime::Runtime::new().unwrap().block_on(async {
		let defaults = ServiceDefaults::init(true, SERVICE).unwrap();
		let outcome = {
			let span = tracing::info_span!(SPAN, otel.status_code = tracing::field::Empty);
			let _entered = span.enter();
			global::meter(ACTIVITY_SOURCE)
				.u64_counter(METRIC)
				.build()
				.add(1, &[]);
			tracing::info!(target: "archive_server::image_test", "{LOG}");
			let synthetic_url = format!("https://storage.test/file?sig={SECRET}");
			// At archive boundaries, only the static operation and status are
			// emitted. A URL or error is not passed into the SDK for scrubbing.
			let work = if mode == "fail" {
				Err(synthetic_url)
			} else {
				Ok(())
			};
			if work.is_err() {
				span.record("otel.status_code", "ERROR");
				tracing::error!(target: "archive_server::image_test", "Archive job failed");
			}
			work.is_err()
		};
		assert!(defaults.finish_finite_job());
		i32::from(outcome)
	});
	std::process::exit(result);
}

#[test]
fn exporter_startup_error_does_not_echo_endpoint() {
	let output = Command::new(std::env::current_exe().unwrap())
		.args(["--exact", "finite_job_child"])
		.env("ARCHIVE_TEST_FINITE_MODE", "success")
		.env(
			"OTEL_EXPORTER_OTLP_ENDPOINT",
			format!("https://[?sig={SECRET}"),
		)
		.env("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
		.env_remove("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")
		.env_remove("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL")
		.output()
		.unwrap();
	assert!(!output.status.success());
	assert_safe(&output.stdout);
	assert_safe(&output.stderr);
}

#[test]
fn finite_jobs_flush_authenticated_http_protobuf_even_on_failure() {
	for (mode, status) in [("success", 0), ("fail", 1)] {
		let listener = TcpListener::bind("127.0.0.1:0").unwrap();
		listener.set_nonblocking(true).unwrap();
		let endpoint = format!("http://{}", listener.local_addr().unwrap());
		let mut command = Command::new(std::env::current_exe().unwrap());
		for (name, _) in std::env::vars_os() {
			if name.to_string_lossy().starts_with("OTEL_") {
				command.env_remove(name);
			}
		}
		let capture = std::thread::spawn(move || collect(&listener));
		let output = command
			.args(["--exact", "finite_job_child"])
			.env("ARCHIVE_TEST_FINITE_MODE", mode)
			.env("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint)
			.env("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
			.env("OTEL_EXPORTER_OTLP_HEADERS", "x-otlp-test=235%20local-test")
			.env("OTEL_EXPORTER_OTLP_TIMEOUT", "2000")
			.env("OTEL_BSP_SCHEDULE_DELAY", "60000")
			.env("OTEL_BLRP_SCHEDULE_DELAY", "60000")
			.env("OTEL_METRIC_EXPORT_INTERVAL", "60000")
			.output()
			.unwrap();
		// Join after the child and its streams have closed. No raw evidence is
		// printed, including on failed assertions.
		let received = capture.join().unwrap();
		assert_eq!(output.status.code(), Some(status), "wrong exit: {mode}");
		assert_safe(&output.stdout);
		assert_safe(&output.stderr);
		assert_signals(&received, SPAN, METRIC, LOG, mode == "fail");
	}
}

#[test]
#[ignore = "requires built localhost/liedertafel-archive candidate and local container runtime"]
fn actual_image_finite_jobs_flush_authenticated_http_protobuf() {
	let tag = std::env::var("Archive__TestImageTag").unwrap();
	let runtime = std::env::var("ARCHIVE_CONTAINER_RUNTIME").unwrap_or_else(|_| "podman".into());
	let image = format!("localhost/liedertafel-archive:{tag}");
	let network = format!("archive-235-otlp-{}", std::process::id());
	let pg = format!("{network}-db");
	let mut local = Containers {
		runtime,
		network,
		names: Vec::new(),
	};
	local.run(&["network", "create", "--internal", &local.network]);
	let network = local.network.clone();
	local.names.push(pg.clone());
	local.run(&[
		"run",
		"-d",
		"--pull=never",
		"--name",
		&pg,
		"--network",
		&network,
		"--network-alias",
		"pg",
		"-e",
		"POSTGRES_PASSWORD=fixture235",
		"-e",
		"POSTGRES_DB=archive",
		"docker.io/library/postgres:17.6",
	]);
	wait_until(|| {
		local
			.command()
			.args([
				"exec",
				&pg,
				"pg_isready",
				"-h",
				"127.0.0.1",
				"-d",
				"archive",
			])
			.output()
			.unwrap()
			.status
			.success()
	});

	for (mode, status) in [("success", 0), ("fail", 1)] {
		let collector = format!("{network}-{mode}");
		local.names.push(collector.clone());
		local.run(&[
			"run",
			"-d",
			"--pull=never",
			"--name",
			&collector,
			"--network",
			&network,
			"--network-alias",
			"collector",
			"docker.io/library/node:24-bookworm-slim",
			"node",
			"-e",
			include_str!("image-collector.cjs"),
		]);
		let query = |path: &str, method: &str| {
			local.command()
			.args(["exec", &collector, "node", "-e",
				"fetch(process.argv[1],{method:process.argv[2]}).then(async r=>{if(!r.ok)process.exit(1);process.stdout.write(await r.text())}).catch(()=>process.exit(1))",
				&format!("http://127.0.0.1:4318/{path}"), method]).output().unwrap()
		};
		wait_until(|| query("ready", "GET").status.success());

		let connection = if status == 0 {
			"postgresql://postgres:fixture235@pg:5432/archive"
		} else {
			// Actual product config parser receives this private marker and fails;
			// no synthetic job or example is shipped in the image.
			SECRET
		};
		let output = local
			.command()
			.args([
				"run",
				"--rm",
				"--pull=never",
				"--network",
				&network,
				"-e",
				"ARCHIVE_ENVIRONMENT=Development",
				"-e",
				"RUST_LOG=trace",
				"-e",
				"OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4318",
				"-e",
				"OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf",
				"-e",
				"OTEL_EXPORTER_OTLP_HEADERS=x-otlp-test=235%20local-test",
				"-e",
				"OTEL_EXPORTER_OTLP_TIMEOUT=2000",
				"-e",
				"OTEL_BSP_SCHEDULE_DELAY=60000",
				"-e",
				"OTEL_BLRP_SCHEDULE_DELAY=60000",
				"-e",
				"OTEL_METRIC_EXPORT_INTERVAL=60000",
				"-e",
				&format!("OTEL_SERVICE_NAME={SERVICE}"),
				"-e",
				&format!("ConnectionStrings__archive-migrations={connection}"),
				&image,
				"--migrate",
			])
			.output()
			.unwrap();
		assert_safe(&output.stdout);
		assert_safe(&output.stderr);
		assert_eq!(
			output.status.code(),
			Some(status),
			"wrong image job exit: {mode}"
		);

		let snapshot = query("seal", "POST");
		assert!(
			snapshot.status.success(),
			"bad or incomplete authenticated export"
		);
		let mut received = BTreeMap::<String, Vec<Vec<u8>>>::new();
		for line in serde_json::from_slice::<Vec<String>>(&snapshot.stdout).unwrap() {
			let (path, hex) = line.split_once(' ').unwrap();
			let body = hex::decode(hex).unwrap();
			assert_safe(&body);
			received.entry(path.to_owned()).or_default().push(body);
		}
		let log = if status == 0 {
			"Archive job migrate completed"
		} else {
			"Archive job migrate failed"
		};
		assert_signals(&received, "migrate", "archive.jobs.runs", log, status == 1);
		local.run(&["rm", "-f", &collector]);
		local.names.pop();
	}
}

struct Containers {
	runtime: String,
	network: String,
	names: Vec<String>,
}

impl Containers {
	fn command(&self) -> Command {
		let mut command = Command::new("timeout");
		command.args(["45", &self.runtime]);
		command
	}

	fn run(&self, args: &[&str]) {
		assert!(
			self.command().args(args).output().unwrap().status.success(),
			"container runtime operation failed: {}",
			args[0]
		);
	}
}

impl Drop for Containers {
	fn drop(&mut self) {
		for name in self.names.iter().rev() {
			let _ = self.command().args(["rm", "-f", name]).output();
		}
		let _ = self
			.command()
			.args(["network", "rm", &self.network])
			.output();
	}
}

fn wait_until(mut ready: impl FnMut() -> bool) {
	let until = Instant::now() + Duration::from_secs(25);
	while Instant::now() < until {
		if ready() {
			return;
		}
		std::thread::sleep(Duration::from_millis(100));
	}
	panic!("local container did not become ready");
}

fn collect(listener: &TcpListener) -> BTreeMap<String, Vec<Vec<u8>>> {
	let mut captured = BTreeMap::<String, Vec<Vec<u8>>>::new();
	let until = Instant::now() + Duration::from_secs(8);
	while Instant::now() < until {
		match listener.accept() {
			Ok((mut stream, _)) => {
				stream
					.set_read_timeout(Some(Duration::from_secs(2)))
					.unwrap();
				stream
					.set_write_timeout(Some(Duration::from_secs(2)))
					.unwrap();
				let mut request = BufReader::new(&mut stream);
				let mut line = String::new();
				request.read_line(&mut line).unwrap();
				let route = line.split_whitespace().nth(1).unwrap().to_owned();
				assert!(line.starts_with("POST "));
				assert!(matches!(
					route.as_str(),
					"/v1/traces" | "/v1/metrics" | "/v1/logs"
				));
				let mut headers = BTreeMap::new();
				loop {
					line.clear();
					assert_ne!(request.read_line(&mut line).unwrap(), 0);
					if line == "\r\n" {
						break;
					}
					let (key, value) = line.split_once(':').unwrap();
					headers.insert(key.to_ascii_lowercase(), value.trim().to_owned());
				}
				assert!(
					headers
						.get("x-otlp-test")
						.is_some_and(|v| v == "235 local-test")
				);
				assert_eq!(headers["content-type"], "application/x-protobuf");
				let mut body = vec![0; headers["content-length"].parse().unwrap()];
				request.read_exact(&mut body).unwrap();
				assert_safe(&body);
				captured.entry(route).or_default().push(body);
				stream
					.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
					.unwrap();
			}
			Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
				std::thread::sleep(Duration::from_millis(10));
			}
			Err(error) => panic!("collector accept: {error}"),
		}
	}
	captured
}

fn assert_safe(body: &[u8]) {
	for marker in [SECRET, "235 local-test", "storage.test", "fixture235"] {
		assert!(
			!body
				.windows(marker.len())
				.any(|window| window == marker.as_bytes()),
			"unexpected private payload"
		);
	}
}

fn assert_service(resource: Option<&Resource>) {
	assert!(resource.unwrap().attributes.iter().any(|attribute| {
		attribute.key == "service.name"
			&& matches!(attribute.value.as_ref().and_then(|v| v.value.as_ref()), Some(Value::StringValue(name)) if name == SERVICE)
	}));
}

fn assert_signals(
	received: &BTreeMap<String, Vec<Vec<u8>>>,
	span_name: &str,
	metric_name: &str,
	log_body: &str,
	fail: bool,
) {
	assert_eq!(received.len(), 3, "each finite job sends all three signals");
	let mut span_found = false;
	for body in &received["/v1/traces"] {
		for resource in ExportTraceServiceRequest::decode(body.as_slice())
			.unwrap()
			.resource_spans
		{
			assert_service(resource.resource.as_ref());
			for span in resource.scope_spans.into_iter().flat_map(|s| s.spans) {
				if span.name == span_name {
					span_found = true;
					assert_eq!(
						span.status.map(|status| status.code).unwrap_or(0),
						if fail { 2 } else { 0 }
					);
				}
			}
		}
	}
	assert!(span_found);
	let mut metric_found = false;
	for body in &received["/v1/metrics"] {
		for resource in ExportMetricsServiceRequest::decode(body.as_slice())
			.unwrap()
			.resource_metrics
		{
			assert_service(resource.resource.as_ref());
			metric_found |= resource
				.scope_metrics
				.into_iter()
				.flat_map(|s| s.metrics)
				.any(|m| {
					if m.name != metric_name {
						return false;
					}
					let Some(metric::Data::Sum(sum)) = m.data else { return false };
					sum.data_points.into_iter().any(|point| {
						if point.value != Some(number_data_point::Value::AsInt(1)) {
							return false;
						}
						if metric_name == METRIC {
							return true;
						}
						point.attributes.iter().any(|attr| {
							attr.key == "archive.job.name"
								&& matches!(attr.value.as_ref().and_then(|v| v.value.as_ref()), Some(Value::StringValue(name)) if name == span_name)
						}) && point.attributes.iter().any(|attr| {
							attr.key == "archive.job.success"
								&& matches!(attr.value.as_ref().and_then(|v| v.value.as_ref()), Some(Value::BoolValue(success)) if *success != fail)
						})
					})
				});
		}
	}
	assert!(metric_found);
	let mut log_found = false;
	for body in &received["/v1/logs"] {
		for resource in ExportLogsServiceRequest::decode(body.as_slice())
			.unwrap()
			.resource_logs
		{
			assert_service(resource.resource.as_ref());
			log_found |= resource.scope_logs.into_iter().flat_map(|s| s.log_records).any(|record|
				matches!(record.body.and_then(|b| b.value), Some(Value::StringValue(message)) if message == log_body));
		}
	}
	assert!(log_found);
}
