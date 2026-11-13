// Frozen EventsApiTests.cs at the HTTP/real PostgreSQL seam.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::json;
use support::App;

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn saved_auftritt_is_visible_to_members_without_a_publish_step() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let (status, body) = editor.post("/api/events", json!({"kind":"concert","title":"Frühjahrskonzert","dateYear":1950,"dateMonth":5,"dateDay":12,"notes":"Hinweise","sourceNote":"Programmheft"})).await;
	assert_eq!(status, 201, "{body}");
	let event = &body["event"];
	assert_eq!(event["rowVersion"], 0);
	assert_eq!(event["programme"], serde_json::Value::Null);
	assert_eq!(event["documents"], json!([]));
	assert_eq!(event["liedAufnahmen"], json!([]));
	assert_eq!(event["dateDisplay"], "12. Mai 1950");
	let path = format!("/api/events/{}", event["id"].as_str().unwrap());
	let (status, detail) = member.get(&path).await;
	assert_eq!(status, 200);
	assert_eq!(detail, body);
	assert!(detail["event"].get("published").is_none());
	assert_eq!(member.get("/api/events").await.1["total"], 1);
	for gone in ["publish", "unpublish"] {
		assert_eq!(
			editor.post(&format!("{path}/{gone}"), json!({})).await.0,
			404
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn patch_attributes_second_editor_validates_before_stale_clears_optionals_and_replaces_date()
{
	let app = App::new().await;
	let mut first = app.admin().await;
	let mut second = app.sign_in("redaktion@liedertafel.test").await;
	let (s,b)=first.post("/api/events",json!({"kind":"concert","title":"Alter Titel","venue":"Halle","dateYear":1950,"dateMonth":5,"dateDay":12,"startTime":"19:30","notes":"Notiz","sourceNote":"Quelle"})).await;
	assert_eq!(s, 201);
	let id = b["event"]["id"].as_str().unwrap();
	let path = format!("/api/events/{id}");
	let creator: uuid::Uuid =
		sqlx::query_scalar(r#"SELECT "CreatedByAccountId" FROM events WHERE "Id"=$1"#)
			.bind(uuid::Uuid::parse_str(id).unwrap())
			.fetch_one(&app.pool)
			.await
			.unwrap();
	*app.time.0.lock().unwrap() += chrono::Duration::seconds(1);
	let (s,b)=second.request("PATCH",&path,json!({"title":"Neuer Titel","venue":"Stadtpfarrkirche","date":{"year":1950,"month":5,"day":12,"approximate":false},"startTime":"18:15","expectedRowVersion":"0"})).await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["event"]["title"], "Neuer Titel");
	assert_eq!(b["event"]["dateDisplay"], "12. Mai 1950");
	assert_eq!(b["event"]["startTime"], "18:15");
	assert_eq!(b["event"]["rowVersion"], 1);
	let (created, updated): (uuid::Uuid, uuid::Uuid) = sqlx::query_as(
		r#"SELECT "CreatedByAccountId","UpdatedByAccountId" FROM events WHERE "Id"=$1"#,
	)
	.bind(uuid::Uuid::parse_str(id).unwrap())
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_eq!(created, creator);
	assert_ne!(created, updated);
	let (s, b) = second
		.request("PATCH", &path, json!({"title":"","expectedRowVersion":0}))
		.await;
	assert_eq!(s, 400);
	assert_eq!(b["title"], "Der Titel ist erforderlich.");
	assert_eq!(
		second
			.request("PATCH", &path, json!({"expectedRowVersion":0}))
			.await
			.0,
		409
	);
	let (s, b) = second
		.request(
			"PATCH",
			&path,
			json!({"venue":"","notes":" ","sourceNote":"","startTime":"","date":{"approximate":true}}),
		)
		.await;
	assert_eq!(s, 200);
	for key in [
		"venue",
		"notes",
		"sourceNote",
		"startTime",
		"dateYear",
		"dateMonth",
		"dateDay",
	] {
		assert!(b["event"][key].is_null(), "{key}");
	}
	assert_eq!(b["event"]["dateApproximate"], true);
	assert_eq!(b["event"]["dateDisplay"], "Datum unbekannt");
	let (s, b) = second
		.request(
			"PATCH",
			&path,
			json!({"date":{"year":1951,"approximate":true}}),
		)
		.await;
	assert_eq!(s, 200);
	assert_eq!(b["event"]["dateDisplay"], "um 1951");
	let (s, b) = second.raw("PATCH", &path, "null").await;
	assert_eq!(s, 200);
	assert_eq!(b["event"]["rowVersion"], 4);
	assert_eq!(b["event"]["dateYear"], 1951);
	let (s, b) = second.request("PATCH", &path, json!({"date":{}})).await;
	assert_eq!(s, 200);
	assert!(b["event"]["dateYear"].is_null());
	assert_eq!(b["event"]["dateApproximate"], false);
	let mut member = app.member().await;
	assert_eq!(
		member
			.post("/api/events", json!({"title":"A","kind":"concert"}))
			.await
			.0,
		403
	);
	assert_eq!(member.request("PATCH", &path, json!({})).await.0, 403);
	let mut anon = app.client();
	anon.csrf().await;
	assert_eq!(
		anon.post("/api/events", json!({"title":"A","kind":"concert"}))
			.await
			.0,
		401
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn list_precision_order_unfiltered_year_summary_query_binding_and_all_date_displays() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let mut ids = Vec::new();
	for (y, m, d, a, kind, display) in [
		(
			Some(1950),
			Some(1),
			Some(1),
			false,
			"concert",
			"1. Januar 1950",
		),
		(
			Some(1950),
			Some(12),
			None,
			true,
			"festival",
			"um Dezember 1950",
		),
		(Some(1950), None, None, true, "service", "um 1950"),
		(
			Some(1949),
			Some(11),
			Some(11),
			false,
			"concert",
			"11. November 1949",
		),
		(None, None, None, true, "wedding", "Datum unbekannt"),
	] {
		let (s, b) = e
			.post(
				"/api/events",
				json!({"title":display,"kind":kind,"dateYear":y,"dateMonth":m,"dateDay":d,"dateApproximate":a}),
			)
			.await;
		assert_eq!(s, 201, "{b}");
		assert_eq!(b["event"]["dateDisplay"], display);
		ids.push(b["event"]["id"].clone());
	}
	let (_, b) = e.get("/api/events").await;
	assert_eq!(
		b["events"]
			.as_array()
			.unwrap()
			.iter()
			.map(|r| r["id"].clone())
			.collect::<Vec<_>>(),
		ids
	);
	let years = json!([{"year":1950,"count":3},{"year":1949,"count":1},{"year":null,"count":1}]);
	assert_eq!(b["years"], years);
	for query in ["year=1950", "YeAr=%20%2B1950%20", "year=&YEAR=1950"] {
		let (s, b) = e.get(&format!("/api/events?{query}")).await;
		assert_eq!(s, 200, "{b}");
		assert_eq!(b["total"], 3);
		assert_eq!(b["years"], years);
	}
	for query in [
		"year=",
		"year=1950&year=1951",
		"year=1950.0",
		"year=1e3",
		"year=2147483648",
		"kind=Concert",
		"kind=gala",
		"kind=concert&kind=service",
	] {
		assert_eq!(
			e.get(&format!("/api/events?{query}")).await.0,
			400,
			"{query}"
		);
	}
	assert_eq!(
		e.get("/api/events?KiNd=%20concert%20&kind=").await.1["total"],
		2
	);
	for (y, m, d, a, expected) in [
		(Some(1950), Some(5), Some(12), false, "12. Mai 1950"),
		(Some(1950), Some(5), Some(12), true, "ca. 12. Mai 1950"),
		(Some(1950), Some(5), None, false, "Mai 1950"),
		(Some(1950), Some(5), None, true, "um Mai 1950"),
		(Some(1950), None, None, false, "1950"),
		(Some(1950), None, None, true, "um 1950"),
		(None, None, None, false, "Datum unbekannt"),
	] {
		let (s,b)=e.post("/api/events",json!({"kind":"other","title":"Datum","dateYear":y,"dateMonth":m,"dateDay":d,"dateApproximate":a})).await;
		assert_eq!(s, 201);
		assert_eq!(b["event"]["dateDisplay"], expected);
	}
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL"]
async fn all_german_validation_problems_utf16_leap_dates_and_csrf() {
	let app = App::new().await;
	let mut e = app.admin().await;
	for (extra, title) in [
		(json!({"title":""}), "Der Titel ist erforderlich."),
		(json!({"title":"   "}), "Der Titel ist erforderlich."),
		(json!({"title":"x".repeat(201)}), "Der Titel ist zu lang."),
		(json!({"title":"😀".repeat(101)}), "Der Titel ist zu lang."),
		(json!({"dateMonth":5}), "Das Datum ist unvollständig."),
		(
			json!({"dateYear":1950,"dateDay":12}),
			"Das Datum ist unvollständig.",
		),
		(
			json!({"dateYear":1950,"dateMonth":2,"dateDay":30}),
			"Das Datum passt nicht zum angegebenen Monat.",
		),
		(
			json!({"dateYear":1900,"dateMonth":2,"dateDay":29}),
			"Das Datum passt nicht zum angegebenen Monat.",
		),
		(
			json!({"dateYear":1750}),
			"Das Jahr liegt außerhalb des möglichen Bereichs.",
		),
		(
			json!({"dateYear":2101}),
			"Das Jahr liegt außerhalb des möglichen Bereichs.",
		),
		(
			json!({"startTime":"7 Uhr"}),
			"Die Uhrzeit muss im Format HH:MM angegeben werden.",
		),
		(
			json!({"startTime":"7:30"}),
			"Die Uhrzeit muss im Format HH:MM angegeben werden.",
		),
		(json!({"kind":"gala"}), "Unbekannter Auftrittstyp."),
		(
			json!({"venue":"x".repeat(201)}),
			"Der Veranstaltungsort ist zu lang.",
		),
		(
			json!({"notes":"x".repeat(2001)}),
			"Die Hinweise sind zu lang.",
		),
		(
			json!({"sourceNote":"x".repeat(501)}),
			"Die Quellenangabe ist zu lang.",
		),
	] {
		let mut body = json!({"kind":"concert","title":"Ok"});
		body.as_object_mut()
			.unwrap()
			.extend(extra.as_object().unwrap().clone());
		let (s, b) = e.post("/api/events", body).await;
		assert_eq!(s, 400, "{b}");
		assert_eq!(b["title"], title);
	}
	assert_eq!(e.get("/api/events").await.1["total"], 0);
	assert_eq!(e.post("/api/events",json!({"kind":" concert ","title":"😀".repeat(100),"dateYear":2000,"dateMonth":2,"dateDay":29,"startTime":" 23:59 "})).await.0,201);
	e.token.clear();
	assert_eq!(
		e.post("/api/events", json!({"title":"A","kind":"other"}))
			.await
			.0,
		400
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL seeded unknown-year ordering"]
async fn unknown_years_compare_by_guid_even_if_seeded_parts_are_present() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let mut ids = Vec::new();
	for _ in 0..2 {
		let (_, b) = e
			.post("/api/events", json!({"kind":"other","title":"Unknown"}))
			.await;
		ids.push(uuid::Uuid::parse_str(b["event"]["id"].as_str().unwrap()).unwrap());
	}
	ids.sort();
	sqlx::query(
		r#"UPDATE events SET "DateMonth"=5,"DateDay"=12,"DateApproximate"=true WHERE "Id"=$1"#,
	)
	.bind(ids[1])
	.execute(&app.pool)
	.await
	.unwrap();
	let (_, b) = e.get("/api/events").await;
	assert_eq!(b["events"][0]["id"], ids[0].to_string());
	assert_eq!(b["events"][1]["dateDisplay"], "Datum unbekannt");
	app.close().await;
}
