#![allow(clippy::unwrap_used, clippy::expect_used)] // SQL/provider send-barrier assertions.
mod support;
use archive_server::{
	auth::Clock,
	queue::{Message, Queue, QueueError},
	reading::*,
};
use futures::future::BoxFuture;
use std::sync::{
	Arc, Mutex,
	atomic::{AtomicUsize, Ordering},
};
#[derive(Default)]
struct Sender {
	sent: Mutex<Vec<String>>,
	barrier: Option<Arc<tokio::sync::Barrier>>,
	fail: AtomicUsize,
}
impl Queue for Sender {
	fn send<'a>(&'a self, body: &'a str) -> BoxFuture<'a, Result<bool, QueueError>> {
		Box::pin(async move {
			self.sent.lock().unwrap().push(body.into());
			if let Some(b) = &self.barrier {
				b.wait().await;
				b.wait().await;
			}
			if self.fail.load(Ordering::SeqCst) > 0 {
				return Err(QueueError);
			}
			Ok(true)
		})
	}
	fn receive(&self, _: usize, _: i32) -> BoxFuture<'_, Result<Vec<Message>, QueueError>> {
		Box::pin(async { Ok(vec![]) })
	}
	fn delete<'a>(&'a self, _: &'a Message) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async { Ok(()) })
	}
	fn abandon<'a>(&'a self, _: &'a Message, _: i32) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async { Ok(()) })
	}
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; real outbox SQL send-before-stamp version CAS"]
async fn reading_dispatch_and_quiet_enqueue_never_stamp_a_newer_version() {
	for quiet in [false, true] {
		let app = support::App::new_scoped("reading229").await;
		let h = support::reading::Harness::new(
			&app,
			vec![],
			archive_server::ai::Script::new(vec![]),
			Options::default(),
		);
		let (_, id) = h.seed(&app, 20).await;
		let barrier = Arc::new(tokio::sync::Barrier::new(2));
		let queue = Arc::new(Sender {
			barrier: Some(barrier.clone()),
			..Default::default()
		});
		let pool = app.pool.clone();
		let clock = app.time.clone();
		let ledger = h.ledger.clone();
		let model = h.worker.model.clone();
		let task = {
			let queue = queue.clone();
			tokio::spawn(async move {
				if quiet {
					enqueue(&pool, queue.as_ref(), id, 0, clock.as_ref()).await;
					0
				} else {
					dispatch(
						&pool,
						queue.as_ref(),
						&ledger,
						&model,
						&Options::default(),
						clock.as_ref(),
					)
					.await
					.unwrap()
				}
			})
		};
		barrier.wait().await;
		sqlx::query(
			r#"UPDATE reading_jobs SET "RowVersion"=10,"LastEnqueuedAt"=NULL WHERE "DateiId"=$1"#,
		)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
		barrier.wait().await;
		assert_eq!(task.await.unwrap(), 0);
		let row = Job::load(&app.pool, id).await.unwrap().unwrap();
		assert!(row.last_enqueued_at.is_none());
		assert_eq!(row.row_version, 10);
		assert_eq!(queue.sent.lock().unwrap().len(), 1);
		assert!(h.gateway.drain().await);
		app.close().await;
	}
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; bounded oldest strictly-stale dispatcher and accepted-send stamp failure"]
async fn reading_dispatch_is_bounded_and_does_not_retry_accepted_or_rejected_sends_immediately() {
	let app = support::App::new_scoped("reading229").await;
	let now = chrono::DateTime::from_timestamp_micros(app.time.now().timestamp_micros()).unwrap();
	*app.time.0.lock().unwrap() = now;
	let h = support::reading::Harness::new(
		&app,
		vec![],
		archive_server::ai::Script::new(vec![]),
		Options::default(),
	);
	let options = Options {
		queue: archive_server::extraction::Options {
			max_dispatch_per_run: 2,
			..Options::default().queue
		},
		..Options::default()
	};
	let mut ids = vec![];
	for n in 0..5 {
		let (_, id) = h.seed(&app, 20).await;
		ids.push(id);
		sqlx::query(
			r#"UPDATE reading_jobs SET "Status"=$2,"CreatedAt"=$3,"LastEnqueuedAt"=$4 WHERE "DateiId"=$1"#,
		)
		.bind(id)
		.bind(if n == 4 { 1 } else { 0 })
		.bind(now - chrono::Duration::minutes(10 - n))
		.bind(if n == 0 {
			None
		} else if n <= 2 {
			Some(now - options.queue.redisplay_after - chrono::Duration::seconds(1))
		} else {
			Some(now - options.queue.redisplay_after)
		})
		.execute(&app.pool)
		.await
		.unwrap();
	}
	let queue = Sender::default();
	assert_eq!(
		dispatch(
			&app.pool,
			&queue,
			&h.ledger,
			&h.worker.model,
			&options,
			app.time.as_ref()
		)
		.await
		.unwrap(),
		2
	);
	assert_eq!(queue.sent.lock().unwrap().len(), 2);
	assert!(
		Job::load(&app.pool, ids[2])
			.await
			.unwrap()
			.unwrap()
			.last_enqueued_at
			.unwrap() < now - options.queue.redisplay_after
	);
	sqlx::raw_sql(r#"CREATE FUNCTION reading229_refuse_stamp() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic'; END $$;CREATE TRIGGER reading229_stamp BEFORE UPDATE OF "LastEnqueuedAt" ON reading_jobs FOR EACH ROW EXECUTE FUNCTION reading229_refuse_stamp()"#).execute(&app.pool).await.unwrap();
	let before = queue.sent.lock().unwrap().len();
	assert_eq!(
		dispatch(
			&app.pool,
			&queue,
			&h.ledger,
			&h.worker.model,
			&options,
			app.time.as_ref()
		)
		.await
		.unwrap(),
		0
	);
	assert_eq!(queue.sent.lock().unwrap().len(), before + 1);
	assert!(h.gateway.drain().await);
	app.close().await;
}
