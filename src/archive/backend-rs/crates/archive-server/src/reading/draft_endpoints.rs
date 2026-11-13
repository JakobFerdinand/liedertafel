use super::{
	draft::{DraftError, DraftReader},
	http,
};
use crate::{
	Origin,
	auth::{AccessPolicy, AuthError, AuthError::Problem as fail},
};
use axum::{
	Json,
	extract::{DefaultBodyLimit, Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, post},
};
use serde_json::json;
use std::sync::Arc;
pub(crate) const BAD_IDENTITY: &str = "Entwurf und Anfrage fehlen oder sind ungültig.";
pub(crate) const NOT_PDF: &str = "Das Lesen ist nur für PDF-Dateien möglich.";
pub(crate) const BUDGET: &str =
	"Das Monatsbudget der KI ist erreicht. Das Formular lässt sich von Hand ausfüllen.";
pub(crate) const FAILED: &str = "Das Lesen ist gescheitert. Bitte erneut versuchen.";
const SUPERSEDED: &str = "Die Anfrage ist überholt.";
const GONE: &str = "Der Entwurf wurde inzwischen verworfen.";
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![(
		"/api/lied-lesungen",
		post(read)
			.fallback(crate::api::api_miss)
			.layer(DefaultBodyLimit::max(http::FORM_LIMIT)),
	)]
}
async fn read(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let auth = origin
		.auth
		.as_deref()
		.ok_or(fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let form = http::read_form(request, BAD_IDENTITY).await?;
	let (entwurf, number) = http::identity(&form, BAD_IDENTITY)?;
	if !origin.reading_enabled {
		return Err(fail(
			StatusCode::CONFLICT,
			"Das Lesen durch die KI ist noch nicht eingerichtet.",
		));
	}
	let bytes = form
		.file("datei")
		.filter(|b| {
			!b.is_empty()
				&& b.len() as u64 <= origin.reading.max_pdf_bytes
				&& b.starts_with(b"%PDF-")
		})
		.ok_or(fail(StatusCode::UNPROCESSABLE_ENTITY, NOT_PDF))?;
	let datei = crate::clr_guid::try_parse(&form.value("dateiId"));
	let (request_cancel, request_guard) = http::request_lifetime(&origin.shutdown);
	// Keep the handler-drop signal even while ownership is unresolved. Claim's
	// SQL must survive disconnect; only an eventual stateless fallback uses it.
	let _request_guard = request_guard;
	let bytes = bytes.to_vec();
	let actor = access.current.account_id;
	let task_origin = origin.clone();
	let task=origin.spawn_reading(async move {
  let origin=task_origin;
  let auth=origin.auth.as_deref().ok_or(fail(StatusCode::UNAUTHORIZED,crate::auth::UNAUTHORIZED))?;
  // Transfer the COMPLETE claim/registry/read/store future before its first
  // SQL await. A committed owned Claim must never become an unproduced liest.
  let persisted=if let Some(file)=datei {claim(&auth.pool,actor,entwurf,file,number,auth.clock.now()).await?}else{false};
  let tracked=if persisted {datei.map(|file|origin.draft_readings.start(entwurf,file,number,&origin.shutdown))}else{None};
  let cancel=tracked.as_ref().map(|h|h.cancel.clone()).unwrap_or(request_cancel);
  let _tracked=tracked;
  if !persisted&&cancel.is_cancelled(){return Err(fail(StatusCode::BAD_GATEWAY,FAILED));}
  let read_deadline=tokio::time::Instant::now()+std::time::Duration::from_secs(180);
  if persisted {
   // Register BEFORE checking the durable target, closing the commit→registry
   // removal gap. Old delayed registrations cannot cancel a higher request.
   let current:bool=sqlx::query_scalar(r#"SELECT EXISTS (SELECT 1 FROM lied_entwurf_lesungen WHERE "EntwurfId"=$1 AND "DateiId"=$2 AND "AnfrageId"=$3 AND "Status"='liest')"#).bind(entwurf).bind(datei).bind(number).fetch_one(&auth.pool).await?;
   if !current {return Err(fail(StatusCode::NOT_FOUND,GONE));}
  }
  let ai=origin.ai.as_ref().ok_or(fail(StatusCode::BAD_GATEWAY,FAILED))?;
  let reader=DraftReader{model:super::ScanReadingModel::new(ai.gateway.clone(),origin.reading.clone()),extraction:origin.extraction.clone()};
  let mut reading=Box::pin(reader.read(&bytes,Some(actor),&cancel));
  let result=if persisted {
   tokio::select! { biased; _=tokio::time::sleep_until(read_deadline)=>{cancel.cancel();reading.await},result=&mut reading=>result }
  }else{reading.await};
 let outcome=result.map_err(|error|match error {
  DraftError::Material(reason)=>fail(StatusCode::UNPROCESSABLE_ENTITY,reason),
  DraftError::Ai(crate::ai::Error::BudgetExceeded(_))=>fail(StatusCode::TOO_MANY_REQUESTS,BUDGET),
  DraftError::InputTooLarge | DraftError::Ai(crate::ai::Error::InputTooLarge{..})=>fail(StatusCode::UNPROCESSABLE_ENTITY,super::worker::INPUT_TOO_LARGE),
  _=>{tracing::warn!("Draft reading failed (DraftError)");fail(StatusCode::BAD_GATEWAY,FAILED)},
 });
 if persisted {
  let (status,title,fields)=match &outcome {Ok(fields)=>("gelesen","",serde_json::to_string(fields).map_err(|_|fail(StatusCode::BAD_GATEWAY,FAILED))?),Err(AuthError::Problem(status,title))=>(if *status==StatusCode::TOO_MANY_REQUESTS {"nicht-verfuegbar"}else{"fehlgeschlagen"},*title,"[]".into()),_ =>("fehlgeschlagen",FAILED,"[]".into())};
  let changed=sqlx::query(r#"UPDATE lied_entwurf_lesungen SET "Status"=$4,"Meldung"=$5,"Felder"=$6,"UpdatedAt"=$7 WHERE "EntwurfId"=$1 AND "DateiId"=$2 AND "AnfrageId"=$3 AND "Status"='liest'"#).bind(entwurf).bind(datei).bind(number).bind(status).bind(title).bind(fields).bind(auth.clock.now()).execute(&auth.pool).await?.rows_affected();
  if changed==0 {return Err(fail(StatusCode::NOT_FOUND,GONE));}
 }
 outcome
 });
	let suggestions = task
		.await
		.map_err(|_| fail(StatusCode::BAD_GATEWAY, FAILED))??;
	Ok(access.respond(
		Json(json!({"entwurfId":entwurf,"anfrageId":number,"felder":suggestions})).into_response(),
	))
}
async fn claim(
	pool: &sqlx::PgPool,
	actor: uuid::Uuid,
	draft: uuid::Uuid,
	file: uuid::Uuid,
	number: i32,
	now: chrono::DateTime<chrono::Utc>,
) -> Result<bool, AuthError> {
	let mut tx = pool.begin().await?;
	// Parent then Datei key-share locks prevent removal during the short claim;
	// ON CONFLICT's predicate arbitrates equal/newer requests across replicas.
	let owned: Option<uuid::Uuid> = sqlx::query_scalar(
		r#"SELECT "Id" FROM lied_entwuerfe WHERE "Id"=$1 AND "OwnerAccountId"=$2 FOR KEY SHARE"#,
	)
	.bind(draft)
	.bind(actor)
	.fetch_optional(&mut *tx)
	.await?;
	if owned.is_none() {
		return Ok(false);
	}
	let exists: Option<uuid::Uuid> = sqlx::query_scalar(
		r#"SELECT "Id" FROM lied_entwurf_dateien WHERE "EntwurfId"=$1 AND "Id"=$2 FOR KEY SHARE"#,
	)
	.bind(draft)
	.bind(file)
	.fetch_optional(&mut *tx)
	.await?;
	if exists.is_none() {
		return Ok(false);
	}
	let claimed:Option<i32>=sqlx::query_scalar(r#"INSERT INTO lied_entwurf_lesungen ("EntwurfId","DateiId","AnfrageId","Status","Meldung","Felder","UpdatedAt") VALUES ($1,$2,$3,'liest','','[]',$4) ON CONFLICT ("EntwurfId","DateiId") DO UPDATE SET "AnfrageId"=$3,"Status"='liest',"Meldung"='',"Felder"='[]',"UpdatedAt"=$4 WHERE lied_entwurf_lesungen."AnfrageId"<$3 RETURNING "AnfrageId""#).bind(draft).bind(file).bind(number).bind(now).fetch_optional(&mut *tx).await?;
	tx.commit().await?;
	if claimed.is_none() {
		return Err(fail(StatusCode::BAD_REQUEST, SUPERSEDED));
	}
	Ok(true)
}
