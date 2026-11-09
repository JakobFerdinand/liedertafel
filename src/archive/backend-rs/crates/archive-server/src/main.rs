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

	// The budget watchdog must not outlive a successful drain.
	drain_watcher.abort();
	let _ = signal_task.await;
	let _ = drain_watcher.await;

	defaults.flush();
	defaults.shutdown();
	tracing::info!("Archive origin was shut down cleanly");
	serve_result
}

/// Startup failures: configuration (including the mandatory dev OTLP
/// endpoint), listener binding and serving. Every path exits nonzero so the
/// apphost resource fails visibly.
#[derive(Debug, thiserror::Error)]
enum StartupError {
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
