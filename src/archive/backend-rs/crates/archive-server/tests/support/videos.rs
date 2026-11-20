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

/// Observe the real CLI's Queue HTTP requests while forwarding every request
/// unchanged to private Azurite. No fake receive/disposition/provider response.
pub struct QueueObserver {
	child: tokio::process::Child,
	port_file: PathBuf,
	requests: PathBuf,
	pub connection: String,
}
impl QueueObserver {
	pub async fn new(directory: &std::path::Path) -> Self {
		let connection = std::env::var("ARCHIVE_TEST_QUEUE").unwrap();
		let endpoint = connection
			.split(';')
			.find_map(|field| field.strip_prefix("QueueEndpoint="))
			.unwrap();
		let url = url::Url::parse(endpoint).unwrap();
		assert_eq!(url.host_str(), Some("127.0.0.1"));
		let port_file = directory.join(format!("queue-observer-{}.port", Uuid::new_v4()));
		let requests = port_file.with_extension("jsonl");
		let script = r#"import http.server,http.client,json,os,sys,urllib.parse
target=urllib.parse.urlparse(sys.argv[1])
fd=os.open(sys.argv[3],os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def forward(self):
  query=urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
  safe={k:v for k,v in query.items() if k in ['numofmessages','visibilitytimeout','comp','restype']}
  os.write(fd,(json.dumps({'method':self.command,'path':urllib.parse.urlparse(self.path).path,'query':safe})+'\n').encode())
  body=self.rfile.read(int(self.headers.get('Content-Length','0')))
  upstream=http.client.HTTPConnection(target.hostname,target.port,timeout=15)
  upstream.request(self.command,self.path,body,dict(self.headers))
  response=upstream.getresponse();data=response.read()
  self.send_response(response.status)
  for name,value in response.getheaders():
   if name.lower() not in ['transfer-encoding','content-length','connection']: self.send_header(name,value)
  self.send_header('Content-Length',str(len(data)));self.send_header('Connection','close');self.end_headers()
  self.wfile.write(data);upstream.close()
 do_GET=do_POST=do_PUT=do_DELETE=forward
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
with open(os.open(sys.argv[2],os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600),'w') as f: f.write(str(server.server_port))
server.serve_forever()
"#;
		let child = tokio::process::Command::new("/usr/bin/python3")
			.args([
				"-c",
				script,
				endpoint,
				port_file.to_str().unwrap(),
				requests.to_str().unwrap(),
			])
			.kill_on_drop(true)
			.stdout(std::process::Stdio::null())
			.stderr(std::process::Stdio::null())
			.spawn()
			.unwrap();
		tokio::time::timeout(std::time::Duration::from_secs(3), async {
			while !port_file.exists() {
				tokio::time::sleep(std::time::Duration::from_millis(10)).await;
			}
		})
		.await
		.unwrap();
		let port: u16 = std::fs::read_to_string(&port_file)
			.unwrap()
			.parse()
			.unwrap();
		let mut observed = url.clone();
		observed.set_port(Some(port)).unwrap();
		Self {
			child,
			port_file,
			requests,
			connection: connection.replace(endpoint, observed.as_str()),
		}
	}
	pub async fn finish(mut self) -> Vec<Value> {
		self.child.kill().await.unwrap(); // waits/reaps this observer only
		let requests = std::fs::read_to_string(&self.requests)
			.unwrap()
			.lines()
			.map(|line| serde_json::from_str(line).unwrap())
			.collect();
		std::fs::remove_file(self.port_file).unwrap();
		std::fs::remove_file(self.requests).unwrap();
		requests
	}
}
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
