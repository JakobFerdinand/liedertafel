use super::{App, Client, storage::FakeStorage, upload};
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;
pub struct Scenario {
	pub app: App,
	pub storage: Arc<FakeStorage>,
	pub editor: Client,
	pub member: Client,
	pub event: Uuid,
	pub lieder: Vec<Uuid>,
	pub entries: Vec<Value>,
	pub recording: Uuid,
	pub revision: Value,
}
pub async fn event(c: &mut Client) -> Uuid {
	let (s, b) = c
		.post(
			"/api/events",
			json!({"title":"Konzert","kind":"concert","dateYear":1998,"dateMonth":3,"dateDay":3}),
		)
		.await;
	assert_eq!(s, 201, "{b}");
	Uuid::parse_str(b["event"]["id"].as_str().unwrap()).unwrap()
}
pub async fn create(c: &mut Client, event: Uuid, label: &str, kind: &str) -> Uuid {
	let (s, b) = c
		.post(
			&format!("/api/events/{event}/recordings"),
			json!({"label":label,"kind":kind}),
		)
		.await;
	assert_eq!(s, 201, "{b}");
	Uuid::parse_str(b["recording"]["id"].as_str().unwrap()).unwrap()
}
pub async fn attach(
	c: &mut Client,
	event: Uuid,
	id: Uuid,
	slot: &str,
	bytes: &[u8],
	content: &str,
	storage: &FakeStorage,
) -> (axum::http::StatusCode, Value) {
	let session = upload::stage(
		c,
		event,
		true,
		json!({"kind":"aufnahme","contentType":content,"fileName":"synthetisch"}),
		bytes,
		content,
		storage,
	)
	.await;
	let (s, b) = upload::finish(c, &session).await;
	assert_eq!(s, 200, "{b}");
	c.request(
		"PUT",
		&format!("/api/recordings/{id}/dateien/{slot}"),
		json!({"dateiId":b["datei"]["id"]}),
	)
	.await
}
pub fn mp4() -> Vec<u8> {
	let mut b = vec![0; 128];
	b[4..12].copy_from_slice(b"ftypisom");
	b
}
impl Scenario {
	pub async fn new() -> Self {
		let mut app = App::new().await;
		let storage = Arc::new(FakeStorage::default());
		app.with_storage(storage.clone());
		let mut editor = app.admin().await;
		let member = app.member().await;
		let event = event(&mut editor).await;
		let mut lieder = Vec::new();
		for title in ["Erstes Lied", "Zweites Lied", "Unmarkiertes Lied"] {
			lieder.push(upload::create(&mut editor, title).await);
		}
		let (s, b) = editor
			.request(
				"PUT",
				&format!("/api/events/{event}/programme/items"),
				json!({"items":lieder.iter().map(|id|json!({"liedId":id})).collect::<Vec<_>>()}),
			)
			.await;
		assert_eq!(s, 200, "{b}");
		let entries = b["programme"]["items"].as_array().unwrap().clone();
		let recording = create(&mut editor, event, "Video Kamera 1", "video").await;
		let (s, b) = attach(
			&mut editor,
			event,
			recording,
			"original",
			&mp4(),
			"video/mp4",
			&storage,
		)
		.await;
		assert_eq!(s, 200, "{b}");
		let revision = b["recording"]["playback"]["dateiId"].clone();
		let (s, b) = editor
			.request(
				"PATCH",
				&format!("/api/recordings/{recording}"),
				json!({"durationSeconds":2000}),
			)
			.await;
		assert_eq!(s, 200, "{b}");
		Self {
			app,
			storage,
			editor,
			member,
			event,
			lieder,
			entries,
			recording,
			revision,
		}
	}
	pub fn path(&self) -> String {
		format!("/api/recordings/{}/passages", self.recording)
	}
	pub async fn add(&mut self, index: usize, start: f64, end: f64) -> Value {
		let(s,b)=self.editor.post(&self.path(),json!({"programmeItemId":self.entries[index]["id"],"startSeconds":start,"endSeconds":end,"expectedPlaybackDateiId":self.revision})).await;
		assert_eq!(s, 201, "{b}");
		b["passage"].clone()
	}
	pub async fn publish(&mut self) {
		let (s, b) = self
			.editor
			.request(
				"PATCH",
				&format!("/api/recordings/{}", self.recording),
				json!({"isPublished":true}),
			)
			.await;
		assert_eq!(s, 200, "{b}");
	}
	pub async fn replace(&mut self) -> Value {
		let (s, b) = attach(
			&mut self.editor,
			self.event,
			self.recording,
			"playback",
			&mp4(),
			"video/mp4",
			&self.storage,
		)
		.await;
		assert_eq!(s, 200, "{b}");
		self.revision = b["recording"]["playback"]["dateiId"].clone();
		self.revision.clone()
	}
	pub async fn view(&mut self, member: bool) -> Value {
		let path = self.path();
		let (s, b) = if member {
			self.member.get(&path).await
		} else {
			self.editor.get(&path).await
		};
		assert_eq!(s, 200, "{b}");
		b
	}
	pub async fn history(&mut self, index: usize, member: bool) -> Value {
		let path = format!("/api/lieder/{}/historie", self.lieder[index]);
		let (s, b) = if member {
			self.member.get(&path).await
		} else {
			self.editor.get(&path).await
		};
		assert_eq!(s, 200, "{b}");
		b
	}
}
