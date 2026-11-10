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
	pub text: Option<String>,
	pub text_source: i32,
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
		sqlx::query_as(r#"SELECT * FROM extraction_jobs WHERE "DateiId"=$1"#)
			.bind(id)
			.fetch_optional(pool)
			.await
	}
	pub fn stale(&self, now: DateTime<Utc>, options: &Options) -> bool {
		self.last_attempt_at
			.is_none_or(|t| t < now - options.stale_running_after)
	}
	pub fn terminal(&self) -> bool {
		matches!(self.status, 2..=4)
	}
}
pub fn extractable(datei: &Datei) -> bool {
	matches!(datei.kind.as_str(), "noten" | "sonstiges")
		&& datei.content_type.eq_ignore_ascii_case("application/pdf")
}
pub async fn insert(
	connection: &mut sqlx::PgConnection,
	id: Uuid,
	actor: Uuid,
	now: DateTime<Utc>,
) -> Result<(), sqlx::Error> {
	sqlx::query(r#"INSERT INTO extraction_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,0,0,$2,$3,$3,0)"#).bind(id).bind(actor).bind(now).execute(connection).await?;
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
			if extractable(datei) {
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
			if extractable(datei) {
				enqueue(pool, self.queue.as_ref(), datei.id, self.clock.as_ref()).await;
			}
			Ok(())
		})
	}
}
pub async fn enqueue(pool: &sqlx::PgPool, queue: &dyn Queue, id: Uuid, clock: &dyn Clock) -> bool {
	if crate::queue::quiet_send(queue, &crate::queue::envelope("extraction", id)).await {
		if sqlx::query(r#"UPDATE extraction_jobs SET "LastEnqueuedAt"=$2 WHERE "DateiId"=$1"#)
			.bind(id)
			.bind(clock.now())
			.execute(pool)
			.await
			.is_err()
		{
			tracing::warn!("Extraction enqueue stamp failed");
		}
		return true;
	}
	false
}
pub async fn dispatch(
	pool: &sqlx::PgPool,
	queue: &dyn Queue,
	options: &Options,
	clock: &dyn Clock,
) -> Result<usize, sqlx::Error> {
	let now = clock.now();
	let ids:Vec<Uuid>=sqlx::query_scalar(r#"SELECT "DateiId" FROM extraction_jobs WHERE "Status"=0 AND ("LastEnqueuedAt" IS NULL OR "LastEnqueuedAt"<$1) ORDER BY "CreatedAt" LIMIT $2"#).bind(now-options.redisplay_after).bind(options.max_dispatch_per_run).fetch_all(pool).await?;
	let mut count = 0;
	for id in ids {
		match queue.send(&crate::queue::envelope("extraction", id)).await {
			Ok(true) => {
				if sqlx::query(
					r#"UPDATE extraction_jobs SET "LastEnqueuedAt"=$2 WHERE "DateiId"=$1"#,
				)
				.bind(id)
				.bind(now)
				.execute(pool)
				.await
				.is_ok()
				{
					count += 1;
				} else {
					tracing::warn!("Extraction dispatch stamp failed");
				}
			}
			_ => tracing::warn!("Extraction dispatch not accepted"),
		}
	}
	Ok(count)
}
