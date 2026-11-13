use super::{
	ScanReadingModel,
	programme::{self, ProgrammError, ProgrammReader},
};
use crate::{
	Origin, ai,
	auth::{AccessPolicy, AuthError},
};
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, post},
};
use std::sync::Arc;
use tracing::Instrument;
pub const BAD_IDENTITY: &str = "Entwurf und Anfrage fehlen oder sind ungültig.";
pub const NOT_READABLE: &str =
	"Das Lesen ist nur für PDF-Dateien und Fotos (JPEG, PNG, WebP) möglich.";
pub const BUDGET: &str =
	"Das Monatsbudget der KI ist erreicht. Das Formular lässt sich von Hand ausfüllen.";
pub const FAILED: &str = "Das Lesen ist gescheitert. Bitte erneut versuchen.";
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![(
		"/api/auftritt-lesungen",
		post(read)
			.fallback(crate::api::api_miss)
			.layer(axum::extract::DefaultBodyLimit::max(
				super::http::FORM_LIMIT,
			)),
	)]
}
async fn read(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let fail = AuthError::Problem;
	let auth = origin
		.auth
		.as_ref()
		.ok_or(fail(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Editor, true)
		.await?;
	let form = super::http::read_form(request, BAD_IDENTITY).await?;
	let (id, number) = super::http::identity(&form, BAD_IDENTITY)?;
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
				&& (programme::is_pdf(b) || programme::is_photo(b))
		})
		.ok_or(fail(StatusCode::UNPROCESSABLE_ENTITY, NOT_READABLE))?;
	let services = origin
		.ai
		.as_ref()
		.ok_or(fail(StatusCode::BAD_GATEWAY, FAILED))?;
	let (cancel, _request_drop) = super::http::request_lifetime(&origin.shutdown);
	let reader = ProgrammReader {
		model: ScanReadingModel::new(services.gateway.clone(), origin.reading.clone()),
		extraction: origin.extraction.clone(),
		pool: auth.pool.clone(),
	};
	let bytes = bytes.to_vec();
	let actor = access.current.account_id;
	let reading = origin
		.reading_tasks
		.spawn(async move { reader.read(&bytes, Some(actor), &cancel).await }.in_current_span())
		.await
		.map_err(|_| fail(StatusCode::BAD_GATEWAY, FAILED))?
		.map_err(|error| match error {
			ProgrammError::Material(reason) => fail(StatusCode::UNPROCESSABLE_ENTITY, reason),
			ProgrammError::Ai(ai::Error::BudgetExceeded(_)) => {
				fail(StatusCode::TOO_MANY_REQUESTS, BUDGET)
			}
			ProgrammError::Ai(ai::Error::InputTooLarge { .. }) => fail(
				StatusCode::UNPROCESSABLE_ENTITY,
				super::worker::INPUT_TOO_LARGE,
			),
			_ => {
				tracing::warn!(
					exception_type = "ProgrammReadingFailure",
					"Programme reading failed"
				);
				fail(StatusCode::BAD_GATEWAY, FAILED)
			}
		})?;
	Ok(access.respond(Json(serde_json::json!({"entwurfId":id,"anfrageId":number,"felder":reading.fields,"programm":reading.programme.into_iter().map(|p|serde_json::json!({"text":p.text,"sicher":p.sure,"lied":p.matched.lied.map(|l|serde_json::json!({"id":l.id,"titel":l.title,"sicher":p.matched.sure})),"kandidaten":p.matched.candidates.into_iter().map(|l|serde_json::json!({"id":l.id,"titel":l.title})).collect::<Vec<_>>()})).collect::<Vec<_>>()})).into_response()))
}
