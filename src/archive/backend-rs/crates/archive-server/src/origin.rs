//! The first Rust origin behind the apphost: assemble the routes, the static
//! export and the tracing layer into one router a maintainer can name.

use std::sync::Arc;

use axum::Router;
use axum::routing::get;
use opentelemetry::global;
use opentelemetry::metrics::Counter;

use crate::api;
use crate::frontend;
use crate::settings::Settings;

/// The `Meter` name shared with the C# backend (`Liedertafel.Archive`), so
/// the internal dashboard reads Rust meters in the same estate.
const METER_NAME: &str = archive_telemetry::ACTIVITY_SOURCE;

/// Shared origin state: immutable settings plus the build-request counter,
/// whose instrument carries the C# backend's own meter for schema parity.
#[derive(Debug)]
pub struct Origin {
	pub settings: Settings,
	pub build_requests: Counter<u64>,
	pub auth: Option<Arc<crate::auth::Auth>>,
}

impl Origin {
	pub fn new(settings: Settings) -> Self {
		Self {
			auth: None,
			build_requests: global::meter(METER_NAME)
				.u64_counter("archive.build.requests")
				.with_description("Build-probe requests to /api/build")
				.build(),
			settings,
		}
	}

	pub fn with_auth(mut self, auth: crate::auth::Auth) -> Self {
		self.auth = Some(Arc::new(auth));
		self
	}
}

/// The whole application: `/alive` (and `/health` in Development), the
/// implemented API routes, then the static export. Unknown pages keep their
/// 404 instead of hydrating a mismatched App Router index document; health
/// probes never produce tracing spans.
pub fn build_router(origin: Arc<Origin>) -> Router {
	let static_root = origin.settings.static_root.clone();
	let mut router = api::api_router(origin.clone())
		.route("/alive", get(api::readiness))
		.fallback_service(frontend::static_site(&static_root));

	if origin.settings.development {
		router = router.route("/health", get(api::readiness));
	}

	use tower_http::trace::TraceLayer;
	router.layer(TraceLayer::new_for_http().make_span_with(frontend::ProbeSkipper))
}
