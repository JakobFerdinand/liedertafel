//! Whole Auftritt recordings. Identity/publication and the two immutable Datei
//! slots are independent from Programme entries. Bytes go directly to Blob.
mod passages;
use crate::{
	Origin,
	auth::{AccessPolicy, Auth, AuthError},
	binding::Shape,
	catalogue::{
		queries::{self, Datei},
		trash,
	},
	clr_guid::GuidPath,
};
use axum::{
	Json,
	extract::{Path, Request, State},
	http::{StatusCode, header},
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, put},
};
use chrono::{DateTime, Utc};
use serde_json::{Value, json};
use std::sync::Arc;
use uuid::Uuid;
const NOT_FOUND: &str = "Aufnahme nicht gefunden.";
const CONFLICT: &str = "Die Aufnahme wurde zwischenzeitlich geändert.";
const STORAGE_FAILURE: &str = "Speicherdienst nicht erreichbar.";
fn fail(status: StatusCode, title: &'static str) -> AuthError {
	AuthError::Problem(status, title)
}
fn auth(origin: &Origin) -> Result<&Auth, AuthError> {
	origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))
}
fn number(v: &Value) -> Option<f64> {
	v.as_f64()
		.or_else(|| v.as_str().and_then(|s| s.parse().ok()))
}
fn guid(v: &Value) -> Option<Uuid> {
	v.as_str().and_then(|s| Uuid::parse_str(s).ok())
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		(
			"/api/events/{id}/recordings",
			get(list).post(create).fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}",
			axum::routing::patch(patch).fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}/dateien/{slot}",
			put(attach).fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}/access",
			get(access).fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}/passages",
			get(passages::list)
				.post(passages::create)
				.fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}/passages/{passageId}",
			axum::routing::patch(passages::patch).fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}/passages/{passageId}/delete",
			axum::routing::post(passages::delete).fallback(crate::api::api_miss),
		),
		(
			"/api/recordings/{id}/passages/review",
			axum::routing::post(passages::review).fallback(crate::api::api_miss),
		),
	]
	.into_iter()
	.map(|(p, r)| {
		(
			p,
			r.layer(axum::middleware::from_fn(crate::events::json_media_type)),
		)
	})
	.collect()
}
#[derive(Clone, Debug, sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
struct Recording {
	id: Uuid,
	event_id: Uuid,
	label: String,
	kind: String,
	original_datei_id: Option<Uuid>,
	playback_datei_id: Option<Uuid>,
	duration_seconds: Option<f64>,
	download_enabled: bool,
	published_at: Option<DateTime<Utc>>,
	published_by_account_id: Option<Uuid>,
	created_at: DateTime<Utc>,
	created_by_account_id: Uuid,
	row_version: i64,
}
struct Files {
	original: Option<Datei>,
	copy: Option<Datei>,
}
impl Files {
	fn playable(&self, kind: &str) -> Option<&Datei> {
		self.copy
			.as_ref()
			.filter(|d| queries::is_playable(&d.content_type, kind))
			.or_else(|| {
				self.original
					.as_ref()
					.filter(|d| queries::is_playable(&d.content_type, kind))
			})
	}
	fn preserved(&self) -> Option<&Datei> {
		self.original.as_ref().or(self.copy.as_ref())
	}
	fn state(&self, kind: &str) -> &'static str {
		if self.playable(kind).is_some() {
			"ready"
		} else if self.preserved().is_some() {
			"needsPlaybackCopy"
		} else {
			"missing"
		}
	}
	fn source(&self, kind: &str) -> Option<&'static str> {
		self.playable(kind).map(|d| {
			if self.copy.as_ref().is_some_and(|c| c.id == d.id) {
				"playbackCopy"
			} else {
				"original"
			}
		})
	}
}
async fn file(pool: &sqlx::PgPool, id: Option<Uuid>) -> Result<Option<Datei>, AuthError> {
	if let Some(id) = id {
		Ok(
			sqlx::query_as(r#"SELECT * FROM dateien WHERE "Id"=$1 AND "DeletedAt" IS NULL"#)
				.bind(id)
				.fetch_optional(pool)
				.await?,
		)
	} else {
		Ok(None)
	}
}
async fn files(pool: &sqlx::PgPool, r: &Recording) -> Result<Files, AuthError> {
	Ok(Files {
		original: file(pool, r.original_datei_id).await?,
		copy: file(pool, r.playback_datei_id).await?,
	})
}
async fn load(pool: &sqlx::PgPool, id: Uuid) -> Result<Recording, AuthError> {
	sqlx::query_as(r#"SELECT * FROM recordings WHERE "Id"=$1"#)
		.bind(id)
		.fetch_optional(pool)
		.await?
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, NOT_FOUND))
}
fn slot(d: Option<&Datei>, kind: &str) -> Value {
	json!({"file":d.map(|d|json!({"dateiId":d.id,"contentType":d.content_type,"sizeBytes":d.size_bytes,"fileName":d.original_file_name,"createdAt":d.created_at,"playable":queries::is_playable(&d.content_type,kind),"storageTier":d.storage_tier,"rowVersion":d.row_version}))})
}
async fn payload(
	pool: &sqlx::PgPool,
	r: &Recording,
	actor: Uuid,
	editor: bool,
) -> Result<Value, AuthError> {
	let f = files(pool, r).await?;
	let p = f.playable(&r.kind);
	Ok(
		json!({"id":r.id,"eventId":r.event_id,"label":r.label,"kind":r.kind,"isPublished":r.published_at.is_some(),"downloadEnabled":r.download_enabled,"durationSeconds":r.duration_seconds,"playback":{"state":f.state(&r.kind),"source":f.source(&r.kind),"dateiId":p.map(|d|d.id),"contentType":p.map(|d|&d.content_type),"sizeBytes":p.map(|d|d.size_bytes)},"createdAt":r.created_at,"editor":if editor{json!({"version":r.row_version,"canChangeFiles":r.created_by_account_id==actor,"publishedAt":r.published_at,"original":slot(f.original.as_ref(),&r.kind),"playbackCopy":slot(f.copy.as_ref(),&r.kind)})}else{Value::Null}}),
	)
}
async fn member_visible(pool: &sqlx::PgPool, r: &Recording, editor: bool) -> Result<(), AuthError> {
	let live: bool = sqlx::query_scalar(
		r#"SELECT EXISTS(SELECT 1 FROM events WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
	)
	.bind(r.event_id)
	.fetch_one(pool)
	.await?;
	if editor || (r.published_at.is_some() && live) {
		Ok(())
	} else {
		Err(fail(StatusCode::NOT_FOUND, NOT_FOUND))
	}
}
async fn list(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let live: bool = sqlx::query_scalar(
		r#"SELECT EXISTS(SELECT 1 FROM events WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
	)
	.bind(id)
	.fetch_one(&auth.pool)
	.await?;
	if !live {
		return Err(fail(
			StatusCode::NOT_FOUND,
			"Der Auftritt wurde nicht gefunden.",
		));
	}
	let rows: Vec<Recording> =
		sqlx::query_as(r#"SELECT * FROM recordings WHERE "EventId"=$1 ORDER BY "CreatedAt","Id""#)
			.bind(id)
			.fetch_all(&auth.pool)
			.await?;
	let mut values = Vec::new();
	for r in rows
		.iter()
		.filter(|r| a.current.is_editor() || r.published_at.is_some())
	{
		values.push(payload(&auth.pool, r, a.current.account_id, a.current.is_editor()).await?);
	}
	Ok(a.respond(Json(json!({"eventId":id,"recordings":values})).into_response()))
}
fn label(raw: &str) -> Result<String, AuthError> {
	let v = raw.trim();
	if v.is_empty() {
		Err(fail(
			StatusCode::BAD_REQUEST,
			"Die Bezeichnung ist erforderlich.",
		))
	} else if v.encode_utf16().count() > 200 {
		Err(fail(
			StatusCode::BAD_REQUEST,
			"Die Bezeichnung ist zu lang.",
		))
	} else {
		Ok(v.into())
	}
}
async fn create(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let actor = a.current.account_id;
	let b = crate::events::body(request, Shape::RecordingCreate).await?;
	let label = label(b["label"].as_str().unwrap_or_default())?;
	let kind = b["kind"].as_str().unwrap_or_default().trim();
	if !matches!(kind, "audio" | "video") {
		return Err(fail(StatusCode::BAD_REQUEST, "Unbekannte Aufnahmeart."));
	}
	let live: bool = sqlx::query_scalar(
		r#"SELECT EXISTS(SELECT 1 FROM events WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
	)
	.bind(id)
	.fetch_one(&auth.pool)
	.await?;
	if !live {
		return Err(fail(
			StatusCode::NOT_FOUND,
			"Der Auftritt wurde nicht gefunden.",
		));
	}
	let rid = Uuid::new_v4();
	let now = auth.clock.now();
	sqlx::query(r#"INSERT INTO recordings ("Id","EventId","Label","Kind","DownloadEnabled","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,false,$5,$6,$5,$6,0)"#).bind(rid).bind(id).bind(label).bind(kind).bind(now).bind(a.current.account_id).execute(&auth.pool).await?;
	Ok(a.respond(
		(
			StatusCode::CREATED,
			[(header::LOCATION, format!("/api/recordings/{rid}"))],
			Json(
				json!({"recording":payload(&auth.pool,&load(&auth.pool,rid).await?,actor,true).await?}),
			),
		)
			.into_response(),
	))
}
async fn patch(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = crate::events::body(request, Shape::RecordingPatch).await?;
	let actor = a.current.account_id;
	let new_label = b["label"].as_str().map(label).transpose()?;
	let duration = number(&b["durationseconds"]);
	if duration.is_some_and(|v| !v.is_finite() || v <= 0.0 || v > 172800.0) {
		return Err(fail(StatusCode::BAD_REQUEST, "Die Dauer ist ungültig."));
	}
	let mut r = load(&auth.pool, id).await?;
	if b["expectedversion"]
		.as_i64()
		.is_some_and(|v| v != r.row_version)
	{
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	let publish = b["ispublished"].as_bool();
	if publish == Some(true)
		&& r.published_at.is_none()
		&& files(&auth.pool, &r).await?.state(&r.kind) == "missing"
	{
		return Err(fail(
			StatusCode::CONFLICT,
			"Die Aufnahme kann erst veröffentlicht werden, wenn eine Datei hochgeladen ist.",
		));
	}
	let old = r.clone();
	if let Some(l) = new_label {
		r.label = l;
	}
	if publish == Some(true) && r.published_at.is_none() {
		r.published_at = Some(auth.clock.now());
		r.published_by_account_id = Some(a.current.account_id);
	} else if publish == Some(false) {
		r.published_at = None;
		r.published_by_account_id = None;
	}
	if let Some(v) = b["downloadenabled"].as_bool() {
		r.download_enabled = v;
	}
	if let Some(v) = duration {
		r.duration_seconds = Some(v);
	}
	if old.label != r.label
		|| old.published_at != r.published_at
		|| old.download_enabled != r.download_enabled
		|| old.duration_seconds != r.duration_seconds
	{
		let n=sqlx::query(r#"UPDATE recordings SET "Label"=$2,"PublishedAt"=$3,"PublishedByAccountId"=$4,"DownloadEnabled"=$5,"DurationSeconds"=$6,"UpdatedAt"=$7,"UpdatedByAccountId"=$8,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$9"#).bind(id).bind(&r.label).bind(r.published_at).bind(r.published_by_account_id).bind(r.download_enabled).bind(r.duration_seconds).bind(auth.clock.now()).bind(a.current.account_id).bind(old.row_version).execute(&auth.pool).await?.rows_affected();
		if n == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	}
	Ok(a.respond(
		Json(
			json!({"recording":payload(&auth.pool,&load(&auth.pool,id).await?,actor,true).await?}),
		)
		.into_response(),
	))
}
async fn attach(
	State(origin): State<Arc<Origin>>,
	Path((id, slot_name)): Path<(String, String)>,
	request: Request,
) -> Result<Response, AuthError> {
	let id = crate::clr_guid::try_parse(&id)
		.ok_or_else(|| fail(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig."))?;
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = crate::events::body(request, Shape::RecordingAttach).await?;
	let actor = a.current.account_id;
	let original = slot_name == "original";
	if !original && slot_name != "playback" {
		return Err(fail(StatusCode::NOT_FOUND, NOT_FOUND));
	}
	let mut r = load(&auth.pool, id).await?;
	if r.created_by_account_id != a.current.account_id {
		return Err(fail(
			StatusCode::FORBIDDEN,
			"Nur die anlegende Person kann die Dateien dieser Aufnahme ändern.",
		));
	}
	let datei = guid(&b["dateiid"]).ok_or_else(|| {
		fail(
			StatusCode::BAD_REQUEST,
			"Bitte eine hochgeladene Datei angeben.",
		)
	})?;
	let automatic: bool =
		sqlx::query_scalar(r#"SELECT EXISTS(SELECT 1 FROM video_jobs WHERE "PlaybackDateiId"=$1)"#)
			.bind(datei)
			.fetch_one(&auth.pool)
			.await?;
	let d = queries::dateien(&auth.pool, None, Some(datei))
		.await?
		.pop()
		.filter(|d| {
			d.event_id == Some(r.event_id)
				&& d.kind == "aufnahme"
				&& Some(d.id) != r.original_datei_id
				&& !automatic
		})
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, "Datei nicht gefunden."))?;
	if (if original {
		r.original_datei_id
	} else {
		r.playback_datei_id
	}) == Some(d.id)
	{
		return Ok(a.respond(
			Json(json!({"recording":payload(&auth.pool,&r,actor,true).await?})).into_response(),
		));
	}
	let used:bool=sqlx::query_scalar(r#"SELECT EXISTS(SELECT 1 FROM recordings WHERE "Id"<>$1 AND ("OriginalDateiId"=$2 OR "PlaybackDateiId"=$2))"#).bind(id).bind(d.id).fetch_one(&auth.pool).await?;
	if used
		|| (if original {
			r.playback_datei_id
		} else {
			r.original_datei_id
		}) == Some(d.id)
	{
		return Err(fail(
			StatusCode::CONFLICT,
			"Diese Datei gehört bereits zu einer Aufnahme.",
		));
	}
	let provider = origin
		.storage
		.as_deref()
		.ok_or_else(|| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
	let now = auth.clock.now();
	let mut tx = auth.pool.begin().await?;
	if d.size_bytes == 0 || (!original && !queries::is_playable(&d.content_type, &r.kind)) {
		// Refused upload is trashed without replacing an earlier playable slot.
		sqlx::query(r#"UPDATE dateien SET "DeletedAt"=$2,"DeletedByAccountId"=$3,"RowVersion"="RowVersion"+1 WHERE "Id"=$1"#).bind(d.id).bind(now).bind(a.current.account_id).execute(&mut *tx).await?;
		tx.commit().await?;
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			if d.size_bytes == 0 {
				"Die Datei ist leer und kann nicht als Aufnahme gespeichert werden."
			} else {
				"Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme."
			},
		));
	}
	let before = files(&auth.pool, &r).await?;
	let played = before.playable(&r.kind).map(|d| d.id);
	let replaced = if original {
		before.original.as_ref()
	} else {
		before.copy.as_ref()
	};
	if let Some(old) = replaced {
		trash::trash_datei(
			&mut tx,
			&auth.pool,
			provider,
			old,
			a.current.account_id,
			now,
			Some(d.id),
		)
		.await
		.map_err(|error| match error {
			// Frozen attachment catches concurrency from every tracked row in
			// this save (Datei/tier included), using the recording's stale title.
			AuthError::Problem(StatusCode::CONFLICT, _) => fail(StatusCode::CONFLICT, CONFLICT),
			other => other,
		})?;
	}
	if original {
		r.original_datei_id = Some(d.id);
		if r.kind == "video"
			&& r.playback_datei_id.is_none()
			&& let Some(copy) = queries::live_copy(&auth.pool, d.id).await?
		{
			r.playback_datei_id = Some(copy.id);
			r.duration_seconds = None;
		}
	} else {
		r.playback_datei_id = Some(d.id);
	}
	// Resolve against the new slot and transaction-visible trash, not stale pool reads.
	let after = Files {
		original: sqlx::query_as(r#"SELECT * FROM dateien WHERE "Id"=$1 AND "DeletedAt" IS NULL"#)
			.bind(r.original_datei_id)
			.fetch_optional(&mut *tx)
			.await?,
		copy: sqlx::query_as(r#"SELECT * FROM dateien WHERE "Id"=$1 AND "DeletedAt" IS NULL"#)
			.bind(r.playback_datei_id)
			.fetch_optional(&mut *tx)
			.await?,
	};
	if after.playable(&r.kind).map(|d| d.id) != played {
		r.duration_seconds = None;
	}
	let result=sqlx::query(r#"UPDATE recordings SET "OriginalDateiId"=$2,"PlaybackDateiId"=$3,"DurationSeconds"=$4,"UpdatedAt"=$5,"UpdatedByAccountId"=$6,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$7"#).bind(id).bind(r.original_datei_id).bind(r.playback_datei_id).bind(r.duration_seconds).bind(now).bind(a.current.account_id).bind(r.row_version).execute(&mut *tx).await;
	match result {
		Ok(n) if n.rows_affected() > 0 => {}
		Ok(_) => return Err(fail(StatusCode::CONFLICT, CONFLICT)),
		Err(sqlx::Error::Database(ref e)) if e.code().as_deref() == Some("23505") => {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
		Err(e) => return Err(e.into()),
	};
	tx.commit().await?;
	Ok(a.respond(
		Json(
			json!({"recording":payload(&auth.pool,&load(&auth.pool,id).await?,actor,true).await?}),
		)
		.into_response(),
	))
}
async fn access(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let r = load(&auth.pool, id).await?;
	member_visible(&auth.pool, &r, a.current.is_editor()).await?;
	let f = files(&auth.pool, &r).await?;
	let d = f
		.playable(&r.kind)
		.or_else(|| f.preserved())
		.ok_or_else(|| {
			fail(
				StatusCode::NOT_FOUND,
				"Für diese Aufnahme wurde noch keine Datei hochgeladen.",
			)
		})?;
	let provider = origin.storage.as_deref();
	let mut view = None;
	let mut download = None;
	let lifetime = origin.read_ticket_lifetime;
	if f.playable(&r.kind).is_some() {
		view = Some(
			provider
				.ok_or_else(|| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?
				.read_ticket(&d.blob_name, lifetime, false, Some(&d.content_type))
				.await
				.map_err(|_| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?,
		);
	}
	if r.download_enabled {
		download = Some(
			provider
				.ok_or_else(|| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?
				.read_ticket(&d.blob_name, lifetime, true, Some(&d.content_type))
				.await
				.map_err(|_| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?,
		);
	}
	Ok(a.respond(Json(json!({"recordingId":id,"kind":r.kind,"playbackState":f.state(&r.kind),"source":f.source(&r.kind),"dateiId":d.id,"contentType":d.content_type,"sizeBytes":d.size_bytes,"durationSeconds":r.duration_seconds,"viewUrl":view,"downloadEnabled":r.download_enabled,"downloadUrl":download,"expiresAt":auth.clock.now()+lifetime})).into_response()))
}
