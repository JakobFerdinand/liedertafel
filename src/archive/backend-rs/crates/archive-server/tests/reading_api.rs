#![allow(clippy::unwrap_used, clippy::expect_used)] // Real authenticated HTTP/SQL assertions.
mod support;
use archive_server::{
	Origin, Settings,
	auth::{Auth, AuthConfig, Clock},
	reading,
};
use serde_json::json;
use std::sync::Arc;
use support::{App, catalogue::*};

fn configure(app: &mut App, enabled: bool) {
	let mut origin = Origin::new(Settings::new(true, Default::default(), 0)).with_auth(
		Auth::new(
			app.pool.clone(),
			AuthConfig::development(),
			app.mail.clone(),
			app.time.clone(),
		)
		.unwrap(),
	);
	origin.reading_enabled = enabled;
	app.router = archive_server::build_router(Arc::new(origin));
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; reading roles/CSRF/disabled/null/no-store and removed routes"]
async fn reading_actions_preserve_authorization_and_disabled_visibility() {
	let mut app = App::new_scoped("reading229").await;
	configure(&mut app, true);
	let mut admin = app.admin().await;
	let mut member = app.member().await;
	let mut anonymous = app.client();
	anonymous.csrf().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let owner = lied(&app, "Noten", json!({})).await;
	let id = datei(&app, owner, false, "noten", "application/pdf").await;
	text(&app, id, "Text", 2, 0).await;
	reading::insert(&app.pool, id, uuid::Uuid::nil(), 20, app.time.now())
		.await
		.unwrap();
	for action in ["retry", "continue"] {
		let path = format!("/api/dateien/{id}/reading/{action}");
		assert_eq!(anonymous.post(&path, json!({})).await.0, 401);
		assert_eq!(member.post(&path, json!({})).await.0, 403);
		let token = editor.token.clone();
		editor.token.clear();
		assert_eq!(editor.post(&path, json!({})).await.0, 400);
		editor.token = token;
	}
	let (_, headers, _) = editor
		.wire("GET", &format!("/api/dateien/extraction?ids={id}"), "")
		.await;
	assert_eq!(headers["cache-control"], "no-store");
	for path in [
		"/api/proposals",
		"/api/provenance/revert",
		"/api/dev/provenance-write",
	] {
		assert_eq!(admin.post(path, json!({})).await.0, 404);
	}
	configure(&mut app, false);
	editor.router = app.router.clone();
	for action in ["retry", "continue"] {
		let (status, body) = editor
			.post(&format!("/api/dateien/{id}/reading/{action}"), json!({}))
			.await;
		assert_eq!(status, 409);
		assert_eq!(
			body["title"],
			"Das Lesen durch die KI ist noch nicht eingerichtet."
		);
	}
	let (_, body) = editor
		.get(&format!("/api/dateien/extraction?ids={id}"))
		.await;
	assert_eq!(body["results"][0]["readingEnabled"], false);
	assert!(body["results"][0]["reading"].is_null());
	assert_eq!(
		reading::Job::load(&app.pool, id)
			.await
			.unwrap()
			.unwrap()
			.row_version,
		0
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL; reading retry/continue HTTP state and material matrix"]
async fn reading_retry_continue_state_matrix_and_insert_races() {
	let mut app = App::new_scoped("reading229").await;
	configure(&mut app, true);
	let mut editor = app.admin().await;
	let owner = lied(&app, "Noten", json!({})).await;
	let id = datei(&app, owner, false, "noten", "application/pdf").await;
	text(&app, id, "Text", 2, 0).await;
	let retry = format!("/api/dateien/{id}/reading/retry");
	let continuation = format!("/api/dateien/{id}/reading/continue");
	assert_eq!(editor.post(&continuation, json!({})).await.0, 409);
	let (a, b) = tokio::join!(
		{
			let mut c = app.sign_in("redaktion@liedertafel.test").await;
			let p = retry.clone();
			async move { c.post(&p, json!({})).await }
		},
		{
			let mut c = app.sign_in("redaktion@liedertafel.test").await;
			let p = retry.clone();
			async move { c.post(&p, json!({})).await }
		}
	);
	assert_eq!(
		(a.0, b.0),
		(axum::http::StatusCode::OK, axum::http::StatusCode::OK)
	);
	for (status, partial, full, restart, expected) in [
		(0, false, false, true, 200),
		(2, false, false, true, 200),
		(3, false, false, true, 200),
		(3, false, false, false, 409),
		(3, true, true, false, 409),
		(3, true, false, false, 200),
		(4, false, false, false, 409),
		(4, false, false, true, 200),
	] {
		sqlx::query(r#"UPDATE reading_jobs SET "Status"=$2,"PagesRead"=10,"PagesTotal"=$3,"PageLimit"=20,"FieldsDone"=true,"AttemptCount"=3,"FailureReason"='Test',"LastAttemptAt"=NULL WHERE "DateiId"=$1"#).bind(id).bind(status).bind(if partial{35}else{10}).execute(&app.pool).await.unwrap();
		sqlx::query(r#"UPDATE extraction_jobs SET "Text"=$2 WHERE "DateiId"=$1"#)
			.bind(id)
			.bind(if full {
				"x".repeat(100000)
			} else {
				"Text".into()
			})
			.execute(&app.pool)
			.await
			.unwrap();
		let (actual, _) = editor
			.post(if restart { &retry } else { &continuation }, json!({}))
			.await;
		assert_eq!(
			actual.as_u16(),
			expected,
			"status={status},partial={partial},full={full},restart={restart}"
		);
		let row = reading::Job::load(&app.pool, id).await.unwrap().unwrap();
		if status == 4 && restart {
			assert_eq!(
				(row.attempt_count, row.pages_read, row.failure_reason),
				(0, 10, None)
			);
		}
		if status == 3 && partial && !full && !restart {
			assert_eq!(
				(row.page_limit, row.fields_done, row.attempt_count),
				(30, false, 0)
			);
		}
	}
	sqlx::query(r#"UPDATE reading_jobs SET "Status"=1,"LastAttemptAt"=$2 WHERE "DateiId"=$1"#)
		.bind(id)
		.bind(app.time.now())
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(editor.post(&retry, json!({})).await.0, 409);
	for status in [0, 1, 4] {
		sqlx::query(r#"UPDATE extraction_jobs SET "Status"=$2 WHERE "DateiId"=$1"#)
			.bind(id)
			.bind(status)
			.execute(&app.pool)
			.await
			.unwrap();
		assert_eq!(editor.post(&retry, json!({})).await.0, 409);
	}
	let document = datei(&app, owner, false, "sonstiges", "application/pdf").await;
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{document}/reading/retry"), json!({}))
			.await
			.0,
		422
	);
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{}/reading/retry", uuid::Uuid::new_v4()),
				json!({})
			)
			.await
			.0,
		404
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit PostgreSQL; unchanged reading HTTP surface"]
async fn reading_actions_auth_state_disabled_partial_and_bodyless_contracts() {
	let mut app = App::new_scoped("reading229").await;
	let mut origin = Origin::new(Settings::new(true, Default::default(), 0)).with_auth(
		Auth::new(
			app.pool.clone(),
			AuthConfig::development(),
			app.mail.clone(),
			app.time.clone(),
		)
		.unwrap(),
	);
	origin.reading_enabled = true;
	app.router = archive_server::build_router(Arc::new(origin));
	let mut editor = app.admin().await;
	let owner = lied(&app, "Waldfahrt", json!({})).await;
	let id = datei(&app, owner, false, "noten", "application/pdf").await;
	text(&app, id, "Text", 2, 0).await;
	let path = format!("/api/dateien/{id}/reading/retry");
	let (status, _) = editor.raw("POST", &path, "malformed").await;
	assert_eq!(status, axum::http::StatusCode::OK);
	let row = reading::Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!((row.status, row.page_limit), (0, 20));
	let (_, body) = editor
		.get(&format!("/api/dateien/extraction?ids={id}"))
		.await;
	assert_eq!(body["results"][0]["reading"]["status"], "queued");
	sqlx::query(r#"UPDATE reading_jobs SET "Status"=3,"PagesRead"=20,"PagesTotal"=35,"FieldsDone"=true WHERE "DateiId"=$1"#).bind(id).execute(&app.pool).await.unwrap();
	let (status, body) = editor
		.post(&format!("/api/dateien/{id}/reading/continue"), json!({}))
		.await;
	assert_eq!(status, axum::http::StatusCode::OK);
	assert_eq!(body["reading"]["pagesRead"], 20);
	let row = reading::Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!((row.page_limit, row.fields_done), (40, false));
	app.close().await;
}
