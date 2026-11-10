//! Supervised producer owns admission, provider and awaited settlement. Dropped
//! consumers cancel delivery, not accounting. Every host must drain before OTLP.
use super::{
	CallSpec, Error, Ledger, ProviderRequest, Request, Script, Update, Usage, estimate_text,
};
use futures::{FutureExt, StreamExt, stream::BoxStream};
use opentelemetry::{
	KeyValue, global,
	trace::{FutureExt as _, SpanKind, Status, TraceContextExt, Tracer},
};
use std::sync::{
	Arc, Mutex,
	atomic::{AtomicBool, Ordering},
};
use tokio::sync::{mpsc, oneshot};
use tokio_util::{sync::CancellationToken, task::TaskTracker};
use tracing_opentelemetry::OpenTelemetrySpanExt;

pub(super) trait Provider: Send + Sync {
	fn stream(&self, request: ProviderRequest) -> BoxStream<'static, Result<Update, Error>>;
}
impl Provider for Script {
	fn stream(&self, request: ProviderRequest) -> BoxStream<'static, Result<Update, Error>> {
		self.stream(request)
	}
}

#[derive(Clone)]
pub struct Gateway {
	inner: Arc<Inner>,
}
struct Inner {
	model: String,
	ledger: Ledger,
	provider: Arc<dyn Provider>,
	tasks: TaskTracker,
	cancel: CancellationToken,
	closed: Mutex<bool>,
	healthy: Arc<AtomicBool>,
}
impl Drop for Inner {
	fn drop(&mut self) {
		self.cancel.cancel();
	}
}
impl Gateway {
	pub fn scripted(ledger: Ledger, script: Script) -> Self {
		Self::new("scripted".into(), ledger, Arc::new(script))
	}
	pub(super) fn new(model: String, ledger: Ledger, provider: Arc<dyn Provider>) -> Self {
		Self {
			inner: Arc::new(Inner {
				model,
				ledger,
				provider,
				tasks: TaskTracker::new(),
				cancel: CancellationToken::new(),
				closed: Mutex::new(false),
				healthy: Arc::new(AtomicBool::new(true)),
			}),
		}
	}
	pub fn model(&self) -> &str {
		&self.inner.model
	}
	/// Synchronous ownership transfer: dropping even an unpolled response future
	/// cannot strand admission/settlement in a request-owned future.
	pub fn start(&self, request: Request, streaming: bool) -> Result<Call, Error> {
		let closed = self.inner.closed.lock().map_err(|_| Error::Closed)?;
		if *closed {
			return Err(Error::Closed);
		}
		let cancel = self.inner.cancel.child_token();
		let producer_cancel = cancel.clone();
		let (tx, rx) = mpsc::channel(1);
		let (done_tx, done_rx) = oneshot::channel();
		let ledger = self.inner.ledger.clone();
		let provider = self.inner.provider.clone();
		let model = self.inner.model.clone();
		let healthy = self.inner.healthy.clone();
		let tags = vec![
			KeyValue::new("gen_ai.operation.name", "chat"),
			KeyValue::new("gen_ai.system", "openai"),
			KeyValue::new("gen_ai.request.model", model.clone()),
			KeyValue::new(
				"archive.ai.feature",
				request
					.operation
					.as_ref()
					.map(|o| o.feature.clone())
					.unwrap_or_default(),
			),
			KeyValue::new(
				"archive.ai.operation.id",
				request
					.operation
					.as_ref()
					.map(|o| o.operation_id.to_string())
					.unwrap_or_default(),
			),
			KeyValue::new(
				"gen_ai.agent.name",
				request.agent.clone().unwrap_or_default(),
			),
		];
		let tracer = global::tracer(archive_telemetry::AI_SOURCE);
		let parent = tracing::Span::current().context();
		let span = tracer
			.span_builder(format!("chat {model}"))
			.with_kind(SpanKind::Client)
			.with_attributes(tags.clone())
			.start_with_context(&tracer, &parent);
		let context = parent.with_span(span);
		let call_context = context.clone();
		self.inner.tasks.spawn(async move {
			let started=std::time::Instant::now();
			let metric_tags=tags.iter().take(3).cloned().collect::<Vec<_>>();
			let result = std::panic::AssertUnwindSafe(async {
				let (normalized, call) = normalize(&ledger,&model,request,streaming)?;
				// Admission is cancellable (including waiting on the month lock).
				// Dropping a SQLx transaction rolls it back. An unknown commit may
				// conservatively retain a reservation, as in the frozen host.
				if producer_cancel.is_cancelled() { return Err(Error::Cancelled); }
				let reservation = tokio::select! { biased;
					_ = producer_cancel.cancelled() => return Err(Error::Cancelled),
					reservation = ledger.reserve(call) => reservation?,
				};
				let mut usage = Usage::default();
				let mut stream = provider.stream(normalized);
				let result = async {
					loop {
						let update = tokio::select! { biased; _=producer_cancel.cancelled()=>return Err(Error::Cancelled), update=stream.next()=>update };
						let Some(update) = update else { return Ok(()); };
						let update = update?;
						let report = update.usage;
						let (ack, received) = oneshot::channel();
						tokio::select! { biased; _=producer_cancel.cancelled()=>return Err(Error::Cancelled), result=tx.send((update,ack))=>result.map_err(|_| Error::Cancelled)? }
						// Don't observe queued/unconsumed final usage on abandonment.
						tokio::select! { biased; result=received=>result.map_err(|_| Error::Cancelled)?, _=producer_cancel.cancelled()=>return Err(Error::Cancelled) }
						usage.observe(report);
					}
				}.await;
				drop(stream);
				if usage.reported {
					let metric=global::meter(archive_telemetry::AI_SOURCE).u64_histogram("gen_ai.client.token.usage").with_unit("{token}").build();
					for (kind,count) in [("input",usage.input),("output",usage.output)] { let mut attributes=metric_tags.clone();attributes.push(KeyValue::new("gen_ai.token.type",kind));metric.record(count.max(0) as u64,&attributes); }
					call_context.span().set_attribute(KeyValue::new("gen_ai.usage.input_tokens",usage.input));call_context.span().set_attribute(KeyValue::new("gen_ai.usage.output_tokens",usage.output));
				}
				if !ledger.settle(reservation,usage).await { healthy.store(false,Ordering::Release); }
				result
			}).catch_unwind().await.unwrap_or_else(|_| { healthy.store(false,Ordering::Release);tracing::error!("KI-Gateway: Produzent fehlgeschlagen; offene Reservierung bleibt bestehen.");Err(Error::Provider) });
			if result.is_err() { call_context.span().set_status(Status::error("AI call failed")); }
			global::meter(archive_telemetry::AI_SOURCE).f64_histogram("gen_ai.client.operation.duration").with_unit("s").build().record(started.elapsed().as_secs_f64(),&metric_tags);
			// No provider exception text/events ever enter this span. The only
			// status description is static, unlike SDK exception auto-recording.
			call_context.span().end();
			drop(tx);
			let _ = done_tx.send(result);
		}.with_context(context));
		drop(closed);
		Ok(Call {
			rx,
			done: Some(done_rx),
			cancel,
		})
	}
	/// Cancels producers, waits through all accounting retries, then returns the
	/// durable-accounting health for finite hosts (false must yield nonzero).
	pub async fn drain(&self) -> bool {
		if let Ok(mut closed) = self.inner.closed.lock() {
			*closed = true;
		}
		self.inner.cancel.cancel();
		self.inner.tasks.close();
		self.inner.tasks.wait().await;
		self.inner.healthy.load(Ordering::Acquire)
	}
}
fn normalize(
	ledger: &Ledger,
	model: &str,
	request: Request,
	streaming: bool,
) -> Result<(ProviderRequest, CallSpec), Error> {
	let operation = request.operation.ok_or(Error::Unavailable)?;
	let mut options = request.options;
	if options
		.model_id
		.as_ref()
		.is_some_and(|s| !s.trim().is_empty() && !s.trim().eq_ignore_ascii_case(model))
	{
		return Err(Error::Unavailable);
	}
	options.model_id = None;
	let configured = ledger.options.borrow().clone();
	let ceiling = configured.max_output_tokens.max(1);
	let output = operation
		.max_output_tokens
		.or(options.max_output_tokens)
		.unwrap_or(ceiling)
		.clamp(1, ceiling);
	options.max_output_tokens = Some(output);
	let (estimate, non_text) = estimate_text(&request.messages, &options)?;
	if non_text && operation.non_text_input_tokens.is_none() {
		return Err(Error::Unavailable);
	}
	let estimate = estimate
		.checked_add(operation.non_text_input_tokens.unwrap_or(0).max(0))
		.ok_or(Error::MoneyOverflow)?;
	let bound = configured.max_input_tokens.max(1);
	if estimate > bound {
		return Err(Error::InputTooLarge { estimate, bound });
	}
	let call = CallSpec {
		feature: operation.feature,
		operation_id: operation.operation_id,
		account_id: operation.account_id,
		model: model.into(),
		estimated_input: estimate,
		max_output: output,
	};
	Ok((
		ProviderRequest {
			messages: request.messages,
			options,
			streaming,
		},
		call,
	))
}
pub struct Call {
	rx: mpsc::Receiver<(Update, oneshot::Sender<()>)>,
	done: Option<oneshot::Receiver<Result<(), Error>>>,
	cancel: CancellationToken,
}
impl Call {
	pub fn cancel(&self) {
		self.cancel.cancel();
	}
	pub async fn next(&mut self) -> Result<Option<Update>, Error> {
		if let Some((update, ack)) = self.rx.recv().await {
			let _ = ack.send(());
			return Ok(Some(update));
		}
		if let Some(done) = self.done.take() {
			done.await.map_err(|_| Error::Provider)??;
		}
		Ok(None)
	}
	pub async fn finish(mut self) -> Result<(), Error> {
		self.cancel.cancel();
		self.rx.close();
		if let Some(done) = self.done.take() {
			done.await.map_err(|_| Error::Provider)?
		} else {
			Ok(())
		}
	}
	pub async fn response(mut self) -> Result<Vec<Update>, Error> {
		let mut result = Vec::new();
		while let Some(update) = self.next().await? {
			result.push(update);
		}
		Ok(result)
	}
}
impl Drop for Call {
	fn drop(&mut self) {
		self.cancel.cancel();
	}
}
