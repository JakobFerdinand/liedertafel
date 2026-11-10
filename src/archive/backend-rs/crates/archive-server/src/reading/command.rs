//! Finite command wiring follows the shared Queue drain.
use super::*;
use crate::{
	auth::{Clock, SystemClock},
	queue::{AzureQueue, DrainSettings, QueueError},
	storage::{BlobStorage, StorageConfig},
};
use std::sync::Arc;
#[derive(Debug, thiserror::Error)]
#[error("Leseauftrag fehlgeschlagen.")]
pub struct CommandError;
pub async fn run(
	command: &str,
	development: bool,
	cancel: &tokio_util::sync::CancellationToken,
) -> Result<(), CommandError> {
	let paused =
		|| std::env::var("Archive__MaintenanceMode").is_ok_and(|v| v.eq_ignore_ascii_case("true"));
	if paused() {
		tracing::info!("Reading paused: maintenance mode is enabled");
		return Ok(());
	}
	let options = Options::from_env(development);
	let clock: Arc<dyn Clock> = Arc::new(SystemClock);
	let queue = Arc::new(
		AzureQueue::select(
			std::env::var("ConnectionStrings__archive-queues")
				.ok()
				.as_deref(),
			std::env::var("Archive__Reading__QueueServiceUri")
				.ok()
				.filter(|s| !s.trim().is_empty())
				.or_else(|| std::env::var("Archive__Extraction__QueueServiceUri").ok())
				.as_deref(),
			&options.queue.queue_name,
			development,
			clock.clone(),
		)
		.map_err(|_| CommandError)?,
	);
	tokio::select! {biased;_=cancel.cancelled()=>return Err(CommandError),result=queue.ensure()=>result.map_err(|_|CommandError)?}
	let connection = std::env::var("ConnectionStrings__archive-db").map_err(|_| CommandError)?;
	let pool = archive_db::runtime_pool(&connection).map_err(|_| CommandError)?;
	let ai = crate::ai::Services::from_env_with_script(
		pool.clone(),
		clock.clone(),
		development,
		scripted_scan_reader(),
	)
	.map_err(|_| CommandError)?;
	let model = ScanReadingModel::new(ai.gateway.clone(), options.clone());
	let result=async{
  let dispatched=tokio::select!{biased;_=cancel.cancelled()=>return Err(CommandError),result=dispatch(&pool,queue.as_ref(),&ai.ledger,&model,&options,clock.as_ref())=>result.map_err(|_|CommandError)?};
  tracing::info!(dispatched,"Reading dispatch completed");
  if command=="--dispatch-reading"{let notices=crate::ai::Notices::new(ai.ledger.clone());tokio::select!{_=cancel.cancelled()=>return Err(CommandError),result=notices.review()=>{result.map_err(|_|CommandError)?;}}return Ok(());}
  if !queue.configured(){return Err(CommandError);}
  PdfPageRenderer::from_env().map_err(|_|CommandError)?;
  let storage=Arc::new(BlobStorage::new(StorageConfig::from_env(development).map_err(|_|CommandError)?,clock.clone()).map_err(|_|CommandError)?);
  let mut worker=Worker::new(pool.clone(),storage,clock.clone(),model);worker.maintenance=Arc::new(paused);worker.extraction=crate::extraction::Options::from_env();worker.spool_directory=std::env::var_os("Archive__Pdfium__SpoolDirectory").map(std::path::PathBuf::from).unwrap_or_else(std::env::temp_dir);
  let worker=Arc::new(worker);let parent=cancel.clone();let settings=DrainSettings{batch_size:options.queue.visibility_fit_batch_size(),receive_visibility:options.queue.receive_visibility,time_budget:options.queue.time_budget,max_messages:options.queue.max_messages_per_run,drain_budget:options.queue.drain_budget,backoff:options.queue.visibility_backoff};
  let count=crate::queue::drain(queue.as_ref(),&settings,clock.as_ref(),paused,cancel,move|message,deadline|{let worker=worker.clone();let parent=parent.clone();Box::pin(async move{worker.handle(message,deadline,&parent).await.map_err(|_|QueueError)})}).await.map_err(|_|CommandError)?;
  opentelemetry::global::meter(archive_telemetry::ACTIVITY_SOURCE).u64_counter("archive.reading.processed").build().add(count as u64,&[]);tracing::info!(processed=count,"Reading drain completed");Ok(())
 }.await;
	// This is outside the cancelled command future. Producers/accounting finish
	// before pool close and before the caller's checked finite OTLP flush.
	let healthy = ai.gateway.drain().await;
	pool.close().await;
	if !healthy {
		return Err(CommandError);
	}
	result
}
