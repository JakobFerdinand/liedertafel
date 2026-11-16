use super::Options;
use crate::{
	auth::{AuthError, Clock},
	catalogue::{
		queries::Datei,
		uploads::{FinalizeContext, FinalizedHook},
	},
	queue::Queue,
};
use chrono::{DateTime, Utc};
use futures::future::BoxFuture;
use std::sync::Arc;
use uuid::Uuid;

#[derive(Clone, sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
pub struct Job {
	pub datei_id: Uuid,
	pub status: i32,
	pub playback_datei_id: Option<Uuid>,
	pub failure_reason: Option<String>,
	pub attempt_count: i32,
	pub last_attempt_at: Option<DateTime<Utc>>,
	pub completed_at: Option<DateTime<Utc>>,
	pub last_enqueued_at: Option<DateTime<Utc>>,
	pub triggered_by_account_id: Uuid,
	pub created_at: DateTime<Utc>,
	pub updated_at: DateTime<Utc>,
	pub row_version: i64,
}
impl Job {
	pub async fn load(pool: &sqlx::PgPool, id: Uuid) -> Result<Option<Self>, sqlx::Error> {
		sqlx::query_as(r#"SELECT * FROM video_jobs WHERE "DateiId"=$1"#)
			.bind(id)
			.fetch_optional(pool)
			.await
	}
	pub fn stale(&self, now: DateTime<Utc>, options: &Options) -> bool {
		self.last_attempt_at
			.is_none_or(|t| t < now - options.queue.stale_running_after)
	}
	pub fn terminal(&self) -> bool {
		matches!(self.status, 2..=4)
	}
	pub fn payload(&self) -> serde_json::Value {
		serde_json::json!({"dateiId":self.datei_id,"status":match self.status{0=>"queued",1=>"running",2=>"completed",3=>"notNeeded",_=>"failed"},"playbackDateiId":self.playback_datei_id,"failureReason":self.failure_reason,"attemptCount":self.attempt_count,"completedAt":self.completed_at,"lastAttemptAt":self.last_attempt_at,"lastEnqueuedAt":self.last_enqueued_at,"updatedAt":self.updated_at,"rowVersion":self.row_version})
	}
}
pub(crate) async fn insert(
	connection: &mut sqlx::PgConnection,
	id: Uuid,
	actor: Uuid,
	now: DateTime<Utc>,
) -> Result<(), sqlx::Error> {
	sqlx::query(r#"INSERT INTO video_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,0,0,$2,$3,$3,0)"#).bind(id).bind(actor).bind(now).execute(connection).await?;
	Ok(())
}
pub struct Hook {
	pub queue: Arc<dyn Queue>,
	pub clock: Arc<dyn Clock>,
}
impl FinalizedHook for Hook {
	fn before_save<'a>(
		&'a self,
		datei: &'a Datei,
		context: &'a FinalizeContext,
		connection: &'a mut sqlx::PgConnection,
	) -> BoxFuture<'a, Result<(), AuthError>> {
		Box::pin(async move {
			if super::is_material(&datei.kind, &datei.content_type) {
				insert(connection, datei.id, context.actor_account_id, context.now).await?;
			}
			Ok(())
		})
	}
	fn after_save<'a>(
		&'a self,
		datei: &'a Datei,
		pool: &'a sqlx::PgPool,
	) -> BoxFuture<'a, Result<(), AuthError>> {
		Box::pin(async move {
			if super::is_material(&datei.kind, &datei.content_type) {
				enqueue(pool, self.queue.as_ref(), datei.id, 0, self.clock.as_ref()).await;
			}
			Ok(())
		})
	}
}
pub async fn enqueue(
	pool: &sqlx::PgPool,
	queue: &dyn Queue,
	id: Uuid,
	version: i64,
	clock: &dyn Clock,
) -> bool {
	if !crate::queue::quiet_send(queue, &crate::queue::envelope("transcode", id)).await {
		return false;
	}
	if !sqlx::query(
		r#"UPDATE video_jobs SET "LastEnqueuedAt"=$2 WHERE "DateiId"=$1 AND "RowVersion"=$3"#,
	)
	.bind(id)
	.bind(clock.now())
	.bind(version)
	.execute(pool)
	.await
	.is_ok_and(|r| r.rows_affected() == 1)
	{
		tracing::warn!("Video enqueue stamp failed");
	}
	true
}
pub async fn dispatch(
	pool: &sqlx::PgPool,
	queue: &dyn Queue,
	options: &Options,
	clock: &dyn Clock,
) -> Result<usize, sqlx::Error> {
	let now = clock.now();
	let jobs:Vec<(Uuid,i64)>=sqlx::query_as(r#"SELECT "DateiId","RowVersion" FROM video_jobs WHERE "Status"=0 AND ("LastEnqueuedAt" IS NULL OR "LastEnqueuedAt"<$1) ORDER BY "CreatedAt" LIMIT $2"#).bind(now-options.queue.redisplay_after).bind(options.queue.max_dispatch_per_run).fetch_all(pool).await?;
	let mut count = 0;
	for (id, version) in jobs {
		if matches!(
			queue.send(&crate::queue::envelope("transcode", id)).await,
			Ok(true)
		) {
			if sqlx::query(
				r#"UPDATE video_jobs SET "LastEnqueuedAt"=$2 WHERE "DateiId"=$1 AND "RowVersion"=$3"#,
			)
			.bind(id)
			.bind(now)
			.bind(version)
			.execute(pool)
			.await
			.is_ok_and(|r| r.rows_affected() == 1)
			{
				count += 1;
			} else {
				tracing::warn!("Video dispatch stamp failed");
			}
		} else {
			tracing::warn!("Video dispatch not accepted");
		}
	}
	Ok(count)
}
