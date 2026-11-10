//! Real canonical SQL, delivery-only Queue barrier. No transaction spans send.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Boundary assertions/fixtures.
mod support;
use archive_server::{
	auth::{Auth, AuthConfig, Clock},
	extraction::{Hook, Job, Options, Worker, dispatch},
	queue::{Message, Queue, QueueError},
	storage::{BlobRead, Storage, StorageError},
	{Origin, Settings, build_router},
};
use chrono::Duration;
use futures::future::BoxFuture;
use serde_json::{Value, json};
use std::{
	path::PathBuf,
	sync::{
		Arc, Mutex,
		atomic::{AtomicUsize, Ordering},
	},
};
use support::{App, catalogue::*, storage::FakeStorage, upload::*};
use tokio::sync::Notify;
use uuid::Uuid;

#[derive(Default)]
struct SendBarrier {
	entered: Notify,
	resume: Notify,
	id: Mutex<Option<Uuid>>,
	sends: AtomicUsize,
}
impl Queue for SendBarrier {
	fn send<'a>(&'a self, body: &'a str) -> BoxFuture<'a, Result<bool, QueueError>> {
		Box::pin(async move {
			let body: Value = serde_json::from_str(body).unwrap();
			*self.id.lock().unwrap() =
				Some(Uuid::parse_str(body["dateiId"].as_str().unwrap()).unwrap());
			if self.sends.fetch_add(1, Ordering::SeqCst) == 0 {
				// Accepted by provider, but its caller cannot stamp until released.
				self.entered.notify_one();
				self.resume.notified().await;
			}
			Ok(true)
		})
	}
	fn receive(&self, _: usize, _: i32) -> BoxFuture<'_, Result<Vec<Message>, QueueError>> {
		Box::pin(async { Ok(vec![]) })
	}
	fn delete<'a>(&'a self, _: &'a Message) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async { Ok(()) })
	}
	fn abandon<'a>(&'a self, _: &'a Message, _: i32) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async { Ok(()) })
	}
}
async fn snapshot(pool: &sqlx::PgPool, id: Uuid, reading: bool) -> Value {
	sqlx::query_scalar(if reading {
		r#"SELECT to_jsonb(j) FROM reading_jobs j WHERE "DateiId"=$1"#
	} else {
		r#"SELECT to_jsonb(j) FROM extraction_jobs j WHERE "DateiId"=$1"#
	})
	.bind(id)
	.fetch_one(pool)
	.await
	.unwrap()
}
async fn release(pool: &sqlx::PgPool, queue: &SendBarrier, race: bool, reading: bool) -> Value {
	queue.entered.notified().await;
	let id = queue.id.lock().unwrap().unwrap();
	if race {
		// Commit independent worker/editor reset while the provider is blocked.
		// Would deadlock if the production path held a transaction/row lock over I/O.
		assert_eq!(sqlx::query(if reading {
			r#"UPDATE reading_jobs SET "RowVersion"="RowVersion"+7,"Status"=0,"AttemptCount"=2,"LastEnqueuedAt"=NULL,"UpdatedAt"="UpdatedAt"+interval '1 hour' WHERE "DateiId"=$1"#
		} else {
			r#"UPDATE extraction_jobs SET "RowVersion"="RowVersion"+7,"Status"=0,"AttemptCount"=2,"LastEnqueuedAt"=NULL,"UpdatedAt"="UpdatedAt"+interval '1 hour' WHERE "DateiId"=$1"#
		}).bind(id).execute(pool).await.unwrap().rows_affected(), 1);
	}
	let expected = snapshot(pool, id, reading).await;
	queue.resume.notify_one();
	expected
}
async fn assert_stamp(app: &App, queue: &SendBarrier, expected: Value, race: bool, reading: bool) {
	assert_eq!(
		queue.sends.load(Ordering::SeqCst),
		1,
		"accepted send must not repeat after lost stamp"
	);
	let id = queue.id.lock().unwrap().unwrap();
	let actual = snapshot(&app.pool, id, reading).await;
	if race {
		assert_eq!(
			actual, expected,
			"late accepted-send stamp overwrote newer canonical state"
		);
	} else {
		let mut unstamped = actual;
		assert!(!unstamped["LastEnqueuedAt"].is_null());
		unstamped["LastEnqueuedAt"] = Value::Null;
		assert_eq!(
			unstamped, expected,
			"stamp must not bump RowVersion/UpdatedAt or other source state"
		);
	}
}
#[tokio::test]
#[ignore = "explicit real PostgreSQL provider-send CAS gate"]
async fn dispatcher_selected_token_lost_stamp_count_and_later_recovery() {
	let app = App::new().await;
	let owner = lied(&app, "Dispatch barrier", json!({})).await;
	for race in [true, false] {
		let id = datei(&app, owner, false, "noten", "application/pdf").await;
		text(&app, id, "", 0, 0).await;
		let queue = SendBarrier::default();
		let options = Options::default();
		let (count, expected) = tokio::time::timeout(std::time::Duration::from_secs(10), async {
			tokio::join!(
				dispatch(&app.pool, &queue, &options, app.time.as_ref()),
				release(&app.pool, &queue, race, false)
			)
		})
		.await
		.unwrap();
		assert_stamp(&app, &queue, expected, race, false).await;
		assert_eq!(
			count.unwrap(),
			usize::from(!race),
			"only successfully stamped rows count"
		);
		assert_eq!(
			dispatch(&app.pool, &queue, &options, app.time.as_ref())
				.await
				.unwrap(),
			usize::from(race)
		);
		assert_eq!(queue.sends.load(Ordering::SeqCst), if race { 2 } else { 1 });
		assert!(
			Job::load(&app.pool, id)
				.await
				.unwrap()
				.unwrap()
				.last_enqueued_at
				.is_some()
		);
	}
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit real PostgreSQL authenticated finalize CAS gate"]
async fn finalize_caller_token_lost_stamp_no_resend_and_idempotent_reentry() {
	for race in [true, false] {
		let mut app = App::new().await;
		let storage = Arc::new(FakeStorage::default());
		let queue = Arc::new(SendBarrier::default());
		app.configure_uploads(
			storage.clone(),
			Default::default(),
			vec![Arc::new(Hook {
				queue: queue.clone(),
				clock: app.time.clone(),
			})],
			vec![],
		);
		let mut editor = app.admin().await;
		let owner = create(&mut editor, "Finalize barrier").await;
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
		let (result, expected) = tokio::time::timeout(std::time::Duration::from_secs(10), async {
			tokio::join!(
				finish(&mut editor, &session),
				release(&app.pool, &queue, race, false)
			)
		})
		.await
		.unwrap();
		assert_eq!(result.0, 200, "{}", result.1);
		assert_stamp(&app, &queue, expected, race, false).await;
		assert_eq!(finish(&mut editor, &session).await.0, 200);
		assert_eq!(queue.sends.load(Ordering::SeqCst), 1);
		app.close().await;
	}
}
#[tokio::test]
#[ignore = "explicit real PostgreSQL authenticated retry CAS gate"]
async fn retry_caller_token_after_reset_and_queued_and_insert() {
	for initial in [Some(4), Some(0), None] {
		for race in [true, false] {
			let mut app = App::new().await;
			let queue = Arc::new(SendBarrier::default());
			let auth = Auth::new(
				app.pool.clone(),
				AuthConfig::development(),
				app.mail.clone(),
				app.time.clone(),
			)
			.unwrap();
			let mut origin = Origin::new(Settings::new(true, PathBuf::new(), 0)).with_auth(auth);
			origin.extraction_queue = queue.clone();
			app.router = build_router(Arc::new(origin));
			let mut editor = app.admin().await;
			let owner = lied(&app, "Retry barrier", json!({})).await;
			let id = datei(&app, owner, false, "noten", "application/pdf").await;
			if let Some(status) = initial {
				text(&app, id, "", status, 0).await;
			}
			let path = format!("/api/dateien/{id}/extraction/retry");
			let (result, expected) =
				tokio::time::timeout(std::time::Duration::from_secs(10), async {
					tokio::join!(
						editor.post(&path, json!({})),
						release(&app.pool, &queue, race, false)
					)
				})
				.await
				.unwrap();
			assert_eq!(result.0, 200, "{}", result.1);
			assert_stamp(&app, &queue, expected, race, false).await;
			app.close().await;
		}
	}
}

struct BlankPdf;
impl Storage for BlankPdf {
	fn open_read_bounded<'a>(
		&'a self,
		_: &'a str,
		_: u64,
	) -> BoxFuture<'a, Result<Option<BlobRead>, StorageError>> {
		Box::pin(async {
			let mut bytes = b"%PDF-1.4\n".to_vec();
			let mut offsets = vec![0];
			for (i, object) in [
				"<< /Type /Catalog /Pages 2 0 R >>",
				"<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
				"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] >>",
			]
			.iter()
			.enumerate()
			{
				offsets.push(bytes.len());
				bytes.extend_from_slice(format!("{} 0 obj\n{object}\nendobj\n", i + 1).as_bytes());
			}
			let xref = bytes.len();
			bytes.extend_from_slice(b"xref\n0 4\n0000000000 65535 f \n");
			for offset in offsets.iter().skip(1) {
				bytes.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
			}
			bytes.extend_from_slice(
				format!("trailer << /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF").as_bytes(),
			);
			Ok(Some(
				Box::pin(futures::stream::iter([Ok(azure_core::Bytes::from(bytes))])) as BlobRead,
			))
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
#[ignore = "explicit real PostgreSQL + generated native PDF reading-handoff CAS gate"]
async fn reading_inserted_token_lost_stamp_and_terminal_duplicate() {
	for race in [true, false] {
		let app = App::new().await;
		let owner = lied(&app, "Reading barrier", json!({})).await;
		let id = datei(&app, owner, false, "noten", "application/pdf").await;
		let queue = Arc::new(SendBarrier::default());
		let worker = Worker {
			pool: app.pool.clone(),
			storage: Arc::new(BlankPdf),
			options: Options::default(),
			clock: app.time.clone(),
			maintenance: Arc::new(|| false),
			reading_enabled: true,
			reading_page_limit: 20,
			reading_queue: Some(queue.clone()),
			spool_directory: PathBuf::from("/tmp/opencode"),
		};
		let message = Message {
			id: "handoff".into(),
			receipt: "receipt".into(),
			body: archive_server::queue::envelope("extraction", id),
			dequeue_count: 1,
		};
		let cancel = tokio_util::sync::CancellationToken::new();
		let (result, expected) = tokio::time::timeout(std::time::Duration::from_secs(10), async {
			tokio::join!(
				worker.handle(&message, app.time.now() + Duration::seconds(60), &cancel),
				release(&app.pool, &queue, race, true)
			)
		})
		.await
		.unwrap();
		assert_eq!(result.unwrap(), archive_server::queue::Disposition::Delete);
		assert_eq!(Job::load(&app.pool, id).await.unwrap().unwrap().status, 3);
		assert_stamp(&app, &queue, expected, race, true).await;
		worker
			.handle(&message, app.time.now() + Duration::seconds(60), &cancel)
			.await
			.unwrap();
		assert_eq!(queue.sends.load(Ordering::SeqCst), 1);
		app.close().await;
	}
}
