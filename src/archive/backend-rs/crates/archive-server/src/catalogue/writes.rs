//! The flat field contract shared by create, patch and persisted form state.
use super::{Lied, queries};
use crate::clr_guid::GuidPath;
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
};
use axum::{
	Json,
	extract::{Request, State},
	http::{StatusCode, header},
	response::{IntoResponse, Response},
};
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;

pub(super) const FIELDS: [(&str, &str); 8] = [
	("composer", "Composer"),
	("arranger", "Arranger"),
	("lyricist", "Lyricist"),
	("voiceConfiguration", "VoiceConfiguration"),
	("musicalKey", "MusicalKey"),
	("language", "Language"),
	("lyrics", "Lyrics"),
	("occasion", "Occasion"),
];
/// Code-owned FieldCatalog prompts (form-wire names remain in FIELDS).
pub const FIELD_PROMPTS: [(&str, &str); 7] = [
	("composer", "Komponist (Musik von), nur der Name."),
	("arranger", "Arrangeur (Satz von), nur der Name."),
	("lyricist", "Textdichter (Worte von), nur der Name."),
	(
		"voice_configuration",
		"Besetzung, zum Beispiel \"Männerchor a cappella\" oder \"TTBB\".",
	),
	(
		"musical_key",
		"Tonart, zum Beispiel \"G-Dur\" oder \"d-Moll\".",
	),
	(
		"language",
		"Sprache des Liedtexts, zum Beispiel \"Deutsch\" oder \"Latein\".",
	),
	(
		"lyrics",
		"der gesungene Liedtext mit allen Strophen, Zeilen durch Zeilenumbrüche getrennt, ohne Titel,\n  Namen, Tempo- oder Stimmangaben.",
	),
];
pub(super) const CONFLICT: &str = "Der Eintrag wurde zwischenzeitlich geändert.";
/// Catalogue names differ from form-wire camelCase for the key. Keep the
/// automation allowlist here beside manual validation, never model-selected SQL.
pub(crate) fn automated_column(field: &str) -> Option<&'static str> {
	let form = if field == "musical_key" {
		"musicalKey"
	} else {
		field
	};
	if !["composer", "arranger", "lyricist", "musicalKey", "lyrics"].contains(&form) {
		return None;
	}
	FIELDS
		.iter()
		.find(|(name, _)| *name == form)
		.map(|(_, column)| *column)
}
pub(super) fn invalid() -> AuthError {
	AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig.")
}
pub(super) fn fail(status: StatusCode, title: &'static str) -> AuthError {
	AuthError::Problem(status, title)
}
pub(super) async fn body(
	request: Request,
	shape: super::binding::Shape,
) -> Result<Value, AuthError> {
	let bytes = axum::body::to_bytes(request.into_body(), 2 * 1024 * 1024)
		.await
		.map_err(|_| invalid())?;
	if bytes.is_empty() {
		Ok(Value::Null)
	} else {
		super::binding::parse(&bytes, shape).map_err(|_| invalid())
	}
}
pub(super) fn field<'a>(body: &'a Value, name: &str) -> &'a Value {
	body.as_object()
		.and_then(|o| o.get(&name.to_ascii_lowercase()))
		.unwrap_or(&Value::Null)
}
pub(super) fn string(value: &Value) -> Result<Option<&str>, AuthError> {
	if value.is_null() {
		Ok(None)
	} else {
		value.as_str().map(Some).ok_or_else(invalid)
	}
}
pub(super) fn version(body: &Value) -> Result<Option<i64>, AuthError> {
	let v = field(body, "rowVersion");
	if v.is_null() {
		Ok(None)
	} else {
		v.as_u64()
			.filter(|v| *v <= u32::MAX as u64)
			.map(|v| Some(v as i64))
			.ok_or_else(invalid)
	}
}
pub(super) fn validate_fields(body: &Value, patch: bool, draft: bool) -> Result<(), AuthError> {
	if !body.is_null() && !body.is_object() {
		return Err(invalid());
	}
	if let Some(o) = body.as_object() {
		for (key, value) in o {
			if key.eq_ignore_ascii_case("rowVersion") && patch {
				version(body)?;
				continue;
			}
			if !(key.eq_ignore_ascii_case("title")
				|| FIELDS.iter().any(|(form, _)| {
					(!draft || *form != "occasion") && key.eq_ignore_ascii_case(form)
				})) {
				return Err(fail(StatusCode::BAD_REQUEST, "Unbekanntes Feld."));
			}
			if let Some(s) = string(value)? {
				let max = if key.eq_ignore_ascii_case("lyrics") {
					5000
				} else {
					200
				};
				if (if draft { s } else { s.trim() }).encode_utf16().count() > max {
					return Err(fail(StatusCode::BAD_REQUEST, "Ein Feld ist zu lang."));
				}
			}
		}
	}
	Ok(())
}
pub(super) async fn create(
	State(origin): State<Arc<Origin>>,
	request: Request,
) -> Result<Response, AuthError> {
	write(origin, None, request).await
}
pub(super) async fn patch(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	write(origin, Some(id), request).await
}
async fn write(
	origin: Arc<Origin>,
	id: Option<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let existing = if let Some(id) = id {
		Some(
			queries::lied(&auth.pool, id)
				.await?
				.ok_or_else(|| fail(StatusCode::NOT_FOUND, "Das Lied wurde nicht gefunden."))?,
		)
	} else {
		None
	};
	let body = body(
		request,
		if id.is_some() {
			super::binding::Shape::Patch
		} else {
			super::binding::Shape::Create
		},
	)
	.await?;
	validate_fields(&body, id.is_some(), false)?;
	let now = auth.clock.now();
	let mut lied = existing.clone().unwrap_or_else(|| Lied {
		id: Uuid::new_v4(),
		created_at: now,
		updated_at: now,
		row_version: 1,
		..Default::default()
	});
	if version(&body)?.is_some_and(|v| existing.as_ref().is_some_and(|old| v != old.row_version)) {
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	if let Some(title) = string(field(&body, "title"))? {
		lied.title = title.trim().into();
	}
	if lied.title.is_empty() {
		return Err(fail(StatusCode::BAD_REQUEST, "Bitte einen Titel angeben."));
	}
	// Fixed columns from the catalogue only; client text is always a bind argument.
	let mut values = serde_json::to_value(&lied).map_err(|_| AuthError::Database)?;
	for (form, _) in FIELDS {
		if let Some(value) = string(field(&body, form))? {
			values[form] = if value.trim().is_empty() {
				Value::Null
			} else {
				json!(value.trim())
			};
		}
	}
	lied = serde_json::from_value(values).map_err(|_| AuthError::Database)?;
	lied.updated_at = now;
	if let Some(old) = existing {
		lied.row_version += 1;
		let changed=sqlx::query(r#"UPDATE lieder SET "Title"=$2,"Composer"=$3,"Arranger"=$4,"Lyricist"=$5,"VoiceConfiguration"=$6,"MusicalKey"=$7,"Language"=$8,"Lyrics"=$9,"Occasion"=$10,"UpdatedAt"=$11,"UpdatedByAccountId"=$12,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$13 AND "DeletedAt" IS NULL"#).bind(lied.id).bind(&lied.title).bind(&lied.composer).bind(&lied.arranger).bind(&lied.lyricist).bind(&lied.voice_configuration).bind(&lied.musical_key).bind(&lied.language).bind(&lied.lyrics).bind(&lied.occasion).bind(now).bind(access.current.account_id).bind(old.row_version).execute(&auth.pool).await?.rows_affected();
		if changed == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	} else {
		sqlx::query(r#"INSERT INTO lieder ("Id","Title","Composer","Arranger","Lyricist","VoiceConfiguration","MusicalKey","Language","Lyrics","Occasion","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$11,$12,1)"#).bind(lied.id).bind(&lied.title).bind(&lied.composer).bind(&lied.arranger).bind(&lied.lyricist).bind(&lied.voice_configuration).bind(&lied.musical_key).bind(&lied.language).bind(&lied.lyrics).bind(&lied.occasion).bind(now).bind(access.current.account_id).execute(&auth.pool).await?;
	}
	// Reuse the exact detail adapter, including existing Dateien on patch.
	let saved = queries::lied(&auth.pool, lied.id)
		.await?
		.ok_or(AuthError::Database)?;
	let detail = super::detail_value(&auth.pool, saved).await?;
	let response = if id.is_none() {
		(
			StatusCode::CREATED,
			[(
				header::LOCATION,
				format!("/api/lieder/{}", detail["id"].as_str().unwrap_or_default()),
			)],
			Json(json!({"lied":detail})),
		)
			.into_response()
	} else {
		Json(json!({"lied":detail})).into_response()
	};
	Ok(access.respond(response))
}
