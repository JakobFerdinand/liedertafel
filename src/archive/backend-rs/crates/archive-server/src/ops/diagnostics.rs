use crate::{
	Origin, api,
	auth::{Auth, AuthError},
	queue,
};
use axum::{
	Json,
	body::to_bytes,
	extract::{DefaultBodyLimit, FromRequest, Multipart, Request, State},
	http::{HeaderValue, StatusCode, header},
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use serde_json::json;
use std::{sync::Arc, time::Duration};
use tracing::Instrument;

const MAX_FORM_BYTES: usize = 30_000_000; // frozen Kestrel's default request-body limit
const FORM_TOKEN: &str = "__RequestVerificationToken";

pub(crate) fn routes() -> [(&'static str, MethodRouter<Arc<Origin>>); 4] {
	[
		("/api/dev/database", get(database).fallback(api::api_miss)),
		("/api/dev/ai-budget", get(ai_budget).fallback(api::api_miss)),
		(
			"/api/dev/exercise",
			post(exercise)
				.layer(DefaultBodyLimit::max(MAX_FORM_BYTES))
				.fallback(api::api_miss),
		),
		(
			"/api/dev/extraction-duplicate",
			post(extraction_duplicate)
				.layer(DefaultBodyLimit::max(MAX_FORM_BYTES))
				.fallback(api::api_miss),
		),
	]
}

fn failure() -> Response {
	tracing::error!(target: "Archive.Diagnostics", "Development diagnostic failed");
	let mut response = api::problem(
		StatusCode::INTERNAL_SERVER_ERROR,
		"Die Anfrage konnte nicht verarbeitet werden.",
	);
	// The frozen exception handler replaces cache headers on unhandled
	// failures, unlike CSRF, missing Guid and unconfigured Queue responses.
	response.headers_mut().insert(
		header::CACHE_CONTROL,
		HeaderValue::from_static("no-cache,no-store"),
	);
	response
		.headers_mut()
		.insert(header::PRAGMA, HeaderValue::from_static("no-cache"));
	response
		.headers_mut()
		.insert(header::EXPIRES, HeaderValue::from_static("-1"));
	response
}
async fn database(State(origin): State<Arc<Origin>>) -> Response {
	let Some(auth) = &origin.auth else {
		return failure();
	};
	match archive_db::development_status(&auth.pool).await {
		Ok((connected, pending)) => {
			Json(json!({"connected":connected,"pendingMigrations":pending})).into_response()
		}
		Err(_) => failure(),
	}
}

async fn ai_budget(State(origin): State<Arc<Origin>>) -> Response {
	let (Some(auth), Some(ai)) = (&origin.auth, &origin.ai) else {
		return failure();
	};
	let Ok(status) = ai.ledger.status().await else {
		return failure();
	};
	let totals: Result<(i64, i64, i64, i64, i64), _> = sqlx::query_as(
		r#"
		SELECT count(*), coalesce(sum("Calls"),0)::bigint, coalesce(sum("CostMicroEur"),0)::bigint,
			coalesce(sum("ReservedMicroEur"),0)::bigint,
			(SELECT count(*) FROM ai_budget_months WHERE "YearMonth" = $1)
		FROM ai_usage_entries WHERE "YearMonth" = $1"#,
	)
	.bind(&status.month)
	.fetch_one(&auth.pool)
	.await;
	let Ok((entries, calls, cost, reserved, month_rows)) = totals else {
		return failure();
	};
	Json(json!({"yearMonth":status.month,"spentMicroEur":status.spent_micro_eur,"capMicroEur":status.cap_micro_eur,
		"entries":entries,"calls":calls,"costMicroEur":cost,"reservedMicroEur":reserved,"monthRows":month_rows})).into_response()
}

fn csrf_problem(e: AuthError) -> Response {
	match e {
		AuthError::Problem(status, title) => api::problem(status, title),
		_ => failure(),
	}
}

async fn csrf(origin: &Origin, request: Request) -> Result<(), AuthError> {
	let mut headers = request.headers().clone();
	const REQUEST_TOKEN: &str = "X-CSRF-TOKEN";
	if Auth::has_diagnostic_csrf_cookie(&headers)
		&& !headers.contains_key(REQUEST_TOKEN)
		&& let Some(token) = form_token(request).await
		&& let Ok(value) = HeaderValue::try_from(token)
	{
		headers.insert(REQUEST_TOKEN, value);
	} else if headers.get_all(REQUEST_TOKEN).iter().count() > 1 {
		// StringValues joins nonempty repeated values. Two nonempty tokens
		// never validate, even when an attacker repeats the same token.
		let mut values = headers
			.get_all(REQUEST_TOKEN)
			.into_iter()
			.filter(|v| !v.is_empty());
		let first = values.next().cloned();
		let scalar = if values.next().is_none() { first } else { None };
		headers.remove(REQUEST_TOKEN);
		if let Some(scalar) = scalar {
			headers.insert(REQUEST_TOKEN, scalar);
		}
	}
	Auth::validate_diagnostic_csrf(origin.auth.as_deref(), &headers).await
}

async fn form_token(request: Request) -> Option<String> {
	let mime: mime::Mime = request
		.headers()
		.get(header::CONTENT_TYPE)?
		.to_str()
		.ok()?
		.parse()
		.ok()?;
	let mut token = None;
	if mime.essence_str() == "application/x-www-form-urlencoded" {
		let bytes = to_bytes(request.into_body(), MAX_FORM_BYTES).await.ok()?;
		for (name, value) in url::form_urlencoded::parse(&bytes) {
			if name.eq_ignore_ascii_case(FORM_TOKEN)
				&& !value.is_empty()
				&& (value.len() != 43 || token.replace(value.into_owned()).is_some())
			{
				return None;
			}
		}
	} else if mime.essence_str() == "multipart/form-data" {
		let mut parts = Multipart::from_request(request, &()).await.ok()?;
		while let Some(mut field) = parts.next_field().await.ok()? {
			if field.file_name().is_none()
				&& field
					.name()
					.is_some_and(|name| name.eq_ignore_ascii_case(FORM_TOKEN))
			{
				let mut value = Vec::new();
				while let Some(chunk) = field.chunk().await.ok()? {
					if chunk.len() > 43 - value.len() {
						return None;
					}
					value.extend_from_slice(&chunk);
				}
				if !value.is_empty()
					&& (value.len() != 43
						|| token.replace(String::from_utf8(value).ok()?).is_some())
				{
					return None;
				}
			}
		}
	}
	token
}

async fn exercise(State(origin): State<Arc<Origin>>, request: Request) -> Response {
	if let Err(rejected) = csrf(&origin, request).await {
		return csrf_problem(rejected);
	}
	// The resource-owning task must finish its bounded scratch cleanup even if
	// the HTTP future is dropped. The handler drop and shutdown both cancel work.
	let cancel = origin.shutdown.child_token();
	let _request = cancel.clone().drop_guard();
	let result = origin
		.track_request_work(async move {
			super::local::exercise(&cancel, Some(Duration::from_secs(30))).await
		})
		.await;
	if !matches!(result, Ok(Ok(()))) {
		return failure();
	}
	tracing::info!(target: "Archive.Diagnostics", "Local Blob, queue and mail exercise completed");
	Json(json!({"message":"Blob und Warteschlange geprüft. Testmail wurde gesendet."}))
		.into_response()
}

async fn extraction_duplicate(State(origin): State<Arc<Origin>>, request: Request) -> Response {
	// Minimal API binds the optional query Guid before entering the handler.
	let values = url::form_urlencoded::parse(request.uri().query().unwrap_or("").as_bytes())
		.filter(|(k, _)| k.eq_ignore_ascii_case("dateiId"))
		.map(|(_, v)| v)
		.collect::<Vec<_>>();
	let id = if values.is_empty() {
		None
	} else {
		let scalar = values
			.iter()
			.filter(|v| !v.is_empty())
			.map(|v| v.as_ref())
			.collect::<Vec<_>>()
			.join(",");
		match crate::clr_guid::try_parse(&scalar) {
			Some(id) => Some(id),
			None => return failure(),
		}
	};
	if let Err(rejected) = csrf(&origin, request).await {
		return csrf_problem(rejected);
	}
	let Some(id) = id.filter(|id| !id.is_nil()) else {
		return api::problem(StatusCode::BAD_REQUEST, "Es wurde keine Datei angegeben.");
	};
	let send = async {
		origin
			.extraction_queue
			.send(&queue::envelope("extraction", id))
			.await
	}
	.instrument(tracing::info_span!(
		"archive.queue.send",
		otel.kind = "producer"
	));
	match tokio::time::timeout(Duration::from_secs(30), send).await {
		Ok(Ok(true)) => Json(json!({"sent":true})).into_response(),
		Ok(Ok(false)) => api::problem(
			StatusCode::BAD_GATEWAY,
			"Kein Warteschlangen-Endpunkt für die Textauswertung konfiguriert.",
		),
		Ok(Err(_)) | Err(_) => failure(),
	}
}
