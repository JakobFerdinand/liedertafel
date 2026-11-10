use super::{Job, score, service};
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
	catalogue::queries,
	clr_guid::{GuidPath, try_parse as parse},
};
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use serde_json::{Value, json};
use std::sync::Arc;
fn fail(status: StatusCode, title: &'static str) -> AuthError {
	AuthError::Problem(status, title)
}
const CONFLICT: &str = "Die Datei wurde zwischenzeitlich geändert. Bitte neu laden.";
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		(
			"/api/dateien/extraction",
			get(status).fallback(crate::api::api_miss),
		),
		(
			"/api/dateien/{id}/extraction/retry",
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
			if let Some(id) = parse(part)
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
		if let Some(job) = Job::load(&auth.pool, id).await? {
			let kind: Option<String> = sqlx::query_scalar(
				r#"SELECT "Kind" FROM dateien WHERE "Id"=$1 AND "DeletedAt" IS NULL"#,
			)
			.bind(id)
			.fetch_optional(&auth.pool)
			.await?;
			if let Some(kind) = kind {
				results.push(payload(&origin, &job, &kind).await?);
			}
		}
	}
	Ok(access.respond(Json(json!({"results":results})).into_response()))
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
	if !service::extractable(&datei) {
		return Err(fail(
			StatusCode::UNPROCESSABLE_ENTITY,
			"Die Textauswertung ist nur für PDF-Dateien möglich.",
		));
	}
	let now = auth.clock.now();
	for pass in 0..2 {
		let existing = Job::load(&auth.pool, id).await?;
		let mut enqueue = false;
		if let Some(job) = existing {
			if job.status == 1 && !job.stale(now, &origin.extraction) {
				return Err(fail(StatusCode::CONFLICT, "Die Auswertung läuft bereits."));
			}
			if matches!(job.status, 2 | 3) {
				return Ok(access
					.respond(Json(payload(&origin, &job, &datei.kind).await?).into_response()));
			}
			if matches!(job.status, 1 | 4) {
				let n=sqlx::query(r#"UPDATE extraction_jobs SET "Status"=0,"FailureReason"=NULL,"AttemptCount"=0,"LastAttemptAt"=NULL,"LastEnqueuedAt"=NULL,"UpdatedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1 AND "RowVersion"=$2"#).bind(id).bind(job.row_version).bind(now).execute(&auth.pool).await?.rows_affected();
				if n == 0 {
					return Err(fail(StatusCode::CONFLICT, CONFLICT));
				}
				enqueue = true;
			} else if job.status == 0 && job.last_enqueued_at.is_none() {
				enqueue = true;
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
			enqueue = true;
		}
		if enqueue {
			service::enqueue(
				&auth.pool,
				origin.extraction_queue.as_ref(),
				id,
				auth.clock.as_ref(),
			)
			.await;
		}
		let job = Job::load(&auth.pool, id)
			.await?
			.ok_or(AuthError::Database)?;
		return Ok(access.respond(Json(payload(&origin, &job, &datei.kind).await?).into_response()));
	}
	Err(fail(StatusCode::CONFLICT, CONFLICT))
}
pub async fn payload(origin: &Origin, job: &Job, kind: &str) -> Result<Value, AuthError> {
	let analysis = (job.status == 2).then(|| score::analyze(job.text.as_deref()));
	let reading = if origin.reading_enabled {
		let pool = &origin.auth.as_deref().ok_or(AuthError::Database)?.pool;
		sqlx::query_scalar::<_,Value>(r#"SELECT jsonb_build_object('status',CASE "Status" WHEN 0 THEN 'queued' WHEN 1 THEN 'running' WHEN 2 THEN 'waitingForBudget' WHEN 3 THEN 'completed' WHEN 4 THEN 'failed' END,'pagesTotal',"PagesTotal",'pagesRead',"PagesRead",'partial',COALESCE("PagesRead"<"PagesTotal",false) AND $2,'fieldsApplied',"FieldsApplied",'failureReason',"FailureReason",'attemptCount',"AttemptCount",'completedAt',"CompletedAt",'updatedAt',"UpdatedAt",'rowVersion',"RowVersion") FROM reading_jobs WHERE "DateiId"=$1"#).bind(job.datei_id).bind(job.text.as_ref().is_none_or(|t|t.encode_utf16().count()<origin.extraction.max_text_characters)).fetch_optional(pool).await?.unwrap_or(Value::Null)
	} else {
		Value::Null
	};
	Ok(
		json!({"dateiId":job.datei_id,"status":match job.status{0=>"queued",1=>"running",2=>"completed",3=>"noText",_=>"failed"},"textSource":if job.text_source==1{"reading"}else{"embedded"},"readingEnabled":origin.reading_enabled,"reading":reading,"text":job.text,"cleanText":analysis.as_ref().map(|a|&a.clean_text),"facts":if kind=="noten"{analysis.as_ref().filter(|a|!a.facts.is_empty()).map(|a|&a.facts)}else{None},"failureReason":job.failure_reason,"attemptCount":job.attempt_count,"completedAt":job.completed_at,"lastAttemptAt":job.last_attempt_at,"lastEnqueuedAt":job.last_enqueued_at,"updatedAt":job.updated_at,"rowVersion":job.row_version}),
	)
}
