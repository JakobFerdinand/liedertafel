//! Exact finite command wiring. No host/schema startup side effects.
use super::*;
use crate::{
	auth::{Clock, SystemClock},
	queue::{AzureQueue, DrainSettings, QueueError},
	storage::{BlobStorage, StorageConfig},
};
use std::sync::Arc;
#[derive(Debug, thiserror::Error)]
#[error("Extraktionsauftrag fehlgeschlagen.")]
pub struct CommandError;
pub async fn run(
	command: &str,
	development: bool,
	cancel: &tokio_util::sync::CancellationToken,
) -> Result<(), CommandError> {
	let paused =
		|| std::env::var("Archive__MaintenanceMode").is_ok_and(|s| s.eq_ignore_ascii_case("true"));
	if paused() {
		tracing::info!("Extraction paused: maintenance mode is enabled");
		return Ok(());
	}
	let options = Options::from_env();
	let clock: Arc<dyn Clock> = Arc::new(SystemClock);
	let queue = Arc::new(
		AzureQueue::select(
			std::env::var("ConnectionStrings__archive-queues")
				.ok()
				.as_deref(),
			std::env::var("Archive__Extraction__QueueServiceUri")
				.ok()
				.as_deref(),
			&options.queue_name,
			development,
			clock.clone(),
		)
		.map_err(|_| CommandError)?,
	);
	let connection = std::env::var("ConnectionStrings__archive-db").map_err(|_| CommandError)?;
	let pool = archive_db::runtime_pool(&connection).map_err(|_| CommandError)?;
	let dispatched = tokio::select! {_ = cancel.cancelled()=>return Err(CommandError),result=dispatch(&pool,queue.as_ref(),&options,clock.as_ref())=>result.map_err(|_|CommandError)?};
	tracing::info!(dispatched, "Extraction dispatch completed");
	if command == "--dispatch-extraction" {
		pool.close().await;
		return Ok(());
	}
	if !queue.configured() {
		return Err(CommandError);
	}
	// Packaging/ABI failures are host configuration, not corrupt document bytes
	// or a reason to consume every Datei's durable retry budget.
	pdf::PdfEngine::from_env().map_err(|_| CommandError)?;
	let storage = Arc::new(
		BlobStorage::new(
			StorageConfig::from_env(development).map_err(|_| CommandError)?,
			clock.clone(),
		)
		.map_err(|_| CommandError)?,
	);
	let reading_enabled = std::env::var("Archive__Reading__Enabled")
		.map(|s| s.eq_ignore_ascii_case("true"))
		.unwrap_or(development);
	let reading_queue = if reading_enabled {
		Some(Arc::new(
			AzureQueue::select(
				std::env::var("ConnectionStrings__archive-queues")
					.ok()
					.as_deref(),
				std::env::var("Archive__Reading__QueueServiceUri")
					.ok()
					.as_deref(),
				&std::env::var("Archive__Reading__QueueName").unwrap_or("archive-reading".into()),
				development,
				clock.clone(),
			)
			.map_err(|_| CommandError)?,
		) as Arc<dyn crate::queue::Queue>)
	} else {
		None
	};
	let worker = Worker {
		pool: pool.clone(),
		storage,
		clock: clock.clone(),
		options: options.clone(),
		maintenance: Arc::new(paused),
		reading_enabled,
		reading_page_limit: std::env::var("Archive__Reading__MaxPagesPerRequest")
			.ok()
			.and_then(|v| v.parse().ok())
			.filter(|v| *v > 0)
			.unwrap_or(20),
		reading_queue,
		spool_directory: std::env::var_os("Archive__Pdfium__SpoolDirectory")
			.map(std::path::PathBuf::from)
			.unwrap_or_else(std::env::temp_dir),
	};
	let settings = DrainSettings {
		batch_size: options.visibility_fit_batch_size(),
		receive_visibility: options.receive_visibility,
		time_budget: options.time_budget,
		max_messages: options.max_messages_per_run,
		drain_budget: options.drain_budget,
		backoff: options.visibility_backoff,
	};
	let worker = Arc::new(worker);
	let parent = cancel.clone();
	let count = crate::queue::drain(
		queue.as_ref(),
		&settings,
		clock.as_ref(),
		paused,
		cancel,
		move |message, deadline| {
			let worker = worker.clone();
			let parent = parent.clone();
			Box::pin(async move {
				worker
					.handle(message, deadline, &parent)
					.await
					.map_err(|_| QueueError)
			})
		},
	)
	.await
	.map_err(|_| CommandError)?;
	opentelemetry::global::meter(archive_telemetry::ACTIVITY_SOURCE)
		.u64_counter("archive.extraction.processed")
		.build()
		.add(count as u64, &[]);
	tracing::info!(processed = count, "Extraction drain completed");
	pool.close().await;
	Ok(())
}
