// Test-only hooks assert commit ordering and deliberately fail loudly on bad fixtures.
#![allow(clippy::unwrap_used)]
mod support;
use archive_server::{
	auth::AuthError,
	catalogue::{
		queries::Datei,
		uploads::{FinalizeContext, FinalizedHook},
	},
};
use futures::future::BoxFuture;
use serde_json::json;
use std::sync::{
	Arc, Mutex,
	atomic::{AtomicBool, Ordering},
};
use support::{App, storage::FakeStorage, upload::*};
#[derive(Default)]
struct Hook {
	events: Mutex<Vec<String>>,
	fail: AtomicBool,
}
impl FinalizedHook for Hook {
	fn before_save<'a>(
		&'a self,
		d: &'a Datei,
		_c: &'a FinalizeContext,
		conn: &'a mut sqlx::PgConnection,
	) -> BoxFuture<'a, Result<(), AuthError>> {
		Box::pin(async move {
			self.events.lock().unwrap().push("before".into());
			if self.fail.swap(false, Ordering::SeqCst) {
				return Err(AuthError::Problem(
					axum::http::StatusCode::CONFLICT,
					"Der Eintrag wurde zwischenzeitlich geändert.",
				));
			}
			sqlx::query(r#"INSERT INTO extraction_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ($1,0,0,$2,now(),now(),1)"#).bind(d.id).bind(uuid::Uuid::nil()).execute(conn).await?;
			Ok(())
		})
	}
	fn after_save<'a>(
		&'a self,
		d: &'a Datei,
		pool: &'a sqlx::PgPool,
	) -> BoxFuture<'a, Result<(), AuthError>> {
		Box::pin(async move {
			let exists: bool = sqlx::query_scalar(
				r#"SELECT EXISTS(SELECT 1 FROM extraction_jobs WHERE "DateiId"=$1)"#,
			)
			.bind(d.id)
			.fetch_one(pool)
			.await?;
			assert!(exists);
			self.events.lock().unwrap().push("after".into());
			Err(AuthError::Database)
		})
	}
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL transactional hooks and replay"]
async fn before_hook_failure_cleans_only_attempt_copy_and_after_failure_cannot_uncommit() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	let hook = Arc::new(Hook::default());
	hook.fail.store(true, Ordering::SeqCst);
	app.configure_uploads(
		storage.clone(),
		Default::default(),
		vec![hook.clone()],
		vec![],
	);
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Hooks").await;
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 409, "{b}");
	assert_eq!(storage.objects.lock().unwrap().len(), 1);
	assert_eq!(
		editor.get(&format!("/api/lieder/{lied}")).await.1["lied"]["dateien"],
		json!([])
	);
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(
		hook.events.lock().unwrap().as_slice(),
		["before", "before", "after"]
	);
	assert_eq!(storage.objects.lock().unwrap().len(), 1);
	assert_eq!(finish(&mut editor, &session).await.1, b);
	assert_eq!(hook.events.lock().unwrap().len(), 3);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL two-origin finalize CAS race"]
async fn two_origins_finalizing_one_session_commit_one_datei_loser_removes_only_its_copy() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut first = app.admin().await;
	let lied = create(&mut first, "Finalize Rennen").await;
	let session = stage(
		&mut first,
		lied,
		false,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	app.with_storage(storage.clone());
	let mut second = app.sign_in("verwaltung@liedertafel.test").await;
	*storage.promote_barrier.lock().unwrap() = Some(Arc::new(tokio::sync::Barrier::new(2)));
	let (a, b) = tokio::join!(finish(&mut first, &session), finish(&mut second, &session));
	let mut statuses = [a.0.as_u16(), b.0.as_u16()];
	statuses.sort();
	assert_eq!(statuses, [200, 409], "{a:?} {b:?}");
	*storage.promote_barrier.lock().unwrap() = None;
	let winner = if a.0 == 200 { a.1 } else { b.1 };
	assert_eq!(finish(&mut first, &session).await.1, winner);
	assert_eq!(storage.objects.lock().unwrap().len(), 1);
	assert_eq!(
		first.get(&format!("/api/lieder/{lied}")).await.1["lied"]["dateien"],
		json!([winner["datei"]])
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL two-origin immutable replacement race"]
async fn competing_replacements_share_no_copy_and_losing_session_remains_replayable_until_target_is_gone()
 {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut first = app.admin().await;
	let lied = create(&mut first, "Ersatz Rennen").await;
	let old = upload(
		&mut first,
		lied,
		json!({"stimme":"Tenor 1"}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let request = json!({"replacesDateiId":old["id"]});
	let a = stage(
		&mut first,
		lied,
		false,
		request.clone(),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	let b = stage(
		&mut first,
		lied,
		false,
		request,
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	app.with_storage(storage.clone());
	let mut second = app.sign_in("verwaltung@liedertafel.test").await;
	*storage.promote_barrier.lock().unwrap() = Some(Arc::new(tokio::sync::Barrier::new(2)));
	let (ar, br) = tokio::join!(finish(&mut first, &a), finish(&mut second, &b));
	let mut statuses = [ar.0.as_u16(), br.0.as_u16()];
	statuses.sort();
	assert_eq!(statuses, [200, 409], "{ar:?} {br:?}");
	*storage.promote_barrier.lock().unwrap() = None;
	assert_eq!(
		storage.objects.lock().unwrap().len(),
		3,
		"old + winner + losing staged bytes"
	);
	let losing = if ar.0 == 409 { &a } else { &b };
	let (s, body) = finish(&mut first, losing).await;
	assert_eq!(s, 409);
	assert_eq!(
		body["title"],
		"Die Datei wurde inzwischen ersetzt oder gelöscht."
	);
	assert_eq!(storage.objects.lock().unwrap().len(), 2);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL real save failure after promotion"]
async fn real_sql_unique_failure_rolls_back_session_and_file_preserves_stage_and_retry_heals() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let lied = create(&mut editor, "Save Rennen").await;
	let session = stage(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true}),
		&pdf(64),
		"application/pdf",
		&storage,
	)
	.await;
	sqlx::raw_sql("CREATE FUNCTION refuse_datei() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic conflict' USING ERRCODE='23505'; END $$; CREATE TRIGGER refuse_datei BEFORE INSERT ON dateien FOR EACH ROW EXECUTE FUNCTION refuse_datei()").execute(&app.pool).await.unwrap();
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 409, "{b}");
	assert_eq!(storage.objects.lock().unwrap().len(), 1);
	assert_eq!(
		editor.get(&format!("/api/lieder/{lied}")).await.1["lied"]["dateien"],
		json!([])
	);
	sqlx::raw_sql("DROP TRIGGER refuse_datei ON dateien; DROP FUNCTION refuse_datei()")
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(finish(&mut editor, &session).await.0, 200);
	assert_eq!(storage.objects.lock().unwrap().len(), 1);
	app.close().await;
}
