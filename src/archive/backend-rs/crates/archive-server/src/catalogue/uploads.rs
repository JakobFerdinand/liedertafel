//! Direct-to-Blob session state, independent of transfer blocks owned by the browser.
use super::{
	kinds, queries,
	writes::{CONFLICT, body, fail, field, invalid, string},
};
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
	storage::Storage,
};
use axum::{
	Json,
	extract::{Path, Request, State},
	http::{StatusCode, header},
	response::{IntoResponse, Response},
	routing::{MethodRouter, delete, post},
};
use chrono::{DateTime, Duration, Utc};
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;

pub const STORAGE_FAILURE: &str = "Speicherdienst nicht erreichbar.";
const NOT_FOUND: &str = "Upload nicht gefunden.";
const ABANDONED: &str = "Upload wurde abgebrochen.";
const OWNER: &str = "Nur die anlegendende Person kann den Upload abschließen.";
pub const DATEI_NOT_FOUND: &str = "Datei nicht gefunden.";
const TOO_LARGE: &str = "Die Datei ist zu groß.";
pub struct FinalizeContext {
	pub replaces_datei_id: Option<Uuid>,
	pub actor_account_id: Uuid,
	pub now: DateTime<Utc>,
}
/// Later extraction/video owners add durable rows using this transaction. No
/// worker is registered here; after-save delivery is deliberately best-effort.
pub trait FinalizedHook: Send + Sync {
	fn before_save<'a>(
		&'a self,
		_datei: &'a queries::Datei,
		_context: &'a FinalizeContext,
		_connection: &'a mut sqlx::PgConnection,
	) -> futures::future::BoxFuture<'a, Result<(), AuthError>> {
		Box::pin(async { Ok(()) })
	}
	fn after_save<'a>(
		&'a self,
		_datei: &'a queries::Datei,
		_pool: &'a sqlx::PgPool,
	) -> futures::future::BoxFuture<'a, Result<(), AuthError>> {
		Box::pin(async { Ok(()) })
	}
}

#[derive(Clone)]
pub struct UploadOptions {
	pub max_bytes: i64,
	pub collection_bytes: i64,
	pub block_bytes: i64,
	pub lifetime: Duration,
	pub retention_days: i64,
}
impl Default for UploadOptions {
	fn default() -> Self {
		Self {
			max_bytes: 10 * 1024 * 1024 * 1024,
			collection_bytes: 40 * 1024 * 1024 * 1024,
			block_bytes: 8 * 1024 * 1024,
			lifetime: Duration::minutes(30),
			retention_days: 7,
		}
	}
}
impl UploadOptions {
	pub fn from_env() -> Result<Self, crate::storage::StorageError> {
		let mut result = Self::default();
		for (name, target) in [
			("MaxUploadBytes", &mut result.max_bytes),
			("MaxCollectionBytes", &mut result.collection_bytes),
			("UploadBlockBytes", &mut result.block_bytes),
		] {
			if let Ok(v) = std::env::var(format!("Archive__Assets__{name}")) {
				*target = v
					.parse()
					.map_err(|_| crate::storage::StorageError::Configuration)?;
			}
		}
		if let Ok(v) = std::env::var("Archive__Assets__UploadSessionLifetime") {
			result.lifetime = crate::storage::parse_lifetime(&v)
				.ok_or(crate::storage::StorageError::Configuration)?;
		}
		if let Ok(v) = std::env::var("Archive__Papierkorb__RetentionDays") {
			result.retention_days = v
				.parse()
				.map_err(|_| crate::storage::StorageError::Configuration)?;
		}
		Ok(result)
	}
}
#[derive(sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
struct Pending {
	id: Uuid,
	blob_name: String,
	content_type: String,
	max_size_bytes: i64,
	upload_ticket_expires_at: DateTime<Utc>,
	state: i32,
	created_by_account_id: Uuid,
	declared_file_name: Option<String>,
	declared_size_bytes: Option<i64>,
	datei_kind: Option<String>,
	description: Option<String>,
	event_id: Option<Uuid>,
	finalized_datei_id: Option<Uuid>,
	is_partitur: bool,
	lied_id: Option<Uuid>,
	recorded_at_event_id: Option<Uuid>,
	replaces_datei_id: Option<Uuid>,
	stimme: Option<String>,
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		(
			"/api/lieder/{id}/dateien/upload-session",
			post(initiate_lied).fallback(crate::api::api_miss),
		),
		(
			"/api/events/{id}/dateien/upload-session",
			post(initiate_event).fallback(crate::api::api_miss),
		),
		(
			"/api/upload-sessions/{id}/renew",
			post(renew).fallback(crate::api::api_miss),
		),
		(
			"/api/upload-sessions/{id}",
			delete(cancel).fallback(crate::api::api_miss),
		),
		(
			"/api/upload-sessions/{id}/finalize",
			post(finalize).fallback(crate::api::api_miss),
		),
	]
}
fn storage(origin: &Origin) -> Result<&dyn Storage, AuthError> {
	origin.storage.as_deref().ok_or_else(storage_error)
}
fn storage_error() -> AuthError {
	fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE)
}
fn clean(v: Option<&str>) -> Option<&str> {
	v.map(str::trim).filter(|s| !s.is_empty())
}
fn uuid(body: &Value, name: &str) -> Result<Option<Uuid>, AuthError> {
	string(field(body, name))?
		.map(Uuid::parse_str)
		.transpose()
		.map_err(|_| invalid())
}
fn number(body: &Value, name: &str) -> Result<Option<i64>, AuthError> {
	let v = field(body, name);
	if v.is_null() {
		Ok(None)
	} else {
		v.as_i64().map(Some).ok_or_else(invalid)
	}
}
async fn initiate_lied(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	initiate(origin, id, false, request).await
}
async fn initiate_event(
	State(origin): State<Arc<Origin>>,
	Path(id): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	initiate(origin, id, true, request).await
}
async fn initiate(
	origin: Arc<Origin>,
	id: Uuid,
	event: bool,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let exists = if event {
		sqlx::query_scalar::<_, bool>(
			r#"SELECT EXISTS(SELECT 1 FROM events WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
		)
		.bind(id)
		.fetch_one(&auth.pool)
		.await?
	} else {
		queries::lied(&auth.pool, id).await?.is_some()
	};
	if !exists {
		return Err(fail(
			StatusCode::NOT_FOUND,
			if event {
				"Auftritt nicht gefunden."
			} else {
				"Das Lied wurde nicht gefunden."
			},
		));
	}
	let body = body(request, super::binding::Shape::Upload).await?;
	// The frozen typed request binds every supplied property before domain
	// decisions, including fields a replacement or Partitur will later ignore.
	if !body.is_null() && !body.is_object() {
		return Err(invalid());
	}
	for name in ["kind", "stimme", "description", "fileName", "contentType"] {
		string(field(&body, name))?;
	}
	for name in ["replacesDateiId", "recordedAtEventId"] {
		uuid(&body, name)?;
	}
	number(&body, "sizeBytes")?;
	let partitur = field(&body, "isPartitur");
	if !partitur.is_null() && !partitur.is_boolean() {
		return Err(invalid());
	}
	let replaced = if let Some(old) = uuid(&body, "replacesDateiId")? {
		Some(
			queries::dateien(&auth.pool, None, Some(old))
				.await?
				.pop()
				.filter(|d| {
					if event {
						d.event_id == Some(id) && d.kind == "sonstiges"
					} else {
						d.lied_id == Some(id)
					}
				})
				.ok_or_else(|| fail(StatusCode::NOT_FOUND, DATEI_NOT_FOUND))?,
		)
	} else {
		None
	};
	let kind = replaced.as_ref().map(|d| d.kind.clone()).unwrap_or(
		string(field(&body, "kind"))?
			.unwrap_or(if event { "sonstiges" } else { "noten" })
			.trim()
			.to_lowercase(),
	);
	if !queries::DATEI_KINDS.contains(&kind.as_str()) {
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			"Unbekannter Dateityp.",
		));
	}
	if event && !matches!(kind.as_str(), "sonstiges" | "aufnahme") {
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			"Dieser Dateityp gehört zu einem Lied und nicht zu einem Auftritt.",
		));
	}
	let content_type = clean(string(field(&body, "contentType"))?)
		.unwrap_or(if event && kind == "aufnahme" {
			"video/mp4"
		} else {
			kinds::default_type(&kind)
		})
		.to_lowercase();
	if !(if event && kind == "aufnahme" {
		content_type.starts_with("audio/") || content_type.starts_with("video/")
	} else {
		kinds::allowed(&kind, &content_type)
	}) {
		return Err(fail(StatusCode::UNPROCESSABLE_ENTITY, kinds::UNSUPPORTED));
	}
	let recorded_at = if let Some(d) = &replaced {
		d.recorded_at_event_id
	} else if !event && kind == "aufnahme" {
		uuid(&body, "recordedAtEventId")?
	} else {
		None
	};
	if let Some(event_id) = recorded_at.filter(|_| replaced.is_none()) {
		let visible: bool = sqlx::query_scalar(
			r#"SELECT EXISTS(SELECT 1 FROM events WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
		)
		.bind(event_id)
		.fetch_one(&auth.pool)
		.await?;
		if !visible {
			return Err(fail(StatusCode::BAD_REQUEST, "Auftritt nicht gefunden."));
		}
	}
	let is_partitur = replaced
		.as_ref()
		.map(|d| d.is_partitur)
		.unwrap_or(kind == "noten" && field(&body, "isPartitur").as_bool() == Some(true));
	let stimme = if let Some(d) = &replaced {
		d.stimme.clone()
	} else if is_partitur || matches!(kind.as_str(), "aufnahme" | "sonstiges") {
		None
	} else {
		clean(string(field(&body, "stimme"))?).map(str::to_owned)
	};
	if stimme
		.as_ref()
		.is_some_and(|s| s.encode_utf16().count() > 200)
	{
		return Err(fail(StatusCode::BAD_REQUEST, "Ein Feld ist zu lang."));
	}
	if matches!(kind.as_str(), "noten" | "uebe") && !is_partitur && stimme.is_none() {
		return Err(fail(
			StatusCode::BAD_REQUEST,
			"Bitte eine Stimme angeben oder die Datei als Partitur kennzeichnen.",
		));
	}
	let description = clean(string(field(&body, "description"))?)
		.map(str::to_owned)
		.or_else(|| replaced.as_ref().and_then(|d| d.description.clone()));
	let file = clean(string(field(&body, "fileName"))?);
	if description
		.as_ref()
		.is_some_and(|s| s.encode_utf16().count() > 500)
	{
		return Err(fail(
			StatusCode::BAD_REQUEST,
			"Die Beschreibung ist zu lang.",
		));
	}
	if file.is_some_and(|s| s.encode_utf16().count() > 300) {
		return Err(fail(StatusCode::BAD_REQUEST, "Der Dateiname ist zu lang."));
	}
	let size = number(&body, "sizeBytes")?;
	let options = &origin.uploads;
	if size.is_some_and(|s| s > options.max_bytes) {
		return Err(fail(StatusCode::PAYLOAD_TOO_LARGE, TOO_LARGE));
	}
	let pending:i64=sqlx::query_scalar(r#"SELECT COALESCE(sum(COALESCE("DeclaredSizeBytes","MaxSizeBytes")),0)::bigint FROM upload_sessions WHERE (CASE WHEN $2 THEN "EventId"=$1 ELSE "LiedId"=$1 END) AND "State"=0"#).bind(id).bind(event).fetch_one(&auth.pool).await?;
	let stored:i64=sqlx::query_scalar(r#"SELECT COALESCE(sum("SizeBytes"),0)::bigint FROM dateien WHERE CASE WHEN $2 THEN "EventId"=$1 ELSE "LiedId"=$1 END"#).bind(id).bind(event).fetch_one(&auth.pool).await?;
	if i128::from(size.unwrap_or(0)) + i128::from(pending) + i128::from(stored)
		> i128::from(options.collection_bytes)
	{
		return Err(fail(StatusCode::PAYLOAD_TOO_LARGE, TOO_LARGE));
	}
	let now = auth.clock.now();
	let session = Uuid::new_v4();
	let blob = format!("pending/{}", Uuid::new_v4());
	let expires = now + options.lifetime;
	sqlx::query(r#"INSERT INTO upload_sessions ("Id","LiedId","DateiKind","IsPartitur","Stimme","Description","ReplacesDateiId","RecordedAtEventId","BlobName","ContentType","MaxSizeBytes","DeclaredFileName","DeclaredSizeBytes","UploadTicketExpiresAt","State","CreatedByAccountId","CreatedAt","EventId") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,0,$15,$16,$17)"#).bind(session).bind((!event).then_some(id)).bind(kind).bind(is_partitur).bind(stimme).bind(description).bind(replaced.as_ref().map(|d|d.id)).bind(recorded_at).bind(&blob).bind(content_type).bind(options.max_bytes).bind(file).bind(size).bind(expires).bind(access.current.account_id).bind(now).bind(event.then_some(id)).execute(&auth.pool).await?;
	let url = storage(&origin)?
		.upload_ticket(&blob, options.lifetime)
		.await
		.map_err(|_| storage_error())?;
	Ok(access.respond(
		(
			StatusCode::CREATED,
			[(
				header::LOCATION,
				format!("/api/{}/{id}", if event { "events" } else { "lieder" }),
			)],
			Json(ticket(
				session,
				None,
				url,
				expires,
				options.max_bytes,
				options.block_bytes,
			)),
		)
			.into_response(),
	))
}
fn ticket(
	id: Uuid,
	blob: Option<&str>,
	url: String,
	expires: DateTime<Utc>,
	max: i64,
	block: i64,
) -> Value {
	json!({"uploadSessionId":id,"blobName":blob,"uploadUrl":url,"expiresAt":expires,"maxBytes":max,"blockBytes":block})
}
async fn session(origin: &Origin, id: Uuid, actor: Uuid) -> Result<Pending, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Database)?;
	let session = sqlx::query_as::<_, Pending>(r#"SELECT * FROM upload_sessions WHERE "Id"=$1"#)
		.bind(id)
		.fetch_optional(&auth.pool)
		.await?
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, NOT_FOUND))?;
	if session.created_by_account_id != actor {
		return Err(fail(StatusCode::FORBIDDEN, OWNER));
	}
	Ok(session)
}
async fn renew(
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
	let s = session(&origin, id, access.current.account_id).await?;
	if s.state == 1 {
		return Err(fail(
			StatusCode::CONFLICT,
			"Der Upload wurde bereits abgeschlossen.",
		));
	}
	if s.state != 0 {
		return Err(fail(StatusCode::CONFLICT, ABANDONED));
	}
	let expires = auth.clock.now() + origin.uploads.lifetime;
	let changed = sqlx::query(
		r#"UPDATE upload_sessions SET "UploadTicketExpiresAt"=$2 WHERE "Id"=$1 AND "State"=0"#,
	)
	.bind(id)
	.bind(expires)
	.execute(&auth.pool)
	.await?
	.rows_affected();
	// EF marks State as a concurrency token. Renew/cancel do not catch that
	// failed save in the oracle: never mint/delete after losing that race.
	if changed == 0 {
		return Err(AuthError::Database);
	}
	let url = storage(&origin)?
		.upload_ticket(&s.blob_name, origin.uploads.lifetime)
		.await
		.map_err(|_| storage_error())?;
	Ok(access.respond(
		Json(ticket(
			id,
			Some(&s.blob_name),
			url,
			expires,
			s.max_size_bytes,
			origin.uploads.block_bytes,
		))
		.into_response(),
	))
}
async fn cancel(
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
	let s = session(&origin, id, access.current.account_id).await?;
	if s.state != 0 {
		return Err(fail(
			StatusCode::CONFLICT,
			if s.state == 3 {
				"Der Upload wurde abgebrochen."
			} else {
				ABANDONED
			},
		));
	}
	let changed =
		sqlx::query(r#"UPDATE upload_sessions SET "State"=3 WHERE "Id"=$1 AND "State"=0"#)
			.bind(id)
			.execute(&auth.pool)
			.await?
			.rows_affected();
	if changed == 0 {
		return Err(AuthError::Database);
	}
	if let Ok(storage) = storage(&origin) {
		let _ = storage.delete(&s.blob_name).await;
	}
	Ok(access.respond(StatusCode::NO_CONTENT.into_response()))
}
async fn finalize(
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
	// Frozen FinalizeUploadRequest? binds before handler state decisions, even
	// for finalized/cancelled sessions. Invalid JSON must never be idempotent success.
	let body = body(request, super::binding::Shape::Finalize).await?;
	let s = session(&origin, id, access.current.account_id).await?;
	if s.state == 1 {
		let d = sqlx::query_as::<_, queries::Datei>(r#"SELECT * FROM dateien WHERE "Id"=$1"#)
			.bind(s.finalized_datei_id)
			.fetch_optional(&auth.pool)
			.await?
			.ok_or_else(|| fail(StatusCode::CONFLICT, CONFLICT))?;
		return Ok(access.respond(Json(json!({"datei":d.summary(Value::Null)})).into_response()));
	}
	if s.state != 0 {
		return Err(fail(
			StatusCode::CONFLICT,
			if s.state == 3 {
				"Der Upload wurde abgebrochen."
			} else {
				ABANDONED
			},
		));
	}
	if s.upload_ticket_expires_at < auth.clock.now() {
		sqlx::query(r#"UPDATE upload_sessions SET "State"=2 WHERE "Id"=$1 AND "State"=0"#)
			.bind(id)
			.execute(&auth.pool)
			.await?;
		return Err(fail(
			StatusCode::CONFLICT,
			"Der Uploadzeitraum ist abgelaufen.",
		));
	}
	let declared_size = number(&body, "sizeBytes")?;
	let declared_file = string(field(&body, "fileName"))?.map(str::trim);
	if declared_size
		.zip(s.declared_size_bytes)
		.is_some_and(|(a, b)| a != b)
		|| declared_file
			.filter(|s| !s.is_empty())
			.zip(s.declared_file_name.as_deref())
			.is_some_and(|(a, b)| a != b)
	{
		return Err(fail(
			StatusCode::CONFLICT,
			"Die Datei passt nicht zur bestehenden Uploadsitzung.",
		));
	}
	let kind = s.datei_kind.as_deref().unwrap_or("noten");
	if !queries::DATEI_KINDS.contains(&kind) || (s.lied_id.is_none() && s.event_id.is_none()) {
		abandon(&origin, &s).await?;
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			"Dieser Dateityp kann noch nicht hochgeladen werden.",
		));
	}
	let replaces = if let Some(old) = s.replaces_datei_id {
		let d = queries::dateien(&auth.pool, None, Some(old)).await?.pop();
		if d.is_none() {
			abandon(&origin, &s).await?;
			return Err(fail(
				StatusCode::CONFLICT,
				"Die Datei wurde inzwischen ersetzt oder gelöscht.",
			));
		}
		d
	} else {
		None
	};
	let provider = storage(&origin)?;
	let info = provider
		.probe(&s.blob_name)
		.await
		.map_err(|_| storage_error())?;
	// Preserve probe/header ordering even when probe says the object is missing.
	let header = if info.as_ref().is_some_and(|p| p.size_bytes == 0) {
		Some(vec![])
	} else {
		provider
			.read_header(&s.blob_name, 64)
			.await
			.map_err(|_| storage_error())?
	};
	let info = info.ok_or_else(|| {
		fail(
			StatusCode::CONFLICT,
			"Die Datei wurde noch nicht übertragen.",
		)
	})?;
	let stored:i64=sqlx::query_scalar(r#"SELECT COALESCE(sum("SizeBytes"),0)::bigint FROM dateien WHERE CASE WHEN $1::uuid IS NOT NULL THEN "LiedId"=$1 ELSE "EventId"=$2 END"#).bind(s.lied_id).bind(s.event_id).fetch_one(&auth.pool).await?;
	if i128::from(info.size_bytes) > i128::from(s.max_size_bytes)
		|| i128::from(info.size_bytes) + i128::from(stored)
			> i128::from(origin.uploads.collection_bytes)
	{
		abandon(&origin, &s).await?;
		return Err(fail(StatusCode::PAYLOAD_TOO_LARGE, TOO_LARGE));
	}
	let mut content = info
		.content_type
		.as_deref()
		.filter(|s| !s.is_empty() && !s.eq_ignore_ascii_case("application/octet-stream"))
		.unwrap_or(&s.content_type);
	let accepted = if s.event_id.is_some() && kind == "aufnahme" {
		content = kinds::detect(
			header.as_deref().unwrap_or_default(),
			!s.content_type.starts_with("audio/"),
		);
		info.size_bytes > 0
	} else {
		kinds::accepts(kind, header.as_deref(), content)
	};
	if !accepted {
		abandon(&origin, &s).await?;
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			kinds::invalid_message(kind),
		));
	}
	let datei = Uuid::new_v4();
	let target = format!("dateien/{datei}/{}", Uuid::new_v4());
	let now = auth.clock.now();
	provider
		.promote(&s.blob_name, &target)
		.await
		.map_err(|_| storage_error())?;
	let result=async {
		let mut tx=auth.pool.begin().await?;
		let won=sqlx::query(r#"UPDATE upload_sessions SET "State"=1,"FinalizedDateiId"=$2 WHERE "Id"=$1 AND "State"=0"#).bind(id).bind(datei).execute(&mut *tx).await?.rows_affected();
		if won==0{return Err(fail(StatusCode::CONFLICT,CONFLICT));}
		sqlx::query(r#"INSERT INTO dateien ("Id","LiedId","EventId","Kind","IsPartitur","Stimme","Description","RecordedAtEventId","BlobName","ContentType","SizeBytes","OriginalFileName","CreatedByAccountId","CreatedAt","RowVersion") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,1)"#).bind(datei).bind(s.lied_id).bind(s.event_id).bind(kind).bind(s.is_partitur).bind(&s.stimme).bind(&s.description).bind(s.recorded_at_event_id).bind(&target).bind(content).bind(info.size_bytes as i64).bind(&s.declared_file_name).bind(access.current.account_id).bind(now).execute(&mut *tx).await.map_err(save_error)?;
		if let Some(old)=replaces.as_ref(){super::trash::trash_datei(&mut tx,&auth.pool,provider,old,access.current.account_id,now,Some(datei)).await?;}
		let saved=sqlx::query_as::<_,queries::Datei>(r#"SELECT * FROM dateien WHERE "Id"=$1"#).bind(datei).fetch_one(&mut *tx).await?;
		let context=FinalizeContext{replaces_datei_id:s.replaces_datei_id,actor_account_id:access.current.account_id,now};
		for hook in &origin.finalized_hooks {hook.before_save(&saved,&context,&mut tx).await?;}
		tx.commit().await.map_err(save_error)?;
		Ok::<_,AuthError>(saved)
	}.await;
	let saved = match result {
		Ok(saved) => saved,
		Err(error) => {
			let _ = provider.delete(&target).await;
			return Err(error);
		}
	};
	let _ = provider.delete(&s.blob_name).await;
	for hook in &origin.finalized_hooks {
		let _ = hook.after_save(&saved, &auth.pool).await;
	}
	Ok(access.respond(Json(json!({"datei":saved.summary(Value::Null)})).into_response()))
}
fn save_error(error: sqlx::Error) -> AuthError {
	if error
		.as_database_error()
		.is_some_and(|e| e.is_unique_violation())
	{
		fail(StatusCode::CONFLICT, CONFLICT)
	} else {
		AuthError::Database
	}
}
async fn abandon(origin: &Origin, s: &Pending) -> Result<(), AuthError> {
	if let Ok(provider) = storage(origin) {
		let _ = provider.delete(&s.blob_name).await;
	}
	let auth = origin.auth.as_deref().ok_or(AuthError::Database)?;
	sqlx::query(r#"UPDATE upload_sessions SET "State"=2 WHERE "Id"=$1 AND "State"=0"#)
		.bind(s.id)
		.execute(&auth.pool)
		.await?;
	Ok(())
}
