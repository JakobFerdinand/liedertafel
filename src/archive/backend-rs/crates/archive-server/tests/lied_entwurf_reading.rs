#![allow(clippy::unwrap_used, clippy::expect_used)] // Authenticated HTTP and persisted outcome assertions.
mod support;
use archive_server::{
	Origin, Settings,
	auth::{Auth, AuthConfig},
};
use axum::{body::Body, http::Request};
use http_body_util::BodyExt;
use serde_json::{Value, json};
use std::sync::Arc;
use support::lesung;
use tower::ServiceExt;
use uuid::Uuid;

async fn post(
	client: &support::Client,
	id: Uuid,
	file: Uuid,
	number: i32,
) -> (axum::http::StatusCode, Value) {
	let bytes = support::reading::scan(1);
	let mut body=format!("--lesen230\r\nContent-Disposition: form-data; name=\"entwurfId\"\r\n\r\n{id}\r\n--lesen230\r\nContent-Disposition: form-data; name=\"dateiId\"\r\n\r\n{file}\r\n--lesen230\r\nContent-Disposition: form-data; name=\"anfrageId\"\r\n\r\n{number}\r\n--lesen230\r\nContent-Disposition: form-data; name=\"datei\"; filename=\"a.pdf\"\r\n\r\n").into_bytes();
	body.extend(bytes);
	body.extend(b"\r\n--lesen230--\r\n");
	let response = client
		.router
		.clone()
		.oneshot(
			Request::post("/api/lied-lesungen")
				.header("content-type", "multipart/form-data; boundary=lesen230")
				.header("X-CSRF-TOKEN", &client.token)
				.header(
					"cookie",
					client
						.cookies
						.iter()
						.map(|(k, v)| format!("{k}={v}"))
						.collect::<Vec<_>>()
						.join("; "),
				)
				.body(Body::from(body))
				.unwrap(),
		)
		.await
		.unwrap();
	let status = response.status();
	let bytes = response.into_body().collect().await.unwrap().to_bytes();
	(status, serde_json::from_slice(&bytes).unwrap())
}
#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; owned persisted reading and resume"]
async fn owned_reading_is_kept_for_resume_without_changing_the_form() {
	let mut app = support::App::new_scoped("draft230").await;
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
	let mut client = app.admin().await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(client.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Handgeschrieben","composer":"Eigene Angabe"},"bearbeitet":["composer"],"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let (status, answer) = post(&client, id, file, 1).await;
	assert_eq!(status, 200, "{answer}");
	let (_, body) = client.get(&path).await;
	assert_eq!(body["entwurf"]["dateien"][0]["lesung"]["status"], "gelesen");
	assert_eq!(
		body["entwurf"]["dateien"][0]["lesung"]["felder"],
		answer["felder"]
	);
	assert_eq!(body["entwurf"]["lied"]["composer"], "Eigene Angabe");
	assert_eq!(body["entwurf"]["lied"]["title"], "Handgeschrieben");
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; real committed discard cancels held delivery"]
async fn discarding_the_draft_stops_its_held_model_and_cannot_resurrect_an_outcome() {
	let mut app = support::App::new_scoped("draft230").await;
	let started = Arc::new(tokio::sync::Notify::new());
	let notify = started.clone();
	let script = archive_server::ai::Script::handler(move |_| {
		notify.notify_one();
		vec![archive_server::ai::ScriptStep::Hold]
	});
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
	let (_, rx) = tokio::sync::watch::channel(archive_server::ai::Options::shipped(true));
	let ledger = archive_server::ai::Ledger::new(
		Arc::new(archive_server::ai::PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let gateway = archive_server::ai::Gateway::scripted(ledger.clone(), script);
	origin.ai = Some(archive_server::ai::Services {
		gateway: gateway.clone(),
		ledger,
	});
	app.router = archive_server::build_router(Arc::new(origin));
	let mut operator = app.admin().await;
	let reader = app.sign_in("verwaltung@liedertafel.test").await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(operator.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let task = tokio::spawn(async move { post(&reader, id, file, 1).await });
	tokio::time::timeout(std::time::Duration::from_secs(5), started.notified())
		.await
		.unwrap();
	assert_eq!(operator.request("DELETE", &path, json!({})).await.0, 204);
	let result = tokio::time::timeout(std::time::Duration::from_secs(2), task).await;
	// Never leave a failed red test's held producer running after this assertion.
	let completed = result.is_ok();
	gateway.drain().await;
	assert!(completed, "discard left the actual model call running");
	let (status, body) = result.unwrap().unwrap();
	assert_eq!(status, 404, "{body}");
	assert_eq!(operator.get(&path).await.0, 404);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; supersession, foreign/stateless isolation and concurrent first claims"]
async fn equal_first_claims_are_arbitrated_and_higher_requests_replace_only_the_owned_outcome() {
	let mut app = support::App::new_scoped("draft230").await;
	let script = archive_server::reading::scripted_scan_reader();
	lesung::configure(
		&mut app,
		script.clone(),
		archive_server::ai::Options::shipped(true),
		archive_server::reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let mut editor = app.admin().await;
	let mut foreign = app.sign_in("redaktion@liedertafel.test").await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(editor.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	assert_eq!(post(&foreign, id, file, 30).await.0, 200);
	assert_eq!(foreign.get(&path).await.0, 404);
	assert!(editor.get(&path).await.1["entwurf"]["dateien"][0]["lesung"].is_null());
	let a = app.sign_in("verwaltung@liedertafel.test").await;
	let b = app.sign_in("verwaltung@liedertafel.test").await;
	let (a, b) = tokio::join!(post(&a, id, file, 1), post(&b, id, file, 1));
	assert!(
		(a.0 == 200 && b.0 == 400) || (a.0 == 400 && b.0 == 200),
		"{a:?} {b:?}"
	);
	let before = script.requests().len();
	let old = post(&editor, id, file, 1).await;
	assert_eq!(old.0, 400);
	assert_eq!(old.1["title"], "Die Anfrage ist überholt.");
	assert_eq!(script.requests().len(), before);
	assert_eq!(post(&editor, id, file, 2).await.0, 200);
	assert_eq!(
		editor.get(&path).await.1["entwurf"]["dateien"][0]["lesung"]["anfrageId"],
		2
	);
	// An absent Datei ID is stateless even when the draft itself is owned.
	assert_eq!(post(&editor, id, Uuid::new_v4(), 1).await.0, 200);
	assert_eq!(
		editor.get(&path).await.1["entwurf"]["dateien"][0]["lesung"]["anfrageId"],
		2
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; persisted budget/input/provider/material outcomes and stale projection"]
async fn unavailable_budget_and_retryable_failures_are_kept_for_the_manual_resumed_form() {
	let mut app = support::App::new_scoped("draft230").await;
	let reading = archive_server::reading::Options {
		enabled: true,
		..Default::default()
	};
	lesung::configure(
		&mut app,
		archive_server::reading::scripted_scan_reader(),
		archive_server::ai::Options::shipped(true),
		reading.clone(),
		Default::default(),
	);
	let mut editor = app.admin().await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(editor.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	for (index, (status, expected)) in [
		(429, "nicht-verfuegbar"),
		(422, "fehlgeschlagen"),
		(502, "fehlgeschlagen"),
	]
	.into_iter()
	.enumerate()
	{
		let mut options = archive_server::ai::Options::shipped(true);
		if status == 429 {
			options.monthly_cap_eur = "0.0000001".parse().unwrap();
		}
		if status == 422 {
			options.max_input_tokens = 10;
		}
		let script = if status == 502 {
			archive_server::ai::Script::new(vec![vec![archive_server::ai::ScriptStep::Fail]])
		} else {
			archive_server::reading::scripted_scan_reader()
		};
		let origin = lesung::configure(
			&mut app,
			script.clone(),
			options,
			reading.clone(),
			Default::default(),
		);
		editor.router = app.router.clone();
		let (actual, body) = post(&editor, id, file, index as i32 + 1).await;
		assert_eq!(actual, status, "{body}");
		assert!(origin.ai.as_ref().unwrap().gateway.drain().await);
		let read = editor.get(&path).await.1;
		let outcome = &read["entwurf"]["dateien"][0]["lesung"];
		assert_eq!(outcome["status"], expected);
		assert_eq!(outcome["meldung"], body["title"]);
		assert_eq!(outcome["felder"], json!([]));
		if status != 502 {
			assert!(script.requests().is_empty());
		}
	}
	// Read-only interrupted projection after five minutes; does not rewrite state.
	sqlx::query(r#"UPDATE lied_entwurf_lesungen SET "Status"='liest',"UpdatedAt"=$3 WHERE "EntwurfId"=$1 AND "DateiId"=$2"#).bind(id).bind(file).bind(archive_server::auth::Clock::now(&*app.time)).execute(&app.pool).await.unwrap();
	assert_eq!(
		editor.get(&path).await.1["entwurf"]["dateien"][0]["lesung"]["status"],
		"liest"
	);
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(5) + chrono::Duration::seconds(1);
	assert_eq!(
		editor.get(&path).await.1["entwurf"]["dateien"][0]["lesung"]["status"],
		"fehlgeschlagen"
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; actual remove/retry notifications and stale completion arbitration"]
async fn removing_a_datei_or_a_newer_request_stops_only_that_owned_reading() {
	for newer in [false, true] {
		let mut app = support::App::new_scoped("draft230").await;
		let started = Arc::new(tokio::sync::Notify::new());
		let notify = started.clone();
		let count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
		let calls = count.clone();
		let script = archive_server::ai::Script::handler(move |request| {
			let n = calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
			if n == 0 {
				notify.notify_one();
				return vec![archive_server::ai::ScriptStep::Hold];
			}
			let transcription = request
				.options
				.instructions
				.as_deref()
				.is_some_and(|s| s.contains("Chornoten"));
			vec![archive_server::ai::ScriptStep::Update(
				archive_server::ai::Update {
					text: if transcription {
						"Neuer Titel".into()
					} else {
						json!({"felder":[{"feld":"title","wert":"Neuer Titel","sicherheit":"sicher","zitat":"Neuer Titel"}]}).to_string()
					},
					usage: archive_server::ai::Usage::reported(100, 20),
					..Default::default()
				},
			)]
		});
		let origin = lesung::configure(
			&mut app,
			script,
			archive_server::ai::Options::shipped(true),
			archive_server::reading::Options {
				enabled: true,
				..Default::default()
			},
			Default::default(),
		);
		let mut operator = app.admin().await;
		let reader = app.sign_in("verwaltung@liedertafel.test").await;
		let id = Uuid::new_v4();
		let file = Uuid::new_v4();
		let path = format!("/api/lied-entwuerfe/{id}");
		let draft = json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]});
		assert_eq!(operator.request("PUT", &path, draft).await.0, 200);
		let old = tokio::spawn(async move { post(&reader, id, file, 1).await });
		tokio::time::timeout(std::time::Duration::from_secs(5), started.notified())
			.await
			.unwrap();
		if newer {
			assert_eq!(post(&operator, id, file, 2).await.0, 200);
		} else {
			assert_eq!(
				operator
					.request(
						"PUT",
						&path,
						json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[]})
					)
					.await
					.0,
				200
			);
		}
		let (status, body) = tokio::time::timeout(std::time::Duration::from_secs(3), old)
			.await
			.unwrap()
			.unwrap();
		assert_eq!(status, 404, "{body}");
		let current = operator.get(&path).await.1;
		if newer {
			assert_eq!(current["entwurf"]["dateien"][0]["lesung"]["anfrageId"], 2);
			assert_eq!(
				current["entwurf"]["dateien"][0]["lesung"]["status"],
				"gelesen"
			);
		} else {
			assert_eq!(current["entwurf"]["dateien"], json!([]));
		}
		assert!(origin.ai.as_ref().unwrap().gateway.drain().await);
		app.close().await;
	}
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; actual three-minute persisted read limit"]
async fn persisted_reading_has_its_own_three_minute_limit_not_the_socket_lifetime() {
	let mut app = support::App::new_scoped("draft230").await;
	let started = Arc::new(tokio::sync::Notify::new());
	let notify = started.clone();
	let script = archive_server::ai::Script::handler(move |_| {
		notify.notify_one();
		vec![archive_server::ai::ScriptStep::Hold]
	});
	let origin = lesung::configure(
		&mut app,
		script,
		archive_server::ai::Options::shipped(true),
		archive_server::reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let mut operator = app.admin().await;
	let reader = app.sign_in("verwaltung@liedertafel.test").await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(operator.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let task = tokio::spawn(async move { post(&reader, id, file, 1).await });
	tokio::time::timeout(std::time::Duration::from_secs(5), started.notified())
		.await
		.unwrap();
	tokio::time::pause();
	tokio::time::advance(std::time::Duration::from_secs(181)).await;
	tokio::task::yield_now().await;
	tokio::time::resume();
	let (status, body) = tokio::time::timeout(std::time::Duration::from_secs(10), task)
		.await
		.unwrap()
		.unwrap();
	assert_eq!(status, 502, "{body}");
	assert_eq!(
		operator.get(&path).await.1["entwurf"]["dateien"][0]["lesung"]["status"],
		"fehlgeschlagen"
	);
	assert!(origin.ai.as_ref().unwrap().gateway.drain().await);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium; row-lock-controlled stale terminal UPDATE race"]
async fn blocked_old_terminal_store_rechecks_the_newer_request_after_its_row_lock_releases() {
	let mut app = support::App::new_scoped("draft230").await;
	let started = Arc::new(tokio::sync::Notify::new());
	let notify = started.clone();
	let script = archive_server::ai::Script::handler(move |_| {
		notify.notify_one();
		vec![archive_server::ai::ScriptStep::Hold]
	});
	let origin = lesung::configure(
		&mut app,
		script,
		archive_server::ai::Options::shipped(true),
		archive_server::reading::Options {
			enabled: true,
			..Default::default()
		},
		Default::default(),
	);
	let mut editor = app.admin().await;
	let reader = app.sign_in("verwaltung@liedertafel.test").await;
	let id = Uuid::new_v4();
	let file = Uuid::new_v4();
	let path = format!("/api/lied-entwuerfe/{id}");
	assert_eq!(editor.request("PUT",&path,json!({"entwurfId":id,"lied":{"title":"Entwurf"},"dateien":[{"id":file,"name":"a.pdf","art":"noten","sizeBytes":100}]})).await.0,200);
	let old = tokio::spawn(async move { post(&reader, id, file, 1).await });
	tokio::time::timeout(std::time::Duration::from_secs(5), started.notified())
		.await
		.unwrap();
	let mut winner = app.pool.begin().await.unwrap();
	sqlx::query(r#"SELECT "AnfrageId" FROM lied_entwurf_lesungen WHERE "EntwurfId"=$1 AND "DateiId"=$2 FOR UPDATE"#).bind(id).bind(file).execute(&mut *winner).await.unwrap();
	origin.shutdown.cancel();
	tokio::time::timeout(std::time::Duration::from_secs(5),async{loop {let waiting:bool=sqlx::query_scalar("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE 'UPDATE lied_entwurf_lesungen SET%')").fetch_one(&app.pool).await.unwrap();if waiting{break;}tokio::task::yield_now().await;}}).await.unwrap();
	let fields = json!([{"feld":"title","wert":"Neuer Gewinner","sicher":true}]);
	sqlx::query(r#"UPDATE lied_entwurf_lesungen SET "AnfrageId"=2,"Status"='gelesen',"Felder"=$3 WHERE "EntwurfId"=$1 AND "DateiId"=$2"#).bind(id).bind(file).bind(fields.to_string()).execute(&mut *winner).await.unwrap();
	winner.commit().await.unwrap();
	let (status, body) = old.await.unwrap();
	assert_eq!(status, 404, "{body}");
	let loaded = editor.get(&path).await.1;
	assert_eq!(loaded["entwurf"]["dateien"][0]["lesung"]["felder"], fields);
	assert_eq!(loaded["entwurf"]["dateien"][0]["lesung"]["anfrageId"], 2);
	assert!(origin.drain_readings().await);
	app.close().await;
}
