//! Frozen LiedHistorieTests.cs plus seeded Zeitmarke read compatibility (#227 mutations stay later).
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::{Value, json};
use support::{App, Client, catalogue::*};
use uuid::Uuid;
async fn create(
	c: &mut Client,
	title: &str,
	y: Option<i32>,
	m: Option<i32>,
	d: Option<i32>,
	approx: bool,
) -> Uuid {
	let (s,b)=c.post("/api/events",json!({"title":title,"kind":"concert","dateYear":y,"dateMonth":m,"dateDay":d,"dateApproximate":approx})).await;
	assert_eq!(s, 201, "{b}");
	Uuid::parse_str(b["event"]["id"].as_str().unwrap()).unwrap()
}
async fn programme(c: &mut Client, id: Uuid, items: Value) -> Value {
	let (s, b) = c
		.request(
			"PUT",
			&format!("/api/events/{id}/programme/items"),
			json!({"items":items}),
		)
		.await;
	assert_eq!(s, 200, "{b}");
	b["programme"].clone()
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn history_is_derived_grouped_uncertain_ordered_and_changes_after_programme_edit() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let mut m = app.member().await;
	let song = lied(&app, "Geschichtslied", json!({})).await;
	let other = lied(&app, "Anderes Lied", json!({})).await;
	let mut ids = Vec::new();
	let mut ps = Vec::new();
	for (title, y, month, day, a) in [
		("Späterer Auftritt", Some(1975), Some(6), Some(1), false),
		("Ungefähres Fest", Some(1960), None, None, true),
		("Nur ein Jahr", Some(1950), None, None, false),
		("Undatiert", None, None, None, false),
	] {
		let id = create(&mut e, title, y, month, day, a).await;
		ids.push(id);
		ps.push(
			programme(
				&mut e,
				id,
				json!([{"liedId":other},{"liedId":song},{"liedId":song}]),
			)
			.await,
		);
	}
	let without = create(&mut e, "Ohne das Lied", Some(1999), Some(1), Some(1), false).await;
	programme(&mut e, without, json!([{"liedId":other}])).await;
	let path = format!("/api/lieder/{song}/historie");
	let (s, b) = m.get(&path).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["lied"]["title"], "Geschichtslied");
	assert_eq!(
		b["counts"],
		json!({"events":4,"inProgramme":4,"uncertainDates":3})
	);
	assert_eq!(b["total"], 4);
	assert_eq!(
		b["auftritte"]
			.as_array()
			.unwrap()
			.iter()
			.map(|v| v["eventId"].as_str().unwrap())
			.collect::<Vec<_>>(),
		ids.iter().map(|id| id.to_string()).collect::<Vec<_>>()
	);
	for row in b["auftritte"].as_array().unwrap() {
		assert_eq!(row["entries"], 2);
		assert_eq!(row["inProgramme"], true);
		assert!(row.get("evidenceStatus").is_none());
	}
	assert_eq!(b["auftritte"][0]["dateDisplay"], "1. Juni 1975");
	assert_eq!(b["auftritte"][0]["dateUncertain"], false);
	assert_eq!(b["auftritte"][1]["dateDisplay"], "um 1960");
	assert_eq!(b["auftritte"][2]["datePrecision"], "year");
	assert_eq!(b["auftritte"][3]["dateUncertain"], true);
	let p = &ps[0];
	let (s,b)=e.request("PUT",&format!("/api/events/{}/programme/items",ids[0]),json!({"rowVersion":p["rowVersion"],"items":[{"id":p["items"][2]["id"],"liedId":song},{"id":p["items"][0]["id"],"liedId":other}]})).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(m.get(&path).await.1["total"], 4);
	assert_eq!(
		e.request(
			"PUT",
			&format!("/api/events/{}/programme/items", ids[0]),
			json!({"rowVersion":2,"items":[{"id":p["items"][0]["id"],"liedId":other}]})
		)
		.await
		.0,
		200
	);
	assert_eq!(m.get(&path).await.1["total"], 3);
	assert_eq!(
		m.get(&format!("/api/lieder/{}/historie", Uuid::new_v4()))
			.await
			.0,
		404
	);
	assert_eq!(app.client().get(&path).await.0, 401);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn lied_aufnahme_reference_appears_on_both_pages_live_only_without_a_programme() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let mut m = app.member().await;
	let song = lied(&app, "Aufgenommenes Lied", json!({})).await;
	let concert = create(&mut e, "Sängerfest", Some(1988), Some(9), Some(3), false).await;
	let elsewhere = create(&mut e, "Anderswo", Some(1990), Some(1), Some(1), false).await;
	for (event, deleted, name) in [
		(Some(concert), false, "kept.mp3"),
		(Some(elsewhere), true, "gone.mp3"),
		(None, false, "ohne-auftritt.mp3"),
	] {
		let file = datei(&app, song, false, "aufnahme", "audio/mp4").await;
		sqlx::query(r#"UPDATE dateien SET "RecordedAtEventId"=$2,"OriginalFileName"=$3,"DeletedAt"=$4 WHERE "Id"=$1"#).bind(file).bind(event).bind(name).bind(deleted.then(chrono::Utc::now)).execute(&app.pool).await.unwrap();
	}
	let b = m.get(&format!("/api/lieder/{song}/historie")).await.1;
	assert_eq!(b["total"], 1);
	let row = &b["auftritte"][0];
	assert_eq!(row["eventId"], concert.to_string());
	assert_eq!(row["inProgramme"], false);
	assert_eq!(row["entries"], 0);
	assert_eq!(row["liedAufnahmen"][0]["fileName"], "kept.mp3");
	assert_eq!(row["liedAufnahmen"].as_array().unwrap().len(), 1);
	let detail = m.get(&format!("/api/events/{concert}")).await.1;
	assert_eq!(
		detail["event"]["liedAufnahmen"][0]["liedId"],
		song.to_string()
	);
	assert_eq!(
		detail["event"]["liedAufnahmen"][0]["liedTitle"],
		"Aufgenommenes Lied"
	);
	assert_eq!(detail["event"]["liedAufnahmen"][0]["fileName"], "kept.mp3");
	assert_eq!(
		m.get(&format!("/api/events/{elsewhere}")).await.1["event"]["liedAufnahmen"],
		json!([])
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn history_pages_are_fixed_size_clamped_and_deterministic() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let song = lied(&app, "Dauerbrenner", json!({})).await;
	for i in 0..25 {
		let id = create(
			&mut e,
			&format!("Auftritt {i:02}"),
			Some(1900 + i),
			Some(5),
			Some(5),
			false,
		)
		.await;
		programme(&mut e, id, json!([{"liedId":song}])).await;
	}
	let path = format!("/api/lieder/{song}/historie");
	let b = e.get(&path).await.1;
	assert_eq!(b["total"], 25);
	assert_eq!(b["pageSize"], 20);
	assert_eq!(b["auftritte"].as_array().unwrap().len(), 20);
	assert_eq!(b["auftritte"][0]["eventTitle"], "Auftritt 24");
	let b = e.get(&format!("{path}?page=2")).await.1;
	assert_eq!(
		b["auftritte"]
			.as_array()
			.unwrap()
			.iter()
			.map(|r| r["eventTitle"].as_str().unwrap())
			.collect::<Vec<_>>(),
		[
			"Auftritt 04",
			"Auftritt 03",
			"Auftritt 02",
			"Auftritt 01",
			"Auftritt 00"
		]
	);
	assert_eq!(e.get(&format!("{path}?page=99")).await.1["page"], 2);
	assert_eq!(e.get(&format!("{path}?PAGE=0")).await.1["page"], 1);
	assert_eq!(e.get(&format!("{path}?page=")).await.0, 400);
	assert_eq!(e.get(&format!("{path}?page=1&page=2")).await.0, 400);
	app.close().await;
}
