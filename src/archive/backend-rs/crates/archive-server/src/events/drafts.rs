//! Whole-form, byte-free, owned Auftritt drafts. No reader or model is registered.
use super::*;
use serde_json::value::RawValue;
const NOT_FOUND: &str = "Der angefangene Eintrag wurde nicht gefunden.";
const INVALID: &str = "Der Entwurf ist ungültig.";
#[derive(sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
struct Draft {
	id: Uuid,
	title: String,
	datei_count: i32,
	form: String,
	updated_at: DateTime<Utc>,
}
fn summary(d: &Draft) -> Value {
	json!({"entwurfId":d.id,"titel":d.title,"dateien":d.datei_count,"aktualisiertAm":d.updated_at})
}
pub(super) async fn list(
	State(origin): State<Arc<Origin>>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, false)
		.await?;
	let rows: Vec<Draft> = sqlx::query_as(
		r#"SELECT * FROM auftritt_entwuerfe WHERE "OwnerAccountId"=$1 ORDER BY "UpdatedAt" DESC"#,
	)
	.bind(access.current.account_id)
	.fetch_all(&auth.pool)
	.await?;
	Ok(access.respond(
		Json(json!({"entwuerfe":rows.iter().map(summary).collect::<Vec<_>>()})).into_response(),
	))
}
#[derive(serde::Serialize)]
struct Envelope {
	entwurf: Detail,
}
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct Detail {
	entwurf_id: Uuid,
	titel: String,
	dateien: i32,
	formular: Box<RawValue>,
	aktualisiert_am: DateTime<Utc>,
}
// Guid route constraints use TryParse's N/D/B/P/X forms and whitespace,
// unlike the JSON Guid converter. URN is a uuid crate extension, not CLR Guid.
pub(super) fn path_id(text: &str) -> Option<Uuid> {
	let text = text.trim();
	if text
		.get(..9)
		.is_some_and(|p| p.eq_ignore_ascii_case("urn:uuid:"))
	{
		return None;
	}
	let unwrapped = text
		.strip_prefix('(')
		.and_then(|s| s.strip_suffix(')'))
		.unwrap_or(text);
	if let Ok(id) = Uuid::parse_str(unwrapped) {
		return Some(id);
	}
	let text = text.strip_prefix('{')?.strip_suffix('}')?;
	let mut fields = text.splitn(4, ',');
	let hex = |s: &str| {
		u32::from_str_radix(
			s.trim()
				.strip_prefix("0x")
				.or_else(|| s.trim().strip_prefix("0X"))?,
			16,
		)
		.ok()
	};
	let a = hex(fields.next()?)?;
	let b = u16::try_from(hex(fields.next()?)?).ok()?;
	let c = u16::try_from(hex(fields.next()?)?).ok()?;
	let bytes = fields
		.next()?
		.trim()
		.strip_prefix('{')?
		.strip_suffix('}')?
		.split(',')
		.map(|v| u8::try_from(hex(v)?).ok())
		.collect::<Option<Vec<_>>>()?;
	let bytes: [u8; 8] = bytes.try_into().ok()?;
	let tail = bytes[2..]
		.iter()
		.map(|b| format!("{b:02x}"))
		.collect::<String>();
	Uuid::parse_str(&format!(
		"{a:08x}-{b:04x}-{c:04x}-{:02x}{:02x}-{tail}",
		bytes[0], bytes[1]
	))
	.ok()
}
pub(super) async fn detail(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<String>,
	request: Request,
) -> Result<Response, AuthError> {
	let id =
		path_id(&id).ok_or_else(|| fail(StatusCode::NOT_FOUND, crate::api::API_UNKNOWN_TITLE))?;
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, false)
		.await?;
	let d: Draft =
		sqlx::query_as(r#"SELECT * FROM auftritt_entwuerfe WHERE "Id"=$1 AND "OwnerAccountId"=$2"#)
			.bind(id)
			.bind(access.current.account_id)
			.fetch_optional(&auth.pool)
			.await?
			.ok_or_else(|| fail(StatusCode::NOT_FOUND, NOT_FOUND))?;
	let form = RawValue::from_string(d.form).map_err(|_| AuthError::Database)?;
	Ok(access.respond(
		Json(Envelope {
			entwurf: Detail {
				entwurf_id: d.id,
				titel: d.title,
				dateien: d.datei_count,
				formular: form,
				aktualisiert_am: d.updated_at,
			},
		})
		.into_response(),
	))
}
pub(super) async fn save(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<String>,
	request: Request,
) -> Result<Response, AuthError> {
	let id =
		path_id(&id).ok_or_else(|| fail(StatusCode::NOT_FOUND, crate::api::API_UNKNOWN_TITLE))?;
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = body(request, Shape::EventDraft).await?;
	let bad = || fail(StatusCode::BAD_REQUEST, INVALID);
	let title = b["titel"].as_str().ok_or_else(bad)?;
	let count = b["dateien"]
		.as_i64()
		.filter(|n| (0..=50).contains(n))
		.ok_or_else(bad)?;
	if b["entwurfid"]
		.as_str()
		.and_then(|s| Uuid::parse_str(s).ok())
		!= Some(id)
		|| title.encode_utf16().count() > 200
	{
		return Err(bad());
	}
	let raw = b["formular"].as_str().ok_or_else(bad)?;
	// A case-sensitive dictionary keeps the last *exact* version key; every
	// other inner value stays opaque. TryGetInt32 rejects 1.0 and 1e0.
	let fields: std::collections::HashMap<String, Box<RawValue>> =
		serde_json::from_str(raw).map_err(|_| bad())?;
	if fields.get("version").is_none_or(|v| v.get() != "1") || raw.encode_utf16().count() > 100_000
	{
		return Err(bad());
	}
	let owner: Option<Uuid> =
		sqlx::query_scalar(r#"SELECT "OwnerAccountId" FROM auftritt_entwuerfe WHERE "Id"=$1"#)
			.bind(id)
			.fetch_optional(&auth.pool)
			.await?;
	if owner.is_some_and(|owner| owner != access.current.account_id) {
		return Err(fail(StatusCode::NOT_FOUND, NOT_FOUND));
	}
	let now = auth.clock.now();
	if owner.is_none() {
		let owned: i64 = sqlx::query_scalar(
			r#"SELECT count(*) FROM auftritt_entwuerfe WHERE "OwnerAccountId"=$1"#,
		)
		.bind(access.current.account_id)
		.fetch_one(&auth.pool)
		.await?;
		if owned >= 25 {
			return Err(fail(
				StatusCode::CONFLICT,
				"Es sind zu viele Einträge angefangen. Bitte erst welche verwerfen.",
			));
		}
		sqlx::query(r#"INSERT INTO auftritt_entwuerfe ("Id","OwnerAccountId","Title","DateiCount","Form","CreatedAt","UpdatedAt") VALUES ($1,$2,$3,$4,$5,$6,$6)"#).bind(id).bind(access.current.account_id).bind(title.trim()).bind(count as i32).bind(raw).bind(now).execute(&auth.pool).await?;
	} else {
		sqlx::query(r#"UPDATE auftritt_entwuerfe SET "Title"=$2,"DateiCount"=$3,"Form"=$4,"UpdatedAt"=$5 WHERE "Id"=$1"#).bind(id).bind(title.trim()).bind(count as i32).bind(raw).bind(now).execute(&auth.pool).await?;
	}
	Ok(access.respond(Json(json!({"aktualisiertAm":now})).into_response()))
}
pub(super) async fn discard(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<String>,
	request: Request,
) -> Result<Response, AuthError> {
	let id =
		path_id(&id).ok_or_else(|| fail(StatusCode::NOT_FOUND, crate::api::API_UNKNOWN_TITLE))?;
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	// Bodyless endpoint: do not deserialize even a malformed supplied body.
	sqlx::query(r#"DELETE FROM auftritt_entwuerfe WHERE "Id"=$1 AND "OwnerAccountId"=$2"#)
		.bind(id)
		.bind(access.current.account_id)
		.execute(&auth.pool)
		.await?;
	Ok(access.respond(StatusCode::NO_CONTENT.into_response()))
}
