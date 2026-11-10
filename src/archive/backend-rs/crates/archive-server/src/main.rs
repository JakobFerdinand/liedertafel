//! Entry point of the archive origin: register telemetry, build the router
//! and serve with the same 30-second shutdown budget the C# host carries.

use std::sync::Arc;
use std::time::Duration;

use archive_server::{Origin, build_router, settings::Settings};

const SHUTDOWN_BUDGET: Duration = Duration::from_secs(30);

#[tokio::main]
async fn main() {
	// SQLx and Azure/reqwest enable different Rustls 0.23 providers. Select a
	// process default explicitly so feature unification cannot panic on TLS.
	let _ = rustls::crypto::ring::default_provider().install_default();
	if let Err(error) = run().await {
		eprintln!("archive-server: {error}");
		std::process::exit(1);
	}
}

async fn run() -> Result<(), StartupError> {
	let args: Vec<String> = std::env::args().skip(1).collect();
	if let Some(command) = args.first().filter(|s| {
		matches!(
			s.as_str(),
			"--extract-queue" | "--dispatch-extraction" | "--read-queue" | "--dispatch-reading"
		)
	}) {
		use tracing::Instrument;
		let development = std::env::var("ARCHIVE_ENVIRONMENT").as_deref() == Ok("Development");
		let reading = matches!(command.as_str(), "--read-queue" | "--dispatch-reading");
		let defaults = archive_telemetry::ServiceDefaults::init(
			development,
			if reading {
				"archive-reading"
			} else {
				"archive-extraction"
			},
		)?;
		let cancel = tokio_util::sync::CancellationToken::new();
		let signal = cancel.clone();
		let task = tokio::spawn(async move {
			let mut term =
				tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()).ok();
			tokio::select! {_ = tokio::signal::ctrl_c()=>{},_ = async{if let Some(term)=&mut term{term.recv().await;}else{std::future::pending::<()>().await;}}=>{}}
			signal.cancel();
		});
		let span = if command == "--read-queue" {
			tracing::info_span!("read-queue", otel.status_code = tracing::field::Empty)
		} else if command == "--dispatch-reading" {
			tracing::info_span!("dispatch-reading", otel.status_code = tracing::field::Empty)
		} else if command == "--extract-queue" {
			tracing::info_span!("extract-queue", otel.status_code = tracing::field::Empty)
		} else {
			tracing::info_span!(
				"dispatch-extraction",
				otel.status_code = tracing::field::Empty
			)
		};
		let result = async {
			let result = if reading {
				archive_server::reading::command::run(command, development, &cancel)
					.await
					.map_err(StartupError::Reading)
			} else {
				archive_server::extraction::command::run(command, development, &cancel)
					.await
					.map_err(StartupError::Extraction)
			};
			if result.is_ok() {
				tracing::info!("Archive job {} completed", &command[2..]);
			} else {
				tracing::Span::current().record("otel.status_code", "ERROR");
				tracing::error!("Archive job {} failed", &command[2..]);
			}
			result
		}
		.instrument(span)
		.await;
		task.abort();
		let _ = task.await;
		if !defaults.finish_finite_job() {
			return Err(StartupError::FiniteTelemetry);
		}
		return result;
	}
	if let Some(command) = args.first().filter(|s| {
		matches!(
			s.as_str(),
			"--bootstrap-admin" | "--repair-admin" | "--seed-dev-auth" | "--send-test-mail"
		)
	}) {
		use tracing::Instrument;
		let development = std::env::var("ARCHIVE_ENVIRONMENT").as_deref() == Ok("Development");
		let defaults = archive_telemetry::ServiceDefaults::init(development, "archive-operator")?;
		let span = match command.as_str() {
			"--bootstrap-admin" => {
				tracing::info_span!("bootstrap-admin", otel.status_code = tracing::field::Empty)
			}
			"--repair-admin" => {
				tracing::info_span!("repair-admin", otel.status_code = tracing::field::Empty)
			}
			"--seed-dev-auth" => {
				tracing::info_span!("seed-dev-auth", otel.status_code = tracing::field::Empty)
			}
			_ => tracing::info_span!("send-test-mail", otel.status_code = tracing::field::Empty),
		};
		let result = async {
			let result =
				archive_server::auth::operator::run(command, &args[1..], development).await;
			let job_name = &command[2..];
			if result.is_ok() {
				tracing::info!("Archive job {job_name} completed");
			} else {
				tracing::Span::current().record("otel.status_code", "ERROR");
				tracing::error!("Archive job {job_name} failed");
			}
			result
		}
		.instrument(span)
		.await;
		if !defaults.finish_finite_job() {
			return Err(StartupError::FiniteTelemetry);
		}
		return result.map_err(StartupError::Operator);
	}
	if std::env::args().nth(1).as_deref() == Some("--migrate") {
		let development = std::env::var("ARCHIVE_ENVIRONMENT").as_deref() == Ok("Development");
		let defaults = archive_telemetry::ServiceDefaults::init(development, "archive-migrate")?;
		use tracing::Instrument;
		let result = async {
			let result = archive_db::migrate_from_env().await;
			if result.is_ok() {
				tracing::info!("Archive job migrate completed");
			} else {
				tracing::error!("Archive job migrate failed");
			}
			result
		}
		.instrument(tracing::info_span!("migrate"))
		.await;
		defaults.flush();
		defaults.shutdown();
		return result.map_err(StartupError::Migration);
	}
	let settings = Settings::from_env()?;
	let defaults =
		archive_telemetry::ServiceDefaults::init(settings.development, "archive-server")?;
	let development = settings.development;
	let mut origin = Origin::new(settings);
	origin.uploads = archive_server::catalogue::uploads::UploadOptions::from_env()?;
	origin.extraction = archive_server::extraction::Options::from_env();
	origin.reading = archive_server::reading::Options::from_env(development);
	origin.reading_enabled = origin.reading.enabled;
	origin.reading_queue = Arc::new(
		archive_server::queue::AzureQueue::select(
			std::env::var("ConnectionStrings__archive-queues")
				.ok()
				.as_deref(),
			std::env::var("Archive__Reading__QueueServiceUri")
				.ok()
				.filter(|s| !s.trim().is_empty())
				.or_else(|| std::env::var("Archive__Extraction__QueueServiceUri").ok())
				.as_deref(),
			&origin.reading.queue.queue_name,
			development,
			Arc::new(archive_server::auth::SystemClock),
		)
		.map_err(|_| StartupError::Reading(archive_server::reading::command::CommandError))?,
	);
	origin.extraction_queue = Arc::new(
		archive_server::queue::AzureQueue::select(
			std::env::var("ConnectionStrings__archive-queues")
				.ok()
				.as_deref(),
			std::env::var("Archive__Extraction__QueueServiceUri")
				.ok()
				.as_deref(),
			&origin.extraction.queue_name,
			development,
			Arc::new(archive_server::auth::SystemClock),
		)
		.map_err(|_| StartupError::Extraction(archive_server::extraction::command::CommandError))?,
	);
	origin
		.finalized_hooks
		.push(Arc::new(archive_server::extraction::Hook {
			queue: origin.extraction_queue.clone(),
			clock: Arc::new(archive_server::auth::SystemClock),
		}));
	let config = archive_server::auth::AuthConfig::from_env(development)?;
	let mail = archive_server::auth::mail::from_env(development)?;
	if let Ok(connection) = std::env::var("ConnectionStrings__archive-db") {
		let pool = archive_db::runtime_pool(&connection)?;
		origin = origin.with_auth(archive_server::auth::Auth::new(
			pool,
			config,
			mail,
			Arc::new(archive_server::auth::SystemClock),
		)?);
	}
	let storage_config = archive_server::storage::StorageConfig::from_env(development)?;
	if let Some(auth) = &origin.auth {
		origin.ai = Some(archive_server::ai::Services::from_env(
			auth.pool.clone(),
			auth.clock.clone(),
			development,
		)?);
	}
	let read_lifetime = storage_config.read_lifetime;
	origin = origin.with_storage(
		Arc::new(archive_server::storage::BlobStorage::new(
			storage_config,
			Arc::new(archive_server::auth::SystemClock),
		)?),
		read_lifetime,
	);
	let origin = Arc::new(origin);
	let router = build_router(origin.clone());

	let address = std::net::SocketAddr::from(([0, 0, 0, 0], origin.settings.port));
	let listener = tokio::net::TcpListener::bind(address)
		.await
		.map_err(|source| StartupError::Listen(address, source))?;
	tracing::info!("Archive origin listening on {address}");

	// SIGINT/SIGTERM resolve exactly one knob: the graceful shutdown of axum
	// plus the drain-budget watchdog below, started from the same signal.
	let (signals, _) = tokio::sync::broadcast::channel::<()>(1);
	let signal_task = {
		let signals = signals.clone();
		let shutdown = origin.shutdown.clone();
		tokio::spawn(async move {
			let mut term =
				match tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
					Ok(signal) => signal,
					Err(source) => {
						eprintln!("archive-server: SIGTERM handler could not register: {source}");
						return;
					}
				};
			tokio::select! {
				_ = tokio::signal::ctrl_c() => {},
				_ = term.recv() => {},
			}
			shutdown.cancel();
			let _ = signals.send(());
		})
	};

	let drain_watcher = {
		let mut shutdown_watch = signals.subscribe();
		tokio::spawn(async move {
			if shutdown_watch.recv().await.is_ok() {
				tokio::time::sleep(SHUTDOWN_BUDGET).await;
				eprintln!("archive-server: the 30-second shutdown budget expired");
				std::process::exit(1);
			}
		})
	};

	let mut graceful = signals.subscribe();
	let serve_result = axum::serve(
		listener,
		router.into_make_service_with_connect_info::<std::net::SocketAddr>(),
	)
	.with_graceful_shutdown(async move {
		let _ = graceful.recv().await;
	})
	.await
	.map_err(StartupError::Serve);

	// The watchdog includes model producers and their non-request-cancellable
	// accounting retries. Never flush/shutdown OTLP while producers still exist.
	let accounting_ok = if let Some(ai) = &origin.ai {
		ai.gateway.drain().await
	} else {
		true
	};
	// The budget watchdog must not outlive a successful drain.
	drain_watcher.abort();
	let _ = signal_task.await;
	let _ = drain_watcher.await;

	defaults.flush();
	defaults.shutdown();
	if !accounting_ok {
		return Err(StartupError::AiAccounting);
	}
	tracing::info!("Archive origin was shut down cleanly");
	serve_result
}

/// Startup failures: configuration (including the mandatory dev OTLP
/// endpoint), listener binding and serving. Every path exits nonzero so the
/// apphost resource fails visibly.
#[derive(Debug, thiserror::Error)]
enum StartupError {
	#[error("{0}")]
	Reading(archive_server::reading::command::CommandError),
	#[error("{0}")]
	Ai(#[from] archive_server::ai::Error),
	#[error(
		"KI-Abrechnung konnte nicht vollständig abgeschlossen werden; Reservierungen bleiben bestehen."
	)]
	AiAccounting,
	#[error("{0}")]
	Extraction(archive_server::extraction::command::CommandError),
	#[error("{0}")]
	Storage(#[from] archive_server::storage::StorageError),
	#[error("Archive job telemetry flush failed")]
	FiniteTelemetry,
	#[error("{0}")]
	Operator(#[from] archive_server::auth::operator::OperatorError),
	#[error("{0}")]
	Auth(#[from] archive_server::auth::AuthError),
	#[error("{0}")]
	Migration(#[from] archive_db::MigrationError),
	#[error("{0}")]
	Settings(#[from] archive_server::settings::SettingsError),
	#[error("{0}")]
	Telemetry(#[from] archive_telemetry::InitError),
	#[error("the origin could not listen on {0}: {1}")]
	Listen(std::net::SocketAddr, std::io::Error),
	#[error("the origin failed while serving: {0}")]
	Serve(std::io::Error),
}
