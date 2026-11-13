#![allow(dead_code)] // Shared scan/provider fixtures, independently compiled suites.
use archive_server::{
	ai::*,
	auth::Clock,
	reading::{self, ScanReadingModel, Worker},
	storage::{BlobRead, Storage, StorageError},
};
use futures::{StreamExt, future::BoxFuture};
use std::sync::Arc;
pub struct Bytes(pub Vec<u8>);
impl Storage for Bytes {
	fn open_read_bounded<'a>(
		&'a self,
		_: &'a str,
		_: u64,
	) -> BoxFuture<'a, Result<Option<BlobRead>, StorageError>> {
		Box::pin(async move {
			Ok(Some(
				futures::stream::iter(vec![Ok(azure_core::Bytes::copy_from_slice(&self.0))])
					.boxed(),
			))
		})
	}
	fn read_ticket<'a>(
		&'a self,
		_: &'a str,
		_: chrono::Duration,
		_: bool,
		_: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn set_tier<'a>(&'a self, _: &'a str, _: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
}
/// Public generated raster-only PDF: no text operators, no external/private input.
pub fn scan(pages: i32) -> Vec<u8> {
	let mut objects = vec![
		b"<< /Type /Catalog /Pages 2 0 R >>".to_vec(),
		format!(
			"<< /Type /Pages /Count {pages} /Kids [{}] >>",
			(0..pages)
				.map(|n| format!("{} 0 R", 4 + n * 2))
				.collect::<Vec<_>>()
				.join(" ")
		)
		.into_bytes(),
	];
	let mut image=b"<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 768 >>\nstream\n".to_vec();
	for i in 0..768 {
		image.push(if i % 48 < 24 { 0 } else { 255 });
	}
	image.extend_from_slice(b"\nendstream");
	objects.push(image);
	for _ in 0..pages {
		let page_object = objects.len() + 1;
		objects.push(format!("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /XObject << /Im0 3 0 R >> >> /Contents {} 0 R >>",page_object+1).into_bytes());
		let content = "q 500 0 0 600 30 50 cm /Im0 Do Q";
		objects.push(
			format!(
				"<< /Length {} >>\nstream\n{content}\nendstream",
				content.len()
			)
			.into_bytes(),
		);
	}
	let mut pdf = b"%PDF-1.4\n".to_vec();
	let mut offsets = vec![0];
	for (i, object) in objects.iter().enumerate() {
		offsets.push(pdf.len());
		pdf.extend(format!("{} 0 obj\n", i + 1).as_bytes());
		pdf.extend(object);
		pdf.extend(b"\nendobj\n");
	}
	let xref = pdf.len();
	pdf.extend(format!("xref\n0 {}\n0000000000 65535 f \n", offsets.len()).as_bytes());
	for offset in offsets.iter().skip(1) {
		pdf.extend(format!("{offset:010} 00000 n \n").as_bytes());
	}
	pdf.extend(
		format!(
			"trailer << /Size {} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF",
			offsets.len()
		)
		.as_bytes(),
	);
	pdf
}
pub struct Harness {
	pub worker: Worker,
	pub gateway: Gateway,
	pub ledger: Ledger,
	pub options: tokio::sync::watch::Sender<archive_server::ai::Options>,
	pub script: Script,
}
impl Harness {
	pub fn new(app: &super::App, pdf: Vec<u8>, script: Script, reading: reading::Options) -> Self {
		let (options, rx) = tokio::sync::watch::channel(archive_server::ai::Options::shipped(true));
		let ledger = Ledger::new(
			Arc::new(PostgresStore::new(app.pool.clone())),
			rx,
			app.time.clone(),
		);
		let gateway = Gateway::scripted(ledger.clone(), script.clone());
		let mut worker = Worker::new(
			app.pool.clone(),
			Arc::new(Bytes(pdf)),
			app.time.clone(),
			ScanReadingModel::new(gateway.clone(), reading),
		);
		worker.spool_directory = "/tmp/opencode".into();
		Self {
			worker,
			gateway,
			ledger,
			options,
			script,
		}
	}
	pub async fn seed(&self, app: &super::App, pages: i32) -> (uuid::Uuid, uuid::Uuid) {
		let owner = super::catalogue::lied(app, "Waldfahrt", serde_json::json!({})).await;
		let id = super::catalogue::datei(app, owner, false, "noten", "application/pdf").await;
		super::catalogue::text(app, id, "", 3, 0).await;
		reading::insert(&app.pool, id, uuid::Uuid::nil(), pages, app.time.now())
			.await
			.unwrap();
		(owner, id)
	}
	pub async fn run(&self, id: uuid::Uuid, count: i64) -> archive_server::queue::Disposition {
		self.worker
			.handle(
				&archive_server::queue::Message {
					id: "m".into(),
					receipt: "p".into(),
					body: archive_server::queue::envelope("reading", id),
					dequeue_count: count,
				},
				self.worker.clock.now() + chrono::Duration::minutes(5),
				&Default::default(),
			)
			.await
			.unwrap()
	}
}
