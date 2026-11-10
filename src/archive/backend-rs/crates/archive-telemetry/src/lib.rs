//! OTLP traces, metrics and logs for every backend-rs host.
//!
//! Rust port of the archive's `AddServiceDefaults()` (`src/archive/service-defaults`),
//! driven by the Aspire environment: `OTEL_EXPORTER_OTLP_ENDPOINT` is mandatory
//! in Development, `OTEL_SERVICE_NAME`/`OTEL_RESOURCE_ATTRIBUTES` are consumed
//! exactly as the DCP injection writes them, and Development always samples
//! everything. Console logging alone is insufficient — this crate registers all
//! three signals whenever an OTLP endpoint exists and refuses to boot in
//! Development without one.
//!
//! Deferred from the C# service defaults on purpose:
//! - `AiExceptionRedactionProcessor` belongs to the AI wave (no AI spans exist
//!   yet in Rust).
//! - #221 request spans omit queries/headers and globally suppress credential-
//!   dumping WebAuthn/mail SDK events. Later storage/AI waves must retain safe
//!   URL/error instrumentation at their own boundaries.
//! - OTLP header auth for a dashboard API key: `opentelemetry-otlp` currently
//!   carries headers on HTTP transports only, and local gRPC export needs none.

use opentelemetry::trace::TracerProvider as _;
use opentelemetry::{KeyValue, global};
use std::env;
use std::time::Duration;
use tracing_subscriber::layer::Layer as _;
use tracing_subscriber::prelude::*;

/// ActivitySource/Meter name of the archive, kept for schema parity with the
/// C# backend so the internal dashboard reads the same shapes.
pub const ACTIVITY_SOURCE: &str = "Liedertafel.Archive";

const OTEL_SERVICE_NAME: &str = "OTEL_SERVICE_NAME";
const OTEL_EXPORTER_OTLP_ENDPOINT: &str = "OTEL_EXPORTER_OTLP_ENDPOINT";
const OTEL_RESOURCE_ATTRIBUTES: &str = "OTEL_RESOURCE_ATTRIBUTES";
const OTEL_TRACES_SAMPLER: &str = "OTEL_TRACES_SAMPLER";
const OTEL_TRACES_SAMPLER_ARG: &str = "OTEL_TRACES_SAMPLER_ARG";

/// The three SDK providers a host keeps registered for its whole lifetime;
/// they are flushed and shut down at process exit.
#[derive(Debug, Clone)]
pub struct ServiceDefaults {
	tracer_provider: opentelemetry_sdk::trace::SdkTracerProvider,
	meter_provider: opentelemetry_sdk::metrics::SdkMeterProvider,
	logger_provider: opentelemetry_sdk::logs::SdkLoggerProvider,
}

/// A failure to set up the telemetry estate. Configuration errors fail loudly
/// like the C# host: `InvalidOperationException` for the missing Development
/// endpoint, provider errors otherwise.
#[derive(Debug, thiserror::Error)]
pub enum InitError {
	#[error("Development requires OTLP configuration. Start the archive through AppHost.")]
	MissingOtlpEndpoint,

	#[error("the OTLP exporter could not be built: {0}")]
	ExporterBuild(#[from] opentelemetry_otlp::ExporterBuildError),

	#[error("the tracing subscriber is already registered; initialize telemetry once")]
	TracingSubscriberTaken,
}

impl ServiceDefaults {
	/// Registers the service-defaults equivalent: OTLP exporters when an
	/// endpoint is configured, the global providers and the tracing
	/// subscriber (console plus OTLP layers).
	///
	/// # Errors
	/// In Development without an OTLP endpoint, or when an exporter cannot
	/// resolve its configuration from the environment.
	pub fn init(development: bool, default_service_name: &str) -> Result<Self, InitError> {
		let endpoint = env::var(OTEL_EXPORTER_OTLP_ENDPOINT)
			.ok()
			.filter(|value| !value.is_empty());
		if development && endpoint.is_none() {
			return Err(InitError::MissingOtlpEndpoint);
		}

		let resource = opentelemetry_sdk::Resource::builder()
			.with_service_name(env_string(OTEL_SERVICE_NAME, default_service_name))
			.with_attributes(resource_attributes(&env_string(
				OTEL_RESOURCE_ATTRIBUTES,
				"",
			)))
			.build();

		let mut tracer_builder = opentelemetry_sdk::trace::SdkTracerProvider::builder()
			.with_resource(resource.clone())
			.with_sampler(process_sampler(development));
		if let Some(exporter) = span_exporter(endpoint.is_some())? {
			let batch = opentelemetry_sdk::trace::BatchSpanProcessor::builder(exporter)
				.with_batch_config(
					opentelemetry_sdk::trace::BatchConfigBuilder::default()
						.with_scheduled_delay(Duration::from_millis(
							env_millis("OTEL_BSP_SCHEDULE_DELAY").unwrap_or(1_000),
						))
						.build(),
				)
				.build();
			tracer_builder = tracer_builder.with_span_processor(batch);
		}
		let tracer_provider = tracer_builder.build();
		let meter_provider = build_meter_provider(
			&resource,
			Duration::from_millis(env_millis("OTEL_METRIC_EXPORT_INTERVAL").unwrap_or(60_000)),
			endpoint.is_some(),
		)?;
		let logger_provider = build_logger_provider(
			&resource,
			Duration::from_millis(env_millis("OTEL_BLRP_SCHEDULE_DELAY").unwrap_or(1_000)),
			endpoint.is_some(),
		)?;

		let service_defaults = Self {
			tracer_provider,
			meter_provider,
			logger_provider,
		};
		install_tracing_subscriber(&service_defaults)?;
		global::set_tracer_provider(service_defaults.tracer_provider.clone());
		global::set_meter_provider(service_defaults.meter_provider.clone());

		if endpoint.is_none() {
			tracing::info!("No OTLP endpoint configured; telemetry stays console-only");
		}
		Ok(service_defaults)
	}

	/// Flushes traces and metrics one last time before the process exits (the
	/// flush analog of the C# `RunArchiveJobAsync` for the long-running
	/// hosts; finite workers carry their exact bounded job flush in a later
	/// wave). Log records drain inside `shutdown`, where the Rust log
	/// processor empties its queue.
	pub fn flush(&self) {
		let _ = self.tracer_provider.force_flush();
		let _ = self.meter_provider.force_flush();
		tracing::debug!("Telemetry flushed before shutdown");
	}

	/// Finite operator equivalent of RunArchiveJobAsync: all three flush results
	/// matter, and each provider gets the oracle's five-second shutdown budget.
	/// Provider error payloads are intentionally never formatted into diagnostics.
	pub fn finish_finite_job(&self) -> bool {
		let traces = self.tracer_provider.force_flush().is_ok();
		let metrics = self.meter_provider.force_flush().is_ok();
		let logs = self.logger_provider.force_flush().is_ok();
		if !traces || !metrics || !logs {
			tracing::error!("Archive job telemetry flush failed");
		}
		let traces_stopped = self
			.tracer_provider
			.shutdown_with_timeout(Duration::from_secs(5))
			.is_ok();
		let metrics_stopped = self
			.meter_provider
			.shutdown_with_timeout(Duration::from_secs(5))
			.is_ok();
		let logs_stopped = self
			.logger_provider
			.shutdown_with_timeout(Duration::from_secs(5))
			.is_ok();
		traces && metrics && logs && traces_stopped && metrics_stopped && logs_stopped
	}

	/// Shuts the three providers down; the log batch processor drains its
	/// queued records as part of this.
	pub fn shutdown(&self) {
		let _ = self.tracer_provider.shutdown();
		let _ = self.meter_provider.shutdown();
		let _ = self.logger_provider.shutdown();
	}
}

fn install_tracing_subscriber(defaults: &ServiceDefaults) -> Result<(), InitError> {
	let console_filter = tracing_subscriber::EnvFilter::try_from_default_env()
		.unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info"));
	let console_layer = tracing_subscriber::fmt::layer().with_filter(console_filter);

	// Server and later worker spans flow into the instrumentation scope the
	// C# ActivitySource "Liedertafel.Archive" writes into → same scope name
	// and attribute shapes in the dashboard.
	let otel_trace_layer = tracing_opentelemetry::layer()
		.with_tracer(defaults.tracer_provider.tracer(ACTIVITY_SOURCE));

	// The OpenTelemetryTracingBridge forwards the same structured records to
	// the log exporter — the analog of the C# `AddOpenTelemetry` logging with
	// IncludeFormattedMessage/IncludeScopes.
	let otel_log_layer = tracing_subscriber::Layer::with_filter(
		opentelemetry_appender_tracing::layer::OpenTelemetryTracingBridge::new(
			&defaults.logger_provider,
		),
		quiet_internals(),
	);

	let subscriber = tracing_subscriber::registry()
		.with(console_layer)
		.with(otel_trace_layer)
		.with(otel_log_layer)
		// WebAuthn core intentionally traces full credentials and ceremony state.
		// Mandatory global filter applies to console, traces AND logs, even when
		// RUST_LOG=trace. Application emits safe, fixed auth outcome events instead.
		.with(tracing_subscriber::filter::filter_fn(|metadata| {
			!metadata.target().starts_with("webauthn")
				&& !metadata.target().starts_with("lettre")
				&& !metadata.target().starts_with("azure_")
				&& !metadata.target().starts_with("typespec")
				&& !metadata.target().starts_with("reqwest")
				&& !metadata.target().starts_with("crypto_glue")
				&& !metadata.target().starts_with("hyper")
				&& !metadata.target().starts_with("h2")
				&& !metadata.target().starts_with("tonic")
				&& !metadata.target().starts_with("sqlx")
				&& !metadata.target().starts_with("pdfium_render")
		}));

	tracing::subscriber::set_global_default(subscriber)
		.map_err(|_| InitError::TracingSubscriberTaken)
}

fn quiet_internals() -> tracing_subscriber::EnvFilter {
	// Keep the HTTP/gRPC stack and the OTel internals quiet in both the
	// console and the outgoing log stream: they speak on every request at
	// trace level.
	tracing_subscriber::EnvFilter::new(concat!(
		"error,",
		"archive_server=info,",
		"archive_db=info,",
		"hyper=off,",
		"tonic=off,",
		"h2=off,",
		"prost=off,",
		"opentelemetry=off,",
		"opentelemetry_sdk=off,",
		"opentelemetry_otlp=off,",
		"tower=off,",
		"axum=off"
	))
}

/// Builds the OTLP span exporter, or `None` when the host runs without an
/// endpoint (export is the only thing the C# `UseOtlpExporter()` call is
/// gated on; console logging always exists).
fn span_exporter(with_export: bool) -> Result<Option<opentelemetry_otlp::SpanExporter>, InitError> {
	if !with_export {
		return Ok(None);
	}
	let exporter = opentelemetry_otlp::SpanExporter::builder()
		.with_tonic()
		.build()?;
	Ok(Some(exporter))
}

fn build_meter_provider(
	resource: &opentelemetry_sdk::Resource,
	interval: Duration,
	with_export: bool,
) -> Result<opentelemetry_sdk::metrics::SdkMeterProvider, InitError> {
	let mut builder =
		opentelemetry_sdk::metrics::SdkMeterProvider::builder().with_resource(resource.clone());

	if with_export {
		let exporter = opentelemetry_otlp::MetricExporter::builder()
			.with_tonic()
			.build()?;
		let reader = opentelemetry_sdk::metrics::PeriodicReader::builder(exporter)
			.with_interval(interval)
			.build();
		builder = builder.with_reader(reader);
	}

	Ok(builder.build())
}

fn build_logger_provider(
	resource: &opentelemetry_sdk::Resource,
	schedule_delay: Duration,
	with_export: bool,
) -> Result<opentelemetry_sdk::logs::SdkLoggerProvider, InitError> {
	let builder =
		opentelemetry_sdk::logs::SdkLoggerProvider::builder().with_resource(resource.clone());

	if !with_export {
		return Ok(builder.build());
	}

	let exporter = opentelemetry_otlp::LogExporter::builder()
		.with_tonic()
		.build()?;
	let batch = opentelemetry_sdk::logs::BatchLogProcessor::builder(exporter)
		.with_batch_config(
			opentelemetry_sdk::logs::BatchConfigBuilder::default()
				.with_scheduled_delay(schedule_delay)
				.build(),
		)
		.build();
	Ok(builder.with_log_processor(batch).build())
}

/// Development always samples (the C# service defaults' `AlwaysOnSampler`);
/// otherwise the spec default applies: parent decides, root spans sample with
/// ratio 1.0. An explicitly configured `OTEL_TRACES_SAMPLER` wins either way —
/// Aspire's dev injection sets `always_on`, which equals the Development
/// branch.
fn process_sampler(development: bool) -> opentelemetry_sdk::trace::Sampler {
	if let Some(configured) = configured_sampler() {
		return configured;
	}
	if development {
		opentelemetry_sdk::trace::Sampler::AlwaysOn
	} else {
		opentelemetry_sdk::trace::Sampler::ParentBased(Box::new(
			opentelemetry_sdk::trace::Sampler::TraceIdRatioBased(1.0),
		))
	}
}

fn configured_sampler() -> Option<opentelemetry_sdk::trace::Sampler> {
	let name = env::var(OTEL_TRACES_SAMPLER).ok()?;
	match name.trim() {
		"always_on" => Some(opentelemetry_sdk::trace::Sampler::AlwaysOn),
		"always_off" => Some(opentelemetry_sdk::trace::Sampler::AlwaysOff),
		"traceidratio" => {
			let ratio = env::var(OTEL_TRACES_SAMPLER_ARG)
				.ok()
				.and_then(|value| value.trim().parse::<f64>().ok())
				.unwrap_or(1.0);
			Some(opentelemetry_sdk::trace::Sampler::TraceIdRatioBased(
				ratio.clamp(0.0, 1.0),
			))
		}
		_ => None,
	}
}

/// Reads a plain env value (the DCP expanded its `{{- ... -}}` template when
/// the process was created), falling back to a default when unset or empty.
fn env_string(name: &str, default_value: &str) -> String {
	env::var(name)
		.ok()
		.filter(|value| !value.is_empty())
		.unwrap_or_else(|| default_value.to_string())
}

/// Millisecond duration variables; malformed values fall back to the default
/// so a stray variable can never break startup.
fn env_millis(name: &str) -> Option<u64> {
	env::var(name)
		.ok()
		.and_then(|value| value.trim().parse().ok())
}

/// `OTEL_RESOURCE_ATTRIBUTES` is a comma-separated, percent-encoded
/// key=value list (OpenTelemetry resource env-var specification). Malformed
/// pairs are skipped, never fatal — a stray value must not break startup.
pub fn resource_attributes(list: &str) -> Vec<KeyValue> {
	list.split(',')
		.filter_map(|pair| {
			let (key, value) = pair.split_once('=')?;
			let key = key.trim();
			if key.is_empty() {
				return None;
			}
			Some(KeyValue::new(key.to_string(), percent_decode(value.trim())))
		})
		.collect()
}

/// Small percent-decoder for resource-attribute values (`%HH` → byte),
/// preserving everything else verbatim. Non-UTF8 sequences decode lossily
/// instead of failing the process at startup.
fn percent_decode(value: &str) -> String {
	let bytes = value.as_bytes();
	let mut decoded = Vec::with_capacity(bytes.len());
	let mut index = 0;
	while index < bytes.len() {
		if bytes[index] == b'%' && bytes.len() - index >= 3 {
			let Some(hex) = std::str::from_utf8(&bytes[index + 1..index + 3])
				.ok()
				.and_then(|hex| u8::from_str_radix(hex, 16).ok())
			else {
				decoded.push(bytes[index]);
				index += 1;
				continue;
			};
			decoded.push(hex);
			index += 3;
		} else {
			decoded.push(bytes[index]);
			index += 1;
		}
	}
	String::from_utf8_lossy(&decoded).into_owned()
}

#[cfg(test)]
mod tests {
	use super::*;

	#[test]
	fn resource_attributes_parse_plain_pairs() {
		let attributes = resource_attributes("a=b,c=d");
		assert_eq!(attributes.len(), 2);
		assert_eq!(attributes[0].key.as_str(), "a");
		assert_eq!(attributes[0].value.as_str(), "b");
		assert_eq!(attributes[1].key.as_str(), "c");
	}

	#[test]
	fn resource_attributes_decode_percent_sequences() {
		let attributes = resource_attributes("service.instance.id=archive-api-rs%2F1");
		assert_eq!(
			attributes[0].value.as_str(),
			"archive-api-rs/1",
			"the DCP instance id lands percent-encoded"
		);
	}

	#[test]
	fn resource_attributes_skip_malformed_pairs() {
		let attributes = resource_attributes("=novalue, ok, key=value=with=equals, ,");
		let keys: Vec<&str> = attributes.iter().map(|a| a.key.as_str()).collect();
		assert_eq!(
			keys,
			vec!["key"],
			"empty keys, separator-less pairs and empties are skipped"
		);
	}

	#[test]
	fn percent_decode_keeps_invalid_sequences() {
		assert_eq!(
			percent_decode("a%2zb"),
			"a%2zb",
			"invalid hex stays literal"
		);
		assert_eq!(percent_decode("trailing%2"), "trailing%2");
		assert_eq!(percent_decode("ok%2Fpath"), "ok/path");
	}

	#[test]
	fn sampler_defaults_match_the_spec() {
		let development = process_sampler(true);
		let production = process_sampler(false);
		assert!(
			matches!(development, opentelemetry_sdk::trace::Sampler::AlwaysOn),
			"Development AlwaysOn mirrors the C# service defaults"
		);
		assert!(
			matches!(
				production,
				opentelemetry_sdk::trace::Sampler::ParentBased(_)
			),
			"Production keeps the spec default: parent-based ratio 1.0"
		);
	}
}
