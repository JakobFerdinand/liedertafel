//! Shared owner/file lifecycle. Blobs survive soft deletion; provider tier moves
//! precede the transaction's persisted claim, including immutable replacements.
use super::{
	queries::{self, Datei},
	uploads::STORAGE_FAILURE,
	writes::{CONFLICT, body, fail, version},
};
use crate::clr_guid::GuidPath;
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError},
	storage::Storage,
};
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use chrono::{DateTime, Utc};
use sqlx::{PgConnection, PgPool};
use std::sync::Arc;
use uuid::Uuid;
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	let mut routes = vec![("/api/papierkorb", get(list).fallback(crate::api::api_miss))];
	for path in [
		"/api/lieder/{id}/delete",
		"/api/lieder/{id}/restore",
		"/api/events/{id}/delete",
		"/api/events/{id}/restore",
		"/api/dateien/{id}/delete",
		"/api/dateien/{id}/restore",
		"/api/papierkorb/lieder/{id}/remove",
		"/api/papierkorb/auftritte/{id}/remove",
		"/api/papierkorb/dateien/{id}/remove",
	] {
		routes.push((path, post(action).fallback(crate::api::api_miss)));
	}
	routes
}
async fn action(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let path = request.uri().path().to_owned();
	let remove = path.ends_with("/remove");
	let restore = path.ends_with("/restore");
	let file = path.contains("/dateien/");
	let event = path.contains("/events/") || path.contains("/auftritte/");
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(
			request.headers(),
			if remove {
				AccessPolicy::Administrator
			} else {
				AccessPolicy::Editor
			},
			true,
		)
		.await?;
	let now = auth.clock.now();
	if remove {
		let provider = origin
			.storage
			.as_deref()
			.ok_or_else(|| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
		let found = if file {
			remove_datei(&auth.pool, provider, id, now).await?
		} else {
			remove_owner(&auth.pool, provider, id, event, now).await?
		};
		if !found {
			return Err(fail(
				StatusCode::NOT_FOUND,
				"Der Eintrag wurde nicht gefunden.",
			));
		}
	} else if file {
		let row = if restore {
			sqlx::query_as::<_, Datei>(r#"SELECT * FROM dateien WHERE "Id"=$1"#)
				.bind(id)
				.fetch_optional(&auth.pool)
				.await?
		} else {
			queries::dateien(&auth.pool, None, Some(id)).await?.pop()
		};
		let d = row.ok_or_else(|| fail(StatusCode::NOT_FOUND, super::uploads::DATEI_NOT_FOUND))?;
		let mut tx = auth.pool.begin().await?;
		if restore {
			let deleted: Option<DateTime<Utc>> =
				sqlx::query_scalar(r#"SELECT "DeletedAt" FROM dateien WHERE "Id"=$1"#)
					.bind(id)
					.fetch_one(&mut *tx)
					.await?;
			if expired(deleted, now, origin.uploads.retention_days) {
				return Ok(access.respond(expired_response(origin.uploads.retention_days)));
			}
			if deleted.is_some() {
				let parent:bool=sqlx::query_scalar(r#"SELECT EXISTS(SELECT 1 FROM lieder WHERE "Id"=$1 AND "DeletedAt" IS NOT NULL) OR EXISTS(SELECT 1 FROM events WHERE "Id"=$2 AND "DeletedAt" IS NOT NULL)"#).bind(d.lied_id).bind(d.event_id).fetch_one(&mut *tx).await?;
				if parent {
					return Ok(access.respond(detail_problem("Der übergeordnete Eintrag ist noch im Papierkorb.","Der übergeordnete Eintrag ist noch im Papierkorb und muss erst wiederhergestellt werden.".into())));
				}
				let heal = d.storage_tier == "cold"
					&& !queries::has_separate_playback(&auth.pool, &d).await?;
				if heal {
					let provider = origin
						.storage
						.as_deref()
						.ok_or_else(|| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
					provider
						.set_tier(&d.blob_name, "hot")
						.await
						.map_err(|_| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
				}
				let changed=sqlx::query(r#"UPDATE dateien SET "DeletedAt"=NULL,"DeletedByAccountId"=NULL,"StorageTier"=CASE WHEN $2 THEN 'hot' ELSE "StorageTier" END,"TierChangedAt"=CASE WHEN $2 THEN $3 ELSE "TierChangedAt" END,"RowVersion"="RowVersion"+CASE WHEN $2 THEN 2 ELSE 1 END WHERE "Id"=$1 AND "RowVersion"=$4 AND "DeletedAt" IS NOT NULL"#).bind(id).bind(heal).bind(now).bind(d.row_version).execute(&mut *tx).await?.rows_affected();
				if changed == 0 {
					return Err(fail(StatusCode::CONFLICT, CONFLICT));
				}
			}
		} else {
			let body = body(request, super::binding::Shape::Version).await?;
			if version(&body)? != Some(d.row_version) {
				return Err(fail(StatusCode::CONFLICT, CONFLICT));
			}
			let provider = origin
				.storage
				.as_deref()
				.ok_or_else(|| fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE))?;
			trash_datei(
				&mut tx,
				&auth.pool,
				provider,
				&d,
				access.current.account_id,
				now,
				None,
			)
			.await?;
		}
		tx.commit().await?;
	} else {
		let table = if event { "events" } else { "lieder" };
		let not_found = if event {
			"Auftritt nicht gefunden."
		} else {
			"Das Lied wurde nicht gefunden."
		};
		// Table identifiers are selected solely from the two literals above.
		let row: Option<(Option<DateTime<Utc>>, i64)> = sqlx::query_as(sqlx::AssertSqlSafe(
			format!("SELECT \"DeletedAt\",\"RowVersion\" FROM {table} WHERE \"Id\"=$1"),
		))
		.bind(id)
		.fetch_optional(&auth.pool)
		.await?;
		let (deleted, current) = row
			.filter(|(d, _)| restore || d.is_none())
			.ok_or_else(|| fail(StatusCode::NOT_FOUND, not_found))?;
		if restore {
			if expired(deleted, now, origin.uploads.retention_days) {
				return Ok(access.respond(expired_response(origin.uploads.retention_days)));
			}
			if deleted.is_some() {
				let changed=sqlx::query(sqlx::AssertSqlSafe(format!("UPDATE {table} SET \"DeletedAt\"=NULL,\"DeletedByAccountId\"=NULL,\"RowVersion\"=\"RowVersion\"+1 WHERE \"Id\"=$1 AND \"RowVersion\"=$2"))).bind(id).bind(current).execute(&auth.pool).await?.rows_affected();
				if changed == 0 {
					return Err(fail(StatusCode::CONFLICT, CONFLICT));
				}
			}
		} else {
			let body = body(request, super::binding::Shape::Version).await?;
			if version(&body)? != Some(current) {
				return Err(fail(StatusCode::CONFLICT, CONFLICT));
			}
			let changed=sqlx::query(sqlx::AssertSqlSafe(format!("UPDATE {table} SET \"DeletedAt\"=$2,\"DeletedByAccountId\"=$3,\"RowVersion\"=\"RowVersion\"+1 WHERE \"Id\"=$1 AND \"RowVersion\"=$4 AND \"DeletedAt\" IS NULL"))).bind(id).bind(now).bind(access.current.account_id).bind(current).execute(&auth.pool).await?.rows_affected();
			if changed == 0 {
				return Err(fail(StatusCode::CONFLICT, CONFLICT));
			}
		}
	}
	Ok(access.respond(StatusCode::NO_CONTENT.into_response()))
}
fn expired(deleted: Option<DateTime<Utc>>, now: DateTime<Utc>, days: i64) -> bool {
	deleted.is_some_and(|d| d + chrono::Duration::days(days) <= now)
}
fn expired_response(days: i64) -> Response {
	detail_problem(
		"Die Aufbewahrungsfrist ist abgelaufen.",
		format!("Einträge bleiben {days} Tage im Papierkorb und werden danach endgültig entfernt."),
	)
}
fn detail_problem(title: &'static str, detail: String) -> Response {
	(StatusCode::CONFLICT,[(axum::http::header::CONTENT_TYPE,"application/problem+json")],Json(serde_json::json!({"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","status":409,"title":title,"detail":detail}))).into_response()
}
async fn list(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or_else(|| fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, false)
		.await?;
	let files:Vec<serde_json::Value>=sqlx::query_scalar(r#"SELECT json_build_object('id',d."Id",'kind',d."Kind",'isPartitur',d."IsPartitur",'stimme',d."Stimme",'fileName',d."OriginalFileName",'sizeBytes',d."SizeBytes",'liedId',d."LiedId",'liedTitle',l."Title",'eventId',d."EventId",'eventTitle',e."Title",'deletedAt',d."DeletedAt",'deletedBy',COALESCE(u."DisplayName",u."Email",u."UserName"),'replaced',d."ReplacedByDateiId" IS NOT NULL) FROM dateien d LEFT JOIN lieder l ON l."Id"=d."LiedId" LEFT JOIN events e ON e."Id"=d."EventId" LEFT JOIN "AspNetUsers" u ON u."Id"=d."DeletedByAccountId" WHERE d."DeletedAt" IS NOT NULL ORDER BY d."DeletedAt" DESC,d."Id""#).fetch_all(&auth.pool).await?;
	let lieder:Vec<serde_json::Value>=sqlx::query_scalar(r#"SELECT json_build_object('id',l."Id",'title',l."Title",'deletedAt',l."DeletedAt",'deletedBy',COALESCE(u."DisplayName",u."Email",u."UserName")) FROM lieder l LEFT JOIN "AspNetUsers" u ON u."Id"=l."DeletedByAccountId" WHERE l."DeletedAt" IS NOT NULL ORDER BY l."DeletedAt" DESC,l."Id""#).fetch_all(&auth.pool).await?;
	let events:Vec<serde_json::Value>=sqlx::query_scalar(r#"SELECT json_build_object('id',e."Id",'title',e."Title",'kind',e."Kind",'deletedAt',e."DeletedAt",'deletedBy',COALESCE(u."DisplayName",u."Email",u."UserName")) FROM events e LEFT JOIN "AspNetUsers" u ON u."Id"=e."DeletedByAccountId" WHERE e."DeletedAt" IS NOT NULL ORDER BY e."DeletedAt" DESC,e."Id""#).fetch_all(&auth.pool).await?;
	Ok(access.respond(
		Json(serde_json::json!({"dateien":files,"lieder":lieder,"auftritte":events}))
			.into_response(),
	))
}
pub(super) async fn trash_datei(
	conn: &mut PgConnection,
	pool: &PgPool,
	storage: &dyn Storage,
	datei: &Datei,
	actor: Uuid,
	now: DateTime<Utc>,
	replacement: Option<Uuid>,
) -> Result<(), AuthError> {
	let changed=sqlx::query(r#"UPDATE dateien SET "DeletedAt"=$2,"DeletedByAccountId"=$3,"RowVersion"="RowVersion"+1,"ReplacedByDateiId"=COALESCE($5,"ReplacedByDateiId") WHERE "Id"=$1 AND "RowVersion"=$4 AND "DeletedAt" IS NULL"#).bind(datei.id).bind(now).bind(actor).bind(datei.row_version).bind(replacement).execute(&mut *conn).await?.rows_affected();
	if changed == 0 {
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	// VideoTrash follows a copy even when its job is not Completed.
	sqlx::query(r#"UPDATE dateien d SET "DeletedAt"=$2,"DeletedByAccountId"=$3,"RowVersion"=d."RowVersion"+1 FROM video_jobs j WHERE j."DateiId"=$1 AND d."Id"=j."PlaybackDateiId" AND d."Id"<>$1 AND d."DeletedAt" IS NULL"#).bind(datei.id).bind(now).bind(actor).execute(&mut *conn).await?;
	return_served_to_hot(conn, pool, storage, datei.id, now, false).await
}
pub async fn return_served_to_hot(
	conn: &mut PgConnection,
	pool: &PgPool,
	storage: &dyn Storage,
	copy: Uuid,
	now: DateTime<Utc>,
	best_effort: bool,
) -> Result<(), AuthError> {
	let originals=sqlx::query_as::<_,Datei>(r#"SELECT d.* FROM dateien d WHERE d."Id" IN (SELECT "DateiId" FROM video_jobs WHERE "Status"=2 AND "PlaybackDateiId"=$1 UNION SELECT "OriginalDateiId" FROM recordings WHERE "PlaybackDateiId"=$1 AND "OriginalDateiId" IS NOT NULL)"#).bind(copy).fetch_all(pool).await?;
	for original in originals {
		if original.storage_tier != "cold" {
			continue;
		}
		if storage.set_tier(&original.blob_name, "hot").await.is_err() {
			if best_effort {
				tracing::warn!("Papierkorb: Cold original could not return to Hot");
				continue;
			}
			return Err(fail(StatusCode::BAD_GATEWAY, STORAGE_FAILURE));
		}
		let changed=sqlx::query(r#"UPDATE dateien SET "StorageTier"='hot',"TierChangedAt"=$2,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$3"#).bind(original.id).bind(now).bind(original.row_version).execute(&mut *conn).await?.rows_affected();
		if changed == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	}
	Ok(())
}
/// Shared permanent-removal seam for the later finite cleaner. Rows first,
/// then best-effort bytes; kept recording/passage rows lose only Datei slots.
pub async fn remove_datei(
	pool: &PgPool,
	storage: &dyn Storage,
	id: Uuid,
	now: DateTime<Utc>,
) -> Result<bool, AuthError> {
	let Some(d) = sqlx::query_as::<_, Datei>(r#"SELECT * FROM dateien WHERE "Id"=$1"#)
		.bind(id)
		.fetch_optional(pool)
		.await?
	else {
		return Ok(false);
	};
	let mut tx = pool.begin().await?;
	// Only the fixed cleanup table names enter SQL; all data stays bound.
	for table in ["extraction_jobs", "reading_jobs"] {
		sqlx::query(sqlx::AssertSqlSafe(format!(
			"DELETE FROM {table} WHERE \"DateiId\"=$1"
		)))
		.bind(id)
		.execute(&mut *tx)
		.await?;
	}
	return_served_to_hot(&mut tx, pool, storage, id, now, true).await?;
	sqlx::query(r#"UPDATE recordings SET "OriginalDateiId"=CASE WHEN "OriginalDateiId"=$1 THEN NULL ELSE "OriginalDateiId" END,"PlaybackDateiId"=CASE WHEN "PlaybackDateiId"=$1 THEN NULL ELSE "PlaybackDateiId" END WHERE "OriginalDateiId"=$1 OR "PlaybackDateiId"=$1"#).bind(id).execute(&mut *tx).await?;
	sqlx::query(
		r#"UPDATE recording_passages SET "PlaybackDateiId"=NULL WHERE "PlaybackDateiId"=$1"#,
	)
	.bind(id)
	.execute(&mut *tx)
	.await?;
	sqlx::query(r#"DELETE FROM dateien WHERE "Id"=$1"#)
		.bind(id)
		.execute(&mut *tx)
		.await?;
	tx.commit().await?;
	if storage.delete(&d.blob_name).await.is_err() {
		tracing::warn!("Papierkorb: blob could not be released");
	}
	Ok(true)
}
pub async fn remove_owner(
	pool: &PgPool,
	storage: &dyn Storage,
	id: Uuid,
	event: bool,
	now: DateTime<Utc>,
) -> Result<bool, AuthError> {
	let table = if event { "events" } else { "lieder" };
	let owner = if event { "EventId" } else { "LiedId" };
	// Identifiers derive only from the bool and literal branches above.
	let exists: bool = sqlx::query_scalar(sqlx::AssertSqlSafe(format!(
		"SELECT EXISTS(SELECT 1 FROM {table} WHERE \"Id\"=$1)"
	)))
	.bind(id)
	.fetch_one(pool)
	.await?;
	if !exists {
		return Ok(false);
	}
	let mut tx = pool.begin().await?;
	if event {
		sqlx::query(r#"DELETE FROM recording_passages WHERE "EventId"=$1"#)
			.bind(id)
			.execute(&mut *tx)
			.await?;
		sqlx::query(r#"DELETE FROM recordings WHERE "EventId"=$1"#)
			.bind(id)
			.execute(&mut *tx)
			.await?;
		sqlx::query(r#"DELETE FROM programme_items WHERE "ProgrammeId" IN (SELECT "Id" FROM programmes WHERE "EventId"=$1)"#).bind(id).execute(&mut *tx).await?;
		sqlx::query(r#"DELETE FROM programmes WHERE "EventId"=$1"#)
			.bind(id)
			.execute(&mut *tx)
			.await?;
	} else {
		sqlx::query(r#"DELETE FROM recording_passages WHERE "ProgrammeItemId" IN (SELECT "Id" FROM programme_items WHERE "LiedId"=$1)"#).bind(id).execute(&mut *tx).await?;
		sqlx::query(r#"DELETE FROM programme_items WHERE "LiedId"=$1"#)
			.bind(id)
			.execute(&mut *tx)
			.await?;
	}
	tx.commit().await?;
	let files: Vec<Uuid> = sqlx::query_scalar(sqlx::AssertSqlSafe(format!(
		"SELECT \"Id\" FROM dateien WHERE \"{owner}\"=$1"
	)))
	.bind(id)
	.fetch_all(pool)
	.await?;
	for file in files {
		remove_datei(pool, storage, file, now).await?;
	}
	let mut tx = pool.begin().await?;
	if event {
		sqlx::query(r#"UPDATE dateien SET "RecordedAtEventId"=NULL WHERE "RecordedAtEventId"=$1"#)
			.bind(id)
			.execute(&mut *tx)
			.await?;
	}
	sqlx::query(sqlx::AssertSqlSafe(format!(
		"DELETE FROM {table} WHERE \"Id\"=$1"
	)))
	.bind(id)
	.execute(&mut *tx)
	.await?;
	tx.commit().await?;
	Ok(true)
}
