mod support;
use archive_server::ai::{Ledger, Notices, Options, PostgresStore};
use std::sync::Arc;
use support::App;
#[tokio::test]
#[ignore = "explicit PostgreSQL: model-free cost notices/watermark"]
async fn highest_threshold_notifies_sequentially_and_raised_cap_reenables_notice() {
	let app = App::new_scoped("ai228").await;
	let options = Options {
		notice_thresholds_eur: "5,bloss,12".into(),
		..Default::default()
	};
	let (tx, rx) = tokio::sync::watch::channel(options);
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let notices = Notices::new(ledger);
	let month = notices.state().await.unwrap().month;
	sqlx::query(r#"INSERT INTO ai_usage_entries ("Id","YearMonth","Feature","Model","OperationId","Calls","InputTokens","OutputTokens","ReservedMicroEur","CostMicroEur","CreatedAt","UpdatedAt") VALUES ($1,$2,'chat','test',$3,1,0,0,0,7500000,now(),now())"#).bind(uuid::Uuid::now_v7()).bind(&month).bind(uuid::Uuid::now_v7()).execute(&app.pool).await.unwrap();
	assert_eq!(
		notices.state().await.unwrap().notified_micro_eur,
		0,
		"GET does not notify"
	);
	assert_eq!(
		notices.review().await.unwrap().notified_micro_eur,
		5_000_000
	);
	assert_eq!(
		notices.review().await.unwrap().notified_micro_eur,
		5_000_000
	);
	sqlx::query(r#"UPDATE ai_usage_entries SET "CostMicroEur"=15000000"#)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		notices.review().await.unwrap().notified_micro_eur,
		15_000_000
	);
	tx.send_modify(|o| o.monthly_cap_eur = 25.into());
	sqlx::query(r#"UPDATE ai_usage_entries SET "CostMicroEur"=26000000"#)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		notices.review().await.unwrap().notified_micro_eur,
		25_000_000
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL: editor-only maintenance budget inspection is model-free"]
async fn budget_http_is_editor_only_read_only_and_available_in_maintenance() {
	let app = App::new_scoped("ai228").await;
	let mut anonymous = app.client();
	assert_eq!(anonymous.get("/api/maintenance/ai-budget").await.0, 401);
	let mut member = app.member().await;
	assert_eq!(member.get("/api/maintenance/ai-budget").await.0, 403);
	let mut editor = app.admin().await;
	let mut settings = archive_server::Settings::new(true, std::path::PathBuf::new(), 0);
	settings.maintenance = true;
	let auth = archive_server::auth::Auth::new(
		app.pool.clone(),
		archive_server::auth::AuthConfig::development(),
		app.mail.clone(),
		app.time.clone(),
	)
	.unwrap();
	editor.router = archive_server::build_router(Arc::new(
		archive_server::Origin::new(settings).with_auth(auth),
	));
	for _ in 0..2 {
		let (status, state) = editor.get("/api/maintenance/ai-budget").await;
		assert_eq!(status, 200, "{state}");
		assert_eq!(state["capEur"], 15);
		assert_eq!(state["spentEur"].as_f64(), Some(0.0));
		assert_eq!(state["notifiedEur"].as_f64(), Some(0.0));
		assert_eq!(state["features"], serde_json::json!([]));
	}
	let rows: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_usage_entries")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	let months: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_budget_months")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!((rows, months), (0, 0));
	app.close().await;
}
