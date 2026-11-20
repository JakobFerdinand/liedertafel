//! R1 actual-frozen independent fixture → real PostgreSQL/HTTP controls.
// Synthetic database/process fixtures must fail loudly on setup or assertion errors.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::{Value, json};
use sqlx::ConnectOptions;
use support::App;
use uuid::Uuid;
fn id(prefix: i32, n: i32) -> Uuid {
	Uuid::parse_str(&format!("{prefix:08}-0000-0000-0000-{n:012}")).unwrap()
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL culture equality before secondary keys"]
async fn culture_equal_spellings_use_date_and_guid_ties_keep_raw_text_and_ordinal_distinct_names() {
	let app = App::new().await;
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let cases: Vec<Value> =
		serde_json::from_str(include_str!("../../../tests/fixtures/culture-frozen.json")).unwrap();
	for (group, case) in cases.iter().enumerate() {
		let offset = (group as i32 + 1) * 100;
		let event = id(1, offset);
		let common = id(2, offset);
		let programme = id(4, offset);
		let entry = id(5, offset);
		sqlx::query(r#"INSERT INTO events ("Id","Title","Kind","DateApproximate","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,'Culture','concert',false,now(),now(),$2,$2,0)"#).bind(event).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
		sqlx::query(r#"INSERT INTO lieder ("Id","Title","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,'Tie Lied',now(),now(),$2,$2,1)"#).bind(common).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
		sqlx::query(r#"INSERT INTO programmes ("Id","EventId","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,now(),now(),$3,$3,1)"#).bind(programme).bind(event).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
		sqlx::query(
			r#"INSERT INTO programme_items ("Id","ProgrammeId","LiedId","Position") VALUES ($1,$2,$3,1)"#,
		)
		.bind(entry)
		.bind(programme)
		.bind(common)
		.execute(&app.pool)
		.await
		.unwrap();
		let left = case["left"].as_str().unwrap();
		let right = case["right"].as_str().unwrap();
		let titles = [left, right, left, right, right, left];
		let seconds = [1, 0, 0, 0, 1, 0];
		let comparison:i32=sqlx::query_scalar("SELECT CASE WHEN ($1::text COLLATE public.archive_culture)=($2::text COLLATE public.archive_culture) THEN 0 WHEN ($1::text COLLATE public.archive_culture)<($2::text COLLATE public.archive_culture) THEN -1 ELSE 1 END").bind(left).bind(right).fetch_one(&app.pool).await.unwrap();
		assert_eq!(json!(comparison), case["compare"]);
		for n in 1..=6 {
			let song = id(2, offset + n);
			let file = id(3, offset + n);
			let recording = id(6, offset + n);
			let passage = id(7, offset + n);
			let title = titles[n as usize - 1];
			let at: chrono::DateTime<chrono::Utc> = "2026-11-10T00:00:00Z".parse().unwrap();
			sqlx::query(r#"INSERT INTO lieder ("Id","Title","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,now(),now(),$3,$3,1)"#).bind(song).bind(title).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
			sqlx::query(r#"INSERT INTO dateien ("Id","LiedId","RecordedAtEventId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","CreatedAt","CreatedByAccountId","RowVersion") VALUES ($1,$2,$3,'aufnahme',false,$4,'audio/mpeg',10,$5,$6,1)"#).bind(file).bind(song).bind(event).bind(format!("dateien/culture-{offset}-{n}")).bind(at+chrono::Duration::seconds(seconds[n as usize-1])).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
			sqlx::query(r#"INSERT INTO recordings ("Id","EventId","Label","Kind","DownloadEnabled","PublishedAt","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,'audio',false,now(),now(),now(),$4,$4,1)"#).bind(recording).bind(event).bind(title).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
			sqlx::query(r#"INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,1,5,now(),now(),$5,$5,1)"#).bind(passage).bind(recording).bind(entry).bind(event).bind(Uuid::nil()).execute(&app.pool).await.unwrap();
		}
		let (status, detail) = member.get(&format!("/api/events/{event}")).await;
		assert_eq!(status, 200, "{detail}");
		let rows = detail["event"]["liedAufnahmen"].as_array().unwrap();
		let indices = |rows: &[Value], key: &str| {
			rows.iter()
				.map(|r| r[key].as_str().unwrap()[24..].parse::<i32>().unwrap() - offset)
				.collect::<Vec<_>>()
		};
		assert_eq!(
			json!(indices(rows, "id")),
			case["fileOrder"],
			"case {group} titles"
		);
		let (status, history) = member.get(&format!("/api/lieder/{common}/historie")).await;
		assert_eq!(status, 200, "{history}");
		assert_eq!(
			json!(indices(
				history["auftritte"][0]["recordings"].as_array().unwrap(),
				"passageId"
			)),
			case["passageOrder"],
			"case {group} labels"
		);
		let stored: Vec<String> =
			sqlx::query_scalar(r#"SELECT "Title" FROM lieder WHERE "Id"=ANY($1) ORDER BY "Id""#)
				.bind((1..=6).map(|n| id(2, offset + n)).collect::<Vec<_>>())
				.fetch_all(&app.pool)
				.await
				.unwrap();
		assert_eq!(stored, titles);
		let labels: Vec<String> = sqlx::query_scalar(
			r#"SELECT "Label" FROM recordings WHERE "Id"=ANY($1) ORDER BY "Id""#,
		)
		.bind((1..=6).map(|n| id(6, offset + n)).collect::<Vec<_>>())
		.fetch_all(&app.pool)
		.await
		.unwrap();
		assert_eq!(labels, titles);
		let (status, blocked) = editor
			.request(
				"PUT",
				&format!("/api/events/{event}/programme/items"),
				json!({"rowVersion":1,"items":[]}),
			)
			.await;
		assert_eq!(status, 409);
		let message = blocked["title"].as_str().unwrap();
		assert!(message.contains(&format!("„Tie Lied“ in „{left}“")));
		assert!(message.contains(&format!("„Tie Lied“ in „{right}“")));
		assert_eq!(
			message.matches("„Tie Lied“").count(),
			case["ordinalDistinct"].as_u64().unwrap() as usize
		);
	}
	// Reconstruct this test's prior Rust head (1..3) by removing only the new
	// collation/bookkeeping, then drive the real explicit native upgrade.
	let before = stored_snapshot(&app.pool).await;
	let old_history: Value = sqlx::query_scalar(
		"SELECT jsonb_agg(to_jsonb(m) ORDER BY version) FROM _sqlx_migrations m WHERE version<4",
	)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	sqlx::query("DROP COLLATION public.archive_culture")
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		sqlx::query("DELETE FROM _sqlx_migrations WHERE version=4")
			.execute(&app.pool)
			.await
			.unwrap()
			.rows_affected(),
		1
	);
	for _ in 0..2 {
		let output = tokio::time::timeout(
			std::time::Duration::from_secs(30),
			tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
				.arg("--migrate")
				.env("ARCHIVE_ENVIRONMENT", "Production")
				.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
				.env(
					"ConnectionStrings__archive-migrations",
					app.pool.connect_options().to_url_lossy().as_str(),
				)
				.kill_on_drop(true)
				.output(),
		)
		.await
		.unwrap()
		.unwrap();
		assert!(
			output.status.success(),
			"{}",
			String::from_utf8_lossy(&output.stderr)
		);
		assert_eq!(before, stored_snapshot(&app.pool).await);
		let history: Value = sqlx::query_scalar(
			"SELECT jsonb_agg(to_jsonb(m) ORDER BY version) FROM _sqlx_migrations m WHERE version<4",
		)
		.fetch_one(&app.pool)
		.await
		.unwrap();
		assert_eq!(history, old_history);
	}
	app.close().await;
}
async fn stored_snapshot(pool: &sqlx::PgPool) -> Value {
	sqlx::query_scalar(r#"SELECT jsonb_build_object('users',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM "AspNetUsers" t),'events',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM events t),'lieder',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM lieder t),'dateien',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM dateien t),'programmes',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM programmes t),'items',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM programme_items t),'recordings',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM recordings t),'marks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM recording_passages t))"#).fetch_one(pool).await.unwrap()
}
