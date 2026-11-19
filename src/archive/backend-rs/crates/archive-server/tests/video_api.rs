#![allow(clippy::unwrap_used)] // Real authenticated HTTP/SQL assertions.
mod support;
use serde_json::json;
use std::sync::Arc;
use support::{App, storage::FakeStorage, upload};

#[tokio::test]
#[ignore = "explicit current PostgreSQL/authenticated HTTP stored enum fallback gate"]
async fn unknown_stored_video_status_retains_the_frozen_numeric_payload() {
	use archive_server::{
		Origin, Settings,
		auth::{Auth, AuthConfig, SystemClock},
		build_router,
		queue::{AzureQueue, Queue},
	};
	let mut v =
		support::videos::Video::new(support::videos::CLIP, "aufnahme", "video/mp4", false).await;
	let mut origin = Origin::new(Settings::new(true, Default::default(), 0)).with_auth(
		Auth::new(
			v.app.pool.clone(),
			AuthConfig::development(),
			v.app.mail.clone(),
			v.app.time.clone(),
		)
		.unwrap(),
	);
	origin.video_queue = Arc::new(
		AzureQueue::select(
			Some(&std::env::var("ARCHIVE_TEST_QUEUE").unwrap()),
			None,
			&v.queue_name,
			true,
			Arc::new(SystemClock),
		)
		.unwrap(),
	);
	v.app.router = build_router(Arc::new(origin));
	let app = &v.app;
	let id = v.id;
	let mut editor = app.admin().await;
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{id}/video/retry"), json!({}))
			.await
			.0,
		200
	);
	let initial = v.queue.receive(2, 30).await.unwrap();
	assert_eq!(
		initial.len(),
		1,
		"positive actual retry sends one notification"
	);
	v.queue.delete(&initial[0]).await.unwrap();
	let mut failures = Vec::new();
	// Literal frozen VideoEndpoints.ToStatusString cases. Unknown values use
	// the signed integer Enum.ToString(), never the Failed name or a number token.
	for (stored, expected) in [
		(0, "queued"),
		(1, "running"),
		(2, "completed"),
		(3, "notNeeded"),
		(4, "failed"),
		(-1, "-1"),
		(5, "5"),
		(i32::MIN, "-2147483648"),
		(i32::MAX, "2147483647"),
	] {
		sqlx::query(r#"UPDATE video_jobs SET "Status"=$2 WHERE "DateiId"=$1"#)
			.bind(id)
			.bind(stored)
			.execute(&app.pool)
			.await
			.unwrap();
		let (status, body) = editor.get(&format!("/api/dateien/videos?ids={id}")).await;
		assert_eq!(status, 200, "{body}");
		let item = &body["results"][0];
		assert_eq!(item["dateiId"], id.to_string());
		assert_eq!(item["rowVersion"], 0);
		assert_eq!(item["attemptCount"], 0);
		if item["status"] != expected {
			failures.push((stored, item["status"].clone(), expected));
		}
		if !matches!(stored, 0..=4) {
			let before: serde_json::Value =
				sqlx::query_scalar("SELECT to_jsonb(j) FROM video_jobs j WHERE \"DateiId\"=$1")
					.bind(id)
					.fetch_one(&app.pool)
					.await
					.unwrap();
			let (status, reply) = editor
				.post(&format!("/api/dateien/{id}/video/retry"), json!({}))
				.await;
			assert_eq!(status, 200);
			assert_eq!(
				reply, *item,
				"unknown status retry is an unchanged literal payload"
			);
			let after: serde_json::Value =
				sqlx::query_scalar("SELECT to_jsonb(j) FROM video_jobs j WHERE \"DateiId\"=$1")
					.bind(id)
					.fetch_one(&app.pool)
					.await
					.unwrap();
			assert_eq!(
				after, before,
				"unknown status may not authorize a retry/reset/stamp"
			);
			assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
		}
	}
	v.done().await;
	assert!(
		failures.is_empty(),
		"frozen literal HTTP status mismatches: {failures:?}"
	);
}

#[tokio::test]
#[ignore = "explicit current PG/video HTTP anonymous CSRF current membership and no-store controls"]
async fn video_routes_require_current_editor_membership_csrf_and_never_cache() {
	use support::catalogue::{datei, lied};
	let app = App::new().await;
	let mut admin = app.admin().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let editor_id = editor.get("/api/auth/me").await.1["accountId"].clone();
	let owner = lied(&app, "Video access", json!({})).await;
	let id = datei(&app, owner, false, "aufnahme", "video/mp4").await;
	let read = format!("/api/dateien/videos?ids={id}");
	let write = format!("/api/dateien/{id}/video/retry");
	let mut anonymous = app.client();
	anonymous.csrf().await;
	let mut member = app.member().await;
	for (client, expected) in [(&mut anonymous, 401), (&mut member, 403)] {
		for (method, path) in [("GET", &read), ("POST", &write)] {
			let (status, headers, _) = client.wire(method, path, "{}").await;
			assert_eq!(status, expected);
			assert_eq!(headers["cache-control"], "no-store");
		}
	}
	let token = editor.token.clone();
	for forged in ["", "forged-video-csrf-token"] {
		editor.token = forged.into();
		let (status, headers, _) = editor.wire("POST", &write, "{}").await;
		assert_eq!(status, 400);
		assert_eq!(headers["cache-control"], "no-store");
	}
	editor.token = token;
	let jobs: i64 = sqlx::query_scalar("SELECT count(*) FROM video_jobs")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(jobs, 0, "denied retries may not create work");
	for (method, path) in [("POST", &write), ("GET", &read)] {
		let (status, headers, _) = editor.wire(method, path, "{}").await;
		assert_eq!(status, 200);
		assert_eq!(headers["cache-control"], "no-store");
	}
	let before = editor.get(&read).await.1;
	assert_eq!(
		admin
			.post(
				"/api/admin/members/role",
				json!({"accountId":editor_id,"role":"Member"})
			)
			.await
			.0,
		200
	);
	for (method, path) in [("GET", &read), ("POST", &write)] {
		let (status, headers, _) = editor.wire(method, path, "{}").await;
		assert_eq!(
			status, 403,
			"existing cookie must not retain editor authority"
		);
		assert_eq!(headers["cache-control"], "no-store");
	}
	assert_eq!(
		admin
			.post(
				"/api/admin/members/deactivate",
				json!({"accountId":editor_id})
			)
			.await
			.0,
		200
	);
	for (method, path) in [("GET", &read), ("POST", &write)] {
		let (status, headers, _) = editor.wire(method, path, "{}").await;
		assert_eq!(
			status,
			if method == "GET" { 401 } else { 400 },
			"revoked session is unauthorized; stale identity-bound CSRF is rejected before mutation authorization"
		);
		assert_eq!(headers["cache-control"], "no-store");
	}
	editor.csrf().await;
	let (status, headers, _) = editor.wire("POST", &write, "{}").await;
	assert_eq!(
		status, 401,
		"valid anonymous CSRF cannot restore revoked membership"
	);
	assert_eq!(headers["cache-control"], "no-store");
	assert_eq!(admin.get(&read).await.1, before);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn finalized_video_outbox_and_editor_status_are_real_and_idempotent() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.configure_uploads(
		storage.clone(),
		Default::default(),
		vec![Arc::new(archive_server::videos::Hook {
			queue: Arc::new(archive_server::queue::AzureQueue::unconfigured()),
			clock: app.time.clone(),
		})],
		vec![],
	);
	let mut editor = app.admin().await;
	let owner = upload::create(&mut editor, "Video Upload").await;
	let bytes = support::recordings::mp4();
	let session = upload::stage(
		&mut editor,
		owner,
		false,
		json!({"kind":"aufnahme","contentType":"video/mp4","fileName":"Konzert.mp4"}),
		&bytes,
		"video/mp4",
		&storage,
	)
	.await;
	let (s, b) = upload::finish(&mut editor, &session).await;
	assert_eq!(s, 200, "{b}");
	let id = b["datei"]["id"].as_str().unwrap();
	let path = format!("/api/dateien/videos?ids={id},invalid,{id}");
	let (s, b) = editor.get(&path).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["results"].as_array().unwrap().len(), 1);
	let item = &b["results"][0];
	assert_eq!(item["status"], "queued");
	assert_eq!(item["attemptCount"], 0);
	assert_eq!(item["rowVersion"], 0);
	for key in [
		"playbackDateiId",
		"failureReason",
		"completedAt",
		"lastAttemptAt",
		"lastEnqueuedAt",
	] {
		assert!(item.get(key).unwrap().is_null(), "{key}");
	}
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{id}/video/retry"), json!({}))
			.await
			.0,
		200
	);
	let mut member = app.member().await;
	assert_eq!(member.get(&path).await.0, 403);
	assert_eq!(
		member
			.post(&format!("/api/dateien/{id}/video/retry"), json!({}))
			.await
			.0,
		403
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit authenticated current PostgreSQL video status/retry matrix"]
async fn status_order_bounds_nulls_parent_policy_and_retry_state_matrix() {
	use support::catalogue::*;
	use uuid::Uuid;
	let app = App::new().await;
	let owner = lied(&app, "Video matrix", json!({})).await;
	let mut editor = app.admin().await;
	let first = datei(&app, owner, false, "aufnahme", "video/mp4").await;
	let second = datei(&app, owner, false, "aufnahme", "video/quicktime").await;
	for id in [first, second] {
		let (s, b) = editor
			.post(&format!("/api/dateien/{id}/video/retry"), json!({}))
			.await;
		assert_eq!(s, 200, "{b}");
		assert_eq!(b["rowVersion"], 0);
	}
	let query = format!(
		"/api/dateien/videos?IDS={},invalid,{},{}&ids={}",
		second.simple(),
		first.braced(),
		second,
		Uuid::new_v4()
	);
	let (s, b) = editor.get(&query).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["results"][0]["dateiId"], second.to_string());
	assert_eq!(b["results"][1]["dateiId"], first.to_string());
	assert_eq!(b["results"].as_array().unwrap().len(), 2);
	let many = (0..101)
		.map(|_| Uuid::new_v4().to_string())
		.collect::<Vec<_>>()
		.join(",");
	assert_eq!(
		editor
			.get(&format!("/api/dateien/videos?ids={many}"))
			.await
			.0,
		400
	);
	let path = format!("/api/dateien/{first}/video/retry");
	sqlx::query(r#"UPDATE video_jobs SET "Status"=1,"AttemptCount"=2,"LastAttemptAt"=now()+interval '1 minute',"LastEnqueuedAt"=now() WHERE "DateiId"=$1"#).bind(first).execute(&app.pool).await.unwrap();
	assert_eq!(editor.post(&path, json!({})).await.0, 409);
	sqlx::query(r#"UPDATE video_jobs SET "LastAttemptAt"=now()-interval '2 hours',"FailureReason"='synthetic' WHERE "DateiId"=$1"#).bind(first).execute(&app.pool).await.unwrap();
	let (s, b) = editor.post(&path, json!({})).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["status"], "queued");
	assert_eq!(b["attemptCount"], 0);
	assert_eq!(b["rowVersion"], 1);
	for k in ["lastAttemptAt", "lastEnqueuedAt", "failureReason"] {
		assert!(b[k].is_null());
	}
	for status in [2, 3] {
		sqlx::query(r#"UPDATE video_jobs SET "Status"=$2 WHERE "DateiId"=$1"#)
			.bind(first)
			.bind(status)
			.execute(&app.pool)
			.await
			.unwrap();
		let (s, b) = editor.post(&path, json!({})).await;
		assert_eq!(s, 200, "{b}");
		assert_eq!(b["rowVersion"], 1);
		assert_eq!(
			b["status"],
			if status == 2 {
				"completed"
			} else {
				"notNeeded"
			}
		);
	}
	sqlx::query(r#"UPDATE video_jobs SET "Status"=4,"AttemptCount"=3 WHERE "DateiId"=$1"#)
		.bind(first)
		.execute(&app.pool)
		.await
		.unwrap();
	let (s, b) = editor.post(&path, json!({})).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["attemptCount"], 0);
	assert_eq!(b["rowVersion"], 2);
	let non = datei(&app, owner, false, "aufnahme", "audio/mpeg").await;
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{non}/video/retry"), json!({}))
			.await
			.0,
		422
	);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(owner)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		editor
			.get(&format!("/api/dateien/videos?ids={first}"))
			.await
			.1["results"]
			.as_array()
			.unwrap()
			.len(),
		1
	);
	assert_eq!(editor.post(&path, json!({})).await.0, 404);
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(first)
		.execute(&app.pool)
		.await
		.unwrap();
	assert!(
		editor
			.get(&format!("/api/dateien/videos?ids={first}"))
			.await
			.1["results"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	app.close().await;
}

struct StampRace {
	pool: sqlx::PgPool,
	calls: std::sync::atomic::AtomicUsize,
	fail: bool,
}
impl archive_server::queue::Queue for StampRace {
	fn send<'a>(
		&'a self,
		body: &'a str,
	) -> futures::future::BoxFuture<'a, Result<bool, archive_server::queue::QueueError>> {
		Box::pin(async move {
			self.calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
			if self.fail {
				return Err(archive_server::queue::QueueError);
			}
			let body: serde_json::Value = serde_json::from_str(body).unwrap();
			assert_eq!(body["type"], "transcode");
			let id = uuid::Uuid::parse_str(body["dateiId"].as_str().unwrap()).unwrap();
			// If send ran before commit this independent connection could not see
			// the canonical job/Datei. Simulate a newer reset after accepted send.
			let n = sqlx::query(
				r#"UPDATE video_jobs SET "RowVersion"="RowVersion"+1,"LastEnqueuedAt"=NULL WHERE "DateiId"=$1"#,
			)
			.bind(id)
			.execute(&self.pool)
			.await
			.unwrap()
			.rows_affected();
			assert_eq!(n, 1);
			Ok(true)
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
#[ignore = "explicit real PostgreSQL/HTTP finalize outbox and selected-version CAS"]
async fn finalize_commits_both_owner_outboxes_before_quiet_send_and_never_stamps_a_newer_version() {
	use std::sync::atomic::Ordering;
	for fail in [false, true] {
		let mut app = App::new().await;
		let storage = Arc::new(FakeStorage::default());
		let queue = Arc::new(StampRace {
			pool: app.pool.clone(),
			calls: Default::default(),
			fail,
		});
		app.configure_uploads(
			storage.clone(),
			Default::default(),
			vec![Arc::new(archive_server::videos::Hook {
				queue: queue.clone(),
				clock: app.time.clone(),
			})],
			vec![],
		);
		let mut editor = app.admin().await;
		for is_event in [false, true] {
			let owner = if is_event {
				support::recordings::event(&mut editor).await
			} else {
				upload::create(&mut editor, "Outbox").await
			};
			let bytes = support::recordings::mp4();
			let session = upload::stage(
				&mut editor,
				owner,
				is_event,
				json!({"kind":"aufnahme","contentType":"video/mp4"}),
				&bytes,
				"video/mp4",
				&storage,
			)
			.await;
			let (s, b) = upload::finish(&mut editor, &session).await;
			assert_eq!(s, 200, "{b}");
			let id = b["datei"]["id"].as_str().unwrap();
			let (s, b) = editor.get(&format!("/api/dateien/videos?ids={id}")).await;
			assert_eq!(s, 200, "{b}");
			let item = &b["results"][0];
			assert_eq!(item["rowVersion"], if fail { 0 } else { 1 });
			assert!(item["lastEnqueuedAt"].is_null());
			let before = queue.calls.load(Ordering::SeqCst);
			assert_eq!(upload::finish(&mut editor, &session).await.0, 200);
			assert_eq!(queue.calls.load(Ordering::SeqCst), before);
		}
		assert_eq!(queue.calls.load(Ordering::SeqCst), if fail { 4 } else { 2 });
		app.close().await;
	}
}

#[tokio::test]
#[ignore = "explicit real PostgreSQL upload-finalize rollback/concurrency gate"]
async fn video_outbox_failure_rolls_back_finalize_and_concurrent_replay_creates_one_job() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.configure_uploads(
		storage.clone(),
		Default::default(),
		vec![Arc::new(archive_server::videos::Hook {
			queue: Arc::new(archive_server::queue::AzureQueue::unconfigured()),
			clock: app.time.clone(),
		})],
		vec![],
	);
	let mut editor = app.admin().await;
	let owner = upload::create(&mut editor, "Rollback").await;
	let bytes = support::recordings::mp4();
	let session = upload::stage(
		&mut editor,
		owner,
		false,
		json!({"kind":"aufnahme","contentType":"video/mp4"}),
		&bytes,
		"video/mp4",
		&storage,
	)
	.await;
	sqlx::raw_sql("CREATE FUNCTION video233_refuse() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic'; END $$; CREATE TRIGGER video233_refuse BEFORE INSERT ON video_jobs FOR EACH ROW EXECUTE FUNCTION video233_refuse()").execute(&app.pool).await.unwrap();
	assert_eq!(upload::finish(&mut editor, &session).await.0, 500);
	for table in ["dateien", "video_jobs"] {
		let count: i64 = if table == "dateien" {
			sqlx::query_scalar("SELECT count(*) FROM dateien")
				.fetch_one(&app.pool)
				.await
				.unwrap()
		} else {
			sqlx::query_scalar("SELECT count(*) FROM video_jobs")
				.fetch_one(&app.pool)
				.await
				.unwrap()
		};
		assert_eq!(count, 0);
	}
	sqlx::raw_sql("DROP TRIGGER video233_refuse ON video_jobs; DROP FUNCTION video233_refuse()")
		.execute(&app.pool)
		.await
		.unwrap();
	let mut peer = app.client();
	peer.cookies = editor.cookies.clone();
	peer.token = editor.token.clone();
	let (a, b) = tokio::join!(
		upload::finish(&mut editor, &session),
		upload::finish(&mut peer, &session)
	);
	assert!([200, 409].contains(&a.0.as_u16()));
	assert!([200, 409].contains(&b.0.as_u16()));
	assert!(a.0 == 200 || b.0 == 200);
	assert_eq!(upload::finish(&mut editor, &session).await.0, 200);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM video_jobs")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	app.close().await;
}
