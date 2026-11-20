//! R1: raw HTTP + real PostgreSQL attribution with time advancing on every read.
//! A fixed clock cannot detect multiple captures within the same operation.
// Integration-only fixture construction and persisted-contract assertions fail loudly.
#![allow(clippy::unwrap_used, clippy::expect_used)]
mod support;
use archive_server::{
	Origin, Settings,
	auth::{Auth, AuthConfig, Clock},
	build_router,
};
use chrono::{DateTime, Duration, Utc};
use std::{
	path::PathBuf,
	sync::{
		Arc,
		atomic::{AtomicI64, Ordering},
	},
};
use support::recordings::Scenario;
use uuid::Uuid;

struct ProgressingClock {
	base: DateTime<Utc>,
	ticks: AtomicI64,
}
impl Clock for ProgressingClock {
	fn now(&self) -> DateTime<Utc> {
		// Milliseconds survive PostgreSQL's microsecond precision. Authentication
		// uses this same clock; assertions concern persisted attribution, not a
		// private number/order of clock calls inside auth or domain code.
		self.base + Duration::milliseconds(self.ticks.fetch_add(1, Ordering::SeqCst))
	}
}
fn progress_time(s: &mut Scenario) {
	let clock = Arc::new(ProgressingClock {
		base: *s.app.time.0.lock().unwrap() + Duration::seconds(1),
		ticks: AtomicI64::new(0),
	});
	let auth = Auth::new(
		s.app.pool.clone(),
		AuthConfig::development(),
		s.app.mail.clone(),
		clock,
	)
	.unwrap();
	let router = build_router(Arc::new(
		Origin::new(Settings::new(true, PathBuf::new(), 0))
			.with_auth(auth)
			.with_storage(s.storage.clone(), Duration::minutes(15)),
	));
	s.editor.router = router.clone();
	s.member.router = router;
}
type Stamps = (
	Option<DateTime<Utc>>,
	Option<Uuid>,
	DateTime<Utc>,
	Uuid,
	i64,
);
async fn stamps(s: &Scenario) -> Stamps {
	sqlx::query_as(r#"SELECT "PublishedAt","PublishedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion" FROM recordings WHERE "Id"=$1"#)
		.bind(s.recording).fetch_one(&s.app.pool).await.unwrap()
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL progressing-clock publication attribution and no-op/stale controls"]
async fn publication_reuses_one_timestamp_without_restamping_noops_or_existing_publication() {
	let mut s = Scenario::new().await;
	progress_time(&mut s);
	let path = format!("/api/recordings/{}", s.recording);
	let before = stamps(&s).await;
	assert!(before.0.is_none());
	let (status, body) = s
		.editor
		.raw("PATCH", &path, r#"{"isPublished":true}"#)
		.await;
	assert_eq!(status, 200, "{body}");
	let first = stamps(&s).await;
	assert_eq!(
		first.0,
		Some(first.2),
		"one publication operation has one attribution timestamp"
	);
	assert_eq!(first.1, Some(first.3));
	assert_eq!(first.4, before.4 + 1);
	assert_eq!(
		body["recording"]["editor"]["publishedAt"]
			.as_str()
			.unwrap()
			.parse::<DateTime<Utc>>()
			.unwrap(),
		first.2
	);
	assert_eq!(
		s.editor
			.raw("PATCH", &path, r#"{"isPublished":true}"#)
			.await
			.0,
		200
	);
	assert_eq!(stamps(&s).await, first, "already-published save is a no-op");
	let (status, body) = s
		.editor
		.raw(
			"PATCH",
			&path,
			&format!(r#"{{"isPublished":true,"expectedVersion":{}}}"#, before.4),
		)
		.await;
	assert_eq!(status, 409, "{body}");
	assert_eq!(
		stamps(&s).await,
		first,
		"stale no-op publication is still refused before mutation"
	);

	assert_eq!(
		s.editor
			.raw("PATCH", &path, r#"{"isPublished":true,"label":"Geändert"}"#)
			.await
			.0,
		200
	);
	let relabelled = stamps(&s).await;
	assert_eq!(relabelled.0, first.0, "metadata save does not republish");
	assert_eq!(relabelled.1, first.1);
	assert!(relabelled.2 > first.2);
	assert_eq!(relabelled.4, first.4 + 1);
	assert_eq!(
		s.editor
			.raw(
				"PATCH",
				&path,
				r#"{"label":" Geändert ","isPublished":true,"downloadEnabled":false}"#
			)
			.await
			.0,
		200
	);
	assert_eq!(
		stamps(&s).await,
		relabelled,
		"normalised metadata no-op keeps attribution"
	);

	assert_eq!(
		s.editor
			.raw("PATCH", &path, r#"{"isPublished":false}"#)
			.await
			.0,
		200
	);
	let withdrawn = stamps(&s).await;
	assert!(withdrawn.0.is_none());
	assert!(withdrawn.1.is_none());
	assert!(withdrawn.2 > relabelled.2);
	assert_eq!(withdrawn.4, relabelled.4 + 1);
	assert_eq!(
		s.editor
			.raw("PATCH", &path, r#"{"isPublished":false}"#)
			.await
			.0,
		200
	);
	assert_eq!(stamps(&s).await, withdrawn);
	let (status, body) = s
		.editor
		.raw(
			"PATCH",
			&path,
			&format!(r#"{{"isPublished":true,"expectedVersion":{}}}"#, first.4),
		)
		.await;
	assert_eq!(status, 409, "{body}");
	assert_eq!(
		body["title"],
		"Die Aufnahme wurde zwischenzeitlich geändert."
	);
	assert_eq!(
		stamps(&s).await,
		withdrawn,
		"stale publication writes nothing"
	);
	assert_eq!(
		s.editor
			.raw("PATCH", &path, r#"{"isPublished":true}"#)
			.await
			.0,
		200
	);
	let republished = stamps(&s).await;
	assert_eq!(republished.0, Some(republished.2));
	assert!(republished.2 > withdrawn.2);
	assert_eq!(republished.4, withdrawn.4 + 1);
	s.app.close().await;
}

type PassageStamps = (Uuid, Option<Uuid>, DateTime<Utc>, Uuid, i64);
async fn passage_stamps(s: &Scenario) -> Vec<PassageStamps> {
	sqlx::query_as(r#"SELECT "Id","PlaybackDateiId","UpdatedAt","UpdatedByAccountId","RowVersion" FROM recording_passages WHERE "RecordingId"=$1 ORDER BY "Id""#)
		.bind(s.recording).fetch_all(&s.app.pool).await.unwrap()
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL progressing-clock multi-passage review attribution and selected atomicity"]
async fn selected_review_reuses_one_timestamp_for_all_changed_rows_and_keeps_other_stamps() {
	let mut s = Scenario::new().await;
	let p = s.add(0, 10.0, 20.0).await;
	let q = s.add(1, 30.0, 40.0).await;
	let unselected = s.add(2, 50.0, 60.0).await;
	s.replace().await;
	progress_time(&mut s);
	let path = format!("{}/review", s.path());
	let initial = passage_stamps(&s).await;
	let excluded = Uuid::parse_str(unselected["id"].as_str().unwrap()).unwrap();
	let body = format!(
		r#"{{"expectedPlaybackDateiId":{},"passages":[{{"id":{},"expectedVersion":1}},{{"id":{},"expectedVersion":1}}]}}"#,
		s.revision, p["id"], q["id"]
	);
	let (status, answer) = s.editor.raw("POST", &path, &body).await;
	assert_eq!(status, 200, "{answer}");
	let passages = answer["passages"].as_array().unwrap();
	assert_eq!(passages.len(), 3);
	assert_eq!(passages[0]["timestampState"], "current");
	assert_eq!(passages[1]["timestampState"], "current");
	assert_eq!(
		passages[0]["editor"]["updatedAt"], passages[1]["editor"]["updatedAt"],
		"one selected review has one attribution timestamp"
	);
	assert_eq!(passages[2]["timestampState"], "needsReview");
	let confirmed = passage_stamps(&s).await;
	assert_eq!(
		confirmed.iter().find(|r| r.0 == excluded),
		initial.iter().find(|r| r.0 == excluded),
		"unselected row is not confirmed or restamped"
	);
	let changed: Vec<_> = confirmed.iter().filter(|r| r.0 != excluded).collect();
	assert_eq!(changed[0].2, changed[1].2);
	assert_eq!(changed[0].3, changed[1].3);
	for value in &passages[..2] {
		let id = Uuid::parse_str(value["id"].as_str().unwrap()).unwrap();
		let row = confirmed.iter().find(|r| r.0 == id).unwrap();
		assert_eq!(
			row.1,
			Some(Uuid::parse_str(s.revision.as_str().unwrap()).unwrap())
		);
		assert_eq!(
			row.2,
			value["editor"]["updatedAt"]
				.as_str()
				.unwrap()
				.parse::<DateTime<Utc>>()
				.unwrap()
		);
		assert_eq!(row.4, 2);
	}
	let repeat = format!(
		r#"{{"expectedPlaybackDateiId":{},"passages":[{{"id":{},"expectedVersion":2}},{{"id":{},"expectedVersion":2}}]}}"#,
		s.revision, p["id"], q["id"]
	);
	let (status, repeated) = s.editor.raw("POST", &path, &repeat).await;
	assert_eq!(status, 200, "{repeated}");
	assert_eq!(
		repeated, answer,
		"already-current selected review is a no-op even while time progresses"
	);
	assert_eq!(passage_stamps(&s).await, confirmed);

	s.replace().await;
	let before_stale = s.view(false).await;
	let before_stamps = passage_stamps(&s).await;
	let stale = format!(
		r#"{{"expectedPlaybackDateiId":{},"passages":[{{"id":{},"expectedVersion":2}},{{"id":{},"expectedVersion":1}}]}}"#,
		s.revision, p["id"], q["id"]
	);
	let (status, refused) = s.editor.raw("POST", &path, &stale).await;
	assert_eq!(status, 409, "{refused}");
	assert_eq!(
		refused["title"],
		"Die Zeitmarke wurde zwischenzeitlich geändert."
	);
	assert_eq!(
		s.view(false).await,
		before_stale,
		"stale selection cannot partially confirm the first row"
	);
	assert_eq!(passage_stamps(&s).await, before_stamps);
	let current = format!(
		r#"{{"expectedPlaybackDateiId":{},"passages":[{{"id":{},"expectedVersion":2}},{{"id":{},"expectedVersion":2}}]}}"#,
		s.revision, p["id"], q["id"]
	);
	let (status, updated) = s.editor.raw("POST", &path, &current).await;
	assert_eq!(status, 200, "{updated}");
	assert_eq!(
		updated["passages"][0]["editor"]["updatedAt"],
		updated["passages"][1]["editor"]["updatedAt"]
	);
	assert_eq!(updated["passages"][0]["editor"]["version"], 3);
	assert_eq!(updated["passages"][1]["editor"]["version"], 3);
	let final_stamps = passage_stamps(&s).await;
	assert_eq!(
		final_stamps.iter().find(|r| r.0 == excluded),
		initial.iter().find(|r| r.0 == excluded)
	);
	assert!(
		final_stamps
			.iter()
			.filter(|r| r.0 != excluded)
			.all(|r| r.2 > changed[0].2)
	);
	s.app.close().await;
}
