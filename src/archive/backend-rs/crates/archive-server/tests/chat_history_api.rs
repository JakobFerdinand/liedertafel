//! All four ChatApiTests.Historie methods at actual chat/Gateway/current SQL.
#![allow(clippy::unwrap_used)] // Shared assertion-only synthetic history seeding.
mod support;
use serde_json::{Value, json};
use support::{App, Client, chat::*};
use uuid::Uuid;
async fn seed(year: i32, approximate: bool) -> (App, Harness, Client, Uuid, Uuid) {
	let mut app = App::new_scoped("chat232_history").await;
	let harness = install(&mut app, archive_server::reading::scripted_scan_reader());
	let member = app.member().await;
	let mut admin = app.admin().await;
	let lied = support::catalogue::lied(
		&app,
		"Die Waldfahrt",
		json!({"composer":"Friedrich Silcher"}),
	)
	.await;
	support::catalogue::lied(&app, "Abendlied", json!({"composer":"Franz Schubert"})).await;
	let event = support::catalogue::event(&app).await;
	sqlx::query(r#"UPDATE events SET "Title"=$2,"DateYear"=$3,"DateMonth"=NULL,"DateDay"=NULL,"DateApproximate"=$4 WHERE "Id"=$1"#).bind(event).bind(if year>2090 {"Mai-Konzert"}else{"Sängerfest"}).bind(year).bind(approximate).execute(&app.pool).await.unwrap();
	assert_eq!(
		admin
			.request(
				"PUT",
				&format!("/api/events/{event}/programme/items"),
				json!({"items":[{"liedId":lied}]})
			)
			.await
			.0,
		200
	);
	(app, harness, member, lied, event)
}
fn components(events: &[Value]) -> Vec<&Value> {
	events
		.iter()
		.filter(|e| e["name"] == "archive.a2ui")
		.flat_map(|e| e["value"].as_array().unwrap())
		.filter_map(|m| m["updateComponents"]["components"].as_array())
		.flatten()
		.collect()
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn previous_cited_lied_followup_uses_authoritative_history_and_shows_sung_event_card() {
	let (app, harness, mut member, lied, _) = seed(1932, false).await;
	let thread = Uuid::new_v4().to_string();
	run(&mut member, &thread, "Zeige mir die Waldfahrt als Karte").await;
	let events = run(&mut member, &thread, "Wann haben wir dieses Lied gesungen?").await;
	let answer = text(&events);
	assert!(answer.contains("Sängerfest"));
	assert!(answer.contains("1932"));
	let roots = components(&events);
	assert_eq!(
		roots
			.iter()
			.map(|r| r["component"].as_str().unwrap())
			.collect::<Vec<_>>(),
		["LiedKarte", "AuftrittKarte"]
	);
	assert_eq!(roots[0]["liedId"], lied.to_string());
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn approximate_date_and_published_mark_and_lied_recording_are_honest_hidden_recording_stays_hidden()
 {
	let (app, harness, mut member, lied, event) = seed(1950, true).await;
	let entry:Uuid=sqlx::query_scalar(r#"SELECT i."Id" FROM programme_items i JOIN programmes p ON p."Id"=i."ProgrammeId" WHERE p."EventId"=$1"#).bind(event).fetch_one(&app.pool).await.unwrap();
	for (label, published) in [("Konzertmitschnitt", true), ("Geheimmitschnitt", false)] {
		let recording = Uuid::new_v4();
		sqlx::query(r#"INSERT INTO recordings ("Id","EventId","Label","Kind","DownloadEnabled","PublishedAt","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,'audio',false,$4,now(),now(),$5,$5,1)"#).bind(recording).bind(event).bind(label).bind(published.then(chrono::Utc::now)).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
		sqlx::query(r#"INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,3,80,now(),now(),$5,$5,1)"#).bind(Uuid::new_v4()).bind(recording).bind(entry).bind(event).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
	}
	let file = support::catalogue::datei(&app, lied, false, "aufnahme", "audio/mpeg").await;
	sqlx::query(r#"UPDATE dateien SET "RecordedAtEventId"=$2 WHERE "Id"=$1"#)
		.bind(file)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	let events = run(
		&mut member,
		"",
		"Wann haben wir das Lied Die Waldfahrt gesungen?",
	)
	.await;
	let answer = text(&events);
	for fragment in [
		"um 1950",
		"Zeitmarke",
		"Konzertmitschnitt",
		"Aufnahme des Liedes",
	] {
		assert!(answer.contains(fragment));
	}
	assert!(!answer.contains("Mai"));
	assert!(
		!events
			.iter()
			.any(|e| e.to_string().contains("Geheimmitschnitt"))
	);
	assert!(
		components(&events)
			.iter()
			.any(|c| c["component"] == "AuftrittKarte" && c["datum"] == "um 1950")
	);
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn future_programme_is_not_a_sung_claim_and_shows_ordered_programme_card() {
	let (app, harness, mut member, _, _) = seed(2099, false).await;
	let events = run(
		&mut member,
		"",
		"Wann haben wir das Lied Die Waldfahrt gesungen?",
	)
	.await;
	let answer = text(&events);
	assert!(answer.contains("steht im Programm"));
	assert!(answer.contains("Mai-Konzert"));
	assert!(!answer.contains("gesungen"));
	assert_eq!(
		components(&events)
			.iter()
			.map(|r| r["component"].as_str().unwrap())
			.collect::<Vec<_>>(),
		["LiedKarte", "ProgrammListe"]
	);
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}

#[tokio::test]
#[ignore = "dedicated local PostgreSQL"]
async fn unsung_lied_is_named_honestly_and_title_based_history_needs_no_previous_context() {
	let (app, harness, mut member, _, _) = seed(1932, false).await;
	let unknown = run(
		&mut member,
		"",
		"Wann haben wir das Lied Abendlied gesungen?",
	)
	.await;
	assert!(text(&unknown).contains("Abendlied"));
	assert!(text(&unknown).contains("keine"));
	let sung = run(
		&mut member,
		"",
		"Wann haben wir das Lied Die Waldfahrt gesungen?",
	)
	.await;
	assert!(text(&sung).contains("Sängerfest"));
	assert!(harness.origin.drain_readings().await);
	app.close().await;
}
