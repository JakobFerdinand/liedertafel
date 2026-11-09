//! Entry point of the archive origin: register telemetry, build the router
//! and serve with the same 30-second shutdown budget the C# host carries.

use std::sync::Arc;
use std::time::Duration;

use archive_server::{Origin, build_router, settings::Settings};

const SHUTDOWN_BUDGET: Duration = Duration::from_secs(30);

#[tokio::main]
async fn main() {
	if let Err(error) = run().await {
		eprintln!("archive-server: {error}");
		std::process::exit(1);
	}
}

async fn run() -> Result<(), StartupError> {
	let settings = Settings::from_env()?;
	let defaults =
		archive_telemetry::ServiceDefaults::init(settings.development, "archive-server")?;
	let origin = Arc::new(Origin::new(settings));
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
	let serve_result = axum::serve(listener, router)
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
	Settings(#[from] archive_server::settings::SettingsError),
	#[error("{0}")]
	Telemetry(#[from] archive_telemetry::InitError),
	#[error("the origin could not listen on {0}: {1}")]
	Listen(std::net::SocketAddr, std::io::Error),
	#[error("the origin failed while serving: {0}")]
	Serve(std::io::Error),
}
