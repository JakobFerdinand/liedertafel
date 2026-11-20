use super::{Job, enqueue, insert};
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
	catalogue::queries,
	clr_guid::GuidPath,
};
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, post},
};
use std::sync::Arc;
use uuid::Uuid;
const NOTHING_MORE: &str = "Es gibt keine weiteren Seiten zu lesen.";
const CONFLICT: &str = "Die Datei wurde zwischenzeitlich geändert. Bitte neu laden.";
fn fail(status: StatusCode, title: &'static str) -> AuthError {
	AuthError::Problem(status, title)
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	let mut routes = vec![
		(
			"/api/dateien/{id}/reading/retry",
			post(retry).fallback(crate::api::api_miss),
		),
		(
			"/api/dateien/{id}/reading/continue",
			post(continue_reading).fallback(crate::api::api_miss),
		),
	];
	routes.extend(super::programme_endpoints::routes());
	routes
}
async fn retry(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	act(origin, id, request, true).await
}
async fn continue_reading(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	act(origin, id, request, false).await
}
async fn act(
	origin: Arc<Origin>,
	id: Uuid,
	request: Request,
	restart: bool,
) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	if !origin.reading_enabled {
		return Err(fail(
			StatusCode::CONFLICT,
			"Das Lesen durch die KI ist noch nicht eingerichtet.",
		));
	}
	let datei = queries::dateien(&auth.pool, None, Some(id))
		.await?
		.pop()
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, "Datei nicht gefunden."))?;
	if datei.kind != "noten"
		|| datei.lied_id.is_none()
		|| !datei.content_type.eq_ignore_ascii_case("application/pdf")
	{
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			"Nur Noten eines Liedes werden von der KI gelesen.",
		));
	}
	let extraction = crate::extraction::Job::load(&auth.pool, id)
		.await?
		.filter(|j| matches!(j.status, 2 | 3))
		.ok_or_else(|| {
			fail(
				StatusCode::CONFLICT,
				"Die Textauswertung ist noch nicht abgeschlossen.",
			)
		})?;
	let text_full = extraction
		.text
		.as_ref()
		.is_some_and(|t| t.encode_utf16().count() >= origin.extraction.max_text_characters);
	for pass in 0..2 {
		let now = auth.clock.now();
		let mut version = None;
		if let Some(job) = Job::load(&auth.pool, id).await? {
			if job.status == 1 && !job.stale(now, &origin.reading) {
				return Err(fail(
					StatusCode::CONFLICT,
					"Die Seiten werden gerade gelesen.",
				));
			}
			let next = job
				.pages_read
				.checked_add(origin.reading.max_pages_per_request)
				.ok_or_else(|| fail(StatusCode::CONFLICT, NOTHING_MORE))?;
			let requeue = if !restart {
				if job.status == 3 && job.partial() && !text_full {
					Some((next, false, 0, true))
				} else if matches!(job.status, 0..=2) {
					None
				} else {
					return Err(fail(StatusCode::CONFLICT, NOTHING_MORE));
				}
			} else if matches!(job.status, 1 | 4) {
				Some((
					if job.failure_reason.as_deref() == Some(super::worker::NOTHING_READ)
						&& job.partial()
					{
						next
					} else {
						job.page_limit
					},
					job.fields_done,
					0,
					false,
				))
			} else {
				None
			};
			if let Some((limit, fields_done, attempts, clear_completed)) = requeue {
				let won=sqlx::query(r#"UPDATE reading_jobs SET "Status"=0,"PageLimit"=$3,"FieldsDone"=$4,"AttemptCount"=$5,"FailureReason"=NULL,"CompletedAt"=CASE WHEN $6 THEN NULL ELSE "CompletedAt" END,"LastAttemptAt"=NULL,"LastEnqueuedAt"=NULL,"UpdatedAt"=$7,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(id).bind(job.row_version).bind(limit).bind(fields_done).bind(attempts).bind(clear_completed).bind(now).execute(&auth.pool).await?.rows_affected();
				if won == 0 {
					return Err(fail(StatusCode::CONFLICT, CONFLICT));
				}
				version = Some(job.row_version + 1);
			} else if restart && job.status == 0 && job.last_enqueued_at.is_none() {
				version = Some(job.row_version);
			}
		} else {
			if !restart {
				return Err(fail(StatusCode::CONFLICT, NOTHING_MORE));
			}
			match insert(
				&auth.pool,
				id,
				access.current.account_id,
				origin.reading.max_pages_per_request,
				now,
			)
			.await
			{
				Ok(()) => version = Some(0),
				Err(e)
					if pass == 0
						&& e.as_database_error().is_some_and(|e| {
							e.is_unique_violation() && e.constraint() == Some("PK_reading_jobs")
						}) =>
				{
					continue;
				}
				Err(e) => return Err(e.into()),
			}
		}
		if let Some(version) = version {
			enqueue(
				&auth.pool,
				origin.reading_queue.as_ref(),
				id,
				version,
				auth.clock.as_ref(),
			)
			.await;
		}
		let job = Job::load(&auth.pool, id)
			.await?
			.ok_or(AuthError::Database)?;
		return Ok(access
			.respond(Json(serde_json::json!({"reading":job.payload(text_full)})).into_response()));
	}
	Err(fail(StatusCode::CONFLICT, CONFLICT))
}
