//! Derived programme/Lied-Aufnahme history and seeded recording-link projections.
//! Recording mutation/publication endpoints belong to #227.
use super::*;
use sqlx::Row;
use std::collections::HashMap;
pub(super) async fn aufnahmen(pool: &sqlx::PgPool, event: Uuid) -> Result<Vec<Value>, AuthError> {
	let rows=sqlx::query(r#"SELECT d.*,l."Title" AS "LiedTitle" FROM dateien d JOIN lieder l ON l."Id"=d."LiedId" WHERE d."Kind"='aufnahme' AND d."RecordedAtEventId"=$1 AND d."DeletedAt" IS NULL AND l."DeletedAt" IS NULL ORDER BY l."Title" COLLATE public.archive_culture,d."CreatedAt",d."Id""#).bind(event).fetch_all(pool).await?;
	let mut files = Vec::new();
	for r in rows {
		files.push(json!({"id":r.try_get::<Uuid,_>("Id")?,"liedId":r.try_get::<Uuid,_>("LiedId")?,"liedTitle":r.try_get::<String,_>("LiedTitle")?,"fileName":r.try_get::<Option<String>,_>("OriginalFileName")?,"description":r.try_get::<Option<String>,_>("Description")?,"contentType":r.try_get::<String,_>("ContentType")?,"sizeBytes":r.try_get::<i64,_>("SizeBytes")?,"createdAt":r.try_get::<DateTime<Utc>,_>("CreatedAt")?}));
	}
	Ok(files)
}
pub(super) async fn list(
	State(origin): State<Arc<Origin>>,
	GuidPath(lied): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = auth(&origin)?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let requested = query_number(&request, "page")?.unwrap_or(1).max(1) as usize;
	let song = crate::catalogue::queries::lied(&auth.pool, lied)
		.await?
		.ok_or_else(|| fail(StatusCode::NOT_FOUND, "Lied nicht gefunden."))?;
	let entries:Vec<(Uuid,i64)>=sqlx::query_as(r#"SELECT p."EventId",count(*) FROM programme_items i JOIN programmes p ON p."Id"=i."ProgrammeId" WHERE i."LiedId"=$1 GROUP BY p."EventId""#).bind(lied).fetch_all(&auth.pool).await?;
	let entries: HashMap<_, _> = entries.into_iter().collect();
	let mut files = crate::catalogue::queries::dateien(&auth.pool, Some(lied), None).await?;
	files.retain(|d| d.kind == "aufnahme" && d.recorded_at_event_id.is_some());
	files.sort_by(|a, b| a.created_at.cmp(&b.created_at).then(a.id.cmp(&b.id)));
	let mut rows = visible(&auth.pool).await?;
	rows.retain(|e| {
		entries.contains_key(&e.id) || files.iter().any(|d| d.recorded_at_event_id == Some(e.id))
	});
	rows.sort_by(date::compare);
	let total = rows.len();
	let counts = json!({"events":total,"inProgramme":rows.iter().filter(|e|entries.get(&e.id).is_some_and(|n|*n>0)).count(),"uncertainDates":rows.iter().filter(|e|e.date_approximate||e.precision()!="day").count()});
	let page = requested.min(total.div_ceil(20).max(1));
	let selected: Vec<_> = rows.into_iter().skip((page - 1) * 20).take(20).collect();
	let links = links(
		&auth.pool,
		lied,
		&selected.iter().map(|e| e.id).collect::<Vec<_>>(),
		access.current.is_editor(),
	)
	.await?;
	let items:Vec<_>=selected.iter().map(|e|{let count=entries.get(&e.id).copied().unwrap_or_default();json!({"eventId":e.id,"eventTitle":e.title,"eventKind":e.kind,"dateYear":e.date_year,"dateMonth":e.date_month,"dateDay":e.date_day,"dateApproximate":e.date_approximate,"dateDisplay":e.display(),"datePrecision":e.precision(),"dateUncertain":e.date_approximate||e.precision()!="day","inProgramme":count>0,"entries":count,"recordings":links.get(&e.id).cloned().unwrap_or_default(),"liedAufnahmen":files.iter().filter(|d|d.recorded_at_event_id==Some(e.id)).map(|d|json!({"id":d.id,"fileName":d.original_file_name,"contentType":d.content_type,"description":d.description})).collect::<Vec<_>>()})}).collect();
	Ok(access.respond(Json(json!({"lied":{"id":song.id,"title":song.title},"page":page,"pageSize":20,"total":total,"counts":counts,"auftritte":items})).into_response()))
}
async fn links(
	pool: &sqlx::PgPool,
	lied: Uuid,
	events: &[Uuid],
	editor: bool,
) -> Result<HashMap<Uuid, Vec<Value>>, AuthError> {
	let rows=sqlx::query(r#"SELECT m.*,r."Label",r."Kind",(r."PublishedAt" IS NOT NULL) published,o."Id" original_id,o."ContentType" original_type,c."Id" copy_id,c."ContentType" copy_type FROM recording_passages m JOIN recordings r ON r."Id"=m."RecordingId" JOIN programme_items i ON i."Id"=m."ProgrammeItemId" JOIN events e ON e."Id"=m."EventId" JOIN lieder l ON l."Id"=i."LiedId" LEFT JOIN dateien o ON o."Id"=r."OriginalDateiId" AND o."DeletedAt" IS NULL LEFT JOIN dateien c ON c."Id"=r."PlaybackDateiId" AND c."DeletedAt" IS NULL WHERE i."LiedId"=$1 AND m."EventId"=ANY($2) AND e."DeletedAt" IS NULL AND l."DeletedAt" IS NULL AND ($3 OR r."PublishedAt" IS NOT NULL) ORDER BY r."Label" COLLATE public.archive_culture,m."Id""#).bind(lied).bind(events).bind(editor).fetch_all(pool).await?;
	let mut by_event = HashMap::<Uuid, Vec<Value>>::new();
	for r in rows {
		let kind: String = r.try_get("Kind")?;
		let playable_type = |content: Option<String>| {
			content.is_some_and(|c| {
				if kind == "video" {
					["video/mp4", "video/webm"]
						.iter()
						.any(|v| c.eq_ignore_ascii_case(v))
				} else {
					["audio/mp4", "audio/webm", "audio/mpeg", "audio/wav"]
						.iter()
						.any(|v| c.eq_ignore_ascii_case(v))
				}
			})
		};
		let playable = if playable_type(r.try_get("copy_type")?) {
			r.try_get::<Option<Uuid>, _>("copy_id")?
		} else if playable_type(r.try_get("original_type")?) {
			r.try_get::<Option<Uuid>, _>("original_id")?
		} else {
			None
		};
		let state =
			if playable.is_some() && playable == r.try_get::<Option<Uuid>, _>("PlaybackDateiId")? {
				"current"
			} else {
				"needsReview"
			};
		let withheld = !editor && state == "needsReview";
		by_event.entry(r.try_get("EventId")?).or_default().push(json!({"passageId":r.try_get::<Uuid,_>("Id")?,"recordingId":r.try_get::<Uuid,_>("RecordingId")?,"recordingLabel":r.try_get::<String,_>("Label")?,"kind":kind,"isPublished":r.try_get::<bool,_>("published")?,"startSeconds":if withheld{None}else{Some(r.try_get::<f64,_>("StartSeconds")?)},"endSeconds":if withheld{None}else{Some(r.try_get::<f64,_>("EndSeconds")?)},"timestampState":state}));
	}
	Ok(by_event)
}
