// Integration fixture construction uses assertions/unwrap like the frozen suites.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use archive_server::ai::{CallSpec, Ledger, Options, PostgresStore, Price, Usage};
use std::sync::Arc;
use support::App;
use uuid::Uuid;

fn options() -> Options {
	let mut options = Options::default();
	options
		.models
		.insert("test-model".into(), Price::new("1", "4").unwrap());
	options
}
fn call(operation: Uuid) -> CallSpec {
	CallSpec {
		feature: "chat".into(),
		operation_id: operation,
		account_id: None,
		model: "test-model".into(),
		estimated_input: 4000,
		max_output: 2000,
	}
}

#[tokio::test]
#[ignore = "explicit PostgreSQL: cancellation of a queued admission precedes provider"]
async fn cancelled_admission_does_not_wait_for_month_lock_or_call_provider() {
	use archive_server::ai::{Gateway, Message, Operation, Request, Script};
	let app = App::new_scoped("ai228").await;
	let mut prices = options();
	prices
		.models
		.insert("scripted".into(), Price::new("1", "4").unwrap());
	let (_, rx) = tokio::sync::watch::channel(prices);
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let r = ledger.reserve(call(Uuid::now_v7())).await.unwrap();
	let month = r.month.clone();
	ledger.settle(r, Usage::reported(0, 0)).await;
	let mut tx = app.pool.begin().await.unwrap();
	sqlx::query(r#"SELECT 1 FROM ai_budget_months WHERE "YearMonth"=$1 FOR UPDATE"#)
		.bind(month)
		.execute(&mut *tx)
		.await
		.unwrap();
	let script = Script::new(vec![vec![]]);
	let gateway = Gateway::scripted(ledger, script.clone());
	let mut request = Request::new(vec![Message::text("user", "Frage")]);
	request.operation = Some(Operation::start("chat", None));
	let call = gateway.start(request, true).unwrap();
	tokio::time::sleep(std::time::Duration::from_millis(100)).await;
	call.cancel();
	assert!(matches!(
		tokio::time::timeout(std::time::Duration::from_secs(1), call.response())
			.await
			.unwrap(),
		Err(archive_server::ai::Error::Cancelled)
	));
	assert!(script.requests().is_empty());
	tx.rollback().await.unwrap();
	assert!(gateway.drain().await);
	let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_usage_entries")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL: real AI ledger SQL"]
async fn calls_of_one_operation_add_to_one_row_without_rewriting_identity() {
	let app = App::new_scoped("ai228").await;
	*app.time.0.lock().unwrap() = "2026-11-10T12:00:00Z".parse().unwrap();
	let (_, rx) = tokio::sync::watch::channel(options());
	let budget = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let operation = Uuid::now_v7();
	let account = Uuid::now_v7();
	let mut first_call = call(operation);
	first_call.account_id = Some(account);
	let first = budget.reserve(first_call).await.unwrap();
	assert!(budget.settle(first, Usage::reported(1000, 500)).await);
	let mut second = call(operation);
	second.feature = "ignored-on-conflict".into();
	second.account_id = Some(Uuid::now_v7());
	let second = budget.reserve(second).await.unwrap();
	assert!(budget.settle(second, Usage::reported(2000, 250)).await);
	let rows = budget.month_entries("2026-11").await.unwrap();
	assert_eq!(rows.len(), 1);
	let row = &rows[0];
	assert_eq!(
		(
			&*row.feature,
			row.calls,
			row.input_tokens,
			row.output_tokens,
			row.cost_micro_eur,
			row.reserved_micro_eur
		),
		("chat", 2, 3000, 750, 6000, 0)
	);
	let attributed: Option<Uuid> =
		sqlx::query_scalar(r#"SELECT "AccountId" FROM ai_usage_entries"#)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(attributed, Some(account));
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL: original-month row ID settlement across Vienna rollover"]
async fn settlement_after_month_rollover_charges_the_original_sql_row() {
	let app = App::new_scoped("ai228").await;
	*app.time.0.lock().unwrap() = "2026-10-31T22:59:00Z".parse().unwrap();
	let (_, rx) = tokio::sync::watch::channel(options());
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let mut c = call(Uuid::now_v7());
	c.estimated_input = 100;
	c.max_output = 200;
	let reservation = ledger.reserve(c).await.unwrap();
	assert_eq!(reservation.month, "2026-10");
	*app.time.0.lock().unwrap() = "2026-10-31T23:01:00Z".parse().unwrap();
	assert!(ledger.settle(reservation, Usage::reported(50, 10)).await);
	let current = ledger.status().await.unwrap();
	assert_eq!(
		(current.month.as_str(), current.spent_micro_eur),
		("2026-11", 0)
	);
	let rows = ledger.month_entries("2026-10").await.unwrap();
	assert_eq!(
		(rows[0].cost_micro_eur, rows[0].reserved_micro_eur),
		(90, 0)
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL: 24 simultaneous admissions and independent replica"]
async fn parallel_24_callers_use_every_slot_but_never_reserve_above_cap() {
	let app = App::new_scoped("ai228").await;
	let mut options = options();
	options.monthly_cap_eur = "0.005".parse().unwrap();
	let (_, rx) = tokio::sync::watch::channel(options);
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx.clone(),
		app.time.clone(),
	);
	let start = Arc::new(tokio::sync::Barrier::new(24));
	let mut jobs = tokio::task::JoinSet::new();
	for _ in 0..24 {
		let start = start.clone();
		let ledger = ledger.clone();
		jobs.spawn(async move {
			start.wait().await;
			let mut c = call(Uuid::now_v7());
			c.estimated_input = 100;
			c.max_output = 200;
			ledger.reserve(c).await
		});
	}
	let mut admitted = 0;
	while let Some(result) = jobs.join_next().await {
		match result.unwrap() {
			Ok(r) => {
				assert_eq!(r.reserved_micro_eur, 900);
				admitted += 1;
			}
			Err(archive_server::ai::Error::BudgetExceeded(_)) => {}
			Err(e) => panic!("unexpected refusal {e}"),
		}
	}
	assert_eq!(admitted, 5);
	assert_eq!(ledger.status().await.unwrap().spent_micro_eur, 4500);
	let connection = app.pool.connect_options().clone();
	let replica_pool = sqlx::postgres::PgPoolOptions::new()
		.connect_with((*connection).clone())
		.await
		.unwrap();
	let replica = Ledger::new(
		Arc::new(PostgresStore::new(replica_pool.clone())),
		rx,
		app.time.clone(),
	);
	let mut c = call(Uuid::now_v7());
	c.estimated_input = 100;
	c.max_output = 200;
	assert!(matches!(
		replica.reserve(c).await,
		Err(archive_server::ai::Error::BudgetExceeded(_))
	));
	replica_pool.close().await;
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL: 32 workers x 10 reserve/settle rounds"]
async fn contended_32_by_10_callers_lose_no_reservations_tokens_or_settlements() {
	let app = App::new_scoped("ai228").await;
	let (_, rx) = tokio::sync::watch::channel(options());
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let mut jobs = tokio::task::JoinSet::new();
	for worker in 0..32 {
		let ledger = ledger.clone();
		jobs.spawn(async move {
			for round in 0..10 {
				let mut c = call(Uuid::now_v7());
				c.estimated_input = 100;
				c.max_output = 200;
				let r = ledger.reserve(c).await.unwrap();
				tokio::time::sleep(std::time::Duration::from_millis((worker + round) % 31)).await;
				assert!(ledger.settle(r, Usage::reported(50, 10)).await);
			}
		});
	}
	while let Some(result) = jobs.join_next().await {
		result.unwrap();
	}
	let rows = ledger
		.month_entries(&ledger.status().await.unwrap().month)
		.await
		.unwrap();
	assert_eq!(rows.len(), 1);
	assert_eq!(
		(
			rows[0].calls,
			rows[0].input_tokens,
			rows[0].output_tokens,
			rows[0].cost_micro_eur,
			rows[0].reserved_micro_eur
		),
		(320, 16000, 3200, 28800, 0)
	);
	let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_usage_entries")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 320);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL: first-month creation race across independent pools"]
async fn replicas_opening_month_both_admit_and_create_only_one_month_lock() {
	let app = App::new_scoped("ai228").await;
	let (_, rx) = tokio::sync::watch::channel(options());
	let first = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx.clone(),
		app.time.clone(),
	);
	let pool = sqlx::postgres::PgPoolOptions::new()
		.connect_with((*app.pool.connect_options()).clone())
		.await
		.unwrap();
	let second = Ledger::new(
		Arc::new(PostgresStore::new(pool.clone())),
		rx,
		app.time.clone(),
	);
	let start = Arc::new(tokio::sync::Barrier::new(8));
	let mut jobs = tokio::task::JoinSet::new();
	for i in 0..8 {
		let ledger = if i % 2 == 0 {
			first.clone()
		} else {
			second.clone()
		};
		let start = start.clone();
		jobs.spawn(async move {
			start.wait().await;
			let mut c = call(Uuid::now_v7());
			c.estimated_input = 100;
			c.max_output = 200;
			ledger.reserve(c).await.unwrap()
		});
	}
	while let Some(result) = jobs.join_next().await {
		assert_eq!(result.unwrap().reserved_micro_eur, 900);
	}
	assert_eq!(first.status().await.unwrap().spent_micro_eur, 7200);
	let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_budget_months")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	pool.close().await;
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL: lock timeout and independent settlement/original month"]
async fn five_second_lock_timeout_fails_closed_without_new_row() {
	let app = App::new_scoped("ai228").await;
	let (_, rx) = tokio::sync::watch::channel(options());
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let mut c = call(Uuid::now_v7());
	c.estimated_input = 100;
	c.max_output = 200;
	let r = ledger.reserve(c.clone()).await.unwrap();
	let mut tx = app.pool.begin().await.unwrap();
	sqlx::query(r#"SELECT 1 FROM ai_budget_months WHERE "YearMonth"=$1 FOR UPDATE"#)
		.bind(&r.month)
		.execute(&mut *tx)
		.await
		.unwrap();
	assert!(
		ledger.settle(r, Usage::reported(50, 10)).await,
		"settlement takes no month lock"
	);
	let before = std::time::Instant::now();
	assert!(ledger.reserve(c).await.is_err());
	assert!(before.elapsed() >= std::time::Duration::from_secs(5));
	tx.rollback().await.unwrap();
	assert_eq!(ledger.status().await.unwrap().spent_micro_eur, 90);
	app.close().await;
}
