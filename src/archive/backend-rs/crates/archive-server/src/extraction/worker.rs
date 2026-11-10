use super::{
	Job, Options,
	pdf::{self, Outcome, PdfEngine},
	service,
};
use crate::{
	auth::Clock,
	catalogue::queries,
	queue::{Disposition, Message, Queue},
	storage::Storage,
};
use chrono::{DateTime, Duration, Utc};
use futures::StreamExt;
use std::{path::PathBuf, sync::Arc};
use tokio_util::sync::CancellationToken;
use tracing::{Instrument, info_span};
use tracing_opentelemetry::OpenTelemetrySpanExt;

pub const EXHAUSTED: &str = "Die Verarbeitung ist nach mehreren Versuchen fehlgescheitert.";
#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error("Extraktion abgebrochen.")]
	Cancelled,
	#[error("Extraktionsspeicher nicht verfügbar.")]
	Database,
}
impl From<sqlx::Error> for Error {
	fn from(_: sqlx::Error) -> Self {
		Self::Database
	}
}
pub struct Worker {
	pub pool: sqlx::PgPool,
	pub storage: Arc<dyn Storage>,
	pub options: Options,
	pub clock: Arc<dyn Clock>,
	pub maintenance: Arc<dyn Fn() -> bool + Send + Sync>,
	pub reading_enabled: bool,
	pub reading_page_limit: i32,
	pub reading_queue: Option<Arc<dyn Queue>>,
	pub spool_directory: PathBuf,
}
impl Worker {
	pub async fn handle(
		&self,
		message: &Message,
		deadline: DateTime<Utc>,
		parent: &CancellationToken,
	) -> Result<Disposition, Error> {
		let Some(envelope) = crate::queue::parse_envelope(&message.body, "extraction") else {
			tracing::warn!("Extraction message discarded: invalid envelope");
			return Ok(Disposition::Delete);
		};
		let span = info_span!("archive.queue.process", otel.kind = "consumer");
		let _ = span.set_parent(crate::queue::remote_context(&envelope));
		self.process(envelope.datei_id, message, deadline, parent)
			.instrument(span)
			.await
	}
	async fn process(
		&self,
		id: uuid::Uuid,
		message: &Message,
		deadline: DateTime<Utc>,
		parent: &CancellationToken,
	) -> Result<Disposition, Error> {
		if parent.is_cancelled() {
			return Err(Error::Cancelled);
		}
		let remaining = (deadline - self.clock.now()).to_std().unwrap_or_default();
		let budget = parent.child_token();
		let _guard = budget.clone().drop_guard();
		let mut lease = None;
		let mut attempt = Box::pin(self.attempt(id, message, &mut lease, &budget, deadline));
		let result = tokio::select! {
			biased;
			_ = parent.cancelled()=>return Err(Error::Cancelled),
			_ = tokio::time::sleep(remaining)=>None,
			result = &mut attempt=>Some(result),
		};
		// Drop the query/Blob future before re-reading the lease after expiry.
		drop(attempt);
		if let Some(result) = result {
			return result;
		}
		budget.cancel();
		tracing::warn!("Extraction exceeded its time budget");
		let Some(fresh) = Job::load(&self.pool, id).await? else {
			return Ok(self.backoff(message));
		};
		if fresh.terminal() {
			return Ok(Disposition::Delete);
		}
		if let Some((version, count)) = lease
			&& fresh.status == 1
			&& fresh.row_version == version
		{
			return self.retry(&fresh, message, count).await;
		}
		Ok(self.backoff(message))
	}
	async fn attempt(
		&self,
		id: uuid::Uuid,
		message: &Message,
		lease: &mut Option<(i64, i32)>,
		budget: &CancellationToken,
		deadline: DateTime<Utc>,
	) -> Result<Disposition, Error> {
		if self.clock.now() >= deadline {
			return Ok(self.backoff(message));
		}
		if (self.maintenance)() {
			return Ok(Disposition::Abandon(self.options.visibility_backoff));
		}
		let Some(datei) = queries::dateien(&self.pool, None, Some(id)).await?.pop() else {
			return Ok(Disposition::Delete);
		};
		if !service::extractable(&datei) {
			return Ok(Disposition::Delete);
		}
		if Job::load(&self.pool, id).await?.is_none() {
			let mut connection = self.pool.acquire().await?;
			if let Err(e) = service::insert(
				&mut connection,
				id,
				datei.created_by_account_id,
				self.clock.now(),
			)
			.await && !e
				.as_database_error()
				.is_some_and(|e| e.is_unique_violation())
			{
				return Err(e.into());
			}
		}
		let Some(mut job) = Job::load(&self.pool, id).await? else {
			return Ok(self.backoff(message));
		};
		if job.terminal() {
			return Ok(Disposition::Delete);
		}
		if job.status == 1 && !job.stale(self.clock.now(), &self.options) {
			return Ok(Disposition::Abandon(self.options.visibility_backoff));
		}
		if job.attempt_count >= self.options.max_attempts {
			return self
				.save(
					&job,
					4,
					job.text.as_deref(),
					Some(EXHAUSTED),
					false,
					Disposition::Delete,
				)
				.await;
		}
		if job.status != 1 && i128::from(datei.size_bytes) > i128::from(self.options.max_pdf_bytes)
		{
			return self
				.save(
					&job,
					4,
					job.text.as_deref(),
					Some(pdf::TOO_LARGE),
					false,
					Disposition::Delete,
				)
				.await;
		}
		let now = self.clock.now();
		let won=sqlx::query(r#"UPDATE extraction_jobs SET "Status"=1,"AttemptCount"="AttemptCount"+1,"LastAttemptAt"=$3,"UpdatedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(id).bind(job.row_version).bind(now).execute(&self.pool).await?.rows_affected();
		if won == 0 {
			return Ok(Disposition::Abandon(self.options.visibility_backoff));
		}
		job.status = 1;
		job.attempt_count += 1;
		job.row_version += 1;
		*lease = Some((job.row_version, job.attempt_count));
		if i128::from(datei.size_bytes) > i128::from(self.options.max_pdf_bytes) {
			return self
				.save(
					&job,
					4,
					job.text.as_deref(),
					Some(pdf::TOO_LARGE),
					false,
					Disposition::Delete,
				)
				.await;
		}
		let outcome = self.extract(&datei.blob_name, budget, deadline).await;
		if budget.is_cancelled() || self.clock.now() >= deadline {
			return self.retry(&job, message, job.attempt_count).await;
		}
		match outcome {
			Ok(outcome) => {
				let (status, text, reason) = match outcome {
					Outcome::Text(t) => (2, Some(t), None),
					Outcome::NoText => (3, None, None),
					Outcome::Failure(r) => (4, None, Some(r)),
				};
				let handoff = self.reading_enabled
					&& matches!(status, 2 | 3)
					&& datei.kind == "noten"
					&& datei.lied_id.is_some();
				self.save(
					&job,
					status,
					text.as_deref(),
					reason,
					handoff,
					Disposition::Delete,
				)
				.await
			}
			Err(_) => self.retry(&job, message, job.attempt_count).await,
		}
	}
	async fn extract(
		&self,
		blob: &str,
		budget: &CancellationToken,
		deadline: DateTime<Utc>,
	) -> Result<Outcome, pdf::Error> {
		let mut stream = self
			.storage
			.open_read_bounded(blob, self.options.max_pdf_bytes)
			.await
			.map_err(|_| pdf::Error::Io)?
			.ok_or(pdf::Error::Io)?;
		let spool = Spool::new(&self.spool_directory).map_err(|_| pdf::Error::Io)?;
		let mut file = tokio::fs::OpenOptions::new()
			.write(true)
			.open(&spool.0)
			.await
			.map_err(|_| pdf::Error::Io)?;
		let mut stored = 0u64;
		while let Some(bytes) = stream.next().await {
			if budget.is_cancelled() || self.clock.now() >= deadline {
				return Err(pdf::Error::Cancelled);
			}
			let bytes = bytes.map_err(|_| pdf::Error::Io)?;
			let allowed = self.options.max_pdf_bytes.saturating_sub(stored);
			if bytes.len() as u64 > allowed {
				return Ok(Outcome::Failure(pdf::TOO_LARGE));
			}
			use tokio::io::AsyncWriteExt;
			file.write_all(&bytes).await.map_err(|_| pdf::Error::Io)?;
			stored += bytes.len() as u64;
		}
		drop(stream);
		drop(file);
		let engine = PdfEngine::from_env()?;
		let options = self.options.clone();
		let budget = budget.clone();
		let clock = self.clock.clone();
		tokio::task::spawn_blocking(move || {
			let result = engine.extract(&spool.0, &options, || {
				budget.is_cancelled() || clock.now() >= deadline
			});
			drop(spool);
			result
		})
		.await
		.map_err(|_| pdf::Error::Io)?
	}
	fn backoff(&self, message: &Message) -> Disposition {
		let millis = i128::from(self.options.visibility_backoff.num_milliseconds())
			* i128::from(message.dequeue_count.max(0));
		Disposition::Abandon(Duration::milliseconds(
			millis.min(i128::from(self.options.redisplay_after.num_milliseconds())) as i64,
		))
	}
	async fn retry(&self, job: &Job, message: &Message, count: i32) -> Result<Disposition, Error> {
		if count >= self.options.max_attempts
			|| message.dequeue_count >= i64::from(self.options.max_attempts)
		{
			self.save(
				job,
				4,
				job.text.as_deref(),
				Some(EXHAUSTED),
				false,
				Disposition::Delete,
			)
			.await
		} else {
			self.save(
				job,
				0,
				job.text.as_deref(),
				job.failure_reason.as_deref(),
				false,
				self.backoff(message),
			)
			.await
		}
	}
	async fn save(
		&self,
		job: &Job,
		status: i32,
		text: Option<&str>,
		reason: Option<&str>,
		handoff: bool,
		disposition: Disposition,
	) -> Result<Disposition, Error> {
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		let stamp_attempt =
			reason == Some(EXHAUSTED) || (job.status != 1 && reason == Some(pdf::TOO_LARGE));
		let won=sqlx::query(r#"UPDATE extraction_jobs SET "Status"=$3,"Text"=$4,"FailureReason"=$5,"CompletedAt"=$6,"UpdatedAt"=$7,"LastAttemptAt"=CASE WHEN $8 THEN $7 ELSE "LastAttemptAt" END,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(job.datei_id).bind(job.row_version).bind(status).bind(text).bind(reason).bind(matches!(status,2|3).then_some(now)).bind(now).bind(stamp_attempt).execute(&mut *tx).await?.rows_affected();
		if won == 0 {
			tx.rollback().await?;
			let fresh = Job::load(&self.pool, job.datei_id).await?;
			return Ok(if fresh.is_some_and(|j| j.terminal()) {
				Disposition::Delete
			} else {
				Disposition::Abandon(self.options.visibility_backoff)
			});
		}
		let inserted = if handoff {
			sqlx::query(r#"INSERT INTO reading_jobs ("DateiId","Status","PagesRead","PageLimit","FieldsDone","FieldsApplied","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,0,0,$4,false,0,0,$2,$3,$3,0) ON CONFLICT ("DateiId") DO NOTHING"#).bind(job.datei_id).bind(job.triggered_by_account_id).bind(now).bind(if self.reading_page_limit>0{self.reading_page_limit}else{20}).execute(&mut *tx).await?.rows_affected()>0
		} else {
			false
		};
		tx.commit().await?;
		if inserted
			&& let Some(queue) = &self.reading_queue
			&& crate::queue::quiet_send(
				queue.as_ref(),
				&crate::queue::envelope("reading", job.datei_id),
			)
			.await
		{
			let _ =
				sqlx::query(r#"UPDATE reading_jobs SET "LastEnqueuedAt"=$2 WHERE "DateiId"=$1"#)
					.bind(job.datei_id)
					.bind(self.clock.now())
					.execute(&self.pool)
					.await;
		}
		Ok(disposition)
	}
}
struct Spool(PathBuf);
impl Spool {
	fn new(directory: &std::path::Path) -> std::io::Result<Self> {
		use std::os::unix::fs::OpenOptionsExt;
		let path = directory.join(format!("226-pdf-{}.spool", uuid::Uuid::new_v4()));
		std::fs::OpenOptions::new()
			.create_new(true)
			.write(true)
			.mode(0o600)
			.open(&path)?;
		Ok(Self(path))
	}
}
impl Drop for Spool {
	fn drop(&mut self) {
		let _ = std::fs::remove_file(&self.0);
	}
}
