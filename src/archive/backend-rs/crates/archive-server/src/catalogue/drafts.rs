//! Angefangen persists only the editor's form and file identities, never bytes.
//! #230 owns model calls and server-written reading outcomes. Retained outcomes
//! survive a form save; removing a file cascades them before lifecycle notification.
use super::writes::{FIELDS, body, fail, field, invalid, string, validate_fields};
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
};
use axum::{
	Json,
	extract::{Path, Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get},
};
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;
const NOT_FOUND: &str = "Der angefangene Eintrag wurde nicht gefunden.";
const INVALID: &str = "Der Entwurf ist ungültig.";
const FORMS: [&str; 8] = [
	"title",
	"composer",
	"arranger",
	"lyricist",
	"voiceConfiguration",
	"musicalKey",
	"language",
	"lyrics",
];
/// Commit notification seam for #230's cancellation registry. No reader is
/// registered by #224 and no suggestion/model integration is claimed.
pub trait DraftLifecycle: Send + Sync {
	fn removed(&self, entwurf: Uuid, datei: Uuid);
	fn discarded(&self, entwurf: Uuid);
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		(
			"/api/lied-entwuerfe",
			get(list).fallback(crate::api::api_miss),
		),
		(
			"/api/lied-entwuerfe/{id}",
			get(load)
				.put(save)
				.delete(discard)
				.fallback(crate::api::api_miss),
		),
	]
}
async fn list(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, false)
		.await?;
	let rows:Vec<Value>=sqlx::query_scalar(r#"SELECT json_build_object('entwurfId',e."Id",'titel',e."Title",'dateien',(SELECT count(*) FROM lied_entwurf_dateien d WHERE d."EntwurfId"=e."Id"),'aktualisiertAm',e."UpdatedAt") FROM lied_entwuerfe e WHERE e."OwnerAccountId"=$1 ORDER BY e."UpdatedAt" DESC"#).bind(access.current.account_id).fetch_all(&auth.pool).await?;
	Ok(access.respond(Json(json!({"entwuerfe":rows})).into_response()))
}
async fn load(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, false)
		.await?;
	let row: Option<Value> = sqlx::query_scalar(
		r#"SELECT to_json(e) FROM lied_entwuerfe e WHERE "Id"=$1 AND "OwnerAccountId"=$2"#,
	)
	.bind(id)
	.bind(access.current.account_id)
	.fetch_optional(&auth.pool)
	.await?;
	let row = row.ok_or_else(|| fail(StatusCode::NOT_FOUND, NOT_FOUND))?;
	let mut values = json!({"title":row["Title"]});
	for (form, column) in FIELDS.iter().filter(|(f, _)| *f != "occasion") {
		values[*form] = row[*column].clone();
	}
	let edited: Vec<_> = row["EditedFields"]
		.as_str()
		.unwrap_or_default()
		.split(',')
		.filter(|s| !s.is_empty())
		.collect();
	let suggestions: serde_json::Map<String, Value> = row["Suggestions"]
		.as_str()
		.unwrap_or_default()
		.split(',')
		.filter_map(|s| s.split_once(':'))
		.map(|(k, v)| (k.into(), json!(v)))
		.collect();
	let mut files:Vec<Value>=sqlx::query_scalar(r#"SELECT json_build_object('id',d."Id",'name',d."Name",'sizeBytes',d."SizeBytes",'art',d."Art",'stimme',d."Stimme",'artBearbeitet',d."ArtEdited",'stimmeBearbeitet',d."StimmeEdited",'lesen',json_build_object('status',d."LesenStatus",'anfrageId',d."LesenAnfrageId",'meldung',d."LesenMeldung"),'lesung',CASE WHEN l."DateiId" IS NULL THEN NULL ELSE json_build_object('anfrageId',l."AnfrageId",'status',l."Status",'meldung',l."Meldung",'felder',l."Felder"::json,'updatedAt',l."UpdatedAt") END) FROM lied_entwurf_dateien d LEFT JOIN lied_entwurf_lesungen l ON l."EntwurfId"=d."EntwurfId" AND l."DateiId"=d."Id" WHERE d."EntwurfId"=$1 ORDER BY d."Position""#).bind(id).fetch_all(&auth.pool).await?;
	for file in &mut files {
		if let Some(reading) = file["lesung"].as_object_mut() {
			let updated = reading.remove("updatedAt").and_then(|v| {
				v.as_str()
					.and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
			});
			if reading["status"] == "liest"
				&& updated.is_some_and(|t| {
					auth.clock.now() - t.with_timezone(&chrono::Utc) > chrono::Duration::minutes(5)
				}) {
				reading.insert("status".into(), json!("fehlgeschlagen"));
				reading.insert(
					"meldung".into(),
					json!("Das Lesen wurde unterbrochen. Bitte erneut lesen."),
				);
				reading.insert("felder".into(), json!([]));
			}
		}
	}
	Ok(access.respond(Json(json!({"entwurf":{"entwurfId":id,"lied":values,"bearbeitet":edited,"vorschlaege":suggestions,"dateien":files,"aktualisiertAm":row["UpdatedAt"]}})).into_response()))
}
struct DraftFile {
	id: Uuid,
	name: String,
	size: i64,
	art: String,
	stimme: String,
	art_edited: bool,
	stimme_edited: bool,
	status: String,
	request: i32,
	message: String,
}
fn bad() -> AuthError {
	fail(StatusCode::BAD_REQUEST, INVALID)
}
fn array<'a>(body: &'a Value, key: &str) -> Result<&'a [Value], AuthError> {
	let v = field(body, key);
	if v.is_null() {
		Ok(&[])
	} else {
		v.as_array().map(Vec::as_slice).ok_or_else(invalid)
	}
}
async fn save(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let body = body(request).await?;
	if string(field(&body, "entwurfId"))?.and_then(|s| Uuid::parse_str(s).ok()) != Some(id)
		|| !field(&body, "lied").is_object()
	{
		return Err(bad());
	}
	let lied = field(&body, "lied");
	validate_fields(lied, false, true)?;
	let fields = FORMS
		.iter()
		.map(|f| string(field(lied, f)).map(|v| v.unwrap_or_default().to_owned()))
		.collect::<Result<Vec<_>, _>>()?;
	let edited = array(&body, "bearbeitet")?;
	if edited
		.iter()
		.any(|v| !v.as_str().is_some_and(|s| FORMS.contains(&s)))
	{
		return Err(bad());
	}
	let edited = FORMS
		.iter()
		.filter(|f| edited.contains(&json!(f)))
		.copied()
		.collect::<Vec<_>>()
		.join(",");
	let suggestions = field(&body, "vorschlaege");
	if !suggestions.is_null() && !suggestions.is_object() {
		return Err(invalid());
	}
	if suggestions.as_object().is_some_and(|o| {
		o.iter().any(|(k, v)| {
			!FORMS.contains(&k.as_str()) || !matches!(v.as_str(), Some("sicher" | "unsicher"))
		})
	}) {
		return Err(bad());
	}
	let suggestions = FORMS
		.iter()
		.filter_map(|f| suggestions[*f].as_str().map(|v| format!("{f}:{v}")))
		.collect::<Vec<_>>()
		.join(",");
	let files = array(&body, "dateien")?;
	if files.len() > 50 {
		return Err(bad());
	}
	let mut wanted = Vec::new();
	for file in files {
		let file_id = string(field(file, "id"))?
			.and_then(|s| Uuid::parse_str(s).ok())
			.ok_or_else(bad)?;
		if wanted.iter().any(|f: &DraftFile| f.id == file_id) {
			return Err(bad());
		}
		let name = string(field(file, "name"))?.ok_or_else(bad)?;
		let size = field(file, "sizeBytes")
			.as_i64()
			.filter(|s| *s >= 0)
			.ok_or_else(bad)?;
		let art = string(field(file, "art"))?.ok_or_else(bad)?;
		if name.trim().is_empty()
			|| name.encode_utf16().count() > 300
			|| !["partitur", "noten", "uebe", "aufnahme", "sonstiges"].contains(&art)
		{
			return Err(bad());
		}
		let stimme = string(field(file, "stimme"))?.unwrap_or_default();
		if stimme.encode_utf16().count() > 200 {
			return Err(fail(StatusCode::BAD_REQUEST, "Ein Feld ist zu lang."));
		}
		let lesen = field(file, "lesen");
		let (status, number, message) = if lesen.is_null() {
			("keine", 0, "")
		} else {
			let status = string(field(lesen, "status"))?.ok_or_else(bad)?;
			let number = field(lesen, "anfrageId")
				.as_i64()
				.filter(|n| *n >= 0 && *n <= i32::MAX as i64)
				.ok_or_else(bad)?;
			let message = string(field(lesen, "meldung"))?.unwrap_or_default();
			if ![
				"keine",
				"liest",
				"gelesen",
				"fehlgeschlagen",
				"nicht-verfuegbar",
			]
			.contains(&status)
				|| message.encode_utf16().count() > 500
			{
				return Err(bad());
			}
			(status, number as i32, message)
		};
		let flag = |name| {
			let v = field(file, name);
			if v.is_null() {
				Ok(false)
			} else {
				v.as_bool().ok_or_else(invalid)
			}
		};
		wanted.push(DraftFile {
			id: file_id,
			name: name.trim().into(),
			size,
			art: art.into(),
			stimme: stimme.into(),
			art_edited: flag("artBearbeitet")?,
			stimme_edited: flag("stimmeBearbeitet")?,
			status: status.into(),
			request: number,
			message: message.into(),
		});
	}
	let mut tx = auth.pool.begin().await?;
	// Serialize first claim for a browser UUID across replicas; then bound this
	// account's cardinality with its canonical Identity row, not a process lock.
	sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,224))")
		.bind(id.to_string())
		.execute(&mut *tx)
		.await?;
	let owner: Option<Uuid> =
		sqlx::query_scalar(r#"SELECT "OwnerAccountId" FROM lied_entwuerfe WHERE "Id"=$1"#)
			.bind(id)
			.fetch_optional(&mut *tx)
			.await?;
	if owner.is_some_and(|o| o != access.current.account_id) {
		return Err(fail(StatusCode::NOT_FOUND, NOT_FOUND));
	}
	if owner.is_none() {
		sqlx::query(r#"SELECT "Id" FROM "AspNetUsers" WHERE "Id"=$1 FOR UPDATE"#)
			.bind(access.current.account_id)
			.execute(&mut *tx)
			.await?;
		let count: i64 =
			sqlx::query_scalar(r#"SELECT count(*) FROM lied_entwuerfe WHERE "OwnerAccountId"=$1"#)
				.bind(access.current.account_id)
				.fetch_one(&mut *tx)
				.await?;
		if count >= 25 {
			return Err(fail(
				StatusCode::CONFLICT,
				"Es sind zu viele Einträge angefangen. Bitte erst welche verwerfen.",
			));
		}
	}
	let now = auth.clock.now();
	sqlx::query(r#"INSERT INTO lied_entwuerfe ("Id","OwnerAccountId","Title","Composer","Arranger","Lyricist","VoiceConfiguration","MusicalKey","Language","Lyrics","EditedFields","Suggestions","CreatedAt","UpdatedAt") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$13) ON CONFLICT ("Id") DO UPDATE SET "Title"=$3,"Composer"=$4,"Arranger"=$5,"Lyricist"=$6,"VoiceConfiguration"=$7,"MusicalKey"=$8,"Language"=$9,"Lyrics"=$10,"EditedFields"=$11,"Suggestions"=$12,"UpdatedAt"=$13"#).bind(id).bind(access.current.account_id).bind(&fields[0]).bind(&fields[1]).bind(&fields[2]).bind(&fields[3]).bind(&fields[4]).bind(&fields[5]).bind(&fields[6]).bind(&fields[7]).bind(edited).bind(suggestions).bind(now).execute(&mut *tx).await?;
	let ids: Vec<_> = wanted.iter().map(|f| f.id).collect();
	let removed: Vec<Uuid> = sqlx::query_scalar(
		r#"DELETE FROM lied_entwurf_dateien WHERE "EntwurfId"=$1 AND NOT ("Id"=ANY($2)) RETURNING "Id""#,
	)
	.bind(id)
	.bind(&ids)
	.fetch_all(&mut *tx)
	.await?;
	for (position, file) in wanted.iter().enumerate() {
		sqlx::query(r#"INSERT INTO lied_entwurf_dateien ("EntwurfId","Id","Position","Name","SizeBytes","Art","Stimme","ArtEdited","StimmeEdited","LesenStatus","LesenAnfrageId","LesenMeldung") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) ON CONFLICT ("EntwurfId","Id") DO UPDATE SET "Position"=$3,"Name"=$4,"SizeBytes"=$5,"Art"=$6,"Stimme"=$7,"ArtEdited"=$8,"StimmeEdited"=$9,"LesenStatus"=$10,"LesenAnfrageId"=$11,"LesenMeldung"=$12"#).bind(id).bind(file.id).bind(position as i32).bind(&file.name).bind(file.size).bind(&file.art).bind(&file.stimme).bind(file.art_edited).bind(file.stimme_edited).bind(&file.status).bind(file.request).bind(&file.message).execute(&mut *tx).await?;
	}
	tx.commit().await?;
	for file in removed {
		for observer in &origin.draft_lifecycle {
			observer.removed(id, file);
		}
	}
	Ok(access.respond(Json(json!({"aktualisiertAm":now})).into_response()))
}
async fn discard(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let changed =
		sqlx::query(r#"DELETE FROM lied_entwuerfe WHERE "Id"=$1 AND "OwnerAccountId"=$2"#)
			.bind(id)
			.bind(access.current.account_id)
			.execute(&auth.pool)
			.await?
			.rows_affected();
	if changed > 0 {
		for observer in &origin.draft_lifecycle {
			observer.discarded(id);
		}
	}
	Ok(access.respond(StatusCode::NO_CONTENT.into_response()))
}
