//! Official singular Queue SDK; same credentials, signing and bounded transport
//! as Blob. Receipt/body fields deliberately have no Debug implementation.
use super::*;
use azure_storage_queue::{
	QueueClient, QueueServiceClient, QueueServiceClientOptions,
	models::{QueueClientReceiveMessagesOptions, QueueMessage},
};

pub struct Message {
	pub id: String,
	pub receipt: String,
	pub body: String,
	pub dequeue_count: i64,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Disposition {
	Delete,
	Abandon(Duration),
}
pub struct DrainSettings {
	pub batch_size: usize,
	pub receive_visibility: Duration,
	pub time_budget: Duration,
	pub max_messages: usize,
	pub drain_budget: Duration,
	pub backoff: Duration,
}
pub fn seconds(value: Duration) -> Result<i32, QueueError> {
	i32::try_from(value.num_seconds()).map_err(|_| QueueError)
}
/// Shared finite pump for later domain workers. Each handler reloads durable state
/// from the pool: there is no cross-message tracked row/cache or open transaction.
pub async fn drain<F>(
	source: &dyn Queue,
	settings: &DrainSettings,
	clock: &dyn Clock,
	maintenance: impl Fn() -> bool,
	cancel: &tokio_util::sync::CancellationToken,
	mut handle: F,
) -> Result<usize, QueueError>
where
	F: for<'a> FnMut(
		&'a Message,
		chrono::DateTime<chrono::Utc>,
	) -> BoxFuture<'a, Result<Disposition, QueueError>>,
{
	let deadline = clock.now() + settings.drain_budget;
	let mut processed = 0;
	while processed < settings.max_messages {
		if cancel.is_cancelled() {
			return Err(QueueError);
		}
		if maintenance() || clock.now() >= deadline {
			break;
		}
		let batch_deadline = clock.now() + settings.receive_visibility;
		let messages = tokio::select! {_ = cancel.cancelled()=>return Err(QueueError),result=source.receive(settings.batch_size.min(settings.max_messages-processed),seconds(settings.receive_visibility)?)=>result?};
		if messages.is_empty() {
			break;
		}
		let mut batch_exhausted = false;
		for message in messages {
			if cancel.is_cancelled() {
				return Err(QueueError);
			}
			let now = clock.now();
			batch_exhausted |= now >= batch_deadline;
			let interrupted = batch_exhausted || now >= deadline || maintenance();
			let disposition = if interrupted {
				Disposition::Abandon(Duration::zero())
			} else {
				let result = tokio::select! {_ = cancel.cancelled()=>return Err(QueueError),result=handle(&message,(now+settings.time_budget).min(batch_deadline))=>result};
				processed += 1;
				result.unwrap_or(Disposition::Abandon(settings.backoff))
			};
			let result = tokio::select! {_ = cancel.cancelled()=>return Err(QueueError),result=async{match disposition{Disposition::Delete=>source.delete(&message).await,Disposition::Abandon(delay)=>source.abandon(&message,seconds(delay)?).await}}=>result};
			if result.is_err() {
				tracing::warn!("Queue message disposition failed");
			}
		}
		if batch_exhausted {
			break;
		}
	}
	Ok(processed)
}
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Envelope {
	#[serde(rename = "type")]
	pub kind: Option<String>,
	pub datei_id: uuid::Uuid,
	pub trace_parent: Option<String>,
	pub trace_state: Option<String>,
}
pub fn parse_envelope(body: &str, kind: &str) -> Option<Envelope> {
	let value =
		crate::binding::parse(body.as_bytes(), crate::binding::Shape::QueueEnvelope).ok()?;
	let envelope = Envelope {
		kind: value["type"].as_str().map(str::to_owned),
		datei_id: uuid::Uuid::parse_str(value["dateiid"].as_str()?).ok()?,
		trace_parent: value["traceparent"].as_str().map(str::to_owned),
		trace_state: value["tracestate"].as_str().map(str::to_owned),
	};
	if envelope.kind.as_deref() != Some(kind) || envelope.datei_id.is_nil() {
		None
	} else {
		Some(envelope)
	}
}
pub fn remote_context(envelope: &Envelope) -> opentelemetry::Context {
	use opentelemetry::propagation::TextMapPropagator;
	let mut carrier = std::collections::HashMap::new();
	if let Some(p) = &envelope.trace_parent {
		carrier.insert("traceparent".into(), p.clone());
	}
	if let Some(s) = &envelope.trace_state {
		carrier.insert("tracestate".into(), s.clone());
	}
	opentelemetry_sdk::propagation::TraceContextPropagator::new().extract(&carrier)
}
#[derive(Debug, Clone, Copy, thiserror::Error)]
#[error("Warteschlangendienst nicht verfügbar.")]
pub struct QueueError;
impl From<azure_core::Error> for QueueError {
	fn from(_: azure_core::Error) -> Self {
		Self
	}
}
pub trait Queue: Send + Sync {
	fn send<'a>(&'a self, body: &'a str) -> BoxFuture<'a, Result<bool, QueueError>>;
	fn receive(
		&self,
		size: usize,
		visibility: i32,
	) -> BoxFuture<'_, Result<Vec<Message>, QueueError>>;
	fn delete<'a>(&'a self, message: &'a Message) -> BoxFuture<'a, Result<(), QueueError>>;
	fn abandon<'a>(
		&'a self,
		message: &'a Message,
		visibility: i32,
	) -> BoxFuture<'a, Result<(), QueueError>>;
}
pub struct AzureQueue {
	client: Option<QueueClient>,
}
/// Quiet outbox notification only: never repeat an accepted send merely because
/// the caller's later stamp fails. Dispatch deliberately sends only once.
pub async fn quiet_send(queue: &dyn Queue, body: &str) -> bool {
	for _ in 0..2 {
		match queue.send(body).await {
			Ok(accepted) => return accepted,
			Err(_) => tracing::warn!("Queue enqueue failed"),
		}
	}
	false
}
impl AzureQueue {
	pub fn unconfigured() -> Self {
		Self { client: None }
	}
	pub fn select(
		connection: Option<&str>,
		uri: Option<&str>,
		name: &str,
		development: bool,
		clock: Arc<dyn Clock>,
	) -> Result<Self, QueueError> {
		let config = if let Some(connection) = connection.filter(|s| !s.is_empty()) {
			StorageConfig::connection_string_for(connection, name, "Queue")
		} else {
			StorageConfig::select(None, uri, Some(name), development)
		}
		.map_err(|_| QueueError)?;
		let Some(endpoint) = config.endpoint else {
			return Ok(Self { client: None });
		};
		let mut options = QueueServiceClientOptions {
			client_options: client_options(None).map_err(|_| QueueError)?,
			..Default::default()
		};
		let credential = if let Some(key) = config.key {
			options.version = shared_key::EMULATOR_VERSION.into();
			options.client_options.per_try_policies.push(Arc::new(
				shared_key::SharedKeyPolicy::new(config.account, key, clock),
			));
			None
		} else {
			credential(true, development).map_err(|_| QueueError)?
		};
		let client =
			QueueServiceClient::new(endpoint, credential, Some(options))?.queue_client(name)?;
		Ok(Self {
			client: Some(client),
		})
	}
	pub fn configured(&self) -> bool {
		self.client.is_some()
	}
	pub async fn ensure(&self) -> Result<(), QueueError> {
		if let Some(client) = &self.client {
			match client.create(None).await {
				Ok(_) => {}
				Err(e) if matches!(e.kind(),azure_core::error::ErrorKind::HttpResponse{status:azure_core::http::StatusCode::Conflict,error_code:Some(code),..} if code=="QueueAlreadyExists") =>
					{}
				Err(_) => return Err(QueueError),
			}
		}
		Ok(())
	}
}

/// Only W3C context, never baggage or provider metadata.
pub fn envelope(kind: &str, id: uuid::Uuid) -> String {
	use opentelemetry::propagation::TextMapPropagator;
	use tracing_opentelemetry::OpenTelemetrySpanExt;
	let mut carrier = std::collections::HashMap::new();
	opentelemetry_sdk::propagation::TraceContextPropagator::new()
		.inject_context(&tracing::Span::current().context(), &mut carrier);
	serde_json::json!({"type":kind,"dateiId":id,"traceParent":carrier.get("traceparent"),"traceState":carrier.get("tracestate")}).to_string()
}
impl Queue for AzureQueue {
	fn send<'a>(&'a self, body: &'a str) -> BoxFuture<'a, Result<bool, QueueError>> {
		Box::pin(async move {
			let Some(client) = &self.client else {
				return Ok(false);
			};
			// Azure non_exhaustive model may gain fields; keep its default update.
			#[allow(clippy::needless_update)]
			let request = QueueMessage {
				message_text: Some(body.into()),
				..Default::default()
			};
			client
				.send_message(RequestContent::try_from(request)?, None)
				.await?;
			Ok(true)
		})
	}
	fn receive(
		&self,
		size: usize,
		visibility: i32,
	) -> BoxFuture<'_, Result<Vec<Message>, QueueError>> {
		Box::pin(async move {
			if !(1..=32).contains(&size) || !(1..=604800).contains(&visibility) {
				return Err(QueueError);
			}
			let response = self
				.client
				.as_ref()
				.ok_or(QueueError)?
				.receive_messages(Some(QueueClientReceiveMessagesOptions {
					number_of_messages: Some(size as i32),
					visibility_timeout: Some(visibility),
					..Default::default()
				}))
				.await?
				.into_model()?;
			response
				.items
				.unwrap_or_default()
				.into_iter()
				.map(|m| {
					let count = m.dequeue_count.filter(|n| *n > 0).ok_or(QueueError)?;
					Ok(Message {
						id: m.message_id.filter(|s| !s.is_empty()).ok_or(QueueError)?,
						receipt: m.pop_receipt.filter(|s| !s.is_empty()).ok_or(QueueError)?,
						body: m.message_text.ok_or(QueueError)?,
						dequeue_count: count,
					})
				})
				.collect()
		})
	}
	fn delete<'a>(&'a self, message: &'a Message) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async move {
			self.client
				.as_ref()
				.ok_or(QueueError)?
				.delete_message(&message.id, &message.receipt, None)
				.await?;
			Ok(())
		})
	}
	fn abandon<'a>(
		&'a self,
		message: &'a Message,
		visibility: i32,
	) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async move {
			if !(0..=604800).contains(&visibility) {
				return Err(QueueError);
			}
			// None is NO BODY: preserve the original envelope; this receipt is done.
			self.client
				.as_ref()
				.ok_or(QueueError)?
				.update_message(&message.id, &message.receipt, visibility, None)
				.await?;
			Ok(())
		})
	}
}
