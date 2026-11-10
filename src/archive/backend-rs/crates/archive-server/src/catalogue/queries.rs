//! Canonical live-row reads. Parent visibility belongs here, never in a provider.
use super::Lied;
use chrono::{DateTime, Utc};
use sqlx::PgPool;
use uuid::Uuid;

pub const DATEI_KINDS: [&str; 4] = ["noten", "uebe", "aufnahme", "sonstiges"];
const PARENTS: &str =
	r#" LEFT JOIN lieder l ON d."LiedId"=l."Id" LEFT JOIN events e ON d."EventId"=e."Id" "#;
const VISIBLE: &str = r#" d."DeletedAt" IS NULL AND (d."LiedId" IS NULL OR l."DeletedAt" IS NULL) AND (d."EventId" IS NULL OR e."DeletedAt" IS NULL) "#;

#[derive(Clone, Debug, sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
pub struct Datei {
	pub id: Uuid,
	pub lied_id: Option<Uuid>,
	pub event_id: Option<Uuid>,
	pub kind: String,
	pub is_partitur: bool,
	pub stimme: Option<String>,
	pub description: Option<String>,
	pub recorded_at_event_id: Option<Uuid>,
	pub blob_name: String,
	pub content_type: String,
	pub size_bytes: i64,
	pub original_file_name: Option<String>,
	pub created_at: DateTime<Utc>,
	pub row_version: i64,
	pub storage_tier: String,
	pub tier_changed_at: Option<DateTime<Utc>>,
}
impl Datei {
	pub fn summary(&self, recorded_at_event: serde_json::Value) -> serde_json::Value {
		serde_json::json!({"id":self.id,"kind":self.kind,"isPartitur":self.is_partitur,"stimme":self.stimme,"description":self.description,"fileName":self.original_file_name,"contentType":self.content_type,"sizeBytes":self.size_bytes,"createdAt":self.created_at,"rowVersion":self.row_version,"recordedAtEvent":recorded_at_event,"storageTier":self.storage_tier,"tierChangedAt":self.tier_changed_at})
	}
}

/// The DateiQueries.Visible predicate, including both parents. Optional selectors
/// narrow the SQL itself; callers never authorize by probing Blob storage.
pub async fn dateien(
	pool: &PgPool,
	lied_id: Option<Uuid>,
	id: Option<Uuid>,
) -> Result<Vec<Datei>, sqlx::Error> {
	let mut query = sqlx::QueryBuilder::<sqlx::Postgres>::new("SELECT d.* FROM dateien d ");
	query.push(PARENTS).push(" WHERE ").push(VISIBLE);
	if let Some(lied_id) = lied_id {
		query.push(r#" AND d."LiedId"="#).push_bind(lied_id);
	}
	if let Some(id) = id {
		query.push(r#" AND d."Id"="#).push_bind(id);
	}
	query.build_query_as::<Datei>().fetch_all(pool).await
}

pub(super) async fn noten_texts(pool: &PgPool) -> Result<Vec<(Uuid, String)>, sqlx::Error> {
	let mut query = sqlx::QueryBuilder::<sqlx::Postgres>::new(
		r#"SELECT d."LiedId",j."Text" FROM dateien d JOIN extraction_jobs j ON j."DateiId"=d."Id" "#,
	);
	query.push(PARENTS).push(" WHERE ").push(VISIBLE).push(
		r#" AND d."LiedId" IS NOT NULL AND d."Kind"='noten' AND j."Status"=2 AND j."Text" IS NOT NULL"#,
	);
	query
		.build_query_as::<(Uuid, String)>()
		.fetch_all(pool)
		.await
}

pub async fn lieder(pool: &PgPool) -> Result<Vec<Lied>, sqlx::Error> {
	sqlx::query_as::<_, Lied>(
		r#"SELECT * FROM lieder WHERE "DeletedAt" IS NULL ORDER BY "Title","Id""#,
	)
	.fetch_all(pool)
	.await
}
pub async fn lied(pool: &PgPool, id: Uuid) -> Result<Option<Lied>, sqlx::Error> {
	sqlx::query_as::<_, Lied>(r#"SELECT * FROM lieder WHERE "DeletedAt" IS NULL AND "Id"=$1"#)
		.bind(id)
		.fetch_optional(pool)
		.await
}

pub fn is_video(d: &Datei) -> bool {
	d.kind == "aufnahme"
		&& d.content_type
			.get(..6)
			.is_some_and(|s| s.eq_ignore_ascii_case("video/"))
}
pub async fn live_copy(pool: &PgPool, id: Uuid) -> Result<Option<Datei>, sqlx::Error> {
	sqlx::query_as::<_,Datei>(r#"SELECT d.* FROM video_jobs j JOIN dateien d ON j."PlaybackDateiId"=d."Id" WHERE j."DateiId"=$1 AND j."Status"=2 AND d."DeletedAt" IS NULL"#).bind(id).fetch_optional(pool).await
}
pub async fn copy_ids(pool: &PgPool, dateien: &[Datei]) -> Result<Vec<Uuid>, sqlx::Error> {
	let ids: Vec<_> = dateien
		.iter()
		.filter(|d| is_video(d))
		.map(|d| d.id)
		.collect();
	if ids.is_empty() {
		return Ok(vec![]);
	}
	sqlx::query_scalar(r#"SELECT "PlaybackDateiId" FROM video_jobs WHERE "DateiId"=ANY($1) AND "Status"=2 AND "PlaybackDateiId" IS NOT NULL"#).bind(ids).fetch_all(pool).await
}
pub async fn has_separate_playback(pool: &PgPool, datei: &Datei) -> Result<bool, sqlx::Error> {
	if is_video(datei) && live_copy(pool, datei.id).await?.is_some() {
		return Ok(true);
	}
	let slots:Vec<(String,Option<String>)>=sqlx::query_as(r#"SELECT r."Kind",d."ContentType" FROM recordings r LEFT JOIN dateien d ON d."Id"=r."PlaybackDateiId" AND d."DeletedAt" IS NULL WHERE r."OriginalDateiId"=$1"#).bind(datei.id).fetch_all(pool).await?;
	Ok(!slots.is_empty()
		&& slots
			.iter()
			.all(|(kind, content)| content.as_deref().is_some_and(|c| is_playable(c, kind))))
}
pub(crate) fn is_playable(content: &str, kind: &str) -> bool {
	let types: &[&str] = if kind == "video" {
		&["video/mp4", "video/webm"]
	} else {
		&["audio/mp4", "audio/webm", "audio/mpeg", "audio/wav"]
	};
	types.iter().any(|t| t.eq_ignore_ascii_case(content))
}

#[derive(sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
struct RecordedEvent {
	id: Uuid,
	title: String,
	date_year: Option<i32>,
	date_month: Option<i32>,
	date_day: Option<i32>,
	date_approximate: bool,
}
pub async fn recorded_events(
	pool: &PgPool,
	dateien: &[Datei],
) -> Result<std::collections::HashMap<Uuid, serde_json::Value>, sqlx::Error> {
	let ids: Vec<_> = dateien
		.iter()
		.filter_map(|d| d.recorded_at_event_id)
		.collect();
	let rows=sqlx::query_as::<_,RecordedEvent>(r#"SELECT "Id","Title","DateYear","DateMonth","DateDay","DateApproximate" FROM events WHERE "Id"=ANY($1) AND "DeletedAt" IS NULL"#).bind(ids).fetch_all(pool).await?;
	Ok(rows.into_iter().map(|e| (e.id,serde_json::json!({"id":e.id,"title":e.title,"dateDisplay":event_date_display(e.date_year,e.date_month,e.date_day,e.date_approximate)}))).collect())
}
/// Read-only EventDate.Display subset needed by a Lied's recorded-at link.
/// #225 owns the remaining date ordering/validation and event endpoints.
pub fn event_date_display(
	year: Option<i32>,
	month: Option<i32>,
	day: Option<i32>,
	approx: bool,
) -> String {
	const MONTHS: [&str; 12] = [
		"Januar",
		"Februar",
		"März",
		"April",
		"Mai",
		"Juni",
		"Juli",
		"August",
		"September",
		"Oktober",
		"November",
		"Dezember",
	];
	let Some(year) = year else {
		return "Datum unbekannt".into();
	};
	let Some(month) = month
		.and_then(|m| usize::try_from(m - 1).ok())
		.and_then(|m| MONTHS.get(m))
	else {
		return format!("{}{year}", if approx { "um " } else { "" });
	};
	match day {
		Some(day) => format!("{}{day}. {month} {year}", if approx { "ca. " } else { "" }),
		None => format!("{}{month} {year}", if approx { "um " } else { "" }),
	}
}
