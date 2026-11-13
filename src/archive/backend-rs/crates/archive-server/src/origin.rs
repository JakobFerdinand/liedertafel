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
pub struct Origin {
	pub settings: Settings,
	pub build_requests: Counter<u64>,
	pub auth: Option<Arc<crate::auth::Auth>>,
	pub storage: Option<Arc<dyn crate::storage::Storage>>,
	pub read_ticket_lifetime: chrono::Duration,
	pub uploads: crate::catalogue::uploads::UploadOptions,
	pub draft_lifecycle: Vec<Arc<dyn crate::catalogue::drafts::DraftLifecycle>>,
	pub finalized_hooks: Vec<Arc<dyn crate::catalogue::uploads::FinalizedHook>>,
	pub extraction: crate::extraction::Options,
	pub extraction_queue: Arc<dyn crate::queue::Queue>,
	pub reading_enabled: bool,
	pub reading: crate::reading::Options,
	pub reading_queue: Arc<dyn crate::queue::Queue>,
	pub ai: Option<crate::ai::Services>,
	/// Host signal, separate from HTTP disconnect. Later persisted draft readers
	/// link their owned registry work to this, NOT to a tab's request lifetime.
	pub shutdown: tokio_util::sync::CancellationToken,
	/// Joins complete HTTP reading producers (including native work and durable
	/// outcome storage) independently of a disconnected handler's join handle.
	pub reading_tasks: tokio_util::task::TaskTracker,
}
impl std::fmt::Debug for Origin {
	fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.debug_struct("Origin")
			.field("settings", &self.settings)
			.finish_non_exhaustive()
	}
}

impl Origin {
	/// Owned work outlives a handler, not its telemetry parent. Tokio tasks do
	/// not inherit tracing spans automatically; preserve it at ownership transfer.
	pub fn spawn_reading<F>(&self, future: F) -> tokio::task::JoinHandle<F::Output>
	where
		F: std::future::Future + Send + 'static,
		F::Output: Send + 'static,
	{
		use tracing::Instrument;
		self.reading_tasks.spawn(future.in_current_span())
	}
	/// Call only AFTER signalling shutdown and awaiting Axum. Detached persisted
	/// consumers must finish native work/outcome storage before Gateway accounting
	/// drain and terminal telemetry. This ordering cannot wait on an HTTP response.
	pub async fn drain_readings(&self) -> bool {
		self.shutdown.cancel();
		self.reading_tasks.close();
		self.reading_tasks.wait().await;
		if let Some(ai) = &self.ai {
			ai.gateway.drain().await
		} else {
			true
		}
	}
	pub fn new(settings: Settings) -> Self {
		Self {
			auth: None,
			storage: None,
			read_ticket_lifetime: chrono::Duration::minutes(15),
			uploads: crate::catalogue::uploads::UploadOptions::default(),
			draft_lifecycle: Vec::new(),
			finalized_hooks: Vec::new(),
			extraction: Default::default(),
			extraction_queue: Arc::new(crate::queue::AzureQueue::unconfigured()),
			reading_enabled: false,
			reading: Default::default(),
			reading_queue: Arc::new(crate::queue::AzureQueue::unconfigured()),
			ai: None,
			shutdown: Default::default(),
			reading_tasks: Default::default(),
			build_requests: global::meter(METER_NAME)
				.u64_counter("archive.build.requests")
				.with_description("Build-probe requests to /api/build")
				.build(),
			settings,
		}
	}

	pub fn with_auth(mut self, auth: crate::auth::Auth) -> Self {
		if self.ai.is_none() {
			let (_, options) =
				tokio::sync::watch::channel(crate::ai::Options::shipped(self.settings.development));
			let ledger = crate::ai::Ledger::new(
				Arc::new(crate::ai::PostgresStore::new(auth.pool.clone())),
				options,
				auth.clock.clone(),
			);
			self.ai = Some(crate::ai::Services {
				gateway: crate::ai::Gateway::scripted(
					ledger.clone(),
					crate::ai::Script::new(Vec::new()),
				),
				ledger,
			});
		}
		self.auth = Some(Arc::new(auth));
		self
	}
	pub fn with_storage(
		mut self,
		storage: Arc<dyn crate::storage::Storage>,
		read_ticket_lifetime: chrono::Duration,
	) -> Self {
		self.storage = Some(storage);
		self.read_ticket_lifetime = read_ticket_lifetime;
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
