#[derive(Debug, thiserror::Error)]
#[error("Videoauftrag fehlgeschlagen.")]
pub struct CommandError;
pub async fn run(
	command: &str,
	development: bool,
	cancel: &tokio_util::sync::CancellationToken,
) -> Result<(), CommandError> {
	// Frozen GetValue<bool>: absent is false, but present blank/malformed
	// values fail closed. Snapshot before opening dependencies and reuse it
	// for both subsequent gates; process environment is not a reload source.
	let maintenance = match std::env::var("Archive__MaintenanceMode") {
		Err(std::env::VarError::NotPresent) => false,
		Ok(value) => value
			.trim()
			.to_ascii_lowercase()
			.parse()
			.map_err(|_| CommandError)?,
		Err(_) => return Err(CommandError),
	};
	if maintenance {
		tracing::info!("Transcode paused: maintenance mode is enabled");
		return Ok(());
	}
	use crate::{
		auth::{Clock, SystemClock},
		queue::DrainSettings,
		storage::{BlobStorage, StorageConfig},
	};
	use std::sync::Arc;
	let options = super::Options::from_env();
	let clock: Arc<dyn Clock> = Arc::new(SystemClock);
	let queue =
		super::select_queue(&options, development, clock.clone()).map_err(|_| CommandError)?;
	tokio::select! {biased;_=cancel.cancelled()=>return Err(CommandError),result=queue.ensure()=>result.map_err(|_|CommandError)?};
	let connection = std::env::var("ConnectionStrings__archive-db").map_err(|_| CommandError)?;
	let pool = archive_db::runtime_pool(&connection).map_err(|_| CommandError)?;
	let result=async {
		let count=tokio::select!{biased;_=cancel.cancelled()=>return Err(CommandError),result=super::dispatch(&pool,&queue,&options,clock.as_ref())=>result.map_err(|_|CommandError)?};
		tracing::info!(dispatched=count,"Video dispatch completed");
		if command=="--dispatch-videos"{return Ok(());}
		if !queue.configured(){return Err(CommandError);}
		let storage=Arc::new(BlobStorage::new(StorageConfig::from_env(development).map_err(|_|CommandError)?,clock.clone()).map_err(|_|CommandError)?);
		let worker=Arc::new(super::worker::Worker{pool:pool.clone(),storage,clock:clock.clone(),options:options.clone(),maintenance,tasks:Default::default(),directory:std::env::temp_dir()});
		let settings=DrainSettings{batch_size:options.queue.visibility_fit_batch_size(),receive_visibility:options.queue.receive_visibility,time_budget:options.queue.time_budget,max_messages:options.queue.max_messages_per_run,drain_budget:options.queue.drain_budget,backoff:options.queue.visibility_backoff};
		let parent=cancel.clone();let owned=worker.clone();
		let result=crate::queue::drain(&queue,&settings,clock.as_ref(),||maintenance,cancel,move |message,deadline|{let worker=owned.clone();let parent=parent.clone();Box::pin(async move{worker.handle(message,deadline,&parent).await.map_err(|_|crate::queue::QueueError)})}).await;
		worker.tasks.close();worker.tasks.wait().await;
		let count=result.map_err(|_|CommandError)?;
		opentelemetry::global::meter(archive_telemetry::ACTIVITY_SOURCE).u64_counter("archive.video.processed").build().add(count as u64,&[]);
		tracing::info!(processed=count,"Video drain completed");Ok(())
	}.await;
	pool.close().await;
	result
}
