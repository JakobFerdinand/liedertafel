#![allow(clippy::unwrap_used, clippy::expect_used)] // Integration assertions.
mod support;
use serde_json::json;
use support::{App, catalogue::*};

#[derive(Default)]
struct Capture(
	std::sync::Mutex<Vec<String>>,
	std::sync::atomic::AtomicUsize,
	std::sync::Mutex<Option<String>>,
);
impl archive_server::queue::Queue for Capture {
	fn send<'a>(
		&'a self,
		body: &'a str,
	) -> futures::future::BoxFuture<'a, Result<bool, archive_server::queue::QueueError>> {
		Box::pin(async move {
			self.0.lock().unwrap().push(body.into());
			if self.1.load(std::sync::atomic::Ordering::Relaxed) > 0
				|| self
					.2
					.lock()
					.unwrap()
					.as_ref()
					.is_some_and(|id| body.contains(id))
			{
				Err(archive_server::queue::QueueError)
			} else {
				Ok(true)
			}
		})
	}
	fn receive(
		&self,
		_: usize,
		_: i32,
	) -> futures::future::BoxFuture<
		'_,
		Result<Vec<archive_server::queue::Message>, archive_server::queue::QueueError>,
	> {
		Box::pin(async { Ok(vec![]) })
	}
	fn delete<'a>(
		&'a self,
		_: &'a archive_server::queue::Message,
	) -> futures::future::BoxFuture<'a, Result<(), archive_server::queue::QueueError>> {
		Box::pin(async { Ok(()) })
	}
	fn abandon<'a>(
		&'a self,
		_: &'a archive_server::queue::Message,
		_: i32,
	) -> futures::future::BoxFuture<'a, Result<(), archive_server::queue::QueueError>> {
		Box::pin(async { Ok(()) })
	}
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn dispatcher_selects_only_strictly_stale_queued_oldest_bounded_and_continues_failed_send() {
	use archive_server::{
		auth::Clock,
		extraction::{Job, Options, dispatch},
	};
	let app = App::new().await;
	let owner = lied(&app, "Dispatch", json!({})).await;
	let queue = Capture::default();
	let options = Options::default();
	let now = chrono::DateTime::from_timestamp_micros(app.time.now().timestamp_micros()).unwrap();
	*app.time.0.lock().unwrap() = now;
	let mut ids = vec![];
	for index in 0..5 {
		let id = datei(&app, owner, false, "noten", "application/pdf").await;
		text(
			&app,
			id,
			"",
			if index == 3 {
				1
			} else if index == 4 {
				4
			} else {
				0
			},
			0,
		)
		.await;
		sqlx::query(
			r#"UPDATE extraction_jobs SET "CreatedAt"=$2,"LastEnqueuedAt"=$3 WHERE "DateiId"=$1"#,
		)
		.bind(id)
		.bind(now - chrono::Duration::minutes(10 - index))
		.bind(if index == 0 {
			None
		} else if index == 1 {
			Some(now - options.redisplay_after - chrono::Duration::seconds(1))
		} else {
			Some(now - options.redisplay_after)
		})
		.execute(&app.pool)
		.await
		.unwrap();
		ids.push(id);
	}
	let mut bounded = options.clone();
	bounded.max_dispatch_per_run = 1;
	assert_eq!(
		dispatch(&app.pool, &queue, &bounded, app.time.as_ref())
			.await
			.unwrap(),
		1
	);
	assert_eq!(queue.0.lock().unwrap().len(), 1);
	assert!(queue.0.lock().unwrap()[0].contains(&ids[0].to_string()));
	*queue.2.lock().unwrap() = Some(ids[1].to_string());
	let last = datei(&app, owner, false, "noten", "application/pdf").await;
	text(&app, last, "", 0, 0).await;
	assert_eq!(
		dispatch(&app.pool, &queue, &options, app.time.as_ref())
			.await
			.unwrap(),
		1
	);
	assert!(
		Job::load(&app.pool, last)
			.await
			.unwrap()
			.unwrap()
			.last_enqueued_at
			.is_some()
	);
	assert_eq!(queue.0.lock().unwrap().len(), 3);
	assert_eq!(
		dispatch(
			&app.pool,
			&archive_server::queue::AzureQueue::unconfigured(),
			&options,
			app.time.as_ref()
		)
		.await
		.unwrap(),
		0
	);
	assert_eq!(
		Job::load(&app.pool, ids[1])
			.await
			.unwrap()
			.unwrap()
			.last_enqueued_at,
		Some(now - options.redisplay_after - chrono::Duration::seconds(1))
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn finalize_hook_outbox_same_commit_quiet_two_sends_stamp_failure_and_reentry() {
	use std::sync::{Arc, atomic::Ordering};
	use support::{storage::FakeStorage, upload::*};
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	let queue = Arc::new(Capture::default());
	app.configure_uploads(
		storage.clone(),
		Default::default(),
		vec![Arc::new(archive_server::extraction::Hook {
			queue: queue.clone(),
			clock: app.time.clone(),
		})],
		vec![],
	);
	let mut editor = app.admin().await;
	let owner = create(&mut editor, "Hook").await;
	let session = stage(
		&mut editor,
		owner,
		false,
		json!({"kind":"noten","isPartitur":true,"fileName":"score.pdf","contentType":"application/pdf"}),
		&pdf(128),
		"application/pdf",
		&storage,
	)
	.await;
	let (code, body) = finish(&mut editor, &session).await;
	assert_eq!(code, 200, "{body}");
	let id = uuid::Uuid::parse_str(body["datei"]["id"].as_str().unwrap()).unwrap();
	let job = archive_server::extraction::Job::load(&app.pool, id)
		.await
		.unwrap()
		.unwrap();
	assert!(job.last_enqueued_at.is_some());
	assert_eq!(queue.0.lock().unwrap().len(), 1);
	let wire: serde_json::Value = serde_json::from_str(&queue.0.lock().unwrap()[0]).unwrap();
	assert_eq!(wire["type"], "extraction");
	assert_eq!(wire["dateiId"], id.to_string());
	assert!(wire.get("traceParent").is_some());
	assert!(wire.get("traceState").is_some());
	assert_eq!(finish(&mut editor, &session).await.0, 200);
	assert_eq!(queue.0.lock().unwrap().len(), 1);
	queue.1.store(1, Ordering::Relaxed);
	let second = upload(
		&mut editor,
		owner,
		json!({"kind":"sonstiges","contentType":"application/pdf","fileName":"other.pdf"}),
		&pdf(128),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(queue.0.lock().unwrap().len(), 3);
	let id = uuid::Uuid::parse_str(second["id"].as_str().unwrap()).unwrap();
	assert!(
		archive_server::extraction::Job::load(&app.pool, id)
			.await
			.unwrap()
			.unwrap()
			.last_enqueued_at
			.is_none()
	);
	// A stamp-only failure follows an accepted send and must not resend it.
	queue.1.store(0, Ordering::Relaxed);
	sqlx::raw_sql("CREATE FUNCTION reject_stamp226() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic stamp failure'; END $$; CREATE TRIGGER reject_stamp226 BEFORE UPDATE ON extraction_jobs FOR EACH ROW EXECUTE FUNCTION reject_stamp226();").execute(&app.pool).await.unwrap();
	let third = upload(
		&mut editor,
		owner,
		json!({"kind":"noten","isPartitur":true,"contentType":"application/pdf","fileName":"third.pdf"}),
		&pdf(128),
		"application/pdf",
		&storage,
	)
	.await;
	assert_eq!(queue.0.lock().unwrap().len(), 4);
	let id = uuid::Uuid::parse_str(third["id"].as_str().unwrap()).unwrap();
	assert!(
		archive_server::extraction::Job::load(&app.pool, id)
			.await
			.unwrap()
			.unwrap()
			.last_enqueued_at
			.is_none()
	);
	sqlx::raw_sql("DROP TRIGGER reject_stamp226 ON extraction_jobs;")
		.execute(&app.pool)
		.await
		.unwrap();
	let audio = upload(
		&mut editor,
		owner,
		json!({"kind":"uebe","stimme":"Tenor 1","contentType":"audio/mpeg","fileName":"voice.mp3"}),
		b"ID3synthetic",
		"audio/mpeg",
		&storage,
	)
	.await;
	assert!(
		archive_server::extraction::Job::load(
			&app.pool,
			uuid::Uuid::parse_str(audio["id"].as_str().unwrap()).unwrap()
		)
		.await
		.unwrap()
		.is_none()
	);
	assert_eq!(queue.0.lock().unwrap().len(), 4);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn status_retry_auth_bounds_terminal_and_stale() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = lied(&app, "Extraction", json!({})).await;
	let id = datei(&app, lied, false, "noten", "application/pdf").await;
	assert_eq!(member.get("/api/dateien/extraction").await.0, 403);
	assert_eq!(app.client().get("/api/dateien/extraction").await.0, 401);
	let path = format!("/api/dateien/{id}/extraction/retry");
	let (code, body) = editor.post(&path, json!({})).await;
	assert_eq!(code, 200, "{body}");
	assert_eq!(body["status"], "queued");
	assert!(body["lastEnqueuedAt"].is_null());
	let (_, body) = editor
		.get(&format!(
			"/api/dateien/extraction?ids=invalid,{id},{id},{}",
			uuid::Uuid::new_v4()
		))
		.await;
	assert_eq!(body["results"].as_array().unwrap().len(), 1);
	sqlx::query(
		r#"UPDATE extraction_jobs SET "Status"=1,"LastAttemptAt"=now() WHERE "DateiId"=$1"#,
	)
	.bind(id)
	.execute(&app.pool)
	.await
	.unwrap();
	assert_eq!(editor.post(&path, json!({})).await.0, 409);
	sqlx::query(r#"UPDATE extraction_jobs SET "LastAttemptAt"=NULL WHERE "DateiId"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&path, json!({})).await.1["attemptCount"], 0);
	sqlx::query(
		r#"UPDATE extraction_jobs SET "Status"=2,"Text"='Tenor 1 A♭ 44 Hosianna' WHERE "DateiId"=$1"#,
	)
	.bind(id)
	.execute(&app.pool)
	.await
	.unwrap();
	let (_, body) = editor.post(&path, json!({})).await;
	assert_eq!(body["status"], "completed");
	assert_eq!(body["facts"]["voice"], "Tenor 1");
	assert_eq!(body["facts"]["musicalKey"], "A♭");
	let too_many = (0..101)
		.map(|_| uuid::Uuid::new_v4().to_string())
		.collect::<Vec<_>>()
		.join(",");
	assert_eq!(
		editor
			.get(&format!("/api/dateien/extraction?ids={too_many}"))
			.await
			.0,
		400
	);
	let original = editor.token.clone();
	editor.token.clear();
	let (status, headers, _) = editor.wire("POST", &path, "").await;
	assert_eq!(status, 400);
	assert_eq!(headers["cache-control"], "no-store");
	editor.token = original;
	let audio = datei(&app, lied, false, "uebe", "audio/mpeg").await;
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{audio}/extraction/retry"), json!({}))
			.await
			.0,
		422
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL insert-race gate"]
async fn retry_insert_race_reloads_running_terminal_or_failed_winner() {
	use archive_server::auth::Clock;
	let app = App::new().await;
	let mut editor = app.admin().await;
	let owner = lied(&app, "Insert race", json!({})).await;
	sqlx::raw_sql("CREATE FUNCTION pause_insert226() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF current_setting('archive.fixture226',true) IS DISTINCT FROM 'yes' THEN PERFORM pg_sleep(0.5); END IF; RETURN NEW; END $$; CREATE TRIGGER pause_insert226 BEFORE INSERT ON extraction_jobs FOR EACH ROW EXECUTE FUNCTION pause_insert226();").execute(&app.pool).await.unwrap();
	for (status, expected, reply) in [
		(1, 409, ""),
		(2, 200, "completed"),
		(3, 200, "noText"),
		(4, 200, "queued"),
	] {
		let id = datei(&app, owner, false, "noten", "application/pdf").await;
		let path = format!("/api/dateien/{id}/extraction/retry");
		let mut client = app.sign_in("verwaltung@liedertafel.test").await;
		let task = tokio::spawn(async move { client.post(&path, json!({})).await });
		let mut sleeping = false;
		for _ in 0..100 {
			sleeping=sqlx::query_scalar::<_,bool>("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep')").fetch_one(&app.pool).await.unwrap();
			if sleeping {
				break;
			}
			tokio::time::sleep(std::time::Duration::from_millis(5)).await;
		}
		assert!(sleeping, "HTTP insert did not reach the SQL race boundary");
		let mut tx = app.pool.begin().await.unwrap();
		sqlx::raw_sql("SET LOCAL archive.fixture226='yes'")
			.execute(&mut *tx)
			.await
			.unwrap();
		sqlx::query(r#"INSERT INTO extraction_jobs ("DateiId","Status","AttemptCount","LastAttemptAt","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,$2,3,$3,$4,$3,$3,7)"#).bind(id).bind(status).bind(app.time.now()).bind(uuid::Uuid::nil()).execute(&mut *tx).await.unwrap();
		tx.commit().await.unwrap();
		let (code, body) = task.await.unwrap();
		assert_eq!(code, expected, "{body}");
		if code == 200 {
			assert_eq!(body["status"], reply);
		}
		let (_, body) = editor
			.get(&format!("/api/dateien/extraction?ids={id}"))
			.await;
		assert_eq!(
			body["results"][0]["attemptCount"],
			if status == 4 { 0 } else { 3 }
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL bounded insert-failure gate"]
async fn retry_insert_failure_stops_at_two_passes() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let owner = lied(&app, "Insert failure", json!({})).await;
	let id = datei(&app, owner, false, "noten", "application/pdf").await;
	sqlx::raw_sql("CREATE SEQUENCE failed_insert226; CREATE FUNCTION fail_insert226() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('failed_insert226'); RAISE EXCEPTION 'synthetic insert refusal'; END $$; CREATE TRIGGER fail_insert226 BEFORE INSERT ON extraction_jobs FOR EACH ROW EXECUTE FUNCTION fail_insert226();").execute(&app.pool).await.unwrap();
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{id}/extraction/retry"), json!({}))
			.await
			.0,
		409
	);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT last_value FROM failed_insert226")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		2
	);
	app.close().await;
}
