use super::*;
use sqlx::Row;
use std::collections::HashSet;
const CONFLICT: &str = "Das Programm wurde zwischenzeitlich geändert.";
pub(super) async fn embed(pool: &sqlx::PgPool, event: Uuid) -> Result<Value, AuthError> {
	let p: Option<(Uuid, i64, DateTime<Utc>)> = sqlx::query_as(
		r#"SELECT "Id","RowVersion","UpdatedAt" FROM programmes WHERE "EventId"=$1"#,
	)
	.bind(event)
	.fetch_optional(pool)
	.await?;
	let Some((id, version, updated)) = p else {
		return Ok(Value::Null);
	};
	let rows=sqlx::query(r#"SELECT i.*,l."Title",l."VoiceConfiguration",l."MusicalKey" FROM programme_items i LEFT JOIN lieder l ON l."Id"=i."LiedId" WHERE i."ProgrammeId"=$1 ORDER BY i."Position",i."Id""#).bind(id).fetch_all(pool).await?;
	let mut items = Vec::new();
	for r in rows {
		items.push(json!({"id":r.try_get::<Uuid,_>("Id")?,"position":r.try_get::<i32,_>("Position")?,"liedId":r.try_get::<Uuid,_>("LiedId")?,"liedTitle":r.try_get::<Option<String>,_>("Title")?,"voiceConfiguration":r.try_get::<Option<String>,_>("VoiceConfiguration")?,"musicalKey":r.try_get::<Option<String>,_>("MusicalKey")?,"note":r.try_get::<Option<String>,_>("Note")?}));
	}
	Ok(json!({"id":id,"rowVersion":version,"updatedAt":updated,"items":items}))
}
pub(super) async fn save(
	State(origin): State<Arc<Origin>>,
	Path(event): Path<Uuid>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = body(request, Shape::Programme).await?;
	load(&auth.pool, event).await?;
	let p: Option<(Uuid, i64)> =
		sqlx::query_as(r#"SELECT "Id","RowVersion" FROM programmes WHERE "EventId"=$1"#)
			.bind(event)
			.fetch_optional(&auth.pool)
			.await?;
	if p.is_some_and(|(_, v)| b["rowversion"].as_i64() != Some(v)) {
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	let mut requested = Vec::new();
	let mut ids = HashSet::new();
	let items = b["items"].as_array().cloned().unwrap_or_default();
	// Null entries and duplicates precede per-entry note/catalogue validation.
	for i in &items {
		if i.is_null() {
			return Err(fail(
				StatusCode::BAD_REQUEST,
				"Ein Programmpunkt ist ungültig.",
			));
		}
		if let Some(id) = i["id"].as_str() {
			let id = Uuid::parse_str(id).map_err(|_| invalid())?;
			if !ids.insert(id) {
				return Err(fail(
					StatusCode::BAD_REQUEST,
					"Ein Programmpunkt ist ungültig.",
				));
			}
		}
	}
	for i in &items {
		let note = optional(
			i["note"].as_str().unwrap_or_default(),
			500,
			"Die Notiz ist zu lang.",
		)?;
		let lied = i["liedid"].as_str().and_then(|s| Uuid::parse_str(s).ok());
		let exists: bool = sqlx::query_scalar(
			r#"SELECT EXISTS (SELECT FROM lieder WHERE "Id"=$1 AND "DeletedAt" IS NULL)"#,
		)
		.bind(lied)
		.fetch_one(&auth.pool)
		.await?;
		if !exists {
			return Err(fail(
				StatusCode::BAD_REQUEST,
				"Ein Programmpunkt verweist auf ein nicht vorhandenes Lied.",
			));
		}
		requested.push((
			i["id"].as_str().and_then(|s| Uuid::parse_str(s).ok()),
			lied.ok_or_else(invalid)?,
			note,
		));
	}
	let programme = p.map_or_else(Uuid::new_v4, |(id, _)| id);
	let existing: Vec<(Uuid, i32)> =
		sqlx::query_as(r#"SELECT "Id","Position" FROM programme_items WHERE "ProgrammeId"=$1"#)
			.bind(programme)
			.fetch_all(&auth.pool)
			.await?;
	let orphans: Vec<_> = existing
		.iter()
		.filter(|(id, _)| !ids.contains(id))
		.map(|(id, _)| *id)
		.collect();
	let blocked:Vec<(String,String)>=sqlx::query_as(r#"SELECT COALESCE(l."Title",'Ohne Titel'),r."Label" FROM recording_passages m JOIN programme_items i ON i."Id"=m."ProgrammeItemId" JOIN recordings r ON r."Id"=m."RecordingId" LEFT JOIN lieder l ON l."Id"=i."LiedId" AND l."DeletedAt" IS NULL WHERE m."ProgrammeItemId"=ANY($1)"#).bind(&orphans).fetch_all(&auth.pool).await?;
	if !blocked.is_empty() {
		let names: HashSet<_> = blocked
			.into_iter()
			.map(|(title, label)| format!("„{title}“ in „{label}“"))
			.collect();
		let names: Vec<String> = sqlx::query_scalar(
			r#"SELECT name FROM unnest($1::text[]) AS name ORDER BY name COLLATE "und-x-icu""#,
		)
		.bind(names.into_iter().collect::<Vec<_>>())
		.fetch_all(&auth.pool)
		.await?;
		let message = format!(
			"Zeitmarken vorhanden: {}{}. Entferne zuerst diese Zeitmarken in den Aufnahmen.",
			names.iter().take(3).cloned().collect::<Vec<_>>().join("; "),
			if names.len() > 3 {
				format!(" und {} weitere", names.len() - 3)
			} else {
				String::new()
			}
		);
		return Ok(access.respond(crate::api::problem(StatusCode::CONFLICT, &message)));
	}
	let now = auth.clock.now();
	let mut tx = auth.pool.begin().await?;
	if let Some((_, version)) = p {
		let n=sqlx::query(r#"UPDATE programmes SET "RowVersion"="RowVersion"+1,"UpdatedAt"=$2,"UpdatedByAccountId"=$3 WHERE "Id"=$1 AND "RowVersion"=$4"#).bind(programme).bind(now).bind(access.current.account_id).bind(version).execute(&mut *tx).await?.rows_affected();
		if n == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	} else {
		// Like the frozen handler, first-create uniqueness/FK races are not all
		// relabelled as a friendly stale-edit conflict.
		sqlx::query(r#"INSERT INTO programmes ("Id","EventId","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,$3,$4,1)"#).bind(programme).bind(event).bind(now).bind(access.current.account_id).execute(&mut *tx).await?;
	}
	sqlx::query(r#"DELETE FROM programme_items WHERE "Id"=ANY($1)"#)
		.bind(orphans)
		.execute(&mut *tx)
		.await?;
	let base = existing.len().max(requested.len());
	let mut touched = Vec::new();
	for (n, (requested, lied, note)) in requested.into_iter().enumerate() {
		let known = requested.filter(|id| existing.iter().any(|(old, _)| old == id));
		let id = known.unwrap_or_else(Uuid::new_v4);
		let parked = i32::try_from(base + n + 1).map_err(|_| invalid())?;
		if known.is_some() {
			sqlx::query(
				r#"UPDATE programme_items SET "Position"=$2,"LiedId"=$3,"Note"=$4 WHERE "Id"=$1"#,
			)
			.bind(id)
			.bind(parked)
			.bind(lied)
			.bind(note)
			.execute(&mut *tx)
			.await?;
		} else {
			sqlx::query(r#"INSERT INTO programme_items ("Id","ProgrammeId","Position","LiedId","Note") VALUES ($1,$2,$3,$4,$5)"#).bind(id).bind(programme).bind(parked).bind(lied).bind(note).execute(&mut *tx).await?;
		}
		touched.push(id);
	}
	for (n, id) in touched.into_iter().enumerate() {
		sqlx::query(r#"UPDATE programme_items SET "Position"=$2 WHERE "Id"=$1"#)
			.bind(id)
			.bind(i32::try_from(n + 1).map_err(|_| invalid())?)
			.execute(&mut *tx)
			.await?;
	}
	tx.commit().await?;
	Ok(access.respond(Json(json!({"programme":embed(&auth.pool,event).await?})).into_response()))
}
pub(super) async fn upcoming(
	State(origin): State<Arc<Origin>>,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let today: chrono::NaiveDate = sqlx::query_scalar("SELECT timezone('Europe/Vienna',$1)::date")
		.bind(auth.clock.now())
		.fetch_one(&auth.pool)
		.await?;
	let mut events = visible(&auth.pool).await?;
	events.retain(|e| super::date::upcoming(e, today));
	events.sort_by(|a, b| {
		a.date_year
			.is_none()
			.cmp(&b.date_year.is_none())
			.then(a.date_year.cmp(&b.date_year))
			.then(a.date_month.cmp(&b.date_month))
			.then(a.date_day.cmp(&b.date_day))
			.then(a.id.cmp(&b.id))
	});
	let mut rows = Vec::new();
	for e in events {
		let p = embed(&auth.pool, e.id).await?;
		let count = p["items"].as_array().map_or(0, Vec::len);
		if count > 0 {
			rows.push(json!({"eventId":e.id,"eventTitle":e.title,"kind":e.kind,"dateDisplay":e.display(),"datePrecision":e.precision(),"dateApproximate":e.date_approximate,"venue":e.venue,"startTime":e.start_time,"updatedAt":p["updatedAt"],"itemCount":count}));
		}
	}
	Ok(access.respond(Json(json!({"programmes":rows})).into_response()))
}
