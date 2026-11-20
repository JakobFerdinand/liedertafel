use super::{Job, service};
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
	catalogue::queries,
	clr_guid::{GuidPath, try_parse},
};
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use std::sync::Arc;
const CONFLICT: &str = "Die Datei wurde zwischenzeitlich geändert. Bitte neu laden.";
fn fail(status: StatusCode, title: &'static str) -> AuthError {
	AuthError::Problem(status, title)
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		(
			"/api/dateien/videos",
			get(status).fallback(crate::api::api_miss),
		),
		(
			"/api/dateien/{id}/video/retry",
			post(retry).fallback(crate::api::api_miss),
		),
	]
}
async fn status(
	State(origin): State<Arc<Origin>>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, false)
		.await?;
	let mut ids = Vec::new();
	for (_, value) in
		url::form_urlencoded::parse(request.uri().query().unwrap_or_default().as_bytes())
			.filter(|(k, _)| k.eq_ignore_ascii_case("ids"))
	{
		for part in value.split(',') {
			if let Some(id) = try_parse(part)
				&& !ids.contains(&id)
			{
				ids.push(id);
			}
		}
	}
	if ids.len() > 100 {
		return Err(fail(
			StatusCode::BAD_REQUEST,
			"Es können höchstens 100 Dateien gleichzeitig angefragt werden.",
		));
	}
	let mut results = Vec::new();
	for id in ids {
		let live: bool = sqlx::query_scalar(
			r#"SELECT EXISTS(SELECT 1 FROM dateien WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
		)
		.bind(id)
		.fetch_one(&auth.pool)
		.await?;
		if live && let Some(job) = Job::load(&auth.pool, id).await? {
			results.push(job.payload());
		}
	}
	Ok(access.respond(Json(serde_json::json!({"results":results})).into_response()))
}
async fn retry(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let datei = queries::dateien(&auth.pool, None, Some(id))
		.await?
		.pop()
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, "Datei nicht gefunden."))?;
	if !super::is_material(&datei.kind, &datei.content_type) {
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			"Die Konvertierung ist nur für Videoaufnahmen möglich.",
		));
	}
	let now = auth.clock.now();
	for pass in 0..2 {
		let mut enqueue_version = None;
		if let Some(job) = Job::load(&auth.pool, id).await? {
			if job.status == 1 && !job.stale(now, &origin.video) {
				return Err(fail(
					StatusCode::CONFLICT,
					"Die Konvertierung läuft bereits.",
				));
			}
			if matches!(job.status, 2 | 3) {
				return Ok(access.respond(Json(job.payload()).into_response()));
			}
			if matches!(job.status, 1 | 4) {
				let n=sqlx::query(r#"UPDATE video_jobs SET "Status"=0,"FailureReason"=NULL,"AttemptCount"=0,"LastAttemptAt"=NULL,"LastEnqueuedAt"=NULL,"UpdatedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(id).bind(job.row_version).bind(now).execute(&auth.pool).await?.rows_affected();
				if n == 0 {
					return Err(fail(StatusCode::CONFLICT, CONFLICT));
				}
				enqueue_version = Some(job.row_version + 1);
			} else if job.status == 0 && job.last_enqueued_at.is_none() {
				enqueue_version = Some(job.row_version);
			}
		} else {
			let mut connection = auth.pool.acquire().await?;
			if service::insert(&mut connection, id, access.current.account_id, now)
				.await
				.is_err()
			{
				if pass == 0 {
					continue;
				}
				return Err(fail(StatusCode::CONFLICT, CONFLICT));
			}
			enqueue_version = Some(0);
		}
		if let Some(version) = enqueue_version {
			service::enqueue(
				&auth.pool,
				origin.video_queue.as_ref(),
				id,
				version,
				auth.clock.as_ref(),
			)
			.await;
		}
		let job = Job::load(&auth.pool, id)
			.await?
			.ok_or(AuthError::Database)?;
		return Ok(access.respond(Json(job.payload()).into_response()));
	}
	Err(fail(StatusCode::CONFLICT, CONFLICT))
}
