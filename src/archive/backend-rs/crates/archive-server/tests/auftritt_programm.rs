//! Frozen AuftrittProgrammTests.cs; current SQL identities and transaction gates.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::{Value, json};
use support::{App, Client};
use uuid::Uuid;
async fn event(c: &mut Client, year: i32) -> Uuid {
	let (s, b) = c
		.post(
			"/api/events",
			json!({"kind":"concert","title":"Jahreskonzert","dateYear":year,"dateMonth":5,"dateDay":12}),
		)
		.await;
	assert_eq!(s, 201, "{b}");
	Uuid::parse_str(b["event"]["id"].as_str().unwrap()).unwrap()
}
async fn lied(c: &mut Client, title: &str) -> Value {
	let (s, b) = c
		.post(
			"/api/lieder",
			json!({"title":title,"voiceConfiguration":"TTBB","musicalKey":"G-Dur"}),
		)
		.await;
	assert_eq!(s, 201);
	b["lied"]["id"].clone()
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn editor_saves_one_programme_members_read_it_and_reordering_keeps_entry_ids() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let a = lied(&mut editor, "Die Waldfahrt").await;
	let b = lied(&mut editor, "Abendlied").await;
	let c = lied(&mut editor, "Drittes Lied").await;
	let id = event(&mut editor, 2099).await;
	let path = format!("/api/events/{id}/programme/items");
	assert!(member.get(&format!("/api/events/{id}")).await.1["event"]["programme"].is_null());
	let (s, first) = editor
		.request(
			"PUT",
			&path,
			json!({"rowVersion":999,"items":[{"liedId":a,"note":" Eröffnung "},{"liedId":b},{"liedId":a}]}),
		)
		.await;
	assert_eq!(s, 200, "{first}");
	let first = &first["programme"];
	assert_eq!(first["rowVersion"], 1);
	assert_eq!(first["items"][0]["liedTitle"], "Die Waldfahrt");
	assert_eq!(first["items"][0]["voiceConfiguration"], "TTBB");
	assert_eq!(first["items"][0]["musicalKey"], "G-Dur");
	assert_eq!(first["items"][0]["note"], "Eröffnung");
	assert_ne!(first["items"][0]["id"], first["items"][2]["id"]);
	for removed in ["working", "published", "history", "confirmation"] {
		assert!(first.get(removed).is_none());
	}
	assert_eq!(
		member.get(&format!("/api/events/{id}")).await.1["event"]["programme"],
		*first
	);
	let upcoming = member.get("/api/programmes").await.1;
	assert_eq!(upcoming["programmes"][0]["eventId"], id.to_string());
	assert_eq!(upcoming["programmes"][0]["itemCount"], 3);
	let (s,second)=editor.request("PUT",&path,json!({"rowVersion":1,"items":[{"id":first["items"][2]["id"],"liedId":a},{"id":first["items"][0]["id"],"liedId":c,"note":"Zugabe"},{"liedId":b}]})).await;
	assert_eq!(s, 200, "{second}");
	let second = &second["programme"];
	assert_eq!(second["items"][0]["id"], first["items"][2]["id"]);
	assert_eq!(second["items"][1]["id"], first["items"][0]["id"]);
	assert_eq!(second["items"][1]["liedId"], c);
	assert_eq!(second["rowVersion"], 2);
	assert_eq!(
		second["items"]
			.as_array()
			.unwrap()
			.iter()
			.map(|v| v["position"].as_i64().unwrap())
			.collect::<Vec<_>>(),
		[1, 2, 3]
	);
	let (s, stale) = editor
		.request("PUT", &path, json!({"rowVersion":1,"items":[null]}))
		.await;
	assert_eq!(s, 409);
	assert_eq!(
		stale["title"],
		"Das Programm wurde zwischenzeitlich geändert."
	);
	assert_eq!(
		member.get(&format!("/api/events/{id}")).await.1["event"]["programme"],
		*second
	);
	assert_eq!(member.request("PUT", &path, json!({})).await.0, 403);
	let mut anonymous = app.client();
	anonymous.csrf().await;
	assert_eq!(anonymous.request("PUT", &path, json!({})).await.0, 401);
	assert_eq!(
		editor
			.request(
				"PUT",
				&format!("/api/events/{}/programme/items", Uuid::new_v4()),
				json!({})
			)
			.await
			.0,
		404
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn invalid_entries_write_nothing_empty_programme_and_foreign_ids_are_server_replaced() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let a = lied(&mut e, "Erstes Lied").await;
	let id = event(&mut e, 2099).await;
	let path = format!("/api/events/{id}/programme/items");
	for (items, title) in [
		(
			json!([{"liedId":a,"note":"x".repeat(501)}]),
			"Die Notiz ist zu lang.",
		),
		(
			json!([{"liedId":Uuid::new_v4()}]),
			"Ein Programmpunkt verweist auf ein nicht vorhandenes Lied.",
		),
		(
			json!([{"note":"ohne Lied"}]),
			"Ein Programmpunkt verweist auf ein nicht vorhandenes Lied.",
		),
		(json!([null]), "Ein Programmpunkt ist ungültig."),
	] {
		let (s, b) = e.request("PUT", &path, json!({"items":items})).await;
		assert_eq!(s, 400, "{b}");
		assert_eq!(b["title"], title);
		assert!(e.get(&format!("/api/events/{id}")).await.1["event"]["programme"].is_null());
	}
	let unknown = Uuid::new_v4();
	let (s, b) = e
		.request("PUT", &path, json!({"items":[{"id":unknown,"liedId":a}]}))
		.await;
	assert_eq!(s, 200);
	let entry = b["programme"]["items"][0]["id"].clone();
	assert_ne!(entry, unknown.to_string());
	for body in [
		json!({"rowVersion":1,"items":[{"id":entry,"liedId":a},{"id":entry,"liedId":a}]}),
		json!({"rowVersion":1,"items":[{"id":unknown,"liedId":a},{"id":unknown,"liedId":a}]}),
	] {
		let (s, b) = e.request("PUT", &path, body).await;
		assert_eq!(s, 400);
		assert_eq!(b["title"], "Ein Programmpunkt ist ungültig.");
	}
	let other = event(&mut e, 2099).await;
	let (_, b) = e
		.request(
			"PUT",
			&format!("/api/events/{other}/programme/items"),
			json!({"items":[{"id":entry,"liedId":a}]}),
		)
		.await;
	assert_ne!(b["programme"]["items"][0]["id"], entry);
	assert_eq!(e.request("PUT", &path, json!({"items":[]})).await.0, 409);
	let (s, b) = e
		.request("PUT", &path, json!({"rowVersion":1,"items":null}))
		.await;
	assert_eq!(s, 200);
	assert_eq!(b["programme"]["items"], json!([]));
	assert_eq!(b["programme"]["rowVersion"], 2);
	let empty = event(&mut e, 2099).await;
	assert_eq!(
		e.raw(
			"PUT",
			&format!("/api/events/{empty}/programme/items"),
			"null"
		)
		.await
		.1["programme"]["rowVersion"],
		1
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL seeded Zeitmarke guard and real FK"]
async fn seeded_unpublished_zeitmarken_guard_removal_reorder_and_history_visibility_without_recording_mutations()
 {
	let app = App::new().await;
	let mut e = app.admin().await;
	let mut member = app.member().await;
	let a = lied(&mut e, "Abendlied").await;
	let b = lied(&mut e, "Berglied").await;
	let id = event(&mut e, 1950).await;
	let path = format!("/api/events/{id}/programme/items");
	let (_, p) = e
		.request("PUT", &path, json!({"items":[{"liedId":a},{"liedId":b}]}))
		.await;
	let p = &p["programme"];
	let entry = Uuid::parse_str(p["items"][0]["id"].as_str().unwrap()).unwrap();
	let original = support::catalogue::datei(&app, id, true, "aufnahme", "audio/mpeg").await;
	let recording = support::catalogue::recording(&app, id, original, None, "audio").await;
	sqlx::query(r#"UPDATE recordings SET "PublishedAt"=NULL,"Label"='Konzert' WHERE "Id"=$1"#)
		.bind(recording)
		.execute(&app.pool)
		.await
		.unwrap();
	let mark = Uuid::new_v4();
	sqlx::query(r#"INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion","PlaybackDateiId") VALUES ($1,$2,$3,$4,10,30,now(),$5,now(),$5,1,$6)"#).bind(mark).bind(recording).bind(entry).bind(id).bind(Uuid::nil()).bind(original).execute(&app.pool).await.unwrap();
	let history = format!("/api/lieder/{}/historie", a.as_str().unwrap());
	assert_eq!(
		member.get(&history).await.1["auftritte"][0]["recordings"],
		json!([])
	);
	assert_eq!(
		e.get(&history).await.1["auftritte"][0]["recordings"][0]["timestampState"],
		"current"
	);
	let (s, blocked) = e
		.request("PUT", &path, json!({"rowVersion":1,"items":[]}))
		.await;
	assert_eq!(s, 409, "{blocked}");
	assert_eq!(
		blocked["title"],
		"Zeitmarken vorhanden: „Abendlied“ in „Konzert“. Entferne zuerst diese Zeitmarken in den Aufnahmen."
	);
	assert_eq!(
		e.get(&format!("/api/events/{id}")).await.1["event"]["programme"],
		*p
	);
	let (s, reordered) = e
		.request(
			"PUT",
			&path,
			json!({"rowVersion":1,"items":[{"id":p["items"][1]["id"],"liedId":b},{"id":entry,"liedId":a}]}),
		)
		.await;
	assert_eq!(s, 200, "{reordered}");
	assert_eq!(reordered["programme"]["items"][1]["id"], entry.to_string());
	let err = sqlx::query(r#"DELETE FROM programme_items WHERE "Id"=$1"#)
		.bind(entry)
		.execute(&app.pool)
		.await
		.unwrap_err();
	assert_eq!(
		err.as_database_error().unwrap().code().as_deref(),
		Some("23503")
	);
	assert_eq!(
		err.as_database_error().unwrap().constraint(),
		Some("FK_recording_passages_programme_items_ProgrammeItemId")
	);
	// Published timestamp mismatch withholds member values, never editor values.
	sqlx::query(r#"UPDATE recordings SET "PublishedAt"=now() WHERE "Id"=$1"#)
		.bind(recording)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(r#"UPDATE recording_passages SET "PlaybackDateiId"=NULL WHERE "Id"=$1"#)
		.bind(mark)
		.execute(&app.pool)
		.await
		.unwrap();
	let member_history = member.get(&history).await.1;
	let r = &member_history["auftritte"][0]["recordings"][0];
	assert_eq!(r["timestampState"], "needsReview");
	assert!(r["startSeconds"].is_null());
	assert!(r["endSeconds"].is_null());
	assert_eq!(r["isPublished"], true);
	assert_eq!(r["passageId"], mark.to_string());
	let editor_history = e.get(&history).await.1;
	assert_eq!(
		editor_history["auftritte"][0]["recordings"][0]["startSeconds"],
		10.0
	);
	// An existing marked entry may name another Lied; no invented guard.
	let (s, b) = e
		.request(
			"PUT",
			&path,
			json!({"rowVersion":2,"items":[{"id":p["items"][1]["id"],"liedId":a},{"id":entry,"liedId":b}]}),
		)
		.await;
	assert_eq!(s, 200, "{b}");
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL two-origin concurrency and unique positions"]
async fn concurrent_reorders_commit_once_and_preserve_attribution_event_token_and_contiguous_unique_slots()
 {
	let app = App::new().await;
	let mut e = app.admin().await;
	let mut second = app.sign_in("redaktion@liedertafel.test").await;
	let a = lied(&mut e, "A").await;
	let b = lied(&mut e, "B").await;
	let id = event(&mut e, 2099).await;
	let path = format!("/api/events/{id}/programme/items");
	let (_, p) = e
		.request("PUT", &path, json!({"items":[{"liedId":a},{"liedId":b}]}))
		.await;
	let p = &p["programme"];
	second.router = App::router(
		app.pool.clone(),
		app.mail.clone(),
		app.time.clone(),
		archive_server::auth::AuthConfig::development(),
	);
	let change = json!({"rowVersion":1,"items":[{"id":p["items"][1]["id"],"liedId":b},{"id":p["items"][0]["id"],"liedId":a},{"liedId":a},{"liedId":b}]});
	let (x, y) = tokio::join!(
		e.request("PUT", &path, change.clone()),
		second.request("PUT", &path, change)
	);
	let mut statuses = vec![x.0.as_u16(), y.0.as_u16()];
	statuses.sort();
	assert_eq!(statuses, [200, 409]);
	let detail = e.get(&format!("/api/events/{id}")).await.1;
	assert_eq!(detail["event"]["rowVersion"], 0);
	assert_eq!(detail["event"]["programme"]["rowVersion"], 2);
	assert_eq!(
		detail["event"]["programme"]["items"]
			.as_array()
			.unwrap()
			.iter()
			.map(|i| i["position"].as_i64().unwrap())
			.collect::<Vec<_>>(),
		[1, 2, 3, 4]
	);
	let p_id = Uuid::parse_str(p["id"].as_str().unwrap()).unwrap();
	let error = sqlx::query(
		r#"INSERT INTO programme_items ("Id","ProgrammeId","Position","LiedId") VALUES ($1,$2,1,$3)"#,
	)
	.bind(Uuid::new_v4())
	.bind(p_id)
	.bind(Uuid::parse_str(a.as_str().unwrap()).unwrap())
	.execute(&app.pool)
	.await
	.unwrap_err();
	assert_eq!(
		error.as_database_error().unwrap().code().as_deref(),
		Some("23505")
	);
	let before_event = e.get(&format!("/api/events/{id}")).await.1;
	*app.time.0.lock().unwrap() += chrono::Duration::seconds(1);
	let (s,saved)=second.request("PUT",&path,json!({"rowVersion":2,"items":before_event["event"]["programme"]["items"].as_array().unwrap().iter().map(|i|json!({"id":i["id"],"liedId":i["liedId"]})).collect::<Vec<_>>()})).await;
	assert_eq!(s, 200, "{saved}");
	let (creator, updater): (Uuid, Uuid) = sqlx::query_as(
		r#"SELECT "CreatedByAccountId","UpdatedByAccountId" FROM programmes WHERE "Id"=$1"#,
	)
	.bind(p_id)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_ne!(creator, updater);
	let after = e.get(&format!("/api/events/{id}")).await.1;
	assert_eq!(
		before_event["event"]["updatedAt"],
		after["event"]["updatedAt"]
	);
	assert_eq!(after["event"]["rowVersion"], 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL Vienna precision-aware upcoming"]
async fn upcoming_uses_vienna_today_true_precision_coarse_first_unknown_last_and_hides_empty_or_trashed()
 {
	let app = App::new().await;
	let mut e = app.admin().await;
	let a = lied(&mut e, "A").await;
	*app.time.0.lock().unwrap() = "2026-11-09T23:30:00Z".parse().unwrap(); // Vienna Nov 10
	let mut expected = Vec::new();
	for (title, y, m, d, included) in [
		("Year", Some(2026), None, None, true),
		("Month", Some(2026), Some(11), None, true),
		("Today", Some(2026), Some(11), Some(10), true),
		("Tomorrow", Some(2026), Some(11), Some(11), true),
		("Unknown", None, None, None, true),
		("Yesterday", Some(2026), Some(11), Some(9), false),
		("Last month", Some(2026), Some(10), None, false),
		("Last year", Some(2025), None, None, false),
	] {
		let (s, b) = e
			.post(
				"/api/events",
				json!({"kind":"other","title":title,"dateYear":y,"dateMonth":m,"dateDay":d}),
			)
			.await;
		assert_eq!(s, 201);
		let id = b["event"]["id"].as_str().unwrap();
		assert_eq!(
			e.request(
				"PUT",
				&format!("/api/events/{id}/programme/items"),
				json!({"items":[{"liedId":a}]})
			)
			.await
			.0,
			200
		);
		if included {
			expected.push(title);
		}
	}
	let empty = event(&mut e, 2099).await;
	assert_eq!(
		e.request(
			"PUT",
			&format!("/api/events/{empty}/programme/items"),
			json!({"items":[]})
		)
		.await
		.0,
		200
	);
	let trashed = event(&mut e, 2099).await;
	assert_eq!(
		e.request(
			"PUT",
			&format!("/api/events/{trashed}/programme/items"),
			json!({"items":[{"liedId":a}]})
		)
		.await
		.0,
		200
	);
	assert_eq!(
		e.post(
			&format!("/api/events/{trashed}/delete"),
			json!({"rowVersion":0})
		)
		.await
		.0,
		204
	);
	let (s, b) = e.get("/api/programmes").await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(
		b["programmes"]
			.as_array()
			.unwrap()
			.iter()
			.map(|p| p["eventTitle"].as_str().unwrap())
			.collect::<Vec<_>>(),
		expected
	);
	app.close().await;
}
