use super::{App, Client, storage::FakeStorage};
use serde_json::{Value, json};
use uuid::Uuid;
pub async fn create(editor: &mut Client, title: &str) -> Uuid {
	let (s, b) = editor.post("/api/lieder", json!({"title":title})).await;
	assert_eq!(s, 201, "{b}");
	Uuid::parse_str(b["lied"]["id"].as_str().unwrap()).unwrap()
}
pub fn pdf(size: usize) -> Vec<u8> {
	let mut bytes = vec![0; size];
	bytes[..5].copy_from_slice(b"%PDF-");
	bytes
}
pub async fn open(editor: &mut Client, owner: Uuid, event: bool, request: Value) -> Value {
	let (s, b) = editor
		.post(
			&format!(
				"/api/{}/{owner}/dateien/upload-session",
				if event { "events" } else { "lieder" }
			),
			request,
		)
		.await;
	assert_eq!(s, 201, "{b}");
	b
}
pub async fn stage(
	editor: &mut Client,
	owner: Uuid,
	event: bool,
	request: Value,
	bytes: &[u8],
	content: &str,
	storage: &FakeStorage,
) -> Value {
	let open = open(editor, owner, event, request).await;
	let blob = storage.calls.lock().unwrap().last().unwrap().1.clone();
	storage.store(&blob, bytes, content);
	open
}
pub async fn finish(editor: &mut Client, session: &Value) -> (axum::http::StatusCode, Value) {
	editor
		.post(
			&format!(
				"/api/upload-sessions/{}/finalize",
				session["uploadSessionId"].as_str().unwrap()
			),
			json!({}),
		)
		.await
}
pub async fn upload(
	editor: &mut Client,
	owner: Uuid,
	request: Value,
	bytes: &[u8],
	content: &str,
	storage: &FakeStorage,
) -> Value {
	let session = stage(editor, owner, false, request, bytes, content, storage).await;
	let (s, b) = finish(editor, &session).await;
	assert_eq!(s, 200, "{b}");
	b["datei"].clone()
}
pub async fn blob(app: &App, id: Uuid) -> String {
	sqlx::query_scalar(r#"SELECT "BlobName" FROM dateien WHERE "Id"=$1"#)
		.bind(id)
		.fetch_one(&app.pool)
		.await
		.unwrap()
}
