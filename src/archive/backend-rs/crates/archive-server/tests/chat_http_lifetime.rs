//! Actual TCP SSE lifetime, product Origin tracker and real SQL failures.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Assertion-only socket/SQL fixture helpers outside test functions.
mod support;
use archive_server::ai::{Script, ScriptStep, Update, Usage};
use serde_json::json;
use std::{sync::Arc, time::Duration};
use support::{App, chat::*};
use uuid::Uuid;

async fn bounded<F: std::future::Future>(future: F) -> F::Output {
	tokio::time::timeout(Duration::from_secs(8), future)
		.await
		.expect("owned chat lifecycle deadline")
}
async fn wait_requests(script: &Script, count: usize) {
	bounded(async {
		while script.requests().len() < count {
			tokio::time::sleep(Duration::from_millis(10)).await;
		}
	})
	.await;
}
async fn socket(
	app: &App,
	origin: &Arc<archive_server::Origin>,
	member: &support::Client,
	thread: Uuid,
) -> (reqwest::Response, tokio::task::JoinHandle<()>) {
	let listener = tokio::net::TcpListener::bind(("127.0.0.1", 0))
		.await
		.unwrap();
	let port = listener.local_addr().unwrap().port();
	let router = app.router.clone();
	let shutdown = origin.shutdown.clone();
	let task = tokio::spawn(async move {
		axum::serve(
			listener,
			router.into_make_service_with_connect_info::<std::net::SocketAddr>(),
		)
		.with_graceful_shutdown(shutdown.cancelled_owned())
		.await
		.unwrap();
	});
	let response = reqwest::Client::new()
		.post(format!("http://127.0.0.1:{port}/api/chat"))
		.header(
			"cookie",
			member
				.cookies
				.iter()
				.map(|(k, v)| format!("{k}={v}"))
				.collect::<Vec<_>>()
				.join("; "),
		)
		.header("X-CSRF-TOKEN", &member.token)
		.json(&body(&thread.to_string(), "Synthetische Lebensdauerfrage"))
		.send()
		.await
		.unwrap();
	assert_eq!(response.status(), 200);
	(response, task)
}
async fn until(response: &mut reqwest::Response, pattern: &str) -> String {
	bounded(async {
		let mut bytes = Vec::new();
		loop {
			let chunk = response
				.chunk()
				.await
				.unwrap()
				.expect("SSE ended before control boundary");
			bytes.extend_from_slice(&chunk);
			let text = String::from_utf8_lossy(&bytes);
			if text.contains(pattern) {
				return text.into_owned();
			}
		}
	})
	.await
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL and loopback TCP"]
async fn first_and_second_call_socket_drop_cancel_delivery_but_join_question_and_all_charges() {
	for second in [false, true] {
		let mut app = App::new_scoped("chat232_socket").await;
		let script = if second {
			Script::new(vec![
				vec![
					tool("catalogue_search", json!({"query":""})),
					ScriptStep::Update(Update::usage(Usage::reported(100, 10))),
				],
				vec![ScriptStep::Hold],
			])
		} else {
			Script::new(vec![vec![ScriptStep::Hold]])
		};
		let harness = install(&mut app, script);
		let member = app.member().await;
		let thread = Uuid::new_v4();
		let (mut response, server) = socket(&app, &harness.origin, &member, thread).await;
		until(&mut response, "RUN_STARTED").await;
		wait_requests(&harness.script, if second { 2 } else { 1 }).await;
		drop(response);
		// Do not signal the host until the socket alone has completed history.
		bounded(async {
			loop {
				let n: i64 =
					sqlx::query_scalar(r#"SELECT count(*) FROM chat_messages WHERE "ThreadId"=$1"#)
						.bind(thread)
						.fetch_one(&app.pool)
						.await
						.unwrap();
				if n == 1 {
					break;
				}
				tokio::time::sleep(Duration::from_millis(10)).await;
			}
		})
		.await;
		assert!(bounded(harness.origin.drain_readings()).await);
		bounded(server).await.unwrap();
		let row: (i32, i64, i64) = sqlx::query_as(
			r#"SELECT "Calls","ReservedMicroEur","CostMicroEur" FROM ai_usage_entries"#,
		)
		.fetch_one(&app.pool)
		.await
		.unwrap();
		assert_eq!(row.0, if second { 2 } else { 1 });
		assert_eq!(row.1, 0);
		assert!(row.2 > 0);
		let roles: Vec<String> =
			sqlx::query_scalar(r#"SELECT "Role" FROM chat_messages WHERE "ThreadId"=$1"#)
				.bind(thread)
				.fetch_all(&app.pool)
				.await
				.unwrap();
		assert_eq!(roles, ["user"]);
		app.close().await;
	}
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL and loopback TCP"]
async fn early_run_error_and_consumer_exit_cannot_abandon_a_blocked_history_save_or_host_join() {
	let mut app = App::new_scoped("chat232_blocked").await;
	let harness = install_with(&mut app, Script::new(vec![vec![ScriptStep::Hold]]), |o| {
		o.chat.no_token_seconds = 1
	});
	let member = app.member().await;
	let thread = Uuid::new_v4();
	let (mut response, server) = socket(&app, &harness.origin, &member, thread).await;
	wait_requests(&harness.script, 1).await;
	let mut lock = app.pool.begin().await.unwrap();
	sqlx::raw_sql("LOCK TABLE chat_messages IN ACCESS EXCLUSIVE MODE")
		.execute(&mut *lock)
		.await
		.unwrap();
	let wire = until(&mut response, "RUN_ERROR").await;
	assert!(!wire.contains("RUN_FINISHED"));
	drop(response);
	let join = tokio::spawn({
		let origin = harness.origin.clone();
		async move { origin.drain_readings().await }
	});
	tokio::time::sleep(Duration::from_millis(100)).await;
	assert!(
		!join.is_finished(),
		"host must still own the blocked history save after terminal error"
	);
	lock.commit().await.unwrap();
	assert!(bounded(join).await.unwrap());
	bounded(server).await.unwrap();
	let n: i64 = sqlx::query_scalar(
		r#"SELECT count(*) FROM chat_messages WHERE "ThreadId"=$1 AND "Role"='user'"#,
	)
	.bind(thread)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_eq!(n, 1);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL and loopback TCP"]
async fn paid_answer_then_failed_history_insert_has_one_save_attempt_no_model_replay_and_no_success()
 {
	let mut app = App::new_scoped("chat232_save_failure").await;
	sqlx::raw_sql("CREATE SEQUENCE chat_save_attempts; CREATE FUNCTION fail_chat_save() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('chat_save_attempts'); RAISE EXCEPTION 'synthetic history save refusal'; END $$; CREATE TRIGGER fail_chat_save BEFORE INSERT ON chat_messages FOR EACH ROW EXECUTE FUNCTION fail_chat_save()").execute(&app.pool).await.unwrap();
	let harness = install(
		&mut app,
		Script::new(vec![vec![
			ScriptStep::Update(Update::text("Bezahlter Text")),
			ScriptStep::Update(Update::usage(Usage::reported(100, 10))),
		]]),
	);
	let member = app.member().await;
	let thread = Uuid::new_v4();
	let (response, server) = socket(&app, &harness.origin, &member, thread).await;
	let wire = bounded(response.text()).await.unwrap();
	assert!(wire.contains("Bezahlter Text"));
	assert!(wire.contains("RUN_ERROR"));
	assert!(!wire.contains("RUN_FINISHED"));
	assert!(bounded(harness.origin.drain_readings()).await);
	bounded(server).await.unwrap();
	assert_eq!(harness.script.requests().len(), 1);
	let attempts: i64 = sqlx::query_scalar("SELECT last_value FROM chat_save_attempts")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(attempts, 1);
	let threads: i64 = sqlx::query_scalar("SELECT count(*) FROM chat_threads")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(threads, 0, "thread+turn transaction rolled back");
	let (calls,input,output,reserve,cost):(i32,i64,i64,i64,i64)=sqlx::query_as(r#"SELECT "Calls","InputTokens","OutputTokens","ReservedMicroEur","CostMicroEur" FROM ai_usage_entries"#).fetch_one(&app.pool).await.unwrap();
	assert_eq!((calls, input, output, reserve), (1, 100, 10, 0));
	assert!(cost > 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL and loopback TCP"]
async fn permanent_real_sql_settlement_failure_retains_reservation_and_reports_unhealthy_host_drain()
 {
	let mut app = App::new_scoped("chat232_settle_failure").await;
	sqlx::raw_sql(r#"CREATE SEQUENCE chat_settle_attempts; CREATE FUNCTION fail_chat_settle() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW."ReservedMicroEur" < OLD."ReservedMicroEur" THEN PERFORM nextval('chat_settle_attempts'); RAISE EXCEPTION 'synthetic settlement refusal'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_chat_settle BEFORE UPDATE ON ai_usage_entries FOR EACH ROW EXECUTE FUNCTION fail_chat_settle()"#).execute(&app.pool).await.unwrap();
	let harness = install(
		&mut app,
		Script::new(vec![vec![ScriptStep::Update(Update::usage(
			Usage::reported(100, 10),
		))]]),
	);
	let member = app.member().await;
	let thread = Uuid::new_v4();
	let (response, server) = socket(&app, &harness.origin, &member, thread).await;
	let wire = bounded(response.text()).await.unwrap();
	assert!(
		wire.contains("RUN_FINISHED"),
		"inherited settlement health is a host-drain result, not a fabricated per-call provider error"
	);
	assert!(!bounded(harness.origin.drain_readings()).await);
	bounded(server).await.unwrap();
	let attempts: i64 = sqlx::query_scalar("SELECT last_value FROM chat_settle_attempts")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(attempts, 4);
	let (reserve, cost): (i64, i64) =
		sqlx::query_as(r#"SELECT "ReservedMicroEur","CostMicroEur" FROM ai_usage_entries"#)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert!(reserve > 0);
	assert_eq!(cost, 0);
	app.close().await;
}
