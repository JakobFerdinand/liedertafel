#![allow(clippy::unwrap_used, clippy::expect_used)] // SQL/provider assertions.
mod support;
use archive_server::{
	extraction::{Job, Options, Worker},
	queue::{Message, envelope},
	storage::{BlobRead, Storage, StorageError},
};
use chrono::Duration;
use futures::future::BoxFuture;
use serde_json::json;
use std::{
	path::PathBuf,
	sync::{
		Arc,
		atomic::{AtomicUsize, Ordering},
	},
};
use support::{App, catalogue::*};

struct Missing(AtomicUsize);
impl Storage for Missing {
	fn open_read_bounded<'a>(
		&'a self,
		_: &'a str,
		_: u64,
	) -> BoxFuture<'a, Result<Option<BlobRead>, StorageError>> {
		Box::pin(async move {
			self.0.fetch_add(1, Ordering::Relaxed);
			Ok(None)
		})
	}
	fn read_ticket<'a>(
		&'a self,
		_: &'a str,
		_: Duration,
		_: bool,
		_: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn set_tier<'a>(&'a self, _: &'a str, _: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn transient_terminal_stale_attempt_poison_and_oversize_matrix() {
	let app = App::new().await;
	let lied = lied(&app, "Worker", json!({})).await;
	let id = datei(&app, lied, false, "noten", "application/pdf").await;
	let storage = Arc::new(Missing(AtomicUsize::new(0)));
	let options = Options::default();
	let worker = Worker {
		pool: app.pool.clone(),
		storage: storage.clone(),
		options: options.clone(),
		clock: app.time.clone(),
		maintenance: Arc::new(|| false),
		reading_enabled: false,
		reading_page_limit: 20,
		reading_queue: None,
		spool_directory: PathBuf::from("/tmp/opencode"),
	};
	let mut message = Message {
		id: "test".into(),
		receipt: "receipt".into(),
		body: envelope("extraction", id),
		dequeue_count: 1,
	};
	let cancel = tokio_util::sync::CancellationToken::new();
	let result = worker
		.handle(&message, app.time.now() + Duration::minutes(1), &cancel)
		.await
		.unwrap();
	assert_eq!(
		result,
		archive_server::queue::Disposition::Abandon(Duration::seconds(30))
	);
	let job = Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!((job.status, job.attempt_count), (0, 1));
	message.dequeue_count = 5;
	assert_eq!(
		worker
			.handle(&message, app.time.now() + Duration::minutes(1), &cancel)
			.await
			.unwrap(),
		archive_server::queue::Disposition::Delete
	);
	assert_eq!(Job::load(&app.pool, id).await.unwrap().unwrap().status, 4);
	let opens = storage.0.load(Ordering::Relaxed);
	assert_eq!(
		worker
			.handle(&message, app.time.now() + Duration::minutes(1), &cancel)
			.await
			.unwrap(),
		archive_server::queue::Disposition::Delete
	);
	assert_eq!(opens, storage.0.load(Ordering::Relaxed));
	sqlx::query(r#"UPDATE extraction_jobs SET "Status"=1,"AttemptCount"=5,"LastAttemptAt"=NULL WHERE "DateiId"=$1"#).bind(id).execute(&app.pool).await.unwrap();
	worker
		.handle(&message, app.time.now() + Duration::minutes(1), &cancel)
		.await
		.unwrap();
	let job = Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!((job.status, job.attempt_count), (4, 5));
	assert_eq!(opens, storage.0.load(Ordering::Relaxed));
	message.body = "{malformed".into();
	assert_eq!(
		worker
			.handle(&message, app.time.now() + Duration::minutes(1), &cancel)
			.await
			.unwrap(),
		archive_server::queue::Disposition::Delete
	);
	message.body = envelope("extraction", id);
	sqlx::query(r#"UPDATE extraction_jobs SET "Status"=0,"AttemptCount"=0 WHERE "DateiId"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE dateien SET "SizeBytes"=67108865 WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	worker
		.handle(&message, app.time.now() + Duration::minutes(1), &cancel)
		.await
		.unwrap();
	let job = Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!((job.status, job.attempt_count), (4, 0));
	assert_eq!(opens, storage.0.load(Ordering::Relaxed));
	sqlx::query(r#"UPDATE extraction_jobs SET "Status"=1,"AttemptCount"=4,"LastAttemptAt"=NULL WHERE "DateiId"=$1"#).bind(id).execute(&app.pool).await.unwrap();
	message.dequeue_count = 1;
	worker
		.handle(&message, app.time.now() + Duration::seconds(5), &cancel)
		.await
		.unwrap();
	let job = Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!((job.status, job.attempt_count), (4, 5));
	assert_eq!(
		job.failure_reason.as_deref(),
		Some(archive_server::extraction::pdf::TOO_LARGE)
	);
	assert_eq!(opens, storage.0.load(Ordering::Relaxed));
	for (kind, content) in [("uebe", "audio/mpeg"), ("aufnahme", "application/pdf")] {
		let other = datei(&app, lied, false, kind, content).await;
		message.body = envelope("extraction", other);
		assert_eq!(
			worker
				.handle(&message, app.time.now() + Duration::seconds(5), &cancel)
				.await
				.unwrap(),
			archive_server::queue::Disposition::Delete
		);
		assert!(Job::load(&app.pool, other).await.unwrap().is_none());
	}
	app.close().await;
}
use archive_server::auth::Clock;

struct Bytes(Vec<u8>);
impl Storage for Bytes {
	fn open_read_bounded<'a>(
		&'a self,
		_: &'a str,
		_: u64,
	) -> BoxFuture<'a, Result<Option<BlobRead>, StorageError>> {
		let chunks = self
			.0
			.chunks(4)
			.map(|b| Ok(azure_core::Bytes::copy_from_slice(b)))
			.collect::<Vec<_>>();
		Box::pin(async move { Ok(Some(Box::pin(futures::stream::iter(chunks)) as BlobRead)) })
	}
	fn read_ticket<'a>(
		&'a self,
		_: &'a str,
		_: Duration,
		_: bool,
		_: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn set_tier<'a>(&'a self, _: &'a str, _: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
}
#[tokio::test]
#[ignore = "explicit PostgreSQL + generated native PDFium byte-spool gate"]
async fn actual_stream_length_bounds_private_spool_cleanup_and_encrypted_failure() {
	use base64::Engine;
	let app = App::new().await;
	let owner = lied(&app, "Spool", json!({})).await;
	let cancel = tokio_util::sync::CancellationToken::new();
	let dir = PathBuf::from(format!("/tmp/opencode/226-spool-{}", uuid::Uuid::new_v4()));
	std::fs::create_dir(&dir).unwrap();
	for (bytes, cap, expected) in [
		(
			vec![b'x'; 17],
			16,
			archive_server::extraction::pdf::TOO_LARGE,
		),
		(vec![b'x'; 16], 16, archive_server::extraction::pdf::CORRUPT),
		(
			base64::engine::general_purpose::STANDARD
				.decode(include_str!("fixtures/226-encrypted-pdf.base64").trim())
				.unwrap(),
			67_108_864,
			archive_server::extraction::pdf::ENCRYPTED,
		),
	] {
		let id = datei(&app, owner, false, "noten", "application/pdf").await;
		sqlx::query(r#"UPDATE dateien SET "SizeBytes"=0 WHERE "Id"=$1"#)
			.bind(id)
			.execute(&app.pool)
			.await
			.unwrap();
		let mut w = worker(&app, Arc::new(Bytes(bytes)));
		w.options.max_pdf_bytes = cap;
		w.spool_directory = dir.clone();
		assert_eq!(
			w.handle(&message(id), app.time.now() + Duration::seconds(5), &cancel)
				.await
				.unwrap(),
			archive_server::queue::Disposition::Delete
		);
		let job = Job::load(&app.pool, id).await.unwrap().unwrap();
		assert_eq!(job.status, 4);
		assert_eq!(job.failure_reason.as_deref(), Some(expected));
		assert_eq!(std::fs::read_dir(&dir).unwrap().count(), 0);
	}
	std::fs::remove_dir(dir).unwrap();
	app.close().await;
}

struct Delayed {
	pool: sqlx::PgPool,
	id: uuid::Uuid,
	winner: Option<(i32, i64)>,
	delay: std::time::Duration,
}
impl Storage for Delayed {
	fn open_read_bounded<'a>(
		&'a self,
		_: &'a str,
		_: u64,
	) -> BoxFuture<'a, Result<Option<BlobRead>, StorageError>> {
		Box::pin(async move {
			if let Some((status, bump)) = self.winner {
				sqlx::query(r#"UPDATE extraction_jobs SET "Status"=$2,"RowVersion"="RowVersion"+$3,"Text"='Concurrent winner' WHERE "DateiId"=$1"#).bind(self.id).bind(status).bind(bump).execute(&self.pool).await.unwrap();
			}
			tokio::time::sleep(self.delay).await;
			Ok(None)
		})
	}
	fn read_ticket<'a>(
		&'a self,
		_: &'a str,
		_: Duration,
		_: bool,
		_: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn set_tier<'a>(&'a self, _: &'a str, _: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
}
fn worker(app: &App, storage: Arc<dyn Storage>) -> Worker {
	Worker {
		pool: app.pool.clone(),
		storage,
		options: Options::default(),
		clock: app.time.clone(),
		maintenance: Arc::new(|| false),
		reading_enabled: false,
		reading_page_limit: 20,
		reading_queue: None,
		spool_directory: PathBuf::from("/tmp/opencode"),
	}
}
fn message(id: uuid::Uuid) -> Message {
	Message {
		id: "test226".into(),
		receipt: "receipt".into(),
		body: envelope("extraction", id),
		dequeue_count: 1,
	}
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn deadline_recovery_preserves_terminal_newer_and_released_lease_winners() {
	let app = App::new().await;
	let lied = lied(&app, "Deadline", json!({})).await;
	let cancel = tokio_util::sync::CancellationToken::new();
	for (status, bump, expected_delete) in [
		(2, 1, true),
		(3, 1, true),
		(4, 1, true),
		(1, 1, false),
		(0, 1, false),
	] {
		let id = datei(&app, lied, false, "noten", "application/pdf").await;
		let w = worker(
			&app,
			Arc::new(Delayed {
				pool: app.pool.clone(),
				id,
				winner: Some((status, bump)),
				delay: std::time::Duration::from_secs(2),
			}),
		);
		let disposition = w
			.handle(&message(id), app.time.now() + Duration::seconds(1), &cancel)
			.await
			.unwrap();
		assert_eq!(
			disposition == archive_server::queue::Disposition::Delete,
			expected_delete
		);
		let job = Job::load(&app.pool, id).await.unwrap().unwrap();
		assert_eq!(job.status, status);
		assert_eq!(job.text.as_deref(), Some("Concurrent winner"));
		assert_eq!(job.row_version, 2);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn own_deadline_releases_counted_lease_and_exhausts_only_spent_attempts() {
	let app = App::new().await;
	let lied = lied(&app, "Deadline own", json!({})).await;
	let cancel = tokio_util::sync::CancellationToken::new();
	for (attempts, dequeues, status, count) in [(0, 1, 0, 1), (4, 1, 4, 5), (0, 5, 4, 1)] {
		let id = datei(&app, lied, false, "noten", "application/pdf").await;
		text(&app, id, "", 0, 0).await;
		sqlx::query(r#"UPDATE extraction_jobs SET "AttemptCount"=$2 WHERE "DateiId"=$1"#)
			.bind(id)
			.bind(attempts)
			.execute(&app.pool)
			.await
			.unwrap();
		let w = worker(
			&app,
			Arc::new(Delayed {
				pool: app.pool.clone(),
				id,
				winner: None,
				delay: std::time::Duration::from_secs(2),
			}),
		);
		let mut m = message(id);
		m.dequeue_count = dequeues;
		w.handle(&m, app.time.now() + Duration::seconds(1), &cancel)
			.await
			.unwrap();
		let job = Job::load(&app.pool, id).await.unwrap().unwrap();
		assert_eq!((job.status, job.attempt_count), (status, count));
	}
	let id = datei(&app, lied, false, "noten", "application/pdf").await;
	let missing = Arc::new(Missing(AtomicUsize::new(0)));
	let w = worker(&app, missing.clone());
	w.handle(&message(id), app.time.now(), &cancel)
		.await
		.unwrap();
	assert!(Job::load(&app.pool, id).await.unwrap().is_none());
	assert_eq!(missing.0.load(Ordering::Relaxed), 0);
	cancel.cancel();
	assert!(
		w.handle(&message(id), app.time.now() + Duration::seconds(5), &cancel)
			.await
			.is_err()
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL gate"]
async fn maintenance_visibility_parent_trash_foreign_and_stale_boundary() {
	let app = App::new().await;
	let lied = lied(&app, "Visibility", json!({})).await;
	let cancel = tokio_util::sync::CancellationToken::new();
	let missing = Arc::new(Missing(AtomicUsize::new(0)));
	let mut w = worker(&app, missing.clone());
	// PostgreSQL stores microseconds; compare equality in that common domain.
	let now = chrono::DateTime::from_timestamp_micros(app.time.now().timestamp_micros()).unwrap();
	*app.time.0.lock().unwrap() = now;
	let id = datei(&app, lied, false, "noten", "application/pdf").await;
	w.maintenance = Arc::new(|| true);
	w.handle(&message(id), app.time.now() + Duration::seconds(5), &cancel)
		.await
		.unwrap();
	assert!(Job::load(&app.pool, id).await.unwrap().is_none());
	w.maintenance = Arc::new(|| false);
	text(&app, id, "", 1, 0).await;
	sqlx::query(r#"UPDATE extraction_jobs SET "LastAttemptAt"=$2 WHERE "DateiId"=$1"#)
		.bind(id)
		.bind(app.time.now() - w.options.stale_running_after)
		.execute(&app.pool)
		.await
		.unwrap();
	w.handle(&message(id), app.time.now() + Duration::seconds(5), &cancel)
		.await
		.unwrap();
	assert_eq!(missing.0.load(Ordering::Relaxed), 0);
	sqlx::query(r#"UPDATE extraction_jobs SET "LastAttemptAt"="LastAttemptAt"-interval '1 second' WHERE "DateiId"=$1"#).bind(id).execute(&app.pool).await.unwrap();
	w.handle(&message(id), app.time.now() + Duration::seconds(5), &cancel)
		.await
		.unwrap();
	assert_eq!(missing.0.load(Ordering::Relaxed), 1);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(lied)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		w.handle(&message(id), app.time.now() + Duration::seconds(5), &cancel)
			.await
			.unwrap(),
		archive_server::queue::Disposition::Delete
	);
	assert_eq!(missing.0.load(Ordering::Relaxed), 1);
	app.close().await;
}
