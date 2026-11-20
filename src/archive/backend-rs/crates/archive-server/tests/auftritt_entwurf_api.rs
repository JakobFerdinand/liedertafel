//! All frozen AuftrittEntwurfApiTests persistence cases; no scan/model activity.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::{Value, json};
use support::App;
use uuid::Uuid;
fn draft(id: Uuid, title: &str, count: i32, form: Value) -> Value {
	json!({"entwurfId":id,"titel":title,"dateien":count,"formular":form})
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn draft_is_saved_replaced_owned_listed_discarded_and_never_an_auftritt() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut second = app.sign_in("redaktion@liedertafel.test").await;
	let mut member = app.member().await;
	let id = Uuid::new_v4();
	let path = format!("/api/auftritt-entwuerfe/{id}");
	let form = json!({"version":1,"werte":{"title":"Sängerfest"},"programm":[{"text":"Ave Maria"}],"opaque":[false,{"FILEId":"not a UUID"}]});
	let (s, b) = editor
		.request("PUT", &path, draft(id, " Sängerfest ", 1, form.clone()))
		.await;
	assert_eq!(s, 200, "{b}");
	let (s, b) = editor.get(&path).await;
	assert_eq!(s, 200);
	assert_eq!(b["entwurf"]["formular"], form);
	assert_eq!(b["entwurf"]["titel"], "Sängerfest");
	assert_eq!(b["entwurf"]["dateien"], 1);
	assert_eq!(b["entwurf"]["entwurfId"], id.to_string());
	assert_eq!(second.get(&path).await.0, 404);
	assert_eq!(
		second.get("/api/auftritt-entwuerfe").await.1["entwuerfe"],
		json!([])
	);
	assert_eq!(
		second
			.request("PUT", &path, draft(id, "Fremd", 2, json!({"version":1})))
			.await
			.0,
		404
	);
	assert_eq!(second.raw("DELETE", &path, "malformed").await.0, 204);
	assert_eq!(editor.get(&path).await.1["entwurf"]["titel"], "Sängerfest");
	*app.time.0.lock().unwrap() += chrono::Duration::seconds(1);
	assert_eq!(
		editor
			.request(
				"PUT",
				&path,
				draft(id, "Neu", 2, json!({"version":1,"werte":{"title":"Neu"}}))
			)
			.await
			.0,
		200
	);
	let b = editor.get(&path).await.1;
	assert_eq!(b["entwurf"]["titel"], "Neu");
	assert_eq!(b["entwurf"]["dateien"], 2);
	assert_eq!(
		b["entwurf"]["formular"],
		json!({"version":1,"werte":{"title":"Neu"}})
	);
	*app.time.0.lock().unwrap() += chrono::Duration::seconds(1);
	let newer = Uuid::new_v4();
	assert_eq!(
		editor
			.request(
				"PUT",
				&format!("/api/auftritt-entwuerfe/{newer}"),
				draft(newer, "Zweiter", 3, json!({"version":1}))
			)
			.await
			.0,
		200
	);
	let list = editor.get("/api/auftritt-entwuerfe").await.1;
	assert_eq!(list["entwuerfe"][0]["entwurfId"], newer.to_string());
	assert_eq!(list["entwuerfe"][1]["titel"], "Neu");
	assert_eq!(editor.get("/api/events").await.1["total"], 0);
	assert_eq!(
		editor.get("/api/programmes").await.1["programmes"],
		json!([])
	);
	assert_eq!(editor.get("/api/lieder").await.1["total"], 0);
	for method in ["GET", "PUT", "DELETE"] {
		assert_eq!(
			member
				.request(method, &path, draft(id, "A", 1, json!({"version":1})))
				.await
				.0,
			403
		);
	}
	assert_eq!(member.get("/api/auftritt-entwuerfe").await.0, 403);
	assert_eq!(app.client().get("/api/auftritt-entwuerfe").await.0, 401);
	let token = std::mem::take(&mut editor.token);
	assert_eq!(
		editor
			.request("PUT", &path, draft(id, "A", 1, json!({"version":1})))
			.await
			.0,
		400
	);
	editor.token = token;
	for _ in 0..2 {
		assert_eq!(editor.raw("DELETE", &path, "[not JSON").await.0, 204);
	}
	assert_eq!(editor.get(&path).await.0, 404);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn invalid_shapes_bounds_and_25_owned_drafts_limit_updates_still_work() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let id = Uuid::new_v4();
	let path = format!("/api/auftritt-entwuerfe/{id}");
	for b in [
		Value::Null,
		json!({}),
		draft(Uuid::new_v4(), "A", 1, json!({"version":1})),
		draft(id, "A", 1, json!("kein objekt")),
		draft(id, "A", 1, Value::Null),
		draft(id, "A", -1, json!({"version":1})),
		draft(id, "A", 51, json!({"version":1})),
		draft(id, "A", 1, json!({"version":2})),
		draft(id, &"x".repeat(201), 1, json!({"version":1})),
		draft(id, &" ".repeat(201), 1, json!({"version":1})),
		draft(id, &"😀".repeat(101), 1, json!({"version":1})),
		draft(id, "A", 1, json!({"version":1,"text":"x".repeat(200_000)})),
	] {
		let (s, b) = e.request("PUT", &path, b).await;
		assert_eq!(s, 400, "{b}");
		assert_eq!(b["title"], "Der Entwurf ist ungültig.");
	}
	for n in 0..25 {
		let id = if n == 0 { id } else { Uuid::new_v4() };
		let (s, b) = e
			.request(
				"PUT",
				&format!("/api/auftritt-entwuerfe/{id}"),
				draft(id, "", 0, json!({"version":1})),
			)
			.await;
		assert_eq!(s, 200, "{b}");
	}
	let other = Uuid::new_v4();
	assert_eq!(
		e.request(
			"PUT",
			&format!("/api/auftritt-entwuerfe/{other}"),
			draft(other, "A", 1, json!({"version":1}))
		)
		.await
		.0,
		409
	);
	assert_eq!(
		e.request("PUT", &path, draft(id, "Update", 50, json!({"version":1})))
			.await
			.0,
		200
	);
	assert_eq!(e.get(&path).await.1["entwurf"]["dateien"], 50);
	app.close().await;
}
