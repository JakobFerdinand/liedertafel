//! Zeitmarken anchor a stable Programme entry to one actual playback Datei.
//! Computed review state withholds unreliable positions from members.
use super::*;
const NOT_FOUND: &str = "Zeitmarke nicht gefunden.";
const CONFLICT: &str = "Die Zeitmarke wurde zwischenzeitlich geändert.";
const DUPLICATE: &str = "Für diesen Programmpunkt gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.";
const MISSING_REVISION: &str = "Die Dateiversion der Aufnahme fehlt.";
const NO_PLAYABLE: &str =
	"Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.";
const STALE_PLAYBACK: &str = "Die Datei der Aufnahme wurde zwischenzeitlich ersetzt.";
#[derive(Clone, sqlx::FromRow)]
#[sqlx(rename_all = "PascalCase")]
struct Passage {
	id: Uuid,
	recording_id: Uuid,
	programme_item_id: Uuid,
	playback_datei_id: Option<Uuid>,
	start_seconds: f64,
	end_seconds: f64,
	row_version: i64,
	updated_at: DateTime<Utc>,
}
async fn rows(pool: &sqlx::PgPool, id: Uuid) -> Result<Vec<Passage>, AuthError> {
	Ok(sqlx::query_as(r#"SELECT p.* FROM recording_passages p JOIN programme_items i ON i."Id"=p."ProgrammeItemId" WHERE p."RecordingId"=$1 ORDER BY i."Position",p."Id""#).bind(id).fetch_all(pool).await?)
}
async fn views(pool: &sqlx::PgPool, r: &Recording, editor: bool) -> Result<Vec<Value>, AuthError> {
	let playable = files(pool, r).await?.playable(&r.kind).map(|d| d.id);
	let mut values = Vec::new();
	for p in rows(pool, r.id).await? {
		let detail:Option<(Uuid,i32,String)>=sqlx::query_as(r#"SELECT i."LiedId",i."Position",l."Title" FROM programme_items i JOIN lieder l ON l."Id"=i."LiedId" AND l."DeletedAt" IS NULL WHERE i."Id"=$1"#).bind(p.programme_item_id).fetch_optional(pool).await?;
		let Some((lied, position, title)) = detail else {
			continue;
		};
		let current = playable.is_some() && playable == p.playback_datei_id;
		let withheld = !editor && !current;
		values.push(json!({"id":p.id,"recordingId":p.recording_id,"programmeItemId":p.programme_item_id,"liedId":lied,"liedTitle":title,"position":position,"startSeconds":if withheld{None}else{Some(p.start_seconds)},"endSeconds":if withheld{None}else{Some(p.end_seconds)},"timestampState":if current{"current"}else{"needsReview"},"editor":if editor{json!({"version":p.row_version,"playbackDateiId":p.playback_datei_id,"updatedAt":p.updated_at})}else{Value::Null}}));
	}
	Ok(values)
}
async fn one(pool: &sqlx::PgPool, id: Uuid, passage: Uuid) -> Result<Value, AuthError> {
	views(pool, &load(pool, id).await?, true)
		.await?
		.into_iter()
		.find(|v| guid(&v["id"]) == Some(passage))
		.ok_or(AuthError::Database)
}
pub(super) async fn list(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let r = load(&auth.pool, id).await?;
	let editor = a.current.is_editor();
	member_visible(&auth.pool, &r, editor).await?;
	let passages = views(&auth.pool, &r, editor).await?;
	let mut value = json!({"recordingId":id,"eventId":r.event_id,"passages":passages});
	if editor {
		value["playbackDateiId"] =
			json!(files(&auth.pool, &r).await?.playable(&r.kind).map(|d| d.id));
		value["durationSeconds"] = json!(r.duration_seconds);
		let entries:Vec<Value>=sqlx::query_scalar(r#"SELECT json_build_object('programmeItemId',i."Id",'position',i."Position",'liedId',i."LiedId",'liedTitle',CASE WHEN l."DeletedAt" IS NULL THEN l."Title" ELSE NULL END,'passageId',CASE WHEN l."DeletedAt" IS NULL THEN m."Id" ELSE NULL END) FROM programme_items i JOIN programmes p ON p."Id"=i."ProgrammeId" LEFT JOIN lieder l ON l."Id"=i."LiedId" LEFT JOIN recording_passages m ON m."ProgrammeItemId"=i."Id" AND m."RecordingId"=$2 WHERE p."EventId"=$1 ORDER BY i."Position",i."Id""#).bind(r.event_id).bind(id).fetch_all(&auth.pool).await?;
		value["entries"] = json!(entries);
	}
	Ok(a.respond(Json(value).into_response()))
}
fn round(n: Option<f64>) -> Option<f64> {
	n.map(|v| {
		if v.is_finite() {
			(v * 1000.0).round_ties_even() / 1000.0
		} else {
			v
		}
	})
}
fn times(
	start: Option<f64>,
	end: Option<f64>,
	duration: Option<f64>,
) -> Result<(f64, f64), AuthError> {
	let from = start
		.filter(|v| v.is_finite() && *v >= 0.0 && *v <= 172800.0)
		.ok_or_else(|| fail(StatusCode::BAD_REQUEST, "Der Anfang ist ungültig."))?;
	let to = end
		.filter(|v| v.is_finite() && *v >= 0.0)
		.ok_or_else(|| fail(StatusCode::BAD_REQUEST, "Das Ende ist ungültig."))?;
	if to <= from {
		return Err(fail(
			StatusCode::BAD_REQUEST,
			"Das Ende muss nach dem Anfang liegen.",
		));
	}
	if to > 172800.0 || duration.is_some_and(|n| to > n + 1.0) {
		return Err(fail(
			StatusCode::BAD_REQUEST,
			"Das Ende liegt hinter dem Ende der Aufnahme.",
		));
	}
	Ok((from, to))
}
fn expected(b: &Value) -> Result<Uuid, AuthError> {
	guid(&b["expectedplaybackdateiid"])
		.ok_or_else(|| fail(StatusCode::BAD_REQUEST, MISSING_REVISION))
}
async fn playback(pool: &sqlx::PgPool, r: &Recording, expected: Uuid) -> Result<Uuid, AuthError> {
	let id = files(pool, r)
		.await?
		.playable(&r.kind)
		.map(|d| d.id)
		.ok_or_else(|| fail(StatusCode::CONFLICT, NO_PLAYABLE))?;
	if id != expected {
		return Err(fail(StatusCode::CONFLICT, STALE_PLAYBACK));
	}
	Ok(id)
}
fn insert_error(e: sqlx::Error) -> AuthError {
	if let sqlx::Error::Database(ref db) = e {
		if db.code().as_deref() == Some("23505")
			&& db.constraint() == Some("IX_recording_passages_RecordingId_ProgrammeItemId")
		{
			return fail(StatusCode::CONFLICT, DUPLICATE);
		}
		if db.code().as_deref() == Some("23503")
			&& matches!(
				db.constraint(),
				Some(
					"FK_recording_passages_programme_items_ProgrammeItemId"
						| "FK_recording_passages_recordings_RecordingId_EventId"
				)
			) {
			return fail(StatusCode::CONFLICT, CONFLICT);
		}
	}
	e.into()
}
pub(super) async fn create(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = crate::events::body(request, Shape::PassageCreate).await?;
	let expected = expected(&b)?;
	let r = load(&auth.pool, id).await?;
	let entry = guid(&b["programmeitemid"]);
	let exists:bool=sqlx::query_scalar(r#"SELECT EXISTS(SELECT 1 FROM programme_items i JOIN programmes p ON p."Id"=i."ProgrammeId" WHERE i."Id"=$1 AND p."EventId"=$2)"#).bind(entry).bind(r.event_id).fetch_one(&auth.pool).await?;
	if !exists {
		return Err(fail(
			StatusCode::NOT_FOUND,
			"Der Programmpunkt gehört nicht zum Programm dieses Auftritts.",
		));
	}
	let (start, end) = times(
		round(number(&b["startseconds"])),
		round(number(&b["endseconds"])),
		r.duration_seconds,
	)?;
	let played = playback(&auth.pool, &r, expected).await?;
	let existing: Option<Passage> = sqlx::query_as(
		r#"SELECT * FROM recording_passages WHERE "RecordingId"=$1 AND "ProgrammeItemId"=$2"#,
	)
	.bind(id)
	.bind(entry)
	.fetch_optional(&auth.pool)
	.await?;
	if let Some(p) = existing {
		if p.start_seconds == start && p.end_seconds == end && p.playback_datei_id == Some(played) {
			return Ok(
				a.respond(Json(json!({"passage":one(&auth.pool,id,p.id).await?})).into_response())
			);
		}
		return Err(fail(StatusCode::CONFLICT, DUPLICATE));
	}
	let pid = Uuid::new_v4();
	let now = auth.clock.now();
	sqlx::query(r#"INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","PlaybackDateiId","StartSeconds","EndSeconds","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$8,$9,1)"#).bind(pid).bind(id).bind(entry).bind(r.event_id).bind(played).bind(start).bind(end).bind(now).bind(a.current.account_id).execute(&auth.pool).await.map_err(insert_error)?;
	Ok(a.respond(
		(
			StatusCode::CREATED,
			[(
				header::LOCATION,
				format!("/api/recordings/{id}/passages/{pid}"),
			)],
			Json(json!({"passage":one(&auth.pool,id,pid).await?})),
		)
			.into_response(),
	))
}
fn ids(id: &str, pid: &str) -> Result<(Uuid, Uuid), AuthError> {
	let bad = || fail(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig.");
	Ok((
		crate::clr_guid::try_parse(id).ok_or_else(bad)?,
		crate::clr_guid::try_parse(pid).ok_or_else(bad)?,
	))
}
async fn passage(pool: &sqlx::PgPool, id: Uuid, pid: Uuid) -> Result<Passage, AuthError> {
	sqlx::query_as(r#"SELECT * FROM recording_passages WHERE "RecordingId"=$1 AND "Id"=$2"#)
		.bind(id)
		.bind(pid)
		.fetch_optional(pool)
		.await?
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, NOT_FOUND))
}
fn version(b: &Value, p: &Passage) -> Result<(), AuthError> {
	if b["expectedversion"]
		.as_i64()
		.is_some_and(|v| v != p.row_version)
	{
		Err(fail(StatusCode::CONFLICT, CONFLICT))
	} else {
		Ok(())
	}
}
pub(super) async fn patch(
	State(origin): State<Arc<Origin>>,
	Path((id, pid)): Path<(String, String)>,
	request: Request,
) -> Result<Response, AuthError> {
	let (id, pid) = ids(&id, &pid)?;
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = crate::events::body(request, Shape::PassagePatch).await?;
	let expected = expected(&b)?;
	let p = passage(&auth.pool, id, pid).await?;
	version(&b, &p)?;
	let r = load(&auth.pool, id).await?;
	let played = playback(&auth.pool, &r, expected).await?;
	if p.playback_datei_id != Some(played)
		&& (b["startseconds"].is_null() || b["endseconds"].is_null())
	{
		return Err(fail(
			StatusCode::BAD_REQUEST,
			"Zum Prüfen einer Zeitmarke bitte Anfang und Ende angeben.",
		));
	}
	let (start, end) = times(
		round(number(&b["startseconds"])).or(Some(p.start_seconds)),
		round(number(&b["endseconds"])).or(Some(p.end_seconds)),
		r.duration_seconds,
	)?;
	if start != p.start_seconds || end != p.end_seconds || p.playback_datei_id != Some(played) {
		let n=sqlx::query(r#"UPDATE recording_passages SET "StartSeconds"=$2,"EndSeconds"=$3,"PlaybackDateiId"=$4,"UpdatedAt"=$5,"UpdatedByAccountId"=$6,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$7"#).bind(pid).bind(start).bind(end).bind(played).bind(auth.clock.now()).bind(a.current.account_id).bind(p.row_version).execute(&auth.pool).await?.rows_affected();
		if n == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	}
	Ok(a.respond(Json(json!({"passage":one(&auth.pool,id,pid).await?})).into_response()))
}
pub(super) async fn delete(
	State(origin): State<Arc<Origin>>,
	Path((id, pid)): Path<(String, String)>,
	request: Request,
) -> Result<Response, AuthError> {
	let (id, pid) = ids(&id, &pid)?;
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = crate::events::body(request, Shape::PassageDelete).await?;
	let p = passage(&auth.pool, id, pid).await?;
	version(&b, &p)?;
	let n = sqlx::query(r#"DELETE FROM recording_passages WHERE "Id"=$1 AND "RowVersion"=$2"#)
		.bind(pid)
		.bind(p.row_version)
		.execute(&auth.pool)
		.await?
		.rows_affected();
	if n == 0 {
		return Err(fail(StatusCode::CONFLICT, CONFLICT));
	}
	Ok(a.respond(StatusCode::NO_CONTENT.into_response()))
}
pub(super) async fn review(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let a = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let b = crate::events::body(request, Shape::PassageReview).await?;
	let expected = expected(&b)?;
	let selected = b["passages"]
		.as_array()
		.filter(|v| {
			!v.is_empty()
				&& v.iter()
					.all(|v| guid(&v["id"]).is_some() && v["expectedversion"].as_i64().is_some())
		})
		.ok_or_else(|| {
			fail(
				StatusCode::BAD_REQUEST,
				"Es wurden keine Zeitmarken zum Bestätigen angegeben.",
			)
		})?;
	let r = load(&auth.pool, id).await?;
	let played = playback(&auth.pool, &r, expected).await?;
	let ids: Vec<_> = selected
		.iter()
		.filter_map(|v| guid(&v["id"]))
		.collect::<std::collections::HashSet<_>>()
		.into_iter()
		.collect();
	let mut tx = auth.pool.begin().await?;
	let rows: Vec<Passage> = sqlx::query_as(
		r#"SELECT * FROM recording_passages WHERE "RecordingId"=$1 AND "Id"=ANY($2) ORDER BY "Id""#,
	)
	.bind(id)
	.bind(&ids)
	.fetch_all(&mut *tx)
	.await?;
	if rows.len() != ids.len() {
		return Err(fail(StatusCode::NOT_FOUND, NOT_FOUND));
	}
	for v in selected {
		let p = rows
			.iter()
			.find(|p| Some(p.id) == guid(&v["id"]))
			.ok_or(AuthError::Database)?;
		version(v, p)?;
	}
	for p in rows.iter().filter(|p| p.playback_datei_id != Some(played)) {
		let n=sqlx::query(r#"UPDATE recording_passages SET "PlaybackDateiId"=$2,"UpdatedAt"=$3,"UpdatedByAccountId"=$4,"RowVersion"="RowVersion"+1 WHERE "Id"=$1 AND "RowVersion"=$5"#).bind(p.id).bind(played).bind(auth.clock.now()).bind(a.current.account_id).bind(p.row_version).execute(&mut *tx).await?.rows_affected();
		if n == 0 {
			return Err(fail(StatusCode::CONFLICT, CONFLICT));
		}
	}
	tx.commit().await?;
	Ok(a.respond(
		Json(
			json!({"recordingId":id,"playbackDateiId":played,"passages":views(&auth.pool,&r,true).await?}),
		)
		.into_response(),
	))
}
