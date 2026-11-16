use super::{Action, Job, Options, Plan, Probe, process, service};
use crate::{
	auth::Clock,
	catalogue::queries::{self, Datei},
	queue::{Disposition, Message},
	storage::Storage,
};
use chrono::{DateTime, Duration, Utc};
use std::{
	path::{Path, PathBuf},
	sync::Arc,
};
use tokio_util::{sync::CancellationToken, task::TaskTracker};
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;
use uuid::Uuid;

pub const EXHAUSTED: &str = "Die Konvertierung ist nach mehreren Versuchen fehlgeschlagen.";
pub const UNREADABLE: &str = "Die Aufnahme konnte nicht gelesen werden.";
pub const TRANSCODE_FAILED: &str = "Die Video-Konvertierung ist fehlgeschlagen.";
pub const NO_VIDEO: &str = "Die Aufnahme enthält keinen verwertbaren Video-Stream.";
pub const TOO_LARGE: &str = "Die Aufnahme ist zu groß für die automatische Konvertierung.";
pub const MISSING_TOOL: &str = "Die Konvertierung benötigt ffmpeg, das auf dem Server nicht verfügbar ist. Nach dem Beheben der Einrichtung erneut versuchen.";
#[derive(Debug, thiserror::Error)]
pub(super) enum Error {
	#[error("Videoauftrag abgebrochen.")]
	Cancelled,
	#[error("Videospeicher nicht verfügbar.")]
	Infrastructure,
	#[error("{0}")]
	Material(&'static str),
}
impl From<sqlx::Error> for Error {
	fn from(_: sqlx::Error) -> Self {
		Self::Infrastructure
	}
}
pub(super) struct Worker {
	pub pool: sqlx::PgPool,
	pub storage: Arc<dyn Storage>,
	pub clock: Arc<dyn Clock>,
	pub options: Options,
	pub maintenance: Arc<dyn Fn() -> bool + Send + Sync>,
	pub tasks: TaskTracker,
	pub directory: PathBuf,
}
struct Copy {
	id: Uuid,
	blob: String,
	size: i64,
	name: String,
}
struct Temp(PathBuf);
impl Temp {
	fn new(directory: &Path, suffix: &str) -> Result<Self, Error> {
		use std::os::unix::fs::OpenOptionsExt;
		let path = directory.join(format!("archive-transcode-{}.{suffix}", Uuid::new_v4()));
		std::fs::OpenOptions::new()
			.create_new(true)
			.write(true)
			.mode(0o600)
			.open(&path)
			.map_err(|_| Error::Infrastructure)?;
		Ok(Self(path))
	}
}
impl Drop for Temp {
	fn drop(&mut self) {
		let _ = std::fs::remove_file(&self.0);
	}
}
impl Worker {
	pub async fn handle(
		&self,
		message: &Message,
		deadline: DateTime<Utc>,
		parent: &CancellationToken,
	) -> Result<Disposition, Error> {
		let Some(envelope) = crate::queue::parse_envelope(&message.body, "transcode") else {
			tracing::warn!("Transcode message discarded: invalid envelope");
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
		id: Uuid,
		message: &Message,
		deadline: DateTime<Utc>,
		parent: &CancellationToken,
	) -> Result<Disposition, Error> {
		let budget = parent.child_token();
		let _guard = budget.clone().drop_guard();
		let remaining = (deadline - self.clock.now()).to_std().unwrap_or_default();
		let mut lease = None;
		let result = {
			let attempt = self.attempt(id, message, &mut lease, &budget, deadline);
			tokio::pin!(attempt);
			tokio::select! {biased;_ = parent.cancelled()=>None,_ = tokio::time::sleep(remaining)=>None,result=&mut attempt=>Some(result)}
		};
		budget.cancel();
		// Await actual native child shutdown/reap, including detached owned
		// supervisor tasks, before recovery or the command's telemetry finish.
		self.tasks.close();
		self.tasks.wait().await;
		if parent.is_cancelled() {
			return Err(Error::Cancelled);
		}
		if let Some(result) = result {
			if !matches!(result, Err(Error::Infrastructure)) || lease.is_none() {
				return result;
			}
			// Frozen transient recovery also covers upload/completion-save
			// failures. Reload after rollback; never recover a newer lease.
			tracing::warn!("Transcode infrastructure attempt failed");
		} else {
			tracing::warn!("Transcode exceeded its time budget");
		}
		let fresh = Job::load(&self.pool, id).await?;
		if let Some(fresh) = fresh {
			if fresh.terminal() {
				return Ok(Disposition::Delete);
			}
			if lease == Some(fresh.row_version) && fresh.status == 1 {
				return self.retry(&fresh, message).await;
			}
		}
		Ok(self.backoff(message))
	}
	async fn attempt(
		&self,
		id: Uuid,
		message: &Message,
		lease: &mut Option<i64>,
		budget: &CancellationToken,
		deadline: DateTime<Utc>,
	) -> Result<Disposition, Error> {
		if self.clock.now() >= deadline {
			return Ok(self.backoff(message));
		}
		if (self.maintenance)() {
			return Ok(Disposition::Abandon(self.options.queue.visibility_backoff));
		}
		let Some(datei) = queries::dateien(&self.pool, None, Some(id)).await?.pop() else {
			return Ok(Disposition::Delete);
		};
		if !super::is_material(&datei.kind, &datei.content_type) {
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
			return Ok(Disposition::Abandon(self.options.queue.visibility_backoff));
		}
		if job.attempt_count >= self.options.queue.max_attempts {
			return self
				.save(&job, 4, Some(EXHAUSTED), None, None, Disposition::Delete)
				.await;
		}
		if i128::from(datei.size_bytes) > i128::from(self.options.max_input_bytes) {
			return self
				.save(&job, 4, Some(TOO_LARGE), None, None, Disposition::Delete)
				.await;
		}
		let now = self.clock.now();
		let won=sqlx::query(r#"UPDATE video_jobs SET "Status"=1,"AttemptCount"="AttemptCount"+1,"LastAttemptAt"=$3,"UpdatedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(id).bind(job.row_version).bind(now).execute(&self.pool).await?.rows_affected();
		if won == 0 {
			return Ok(Disposition::Abandon(self.options.queue.visibility_backoff));
		}
		job.status = 1;
		job.attempt_count += 1;
		job.row_version += 1;
		*lease = Some(job.row_version);
		let outcome = self.transcode(&datei, budget).await;
		if budget.is_cancelled() || self.clock.now() >= deadline {
			return self.retry(&job, message).await;
		}
		match outcome {
			Ok(copy) => {
				self.save(
					&job,
					if copy.is_some() { 2 } else { 3 },
					None,
					copy.as_ref(),
					Some(&datei),
					Disposition::Delete,
				)
				.await
			}
			Err(Error::Material(reason)) => {
				self.save(&job, 4, Some(reason), None, None, Disposition::Delete)
					.await
			}
			Err(Error::Cancelled) => Err(Error::Cancelled),
			Err(Error::Infrastructure) => self.retry(&job, message).await,
		}
	}
	fn backoff(&self, message: &Message) -> Disposition {
		let millis = i128::from(self.options.queue.visibility_backoff.num_milliseconds())
			* i128::from(message.dequeue_count.max(0));
		Disposition::Abandon(Duration::milliseconds(millis.min(i128::from(
			self.options.queue.redisplay_after.num_milliseconds(),
		)) as i64))
	}
	async fn retry(&self, job: &Job, message: &Message) -> Result<Disposition, Error> {
		if job.attempt_count >= self.options.queue.max_attempts
			|| message.dequeue_count >= i64::from(self.options.queue.max_attempts)
		{
			self.save(job, 4, Some(EXHAUSTED), None, None, Disposition::Delete)
				.await
		} else {
			self.save(
				job,
				0,
				job.failure_reason.as_deref(),
				None,
				None,
				self.backoff(message),
			)
			.await
		}
	}
	async fn save(
		&self,
		job: &Job,
		status: i32,
		reason: Option<&str>,
		copy: Option<&Copy>,
		original: Option<&Datei>,
		disposition: Disposition,
	) -> Result<Disposition, Error> {
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		if let (Some(copy), Some(original)) = (copy, original) {
			sqlx::query(r#"INSERT INTO dateien ("Id","LiedId","EventId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","OriginalFileName","CreatedByAccountId","CreatedAt","RowVersion") VALUES ($1,$2,$3,$4,false,$5,'video/mp4',$6,$7,$8,$9,1)"#).bind(copy.id).bind(original.lied_id).bind(original.event_id).bind(&original.kind).bind(&copy.blob).bind(copy.size).bind(&copy.name).bind(original.created_by_account_id).bind(now).execute(&mut *tx).await?;
		}
		// CAS job first: no losing worker may publish its uploaded copy/slots.
		// Upload-before-save can still leave unreferenced bytes, as in frozen.
		let won=sqlx::query(r#"UPDATE video_jobs SET "Status"=$3,"FailureReason"=$4,"CompletedAt"=$5,"UpdatedAt"=$6,"PlaybackDateiId"=CASE WHEN $7::uuid IS NULL THEN "PlaybackDateiId" ELSE $7 END,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(job.datei_id).bind(job.row_version).bind(status).bind(reason).bind(matches!(status,2|3).then_some(now)).bind(now).bind(copy.map(|c|c.id)).execute(&mut *tx).await?.rows_affected();
		if won == 0 {
			tx.rollback().await?;
			return Ok(
				if Job::load(&self.pool, job.datei_id)
					.await?
					.is_some_and(|j| j.terminal())
				{
					Disposition::Delete
				} else {
					Disposition::Abandon(self.options.queue.visibility_backoff)
				},
			);
		}
		if let (Some(copy), Some(original)) = (copy, original) {
			sqlx::query(r#"UPDATE recordings SET "PlaybackDateiId"=$2,"DurationSeconds"=NULL,"UpdatedAt"=$3,"UpdatedByAccountId"=$4,"RowVersion"="RowVersion"+1 WHERE "OriginalDateiId"=$1 AND "PlaybackDateiId" IS NULL"#).bind(original.id).bind(copy.id).bind(now).bind(original.created_by_account_id).execute(&mut *tx).await?;
		}
		tx.commit().await?;
		Ok(disposition)
	}
	async fn transcode(
		&self,
		datei: &Datei,
		cancel: &CancellationToken,
	) -> Result<Option<Copy>, Error> {
		use futures::StreamExt;
		use tokio::io::AsyncWriteExt;
		let input = Temp::new(&self.directory, "input")?;
		let output = Temp::new(&self.directory, "mp4")?;
		let mut source = self
			.storage
			.open_read_bounded(&datei.blob_name, self.options.max_input_bytes)
			.await
			.map_err(|_| Error::Infrastructure)?
			.ok_or(Error::Material(UNREADABLE))?;
		let mut file = tokio::fs::OpenOptions::new()
			.write(true)
			.open(&input.0)
			.await
			.map_err(|_| Error::Infrastructure)?;
		let mut copied = 0u64;
		while let Some(bytes) = source.next().await {
			let bytes = bytes.map_err(|_| Error::Infrastructure)?;
			if bytes.len() as u64 > self.options.max_input_bytes.saturating_sub(copied) {
				return Err(Error::Material(TOO_LARGE));
			}
			file.write_all(&bytes)
				.await
				.map_err(|_| Error::Infrastructure)?;
			copied += bytes.len() as u64;
		}
		file.flush().await.map_err(|_| Error::Infrastructure)?;
		drop(file);
		drop(source);
		if i128::from(copied) != i128::from(datei.size_bytes) {
			return Err(Error::Material(UNREADABLE));
		}
		let mut args: Vec<_> = [
			"-v",
			"error",
			"-print_format",
			"json",
			"-show_format",
			"-show_streams",
			"-i",
		]
		.into_iter()
		.map(Into::into)
		.collect();
		args.push(input.0.as_os_str().to_owned());
		let probe = process::run(&self.options.ffprobe_path, args, cancel, &self.tasks).await?;
		let probe = std::str::from_utf8(&probe)
			.ok()
			.and_then(|s| Probe::from_json(s).ok().flatten())
			.ok_or(Error::Material(NO_VIDEO))?;
		let plan = Plan::for_probe(&datei.content_type, &probe);
		if plan.action == Action::Keep {
			return Ok(None);
		}
		let mut args: Vec<_> = ["-y", "-v", "error", "-i"]
			.into_iter()
			.map(Into::into)
			.collect();
		args.push(input.0.as_os_str().to_owned());
		args.extend(
			[
				"-map",
				"0:v:0",
				"-sn",
				"-map_metadata",
				"-1",
				"-c:v",
				"libx264",
				"-crf",
				"20",
				"-preset",
				"medium",
				"-pix_fmt",
				"yuv420p",
				"-vf",
			]
			.into_iter()
			.map(std::ffi::OsString::from),
		);
		args.push(format!("scale={}:{}", plan.scale_width, plan.scale_height).into());
		args.extend(
			[
				"-map",
				"0:a:0?",
				"-c:a",
				"aac",
				"-b:a",
				"192k",
				"-movflags",
				"+faststart",
				"-f",
				"mp4",
			]
			.into_iter()
			.map(std::ffi::OsString::from),
		);
		args.push(output.0.as_os_str().to_owned());
		process::run(&self.options.ffmpeg_path, args, cancel, &self.tasks).await?;
		let size = tokio::fs::metadata(&output.0)
			.await
			.map_err(|_| Error::Infrastructure)?
			.len();
		if size == 0 {
			return Err(Error::Material(TRANSCODE_FAILED));
		}
		let id = Uuid::now_v7();
		let blob = format!("dateien/{id}/{}.mp4", Uuid::now_v7().simple());
		self.storage
			.upload_file(&blob, &output.0, "video/mp4")
			.await
			.map_err(|_| Error::Infrastructure)?;
		let name = datei.original_file_name.as_deref().unwrap_or("Aufnahme");
		let stem = name
			.rfind('.')
			.filter(|i| *i > 0)
			.map(|i| &name[..i])
			.unwrap_or(name);
		Ok(Some(Copy {
			id,
			blob,
			size: i64::try_from(size).map_err(|_| Error::Infrastructure)?,
			name: format!("{stem} - Abspielkopie.mp4"),
		}))
	}
}
