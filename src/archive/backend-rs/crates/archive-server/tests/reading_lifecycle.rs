#![allow(clippy::unwrap_used, clippy::expect_used)] // Real SQL cancellation/recovery assertions.
mod support;
use archive_server::{
	ai::*,
	auth::Clock,
	queue::{Disposition, Message, envelope},
	reading::{self, Job, Options},
};
use std::sync::{
	Arc,
	atomic::{AtomicUsize, Ordering},
};
#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDF; own-progress/foreign/terminal lease recovery"]
async fn deadline_recovers_only_its_latest_progress_lease_and_keeps_the_claim_timestamp() {
	for replacement in [0, 1, 2] {
		let app = support::App::new_scoped("reading229").await;
		let calls = Arc::new(AtomicUsize::new(0));
		let script = Script::handler({
			let calls = calls.clone();
			move |_| {
				if calls.fetch_add(1, Ordering::SeqCst) == 0 {
					vec![ScriptStep::Update(Update {
						text: "Schon bezahlt".into(),
						usage: Usage::reported(100, 20),
						..Default::default()
					})]
				} else {
					vec![ScriptStep::Hold]
				}
			}
		});
		let h = support::reading::Harness::new(
			&app,
			support::reading::scan(4),
			script,
			Options {
				images_per_call: 2,
				max_image_long_side: 100,
				..Default::default()
			},
		);
		let (_, id) = h.seed(&app, 4).await;
		let worker = Arc::new(h.worker);
		let deadline = app.time.now() + chrono::Duration::milliseconds(1500);
		let task = {
			let worker = worker.clone();
			tokio::spawn(async move {
				worker
					.handle(
						&Message {
							id: "m".into(),
							receipt: "p".into(),
							body: envelope("reading", id),
							dequeue_count: 1,
						},
						deadline,
						&Default::default(),
					)
					.await
			})
		};
		tokio::time::timeout(std::time::Duration::from_secs(5), async {
			while h.script.requests().len() < 2 {
				tokio::task::yield_now().await;
			}
		})
		.await
		.unwrap();
		let claimed = Job::load(&app.pool, id).await.unwrap().unwrap();
		assert_eq!(claimed.pages_read, 2);
		if replacement > 0 {
			sqlx::query(
				r#"UPDATE reading_jobs SET "Status"=$2,"RowVersion"="RowVersion"+100 WHERE "DateiId"=$1"#,
			)
			.bind(id)
			.bind(if replacement == 1 { 1 } else { 3 })
			.execute(&app.pool)
			.await
			.unwrap();
		}
		let disposition = task.await.unwrap().unwrap();
		let row = Job::load(&app.pool, id).await.unwrap().unwrap();
		assert_eq!(row.last_attempt_at, claimed.last_attempt_at);
		assert_eq!(row.pages_read, 2);
		match replacement {
			0 => {
				assert!(matches!(disposition, Disposition::Abandon(_)));
				assert_eq!(row.status, 0);
				assert_eq!(row.row_version, claimed.row_version + 1);
			}
			1 => {
				assert!(matches!(disposition, Disposition::Abandon(_)));
				assert_eq!(
					(row.status, row.row_version),
					(1, claimed.row_version + 100)
				);
			}
			_ => {
				assert_eq!(disposition, Disposition::Delete);
				assert_eq!(
					(row.status, row.row_version),
					(3, claimed.row_version + 100)
				);
			}
		}
		assert!(h.gateway.drain().await);
		let sums:(i64,i64,i64)=sqlx::query_as(r#"SELECT sum("CostMicroEur")::bigint,sum("ReservedMicroEur")::bigint,sum("Calls")::bigint FROM ai_usage_entries"#).fetch_one(&app.pool).await.unwrap();
		assert!(sums.0 > 182);
		assert_eq!((sums.1, sums.2), (0, 2));
		app.close().await;
	}
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; parent cancellation drains real gateway settlement"]
async fn cancelled_worker_does_not_apply_a_late_answer_and_settles_its_owned_call() {
	let app = support::App::new_scoped("reading229").await;
	let h = support::reading::Harness::new(
		&app,
		vec![],
		Script::new(vec![vec![ScriptStep::Hold]]),
		Options::default(),
	);
	let (owner, id) = h.seed(&app, 20).await;
	sqlx::query(
		r#"UPDATE extraction_jobs SET "Status"=2,"Text"='Von Franz Schubert' WHERE "DateiId"=$1"#,
	)
	.bind(id)
	.execute(&app.pool)
	.await
	.unwrap();
	let cancel = tokio_util::sync::CancellationToken::new();
	let worker = Arc::new(h.worker);
	let deadline = app.time.now() + chrono::Duration::minutes(1);
	let task = {
		let cancel = cancel.clone();
		tokio::spawn(async move {
			worker
				.handle(
					&Message {
						id: "m".into(),
						receipt: "p".into(),
						body: envelope("reading", id),
						dequeue_count: 1,
					},
					deadline,
					&cancel,
				)
				.await
		})
	};
	tokio::time::timeout(std::time::Duration::from_secs(3), async {
		while h.script.requests().is_empty() {
			tokio::task::yield_now().await;
		}
	})
	.await
	.unwrap();
	cancel.cancel();
	assert!(matches!(
		task.await.unwrap(),
		Err(reading::ReadingError::Cancelled)
	));
	assert!(h.gateway.drain().await);
	let song = archive_server::catalogue::queries::lied(&app.pool, owner)
		.await
		.unwrap()
		.unwrap();
	assert!(song.composer.is_none());
	let (cost, reserved): (i64, i64) =
		sqlx::query_as(r#"SELECT "CostMicroEur","ReservedMicroEur" FROM ai_usage_entries"#)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert!(cost > 0);
	assert_eq!(reserved, 0);
	app.close().await;
}
