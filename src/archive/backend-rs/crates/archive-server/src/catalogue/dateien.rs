use super::queries;
use crate::clr_guid::GuidPath;
use crate::{
	Origin,
	api::problem,
	auth::{AccessPolicy, AuthError},
};
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
};
use serde_json::json;
use std::sync::Arc;
pub(super) const NOT_FOUND: &str = "Die Datei wurde nicht gefunden.";
pub(super) const STORAGE_FAILURE: &str = "Speicherdienst nicht erreichbar.";
pub(super) async fn access(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		crate::auth::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let Some(datei) = queries::dateien(&auth.pool, None, Some(id)).await?.pop() else {
		return Ok(access.respond(problem(StatusCode::NOT_FOUND, NOT_FOUND)));
	};
	if !access
		.current
		.can_read_auftritt_datei(datei.event_id.is_some(), &datei.kind)
	{
		return Ok(access.respond(problem(StatusCode::NOT_FOUND, NOT_FOUND)));
	}
	let storage = origin
		.storage
		.as_deref()
		.ok_or(AuthError::Problem(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
	let lifetime = origin.read_ticket_lifetime;
	let copy = if queries::is_video(&datei) {
		queries::live_copy(&auth.pool, datei.id).await?
	} else {
		None
	};
	let view = copy.as_ref().unwrap_or(&datei);
	let view_url = storage
		.read_ticket(&view.blob_name, lifetime, false, Some(&view.content_type))
		.await
		.map_err(|_| AuthError::Problem(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
	let download_url = storage
		.read_ticket(&datei.blob_name, lifetime, true, Some(&datei.content_type))
		.await
		.map_err(|_| AuthError::Problem(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
	Ok(access.respond(Json(json!({"dateiId":datei.id,"kind":datei.kind,"contentType":view.content_type,"sizeBytes":view.size_bytes,"fileName":datei.original_file_name,"createdAt":datei.created_at,"playbackDateiId":copy.as_ref().map(|d|d.id),"viewUrl":view_url,"downloadUrl":download_url,"expiresAt":auth.clock.now()+lifetime})).into_response()))
}

#[derive(serde::Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct TierRequest {
	tier: Option<String>,
	row_version: Option<u32>,
}
pub(super) async fn tier(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		crate::auth::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let bytes = axum::body::to_bytes(request.into_body(), 65_536)
		.await
		.map_err(|_| AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig."))?;
	let body = if bytes.is_empty() {
		TierRequest::default()
	} else {
		serde_json::from_slice::<Option<TierRequest>>(&bytes)
			.map_err(|_| AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig."))?
			.unwrap_or_default()
	};
	let tier = body
		.tier
		.as_deref()
		.unwrap_or_default()
		.trim()
		.to_ascii_lowercase();
	if !matches!(tier.as_str(), "hot" | "cold") {
		return Ok(access.respond(problem(
			StatusCode::BAD_REQUEST,
			"Die Speicher-Stufe ist unbekannt; erlaubt sind Hot und Cold.",
		)));
	}
	let Some(mut datei) = queries::dateien(&auth.pool, None, Some(id)).await?.pop() else {
		return Ok(access.respond(problem(StatusCode::NOT_FOUND, NOT_FOUND)));
	};
	if body
		.row_version
		.is_some_and(|v| i64::from(v) != datei.row_version)
	{
		return Ok(access.respond(problem(
			StatusCode::CONFLICT,
			"Der Eintrag wurde zwischenzeitlich geändert.",
		)));
	}
	if datei.storage_tier == tier {
		return Ok(access.respond(
			Json(json!({"datei":datei.summary(serde_json::Value::Null)})).into_response(),
		));
	}
	if tier == "cold" && !queries::has_separate_playback(&auth.pool, &datei).await? {
		return Ok(access.respond(problem(StatusCode::UNPROCESSABLE_ENTITY,"Das Original kann erst ins Cold Storage verschoben werden, wenn eine abspielbare Abspielkopie für Mitglieder bereitsteht.")));
	}
	let storage = origin
		.storage
		.as_deref()
		.ok_or(AuthError::Problem(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
	storage
		.set_tier(&datei.blob_name, &tier)
		.await
		.map_err(|_| AuthError::Problem(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
	let now = auth.clock.now();
	let changed=sqlx::query(r#"UPDATE dateien SET "StorageTier"=$2,"TierChangedAt"=$3,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$4"#).bind(id).bind(&tier).bind(now).bind(datei.row_version).execute(&auth.pool).await?.rows_affected();
	if changed == 0 {
		return Ok(access.respond(problem(
			StatusCode::CONFLICT,
			"Der Eintrag wurde zwischenzeitlich geändert.",
		)));
	}
	datei.storage_tier = tier;
	datei.tier_changed_at = Some(now);
	datei.row_version += 1;
	Ok(access
		.respond(Json(json!({"datei":datei.summary(serde_json::Value::Null)})).into_response()))
}
