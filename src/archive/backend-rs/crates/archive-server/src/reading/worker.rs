use super::{Job, PdfPageRenderer, ScanReadingModel};
use crate::{
	auth::Clock,
	extraction::pdf,
	queue::{Disposition, Message},
	storage::Storage,
};
use chrono::{DateTime, Duration, Utc};
use std::sync::Arc;
use tokio_util::sync::CancellationToken;
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;
pub const EXHAUSTED: &str = "Das Lesen ist nach mehreren Versuchen fehlgeschlagen.";
pub const TOO_LARGE: &str = "Das Dokument ist zu groß, um es automatisch zu lesen.";
pub const UNREADABLE: &str = "Das Dokument konnte nicht als Bilder gelesen werden.";
pub const NOTHING_READ: &str = "Auf den Seiten konnte kein Text gelesen werden.";
pub const EXTRACTION_FAILED: &str =
	"Die Textauswertung ist gescheitert; das Lesen der Seiten kann nicht beginnen.";
pub const INPUT_TOO_LARGE: &str = "Die Seiten sind für die automatische Lesung zu groß.";
#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error("Lesung abgebrochen.")]
	Cancelled,
	#[error("Lesungsspeicher nicht verfügbar.")]
	Database,
	#[error("KI-Lesung fehlgeschlagen.")]
	Ai(#[from] crate::ai::Error),
	#[error("Lesungskonflikt.")]
	Race,
	#[error("Dokument nicht lesbar.")]
	Material(&'static str),
	#[error("Dokument vorübergehend nicht verfügbar.")]
	Transient,
}
impl From<sqlx::Error> for Error {
	fn from(_: sqlx::Error) -> Self {
		Self::Database
	}
}
pub struct Worker {
	pub pool: sqlx::PgPool,
	pub storage: Arc<dyn Storage>,
	pub clock: Arc<dyn Clock>,
	pub model: ScanReadingModel,
	pub extraction: crate::extraction::Options,
	pub maintenance: Arc<dyn Fn() -> bool + Send + Sync>,
	pub spool_directory: std::path::PathBuf,
}
struct MessageBudget<'a> {
	cancel: &'a CancellationToken,
	deadline: DateTime<Utc>,
}
impl Worker {
	pub fn new(
		pool: sqlx::PgPool,
		storage: Arc<dyn Storage>,
		clock: Arc<dyn Clock>,
		model: ScanReadingModel,
	) -> Self {
		Self {
			pool,
			storage,
			clock,
			model,
			extraction: Default::default(),
			maintenance: Arc::new(|| false),
			spool_directory: std::env::temp_dir(),
		}
	}
	pub async fn handle(
		&self,
		message: &Message,
		deadline: DateTime<Utc>,
		parent: &CancellationToken,
	) -> Result<Disposition, Error> {
		let Some(envelope) = crate::queue::parse_envelope(&message.body, "reading") else {
			tracing::warn!("Reading message discarded: invalid envelope");
			return Ok(Disposition::Delete);
		};
		let span = tracing::info_span!("archive.queue.process", otel.kind = "consumer");
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
		let cancel = parent.child_token();
		let _guard = cancel.clone().drop_guard();
		let mut lease = None;
		let mut attempt = Box::pin(self.attempt(id, message, &cancel, deadline, &mut lease));
		let result = tokio::select! {biased;_ = parent.cancelled()=>return Err(Error::Cancelled),_=tokio::time::sleep((deadline-self.clock.now()).to_std().unwrap_or_default())=>None,result=&mut attempt=>Some(result)};
		drop(attempt);
		if let Some(result) = result {
			return result;
		}
		cancel.cancel();
		tracing::warn!("Reading exceeded its time budget");
		let Some(job) = Job::load(&self.pool, id).await? else {
			return Ok(self.backoff(message));
		};
		if job.terminal() {
			return Ok(Disposition::Delete);
		}
		// Own progress saves advance the tracked lease. Never use >=: that can
		// release a newer foreign Running lease after a timeout.
		if job.status == 1 && lease == Some(job.row_version) {
			return self.retry(job, message).await;
		}
		Ok(self.backoff(message))
	}
	async fn attempt(
		&self,
		id: uuid::Uuid,
		message: &Message,
		cancel: &CancellationToken,
		deadline: DateTime<Utc>,
		lease: &mut Option<i64>,
	) -> Result<Disposition, Error> {
		if self.clock.now() >= deadline {
			return Ok(self.backoff(message));
		}
		if (self.maintenance)() {
			return Ok(Disposition::Abandon(
				self.model.options.queue.visibility_backoff,
			));
		}
		let Some(datei) = crate::catalogue::queries::dateien(&self.pool, None, Some(id))
			.await?
			.pop()
		else {
			return Ok(Disposition::Delete);
		};
		if datei.kind != "noten"
			|| datei.lied_id.is_none()
			|| !datei.content_type.eq_ignore_ascii_case("application/pdf")
		{
			return Ok(Disposition::Delete);
		}
		let Some(mut job) = Job::load(&self.pool, id).await? else {
			return Ok(Disposition::Delete);
		};
		if job.terminal() {
			return Ok(Disposition::Delete);
		}
		if job.status == 1 && !job.stale(self.clock.now(), &self.model.options) {
			return Ok(Disposition::Abandon(
				self.model.options.queue.visibility_backoff,
			));
		}
		if job.attempt_count >= self.model.options.queue.max_attempts {
			return self.finish(job, 4, Some(EXHAUSTED)).await;
		}
		let now = self.clock.now();
		let won=sqlx::query(r#"UPDATE reading_jobs SET "Status"=1,"AttemptCount"="AttemptCount"+1,"LastAttemptAt"=$3,"UpdatedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(id).bind(job.row_version).bind(now).execute(&self.pool).await?.rows_affected();
		if won == 0 {
			return Ok(self.backoff(message));
		}
		job.status = 1;
		job.attempt_count += 1;
		job.last_attempt_at = Some(now);
		job.row_version += 1;
		*lease = Some(job.row_version);
		let Some(mut extraction) = crate::extraction::Job::load(&self.pool, id).await? else {
			return self.retry(job, message).await;
		};
		if matches!(extraction.status, 0 | 1) {
			return self.retry(job, message).await;
		}
		if extraction.status == 4 {
			return self.finish(job, 4, Some(EXTRACTION_FAILED)).await;
		}
		let mut applied = 0;
		let outcome = self
			.execute(
				&mut job,
				&mut extraction,
				&datei,
				MessageBudget { cancel, deadline },
				lease,
				&mut applied,
			)
			.await;
		job.fields_applied += applied;
		if cancel.is_cancelled() || self.clock.now() >= deadline {
			return self.retry(job, message).await;
		}
		match outcome {
			Ok(()) => {
				job.fields_done = true;
				self.finish(job, 3, None).await
			}
			Err(Error::Ai(crate::ai::Error::BudgetExceeded(_))) => {
				job.attempt_count = (job.attempt_count - 1).max(0);
				self.finish(job, 2, None).await
			}
			Err(Error::Ai(crate::ai::Error::InputTooLarge { .. })) => {
				self.finish(job, 4, Some(INPUT_TOO_LARGE)).await
			}
			Err(Error::Material(reason)) => self.finish(job, 4, Some(reason)).await,
			Err(Error::Race) => Ok(self.backoff(message)),
			Err(_) => {
				tracing::warn!("Reading step failed; retrying within durable bounds");
				self.retry(job, message).await
			}
		}
	}
	async fn execute(
		&self,
		job: &mut Job,
		extraction: &mut crate::extraction::Job,
		datei: &crate::catalogue::queries::Datei,
		budget: MessageBudget<'_>,
		lease: &mut Option<i64>,
		applied: &mut i32,
	) -> Result<(), Error> {
		let MessageBudget { cancel, deadline } = budget;
		let cap = self.extraction.max_text_characters;
		if extraction.status == 3 || extraction.text_source == 1 {
			let mut spool = None;
			let renderer = PdfPageRenderer::from_env().map_err(|_| Error::Transient)?;
			while job.pages_total.is_none()
				|| (job.pages_read < job.pages_total.unwrap_or(0)
					&& job.pages_read < job.page_limit)
			{
				if cancel.is_cancelled() || self.clock.now() >= deadline {
					return Err(Error::Cancelled);
				}
				if extraction
					.text
					.as_ref()
					.is_some_and(|t| t.encode_utf16().count() >= cap)
				{
					break;
				}
				if spool.is_none() {
					if i128::from(datei.size_bytes) > i128::from(self.model.options.max_pdf_bytes) {
						return Err(Error::Material(TOO_LARGE));
					}
					spool = Some(
						pdf::Spool::load(
							self.storage.as_ref(),
							&datei.blob_name,
							self.model.options.max_pdf_bytes,
							&self.spool_directory,
							|| cancel.is_cancelled() || self.clock.now() >= deadline,
						)
						.await
						.map_err(|e| match e {
							pdf::Error::TooLarge => Error::Material(TOO_LARGE),
							pdf::Error::Cancelled => Error::Cancelled,
							_ => Error::Transient,
						})?,
					);
				}
				let file = spool.as_ref().ok_or(Error::Transient)?.clone();
				if job.pages_total.is_none() {
					job.pages_total = Some(
						renderer
							.page_count(file.clone(), cancel.clone())
							.await
							.map_err(material)?,
					);
				}
				let remaining = job.page_limit.min(job.pages_total.unwrap_or(0)) - job.pages_read;
				if remaining <= 0 {
					break;
				}
				let count = self.model.batch_size(
					job.pages_read + 1,
					remaining.min(self.model.options.images_per_call),
				)?;
				let pages = renderer
					.render(
						file,
						job.pages_read + 1,
						count,
						self.model.options.max_image_long_side,
						self.model.options.jpeg_quality,
						cancel.clone(),
					)
					.await
					.map_err(material)?;
				if pages.is_empty() {
					job.pages_total = Some(job.pages_read);
					break;
				}
				let text = self
					.model
					.transcribe(&pages, Some(job.triggered_by_account_id), cancel)
					.await?;
				if cancel.is_cancelled() || self.clock.now() >= deadline {
					return Err(Error::Cancelled);
				}
				let now = self.clock.now();
				let mut tx = self.pool.begin().await?;
				if !text.trim().is_empty() {
					let existing = extraction.text.as_deref().unwrap_or_default();
					let joined = if existing.is_empty() {
						text
					} else {
						format!("{existing}\n{text}")
					};
					let text = super::bound_text(&joined, cap);
					let won=sqlx::query(r#"UPDATE extraction_jobs SET "Text"=$3,"Status"=2,"TextSource"=1,"FailureReason"=NULL,"CompletedAt"=COALESCE("CompletedAt",$4),"UpdatedAt"=$4,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(job.datei_id).bind(extraction.row_version).bind(&text).bind(now).execute(&mut *tx).await?.rows_affected();
					if won == 0 {
						return Err(Error::Race);
					}
					extraction.text = Some(text);
					extraction.text_source = 1;
					extraction.status = 2;
					extraction.row_version += 1;
				}
				let won=sqlx::query(r#"UPDATE reading_jobs SET "PagesRead"=$3,"PagesTotal"=$4,"UpdatedAt"=$5,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(job.datei_id).bind(job.row_version).bind(job.pages_read+pages.len() as i32).bind(job.pages_total).bind(now).execute(&mut *tx).await?.rows_affected();
				if won == 0 {
					return Err(Error::Race);
				}
				tx.commit().await?;
				job.pages_read += pages.len() as i32;
				job.row_version += 1;
				*lease = Some(job.row_version);
			}
			if extraction
				.text
				.as_deref()
				.is_none_or(|t| t.trim().is_empty())
			{
				return Err(Error::Material(NOTHING_READ));
			}
		}
		if !job.fields_done
			&& let Some(lied) = datei.lied_id
		{
			super::fields::Prefill {
				pool: &self.pool,
				datei: datei.id,
				lied,
				actor: Some(job.triggered_by_account_id),
				model: &self.model,
				clock: self.clock.as_ref(),
			}
			.run(
				extraction.text.as_deref().unwrap_or_default(),
				applied,
				cancel,
			)
			.await?;
		}
		Ok(())
	}
	fn backoff(&self, message: &Message) -> Disposition {
		Disposition::Abandon(Duration::milliseconds(
			(i128::from(
				self.model
					.options
					.queue
					.visibility_backoff
					.num_milliseconds(),
			) * i128::from(message.dequeue_count.max(0)))
			.min(i128::from(
				self.model.options.queue.redisplay_after.num_milliseconds(),
			)) as i64,
		))
	}
	async fn retry(&self, job: Job, message: &Message) -> Result<Disposition, Error> {
		if job.attempt_count >= self.model.options.queue.max_attempts
			|| message.dequeue_count >= i64::from(self.model.options.queue.max_attempts)
		{
			return self.finish(job, 4, Some(EXHAUSTED)).await;
		}
		self.save(job, 0, None, self.backoff(message)).await
	}
	async fn finish(
		&self,
		job: Job,
		status: i32,
		reason: Option<&str>,
	) -> Result<Disposition, Error> {
		self.save(job, status, reason, Disposition::Delete).await
	}
	async fn save(
		&self,
		job: Job,
		status: i32,
		reason: Option<&str>,
		disposition: Disposition,
	) -> Result<Disposition, Error> {
		let now = self.clock.now();
		let won=sqlx::query(r#"UPDATE reading_jobs SET "Status"=$3,"FailureReason"=$4,"CompletedAt"=$5,"UpdatedAt"=$6,"RowVersion"="RowVersion"+1,"FieldsApplied"=$7,"FieldsDone"=$8,"AttemptCount"=$9,"PagesTotal"=$10 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(job.datei_id).bind(job.row_version).bind(status).bind(reason).bind((status==3).then_some(now)).bind(now).bind(job.fields_applied).bind(job.fields_done).bind(job.attempt_count).bind(job.pages_total).execute(&self.pool).await?.rows_affected();
		if won == 1 {
			return Ok(disposition);
		}
		Ok(
			if Job::load(&self.pool, job.datei_id)
				.await?
				.is_some_and(|j| j.terminal())
			{
				Disposition::Delete
			} else {
				Disposition::Abandon(self.model.options.queue.visibility_backoff)
			},
		)
	}
}
fn material(error: pdf::Error) -> Error {
	match error {
		pdf::Error::Cancelled => Error::Cancelled,
		_ => Error::Material(UNREADABLE),
	}
}
