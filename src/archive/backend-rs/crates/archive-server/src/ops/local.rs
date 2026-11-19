use crate::{
	auth::{
		SystemClock,
		mail::{Mail, MailMessage, SmtpMail},
	},
	queue::{self, AzureQueue, Queue},
	storage::{BlobStorage, Storage, StorageConfig},
};
use serde::{Deserialize, Serialize};
use std::{sync::Arc, time::Duration};
use tokio_util::sync::CancellationToken;
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;

#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error(transparent)]
	Storage(#[from] crate::storage::StorageError),
	#[error(transparent)]
	Queue(#[from] queue::QueueError),
	#[error(transparent)]
	Mail(#[from] crate::auth::mail::MailError),
	#[error("Local service configuration is missing.")]
	Configuration,
	#[error("Local service round trip failed.")]
	RoundTrip,
	#[error("Local service work was cancelled or timed out.")]
	Cancelled,
}
fn blobs() -> Result<BlobStorage, Error> {
	let conn =
		std::env::var("ConnectionStrings__archive-blobs").map_err(|_| Error::Configuration)?;
	let config = StorageConfig::select(Some(&conn), None, Some("archive-assets"), true)?;
	Ok(BlobStorage::new(config, Arc::new(SystemClock))?)
}
fn queue(name: &str) -> Result<AzureQueue, Error> {
	let conn =
		std::env::var("ConnectionStrings__archive-queues").map_err(|_| Error::Configuration)?;
	let queue = AzureQueue::select(Some(&conn), None, name, true, Arc::new(SystemClock))?;
	if !queue.configured() {
		return Err(Error::Configuration);
	}
	Ok(queue)
}
pub async fn initialize(cancel: &CancellationToken) -> Result<(), Error> {
	tokio::select! {
		biased;
		_ = cancel.cancelled() => Err(Error::Cancelled),
		result = async {
			let storage = blobs()?;
			storage.ensure_private_container().await?;
			for name in [
				crate::extraction::Options::from_env().queue_name,
				crate::reading::Options::from_env(true).queue.queue_name,
				crate::videos::Options::from_env().queue.queue_name,
			] {
				queue(&name)?.ensure().await?;
			}
			storage.ensure_emulator_cors().await?;
			Ok(())
		} => result,
	}
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct SmokeMessage {
	id: String,
	trace_parent: Option<String>,
	trace_state: Option<String>,
}
pub async fn exercise(cancel: &CancellationToken, timeout: Option<Duration>) -> Result<(), Error> {
	let id = uuid::Uuid::new_v4().simple().to_string();
	let path = format!("diagnostics/{id}.txt");
	let storage = blobs()?;
	let queue = queue(&format!("archive-smoke-{id}"))?;
	let result = tokio::select! {
		biased;
		_ = cancel.cancelled() => Err(Error::Cancelled),
		_ = async { match timeout {
			Some(timeout) => tokio::time::sleep(timeout).await,
			None => std::future::pending::<()>().await,
		} } => Err(Error::Cancelled),
		result = round_trip(&id, &path, &storage, &queue) => result,
	};
	// A single budget covers both cleanup attempts, even after cancellation
	// or a first cleanup error. As in the oracle, Blob runs before Queue.
	let deadline = tokio::time::Instant::now() + Duration::from_secs(15);
	let blob = tokio::time::timeout_at(deadline, storage.delete(&path)).await;
	let scratch = tokio::time::timeout_at(deadline, queue.delete_scratch()).await;
	result?;
	blob.map_err(|_| Error::Cancelled)??;
	scratch.map_err(|_| Error::Cancelled)??;
	Ok(())
}
async fn round_trip(
	id: &str,
	path: &str,
	storage: &BlobStorage,
	queue: &AzureQueue,
) -> Result<(), Error> {
	use futures::TryStreamExt;
	storage
		.upload_diagnostic(path, id.as_bytes().to_vec())
		.await?;
	let parts: Vec<_> = storage
		.open_read_bounded(path, id.len() as u64)
		.await?
		.ok_or(Error::RoundTrip)?
		.try_collect()
		.await?;
	if parts.concat() != id.as_bytes() {
		return Err(Error::RoundTrip);
	}
	queue.ensure().await?;
	async {
		let mut carrier = queue::trace_carrier();
		let payload = serde_json::to_string(&SmokeMessage {
			id: id.to_owned(),
			trace_parent: carrier.remove("traceparent"),
			trace_state: carrier.remove("tracestate"),
		})
		.map_err(|_| Error::RoundTrip)?;
		if !queue.send(&payload).await? {
			return Err(Error::RoundTrip);
		}
		Ok(())
	}
	.instrument(tracing::info_span!(
		"archive.queue.send",
		otel.kind = "producer"
	))
	.await?;
	let message = queue.receive(1, 30).await?.pop().ok_or(Error::RoundTrip)?;
	let envelope: SmokeMessage =
		serde_json::from_str(&message.body).map_err(|_| Error::RoundTrip)?;
	let span = tracing::info_span!("archive.queue.process", otel.kind = "consumer");
	let _ = span.set_parent(queue::remote_context(&queue::Envelope {
		kind: None,
		datei_id: uuid::Uuid::nil(),
		trace_parent: envelope.trace_parent,
		trace_state: envelope.trace_state,
	}));
	async {
		if envelope.id != id {
			return Err(Error::RoundTrip);
		}
		queue.delete(&message).await?;
		Ok(())
	}
	.instrument(span)
	.await?;
	let mail = SmtpMail {
		host: std::env::var("Mail__Host").map_err(|_| Error::Configuration)?,
		port: std::env::var("Mail__Port")
			.ok()
			.and_then(|p| p.parse().ok())
			.ok_or(Error::Configuration)?,
		from: "Liedertafel Archiv <archiv@liedertafel.test>".into(),
	};
	mail.send(MailMessage {
		to: "Lokale Entwicklung <entwicklung@liedertafel.test>".into(),
		subject: "Archiv: lokaler Funktionstest".into(),
		text: "Blob und Warteschlange sind erreichbar. Diese Nachricht bleibt im lokalen Mailpostfach.".into(),
	}).instrument(tracing::info_span!("archive.mail.send",otel.kind="client")).await?;
	Ok(())
}
