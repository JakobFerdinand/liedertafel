//! Saved Auftritte are immediately member-visible. Dates retain their real
//! precision; mutations attribute the current editor, never a browser actor.
mod date;
mod documents;
mod drafts;
mod history;
mod programme;
use crate::{
	Origin,
	auth::{AccessPolicy, Auth, AuthError},
	binding::{self, Shape},
	clr_guid::GuidPath,
};
use axum::{
	Json,
	extract::{Request, State},
	http::{StatusCode, header},
	response::{IntoResponse, Response},
	routing::{MethodRouter, get},
};
use chrono::{DateTime, Utc};
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;
pub(super) const NOT_FOUND: &str = "Der Auftritt wurde nicht gefunden.";
pub(super) const CONFLICT: &str = "Der Eintrag wurde zwischenzeitlich geändert.";
pub(super) fn fail(status: StatusCode, title: &'static str) -> AuthError {
	AuthError::Problem(status, title)
}
pub(super) fn invalid() -> AuthError {
	fail(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig.")
}
pub(super) fn auth(origin: &Origin) -> Result<&Auth, AuthError> {
	origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))
}
pub(super) async fn body(request: Request, shape: Shape) -> Result<Value, AuthError> {
	let bytes = axum::body::to_bytes(request.into_body(), 2 * 1024 * 1024)
		.await
		.map_err(|_| invalid())?;
	if bytes.is_empty() {
		Ok(Value::Null)
	} else {
		binding::parse(&bytes, shape).map_err(|_| invalid())
	}
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		(
			"/api/events",
			get(list).post(create).fallback(crate::api::api_miss),
		),
		(
			"/api/events/{id}",
			get(detail).patch(patch).fallback(crate::api::api_miss),
		),
		(
			"/api/events/{id}/programme/items",
			axum::routing::put(programme::save).fallback(crate::api::api_miss),
		),
		(
			"/api/programmes",
			get(programme::upcoming).fallback(crate::api::api_miss),
		),
		(
			"/api/auftritt-entwuerfe",
			get(drafts::list).fallback(crate::api::api_miss),
		),
		(
			"/api/auftritt-entwuerfe/{id}",
			get(drafts::detail)
				.put(drafts::save)
				.delete(drafts::discard)
				.fallback(crate::api::api_miss)
				.layer(axum::middleware::from_fn(crate::clr_guid::constrained)),
		),
		(
			"/api/lieder/{id}/historie",
			get(history::list).fallback(crate::api::api_miss),
		),
		(
			"/api/dateien/{id}",
			axum::routing::patch(documents::patch).fallback(crate::api::api_miss),
		),
	]
	.into_iter()
	.map(|(path, route)| {
		(
			path,
			route.layer(axum::middleware::from_fn(json_media_type)),
		)
	})
	.collect()
}
// Frozen Accepts metadata selects the stable API fallback for non-JSON write
// requests, even before authorization. Bodyless DELETE has no such metadata.
async fn json_media_type(request: Request, next: axum::middleware::Next) -> Response {
	if matches!(
		*request.method(),
		axum::http::Method::POST | axum::http::Method::PUT | axum::http::Method::PATCH
	) {
		let media = request
			.headers()
			.get(header::CONTENT_TYPE)
			.and_then(|v| v.to_str().ok())
			.unwrap_or_default()
			.split(';')
			.next()
			.unwrap_or_default()
			.trim()
			.to_ascii_lowercase();
		if media != "application/json"
			&& !(media.starts_with("application/") && media.ends_with("+json"))
		{
			return crate::api::api_miss().await;
		}
	}
	next.run(request).await
}
#[derive(Clone, Debug, Default, serde::Serialize, sqlx::FromRow)]
#[serde(rename_all = "camelCase")]
#[sqlx(rename_all = "PascalCase")]
pub(super) struct Event {
	pub id: Uuid,
	pub kind: String,
	pub title: String,
	pub venue: Option<String>,
	pub date_year: Option<i32>,
	pub date_month: Option<i32>,
	pub date_day: Option<i32>,
	pub date_approximate: bool,
	pub start_time: Option<String>,
	pub notes: Option<String>,
	pub source_note: Option<String>,
	pub row_version: i64,
	pub created_at: DateTime<Utc>,
	pub updated_at: DateTime<Utc>,
}
impl Event {
	pub fn precision(&self) -> &'static str {
		date::precision(self.date_year, self.date_month, self.date_day)
	}
	pub fn display(&self) -> String {
		crate::catalogue::queries::event_date_display(
			self.date_year,
			self.date_month,
			self.date_day,
			self.date_approximate,
		)
	}
	pub fn item(&self) -> Value {
		json!({"id":self.id,"kind":self.kind,"title":self.title,"venue":self.venue,"dateYear":self.date_year,"dateMonth":self.date_month,"dateDay":self.date_day,"dateApproximate":self.date_approximate,"datePrecision":self.precision(),"dateDisplay":self.display(),"startTime":self.start_time})
	}
}
pub(super) async fn visible(pool: &sqlx::PgPool) -> Result<Vec<Event>, AuthError> {
	Ok(
		sqlx::query_as(r#"SELECT * FROM events WHERE "DeletedAt" IS NULL"#)
			.fetch_all(pool)
			.await?,
	)
}
pub(super) async fn load(pool: &sqlx::PgPool, id: Uuid) -> Result<Event, AuthError> {
	sqlx::query_as(r#"SELECT * FROM events WHERE "Id"=$1 AND "DeletedAt" IS NULL"#)
		.bind(id)
		.fetch_optional(pool)
		.await?
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, NOT_FOUND))
}
// ASP.NET scalar StringValues: case-insensitive keys, nonempty duplicates joined
// in arrival order, present empty remains present (and invalid for int?).
pub(super) fn scalar(request: &Request, key: &str) -> Option<String> {
	let values: Vec<_> =
		url::form_urlencoded::parse(request.uri().query().unwrap_or_default().as_bytes())
			.filter(|(k, _)| k.eq_ignore_ascii_case(key))
			.map(|(_, v)| v.into_owned())
			.collect();
	(!values.is_empty()).then(|| {
		values
			.into_iter()
			.filter(|v| !v.is_empty())
			.collect::<Vec<_>>()
			.join(",")
	})
}
pub(super) fn query_number(request: &Request, key: &str) -> Result<Option<i32>, AuthError> {
	scalar(request, key)
		.map(|v| v.trim_ascii().parse::<i32>().map_err(|_| invalid()))
		.transpose()
}
async fn list(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let year = query_number(&request, "year")?;
	let kind = scalar(&request, "kind").unwrap_or_default();
	let kind = kind.trim();
	if !kind.is_empty() {
		validate_kind(kind)?;
	}
	let mut rows = visible(&auth.pool).await?;
	let mut years = std::collections::BTreeMap::<Option<i32>, usize>::new();
	for e in &rows {
		*years.entry(e.date_year).or_default() += 1;
	}
	let mut years: Vec<_> = years.into_iter().collect();
	years.sort_by_key(|(year, _)| std::cmp::Reverse(*year));
	rows.retain(|e| {
		year.is_none_or(|y| e.date_year == Some(y)) && (kind.is_empty() || kind == e.kind)
	});
	rows.sort_by(date::compare);
	Ok(access.respond(Json(json!({"years":years.into_iter().map(|(year,count)|json!({"year":year,"count":count})).collect::<Vec<_>>(),"total":rows.len(),"events":rows.iter().map(Event::item).collect::<Vec<_>>()})).into_response()))
}
async fn detail(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	Ok(access.respond(
		Json(json!({"event":detail_value(&auth.pool,&load(&auth.pool,id).await?).await?}))
			.into_response(),
	))
}
pub(super) async fn detail_value(pool: &sqlx::PgPool, e: &Event) -> Result<Value, AuthError> {
	let mut value = e.item();
	let object = value.as_object_mut().ok_or(AuthError::Database)?;
	object.extend(serde_json::from_value::<serde_json::Map<String,Value>>(json!({"notes":e.notes,"sourceNote":e.source_note,"documents":documents::list(pool,e.id).await?,"programme":programme::embed(pool,e.id).await?,"liedAufnahmen":history::aufnahmen(pool,e.id).await?,"rowVersion":e.row_version,"createdAt":e.created_at,"updatedAt":e.updated_at})).map_err(|_|AuthError::Database)?);
	Ok(value)
}
async fn create(
	State(origin): State<Arc<Origin>>,
	request: Request,
) -> Result<Response, AuthError> {
	write(origin, None, request).await
}
async fn patch(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	write(origin, Some(id), request).await
}
fn validate_kind(kind: &str) -> Result<(), AuthError> {
	if [
		"concert", "service", "wedding", "funeral", "festival", "other",
	]
	.contains(&kind)
	{
		Ok(())
	} else {
		Err(fail(StatusCode::BAD_REQUEST, "Unbekannter Auftrittstyp."))
	}
}
pub(super) fn optional(
	raw: &str,
	max: usize,
	message: &'static str,
) -> Result<Option<String>, AuthError> {
	let v = raw.trim();
	if v.encode_utf16().count() > max {
		return Err(fail(StatusCode::BAD_REQUEST, message));
	}
	Ok((!v.is_empty()).then(|| v.to_owned()))
}
async fn write(
	origin: Arc<Origin>,
	id: Option<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = body(
		request,
		if id.is_some() {
			Shape::EventPatch
		} else {
			Shape::EventCreate
		},
	)
	.await?;
	let old = if let Some(id) = id {
		Some(load(&auth.pool, id).await?)
	} else {
		None
	};
	let now = auth.clock.now();
	let mut e = old.clone().unwrap_or_else(|| Event {
		id: Uuid::new_v4(),
		created_at: now,
		updated_at: now,
		..Default::default()
	});
	if let Some(v) = b["title"].as_str() {
		e.title = v.trim().into();
	}
	if e.title.is_empty() {
		return Err(fail(StatusCode::BAD_REQUEST, "Der Titel ist erforderlich."));
	}
	if e.title.encode_utf16().count() > 200 {
		return Err(fail(StatusCode::BAD_REQUEST, "Der Titel ist zu lang."));
	}
	if let Some(v) = b["kind"].as_str() {
		e.kind = v.trim().into();
	}
	validate_kind(&e.kind)?;
	for (key, target, max, message) in [
		(
			"venue",
			&mut e.venue,
			200,
			"Der Veranstaltungsort ist zu lang.",
		),
		("notes", &mut e.notes, 2000, "Die Hinweise sind zu lang."),
		(
			"sourcenote",
			&mut e.source_note,
			500,
			"Die Quellenangabe ist zu lang.",
		),
	] {
		if let Some(v) = b[key].as_str() {
			*target = optional(v, max, message)?;
		}
	}
	if let Some(v) = b["starttime"].as_str() {
		e.start_time = optional(v, usize::MAX, "")?;
		if let Some(v) = &e.start_time {
			let bytes = v.as_bytes();
			if bytes.len() != 5
				|| bytes[2] != b':'
				|| ![0, 1, 3, 4].iter().all(|i| bytes[*i].is_ascii_digit())
				|| v[..2].parse::<u32>().unwrap_or(24) > 23
				|| v[3..].parse::<u32>().unwrap_or(60) > 59
			{
				return Err(fail(
					StatusCode::BAD_REQUEST,
					"Die Uhrzeit muss im Format HH:MM angegeben werden.",
				));
			}
		}
	}
	let d = if id.is_none() {
		Some((&b, "dateyear", "datemonth", "dateday", "dateapproximate"))
	} else if b["date"].is_object() {
		Some((&b["date"], "year", "month", "day", "approximate"))
	} else {
		None
	};
	if let Some((d, y, m, day, a)) = d {
		e.date_year = d[y].as_i64().map(|n| n as i32);
		e.date_month = d[m].as_i64().map(|n| n as i32);
		e.date_day = d[day].as_i64().map(|n| n as i32);
		e.date_approximate = d[a].as_bool().unwrap_or(false);
		date::validate(e.date_year, e.date_month, e.date_day)?;
	}
	if old.as_ref().is_some_and(|old| {
		b["expectedrowversion"]
			.as_i64()
			.is_some_and(|v| v != old.row_version)
	}) {
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	if let Some(old) = old {
		let n=sqlx::query(r#"UPDATE events SET "Kind"=$2,"Title"=$3,"Venue"=$4,"StartTime"=$5,"Notes"=$6,"SourceNote"=$7,"DateYear"=$8,"DateMonth"=$9,"DateDay"=$10,"DateApproximate"=$11,"UpdatedAt"=$12,"UpdatedByAccountId"=$13,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$14 AND "DeletedAt" IS NULL"#).bind(e.id).bind(&e.kind).bind(&e.title).bind(&e.venue).bind(&e.start_time).bind(&e.notes).bind(&e.source_note).bind(e.date_year).bind(e.date_month).bind(e.date_day).bind(e.date_approximate).bind(now).bind(access.current.account_id).bind(old.row_version).execute(&auth.pool).await?.rows_affected();
		if n == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	} else {
		sqlx::query(r#"INSERT INTO events ("Id","Kind","Title","Venue","StartTime","Notes","SourceNote","DateYear","DateMonth","DateDay","DateApproximate","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$12,$13,0)"#).bind(e.id).bind(&e.kind).bind(&e.title).bind(&e.venue).bind(&e.start_time).bind(&e.notes).bind(&e.source_note).bind(e.date_year).bind(e.date_month).bind(e.date_day).bind(e.date_approximate).bind(now).bind(access.current.account_id).execute(&auth.pool).await?;
	}
	let response =
		Json(json!({"event":detail_value(&auth.pool,&load(&auth.pool,e.id).await?).await?}));
	Ok(access.respond(if id.is_none() {
		(
			StatusCode::CREATED,
			[(header::LOCATION, format!("/api/events/{}", e.id))],
			response,
		)
			.into_response()
	} else {
		response.into_response()
	}))
}
