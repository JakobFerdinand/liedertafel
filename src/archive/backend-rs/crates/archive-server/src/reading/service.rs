use super::{Options, ScanReadingModel};
use crate::{ai::Ledger, auth::Clock, queue::Queue};
use chrono::{DateTime, Utc};
use uuid::Uuid;
#[derive(Clone, Debug, sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
pub struct Job {
	pub datei_id: Uuid,
	pub status: i32,
	pub pages_total: Option<i32>,
	pub pages_read: i32,
	pub page_limit: i32,
	pub fields_done: bool,
	pub fields_applied: i32,
	pub failure_reason: Option<String>,
	pub attempt_count: i32,
	pub last_attempt_at: Option<DateTime<Utc>>,
	pub last_enqueued_at: Option<DateTime<Utc>>,
	pub triggered_by_account_id: Uuid,
	pub created_at: DateTime<Utc>,
	pub updated_at: DateTime<Utc>,
	pub completed_at: Option<DateTime<Utc>>,
	pub row_version: i64,
}
impl Job {
	pub async fn load(pool: &sqlx::PgPool, id: Uuid) -> Result<Option<Self>, sqlx::Error> {
		sqlx::query_as(r#"SELECT * FROM reading_jobs WHERE "DateiId"=$1"#)
			.bind(id)
			.fetch_optional(pool)
			.await
	}
	pub fn terminal(&self) -> bool {
		matches!(self.status, 2..=4)
	}
	pub fn stale(&self, now: DateTime<Utc>, options: &Options) -> bool {
		self.last_attempt_at
			.is_none_or(|t| t < now - options.queue.stale_running_after)
	}
	pub fn partial(&self) -> bool {
		self.pages_total.is_some_and(|n| self.pages_read < n)
	}
	pub fn payload(&self, text_full: bool) -> serde_json::Value {
		serde_json::json!({"status":match self.status {0=>"queued",1=>"running",2=>"waitingForBudget",3=>"completed",_=>"failed"},"pagesTotal":self.pages_total,"pagesRead":self.pages_read,"partial":self.partial()&&!text_full,"fieldsApplied":self.fields_applied,"failureReason":self.failure_reason,"attemptCount":self.attempt_count,"completedAt":self.completed_at,"updatedAt":self.updated_at,"rowVersion":self.row_version})
	}
}
pub async fn insert(
	pool: &sqlx::PgPool,
	id: Uuid,
	actor: Uuid,
	page_limit: i32,
	now: DateTime<Utc>,
) -> Result<(), sqlx::Error> {
	sqlx::query(r#"INSERT INTO reading_jobs ("DateiId","Status","PagesRead","PageLimit","FieldsDone","FieldsApplied","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,0,0,$3,false,0,0,$2,$4,$4,0)"#).bind(id).bind(actor).bind(page_limit).bind(now).execute(pool).await?;
	Ok(())
}
pub async fn enqueue(
	pool: &sqlx::PgPool,
	queue: &dyn Queue,
	id: Uuid,
	version: i64,
	clock: &dyn Clock,
) -> bool {
	if !crate::queue::quiet_send(queue, &crate::queue::envelope("reading", id)).await {
		return false;
	}
	if !sqlx::query(
		r#"UPDATE reading_jobs SET "LastEnqueuedAt"=$3 WHERE "DateiId"=$1 AND "RowVersion"=$2"#,
	)
	.bind(id)
	.bind(version)
	.bind(clock.now())
	.execute(pool)
	.await
	.is_ok_and(|r| r.rows_affected() == 1)
	{
		tracing::warn!("Reading enqueue stamp failed");
	}
	true
}
pub async fn dispatch(
	pool: &sqlx::PgPool,
	queue: &dyn Queue,
	ledger: &Ledger,
	model: &ScanReadingModel,
	options: &Options,
	clock: &dyn Clock,
) -> Result<usize, sqlx::Error> {
	let now = clock.now();
	let parked: Vec<Job> = sqlx::query_as(
		r#"SELECT * FROM reading_jobs WHERE "Status"=2 ORDER BY "UpdatedAt" LIMIT $1"#,
	)
	.bind(options.queue.max_dispatch_per_run)
	.fetch_all(pool)
	.await?;
	if !parked.is_empty() && ledger.would_admit(&model.worst_case_transcription()).await {
		let mut tx = pool.begin().await?;
		let mut won = true;
		for job in parked {
			won &= sqlx::query(r#"UPDATE reading_jobs SET "Status"=0,"LastEnqueuedAt"=NULL,"UpdatedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(job.datei_id).bind(job.row_version).bind(now).execute(&mut *tx).await?.rows_affected()==1;
		}
		if won {
			tx.commit().await?;
		} else {
			tx.rollback().await?;
		}
	}
	let pending:Vec<Job>=sqlx::query_as(r#"SELECT * FROM reading_jobs WHERE "Status"=0 AND ("LastEnqueuedAt" IS NULL OR "LastEnqueuedAt"<$1) ORDER BY "CreatedAt" LIMIT $2"#).bind(now-options.queue.redisplay_after).bind(options.queue.max_dispatch_per_run).fetch_all(pool).await?;
	let mut count = 0;
	for job in pending {
		if !queue
			.send(&crate::queue::envelope("reading", job.datei_id))
			.await
			.unwrap_or(false)
		{
			continue;
		}
		if sqlx::query(
			r#"UPDATE reading_jobs SET "LastEnqueuedAt"=$3 WHERE "DateiId"=$1 AND "RowVersion"=$2"#,
		)
		.bind(job.datei_id)
		.bind(job.row_version)
		.bind(now)
		.execute(pool)
		.await
		.is_ok_and(|r| r.rows_affected() == 1)
		{
			count += 1;
		} else {
			tracing::warn!("Reading dispatch stamp failed");
		}
	}
	Ok(count)
}
