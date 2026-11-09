//! Flat Lied reads. The frozen catalogue fields and visibility predicates are
//! shared by the browser adapter and later search consumers, not copied per route.
mod dateien;
pub mod queries;
pub mod search;

use crate::{
	Origin,
	api::problem,
	auth::{AccessPolicy, AuthError},
};
use axum::{
	Json,
	extract::{Path, Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use chrono::{DateTime, Utc};
use serde::Serialize;
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;

#[derive(Clone, Debug, Default, Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
#[sqlx(rename_all = "PascalCase")]
pub struct Lied {
	pub id: Uuid,
	pub title: String,
	pub composer: Option<String>,
	pub arranger: Option<String>,
	pub lyricist: Option<String>,
	pub voice_configuration: Option<String>,
	pub musical_key: Option<String>,
	pub language: Option<String>,
	pub lyrics: Option<String>,
	pub occasion: Option<String>,
	pub created_at: DateTime<Utc>,
	pub updated_at: DateTime<Utc>,
	pub row_version: i64,
}
impl Lied {
	fn list_row(&self) -> Value {
		json!({"id":self.id,"title":self.title,"composer":self.composer,"arranger":self.arranger,"lyricist":self.lyricist,"voiceConfiguration":self.voice_configuration,"musicalKey":self.musical_key,"language":self.language,"occasion":self.occasion,"matchIn":[],"dateiTypen":[]})
	}
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		("/api/lieder", get(list).fallback(crate::api::api_miss)),
		(
			"/api/lieder/{id}",
			get(detail).fallback(crate::api::api_miss),
		),
		(
			"/api/dateien/{id}/access",
			get(dateien::access).fallback(crate::api::api_miss),
		),
		(
			"/api/dateien/{id}/storage-tier",
			post(dateien::tier).fallback(crate::api::api_miss),
		),
	]
}
async fn list(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		crate::auth::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let params: Vec<_> =
		url::form_urlencoded::parse(request.uri().query().unwrap_or_default().as_bytes())
			.into_owned()
			.collect();
	// Frozen ASP.NET query keys are case-insensitive. Its scalar StringValues
	// conversion joins nonempty duplicates in arrival order; a present empty key
	// still overrides its alias. The native .NET query oracle pins these details.
	let scalar = |key: &str| {
		let values = params
			.iter()
			.filter(|(k, _)| k.eq_ignore_ascii_case(key))
			.map(|(_, v)| v.as_str())
			.collect::<Vec<_>>();
		(!values.is_empty()).then(|| {
			values
				.into_iter()
				.filter(|v| !v.is_empty())
				.collect::<Vec<_>>()
				.join(",")
		})
	};
	let query_value = scalar("q").unwrap_or_default();
	let query = query_value.trim();
	if query.encode_utf16().count() > 200 {
		return Ok(access.respond(problem(StatusCode::BAD_REQUEST, "Die Suche ist zu lang.")));
	}
	let mut kinds = Vec::new();
	for (_, value) in params
		.iter()
		.filter(|(k, _)| k.eq_ignore_ascii_case("dateiTyp"))
	{
		let kind = value.trim().to_ascii_lowercase();
		if !queries::DATEI_KINDS.contains(&kind.as_str()) {
			return Ok(access.respond(problem(StatusCode::BAD_REQUEST, "Unbekannter Dateityp.")));
		}
		if !kinds.contains(&kind) {
			kinds.push(kind);
		}
	}
	let voice_value = scalar("besetzung").or_else(|| scalar("voiceConfiguration"));
	let occasion_value = scalar("anlass").or_else(|| scalar("occasion"));
	let voice = clean(voice_value.as_deref());
	let occasion = clean(occasion_value.as_deref());
	let number = scalar("page")
		.map(|p| p.trim_ascii().parse::<i32>())
		.transpose()
		.map_err(|_| AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig."))?;
	let number = search::requested_page(number);
	let rows = queries::lieder(&auth.pool).await?;
	let files = queries::dateien(&auth.pool, None, None).await?;
	let kinds_of = |id: Uuid| {
		queries::DATEI_KINDS
			.iter()
			.filter(|k| files.iter().any(|d| d.lied_id == Some(id) && d.kind == **k))
			.copied()
			.collect::<Vec<_>>()
	};
	let filtered: Vec<_> = rows
		.iter()
		.filter(|r| {
			voice.is_none() || search::fold(r.voice_configuration.as_deref()) == search::fold(voice)
		})
		.filter(|r| {
			occasion.is_none() || search::fold(r.occasion.as_deref()) == search::fold(occasion)
		})
		.filter(|r| kinds.iter().all(|k| kinds_of(r.id).contains(&k.as_str())))
		.cloned()
		.collect();
	let tokens = search::tokens_of(query);
	let noten = search::load_noten_texts(&auth.pool, &tokens).await?;
	let matches = search::matching(&tokens, &filtered, &noten, query);
	let lieder: Vec<_> = search::page(&matches, number, 20)
		.iter()
		.map(|m| {
			let mut row = m.lied.list_row();
			row["matchIn"] = json!(search::labels(&tokens, &m.fields));
			row["dateiTypen"] = json!(kinds_of(m.lied.id));
			row
		})
		.collect();
	Ok(access.respond(Json(json!({"query":clean(Some(query)),"page":number,"pageSize":20,"total":matches.len(),"filters":{"voiceConfiguration":voice,"occasion":occasion,"dateiTypen":kinds},"options":{"voiceConfigurations":distinct(rows.iter().filter_map(|r|r.voice_configuration.as_deref())),"occasions":distinct(rows.iter().filter_map(|r|r.occasion.as_deref()))},"lieder":lieder})).into_response()))
}
fn clean(value: Option<&str>) -> Option<&str> {
	value.map(str::trim).filter(|v| !v.is_empty())
}
fn distinct<'a>(values: impl Iterator<Item = &'a str>) -> Vec<&'a str> {
	let mut by_fold = std::collections::HashMap::new();
	for v in values.filter(|v| !v.trim().is_empty()) {
		by_fold
			.entry(search::fold(Some(v)))
			.and_modify(|old: &mut &'a str| {
				if search::ordinal(v, old).is_lt() {
					*old = v;
				}
			})
			.or_insert(v);
	}
	let mut result: Vec<_> = by_fold.into_iter().collect();
	result.sort_by(|(a, _), (b, _)| search::ordinal(a, b));
	result.into_iter().map(|(_, v)| v).collect()
}
async fn detail(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<String>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		crate::auth::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let id = Uuid::parse_str(&id)
		.map_err(|_| AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig."))?;
	let response = match queries::lied(&auth.pool, id).await? {
		Some(lied) => {
			let mut files = queries::dateien(&auth.pool, Some(id), None).await?;
			let copies = queries::copy_ids(&auth.pool, &files).await?;
			files.retain(|d| !copies.contains(&d.id));
			let events = queries::recorded_events(&auth.pool, &files).await?;
			files.sort_by(|a, b| {
				queries::DATEI_KINDS
					.iter()
					.position(|k| *k == a.kind)
					.cmp(&queries::DATEI_KINDS.iter().position(|k| *k == b.kind))
					.then(b.is_partitur.cmp(&a.is_partitur))
					.then_with(|| {
						search::optional_ordinal_ignore_case(
							a.stimme.as_deref(),
							b.stimme.as_deref(),
						)
					})
					.then(a.created_at.cmp(&b.created_at))
					.then(a.id.cmp(&b.id))
			});
			let mut detail = serde_json::to_value(lied).map_err(|_| AuthError::Database)?;
			detail["dateien"] = json!(
				files
					.iter()
					.map(|d| d.summary(
						d.recorded_at_event_id
							.and_then(|id| events.get(&id))
							.cloned()
							.unwrap_or(Value::Null)
					))
					.collect::<Vec<_>>()
			);
			Json(json!({"lied":detail})).into_response()
		}
		None => problem(StatusCode::NOT_FOUND, "Das Lied wurde nicht gefunden."),
	};
	Ok(access.respond(response))
}
