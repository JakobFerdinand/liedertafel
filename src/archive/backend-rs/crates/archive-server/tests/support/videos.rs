//! Actual product CLI + current PG/private Queue/Blob fixtures, no worker fake.
use super::{App, catalogue::*};
use archive_server::{
	auth::SystemClock,
	queue::{AzureQueue, Queue},
	storage::{BlobStorage, StorageConfig},
};
use serde_json::Value;
use std::{path::PathBuf, sync::Arc};
use uuid::Uuid;
pub const CLIP: &[u8] =
	include_bytes!("../../../../../../../tests/archive/apphost/TestAssets/kurzfilm.mp4");
pub struct Video {
	pub app: App,
	pub id: Uuid,
	pub owner: Uuid,
	pub container: String,
	pub queue_name: String,
	pub queue: AzureQueue,
	pub storage: BlobStorage,
	pub blob: String,
	pub url: String,
	pub directory: PathBuf,
}
impl Video {
	pub async fn new(bytes: &[u8], kind: &str, content: &str, event_owner: bool) -> Self {
		let app = App::new_scoped("video233").await;
		let owner = if event_owner {
			event(&app).await
		} else {
			lied(&app, "Video", serde_json::json!({})).await
		};
		let id = datei(&app, owner, event_owner, kind, content).await;
		let connection = std::env::var("ARCHIVE_TEST_AZURITE").unwrap();
		let queues = std::env::var("ARCHIVE_TEST_QUEUE").unwrap();
		assert!(connection.contains("127.0.0.1:"));
		assert!(queues.contains("127.0.0.1:"));
		let container = format!("video233-{}", Uuid::new_v4().simple());
		let queue_name = format!("video233-{}", Uuid::new_v4().simple());
		let storage = BlobStorage::new(
			StorageConfig::connection_string(&connection, &container).unwrap(),
			Arc::new(SystemClock),
		)
		.unwrap();
		let blobs = storage
			.service_client()
			.unwrap()
			.blob_container_client(&container);
		blobs.create(None).await.unwrap();
		let blob = format!("dateien/{id}/original");
		blobs
			.blob_client(&blob)
			.upload(azure_core::http::RequestContent::from(bytes.to_vec()), None)
			.await
			.unwrap();
		sqlx::query(r#"UPDATE dateien SET "BlobName"=$2,"SizeBytes"=$3,"OriginalFileName"='Konzert.mov' WHERE "Id"=$1"#).bind(id).bind(&blob).bind(bytes.len() as i64).execute(&app.pool).await.unwrap();
		let queue = AzureQueue::select(
			Some(&queues),
			None,
			&queue_name,
			true,
			Arc::new(SystemClock),
		)
		.unwrap();
		queue.ensure().await.unwrap();
		let mut url = url::Url::parse(&std::env::var("ARCHIVE_TEST_POSTGRES").unwrap()).unwrap();
		let db: String = sqlx::query_scalar("SELECT current_database()")
			.fetch_one(&app.pool)
			.await
			.unwrap();
		url.set_path(&db);
		let directory = std::env::temp_dir().join(format!("video233-{}", Uuid::new_v4()));
		std::fs::create_dir(&directory).unwrap();
		Self {
			app,
			id,
			owner,
			container,
			queue_name,
			queue,
			storage,
			blob,
			url: url.to_string(),
			directory,
		}
	}
	pub async fn send(&self) {
		self.queue
			.send(&archive_server::queue::envelope("transcode", self.id))
			.await
			.unwrap();
	}
	pub fn command(&self, command: &str, extra: &[(&str, String)]) -> tokio::process::Command {
		let mut c = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"));
		c.arg(command)
			.env("ARCHIVE_ENVIRONMENT", "Production")
			.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
			.env("ConnectionStrings__archive-db", &self.url)
			.env(
				"ConnectionStrings__archive-blobs",
				std::env::var("ARCHIVE_TEST_AZURITE").unwrap(),
			)
			.env(
				"ConnectionStrings__archive-queues",
				std::env::var("ARCHIVE_TEST_QUEUE").unwrap(),
			)
			.env("Archive__Assets__ContainerName", &self.container)
			.env("Archive__Video__QueueName", &self.queue_name)
			.env("Archive__Video__TimeBudget", "00:00:10")
			.env("TMPDIR", &self.directory)
			.envs(extra.iter().cloned())
			.kill_on_drop(true);
		c
	}
	pub async fn run(&self, extra: &[(&str, String)]) -> std::process::Output {
		let output = tokio::time::timeout(
			std::time::Duration::from_secs(30),
			self.command("--transcode-queue", extra).output(),
		)
		.await
		.unwrap()
		.unwrap();
		assert!(output.status.success(), "CLI exit {:?}", output.status);
		output
	}
	pub async fn status(&self) -> Value {
		let mut editor = self.app.admin().await;
		let (s, b) = editor
			.get(&format!("/api/dateien/videos?ids={}", self.id))
			.await;
		assert_eq!(s, 200, "{b}");
		b["results"][0].clone()
	}
	pub async fn seed_job(&self, status: i32, attempts: i32) {
		// Worker-only cases already have an accepted notification; otherwise
		// the product CLI's legitimate pre-drain sweeper adds unrelated work.
		sqlx::query(r#"INSERT INTO video_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","LastEnqueuedAt","RowVersion") VALUES ($1,$2,$3,$4,now(),now(),now(),0)"#).bind(self.id).bind(status).bind(attempts).bind(Uuid::nil()).execute(&self.app.pool).await.unwrap();
	}
	pub async fn done(self) {
		assert!(
			std::fs::read_dir(&self.directory).unwrap().next().is_none(),
			"worker temp files must be gone"
		);
		self.storage
			.service_client()
			.unwrap()
			.blob_container_client(&self.container)
			.delete(None)
			.await
			.unwrap();
		self.app.close().await;
	}
}
