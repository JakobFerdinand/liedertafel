//! The `/api/*` surface. One list of implemented routes drives the router;
//! everything outside it answers the single stable JSON 404/501 miss shape —
//! disabled, not hidden (spec #218, wave 1 [#219]).

use axum::extract::State;
use axum::http::{HeaderValue, StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::routing::{MethodRouter, any, get};
use axum::{Json, Router};
use serde_json::json;
use std::sync::Arc;

use crate::origin::Origin;

/// German ProblemDetails title of the API fallback, lifted verbatim from the
/// frozen C# backend (`MapFallback("/api/{**path}")`) so both backends answer
/// identically until the parity suite retargets.
pub const API_UNKNOWN_TITLE: &str = "API-Endpunkt nicht gefunden.";

/// Stable title for routes the port plan carries but has not translated yet.
/// No oracle text pins it (the C# backend never answers 501 for an existing
/// API surface), so wave 1 defines it here in the same German tone as the
/// 404 title; later waves that switch a route to the 501 miss make it real.
pub const API_UNPORTED_TITLE: &str = "Der Endpunkt ist noch nicht umgestellt.";

/// `application` payload of `/api/build`, verbatim from the C# backend.
pub const APPLICATION: &str = "Liedertafel Archiv";
const MAINTENANCE_MESSAGE: &str = "Wartungsarbeiten: Das Archiv ist vorübergehend nicht verfügbar. Bitte versuchen Sie es später erneut.";

/// Which stable miss a `/api/*` path outside the implemented list answers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ApiMiss {
	/// Path that never existed in the archive API — 404, like today's C#.
	Unknown,
	/// Route the port plan knows but has not translated — 501, so a feature
	/// never advertises a never-existing endpoint officially.
	NotYetPorted,
}

impl ApiMiss {
	/// The one stable shape for every unported `/api/*` call:
	/// `application/problem+json` with a German title. Wave 1 answers every
	/// unported route with the unknown miss (404, the C# API fallback
	/// verbatim); later waves flip their registry entries from the `Unknown`
	/// to the not-yet-ported miss (501) once a known route moves waves.
	pub fn response(self) -> Response {
		match self {
			Self::Unknown => problem(StatusCode::NOT_FOUND, API_UNKNOWN_TITLE),
			Self::NotYetPorted => problem(StatusCode::NOT_IMPLEMENTED, API_UNPORTED_TITLE),
		}
	}
}

/// The list of implemented API routes in wave 1. Adding a route appends
/// exactly one entry here; no other list exists. Method fallbacks keep the
/// C# behavior (unregistered methods answer the miss, not a raw 405). The
/// entries are state-aware handlers, so `implemented_api_routes` receives
/// the `Origin` that `with_state` applies at router assembly.
fn implemented_api_routes(_origin: Arc<Origin>) -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	let mut routes = vec![("/api/build", get(build).fallback(api_miss))];
	routes.push((
		"/api/maintenance",
		get(maintenance_status).fallback(api_miss),
	));
	routes.extend(crate::auth::routes());
	routes.extend(crate::catalogue::routes());
	routes.extend(crate::events::routes());
	routes.extend(crate::extraction::routes());
	routes.extend(crate::recordings::routes());
	routes
}

/// The `/api` scope: implemented routes win, then the single miss shape
/// answers every other `/api/*` call — including the bare `/api` and methods
/// an implemented route does not register.
pub fn api_router(origin: Arc<Origin>) -> Router<()> {
	let mut router = Router::new()
		.route("/api", any(api_miss))
		.route("/api/{*rest}", any(api_miss));
	for (path, method_router) in implemented_api_routes(origin.clone()) {
		router = router.route(path, method_router);
	}
	router
		.with_state(origin.clone())
		.layer(axum::middleware::from_fn_with_state(
			origin,
			maintenance_guard,
		))
}

async fn maintenance_status(State(origin): State<Arc<Origin>>) -> Response {
	([(header::CACHE_CONTROL,"no-store")],Json(json!({"maintenance":origin.settings.maintenance,"message":if origin.settings.maintenance {MAINTENANCE_MESSAGE}else{"Das Archiv ist verfügbar."}}))).into_response()
}
async fn maintenance_guard(
	State(origin): State<Arc<Origin>>,
	request: axum::extract::Request,
	next: axum::middleware::Next,
) -> Response {
	let path = request.uri().path();
	let exempt = ["/api/build", "/api/maintenance"].iter().any(|p| {
		path == *p
			|| path
				.strip_prefix(p)
				.is_some_and(|tail| tail.starts_with('/'))
	});
	if origin.settings.maintenance && !exempt {
		let mut response = problem(StatusCode::SERVICE_UNAVAILABLE, MAINTENANCE_MESSAGE);
		response
			.headers_mut()
			.insert(header::RETRY_AFTER, HeaderValue::from_static("600"));
		return response;
	}
	next.run(request).await
}

/// GET `/api/build`: build-probe payload plus the probe cache policy
/// ("no-cache, no-store", deliberately not the access module's no-store
/// rule) and the `archive.build.requests` meter signal, with the same
/// structured log event the C# host writes.
pub async fn build(State(origin): State<Arc<Origin>>) -> Response {
	origin.build_requests.add(1, &[]);
	tracing::info!(
		version = %crate::settings::build_version(),
		"Archive build information requested ({})",
		crate::settings::build_version(),
	);

	let payload = json!({
		"application": APPLICATION,
		"version": crate::settings::build_version(),
		"development": origin.settings.development,
	});
	(
		StatusCode::OK,
		[(
			header::CACHE_CONTROL,
			HeaderValue::from_static("no-cache, no-store"),
		)],
		Json(payload),
	)
		.into_response()
}

/// Liveness probe. Neither this nor the Development `/health` resolves a
/// database or storage client in any environment — the archive readiness is
/// process health only, exactly like the C# default endpoints.
pub async fn readiness() -> &'static str {
	"Healthy"
}

/// Any `/api/*` route outside the implemented list: the stable miss.
pub async fn api_miss() -> Response {
	ApiMiss::Unknown.response()
}

/// ASP.NET-shape ProblemDetails JSON: `type` link, `title`, `status`. The
/// frontend reads only the title, but the stable shape stays whole on both
/// backends.
pub(crate) fn problem(status: StatusCode, title: &str) -> Response {
	let mut body = json!({
		"title": title,
		"status": status.as_u16(),
	});
	if let Some(section) = rfc9110_section(status) {
		body["type"] = json!(format!(
			"https://tools.ietf.org/html/rfc9110#section-{section}"
		));
	}
	(
		status,
		[(
			header::CONTENT_TYPE,
			HeaderValue::from_static("application/problem+json"),
		)],
		Json(body),
	)
		.into_response()
}

/// Section anchors of RFC 9110 for the statuses the stable shape uses,
/// mirroring the default ASP.NET ProblemDetails `type` links. Only the two
/// miss statuses exist today; more statuses join when a wave brings real
/// handlers for them.
fn rfc9110_section(status: StatusCode) -> Option<&'static str> {
	match status {
		StatusCode::BAD_REQUEST => Some("15.5.1"),
		StatusCode::UNAUTHORIZED => Some("15.5.2"),
		StatusCode::FORBIDDEN => Some("15.5.4"),
		StatusCode::CONFLICT => Some("15.5.10"),
		StatusCode::INTERNAL_SERVER_ERROR => Some("15.6.1"),
		StatusCode::BAD_GATEWAY => Some("15.6.3"),
		StatusCode::SERVICE_UNAVAILABLE => Some("15.6.4"),
		StatusCode::UNPROCESSABLE_ENTITY => Some("15.5.21"),
		StatusCode::NOT_FOUND => Some("15.5.5"),
		StatusCode::NOT_IMPLEMENTED => Some("15.6.2"),
		_ => None,
	}
}

#[cfg(test)]
mod tests {
	use super::*;
	use http_body_util::BodyExt;

	async fn body_json(response: Response) -> serde_json::Value {
		let bytes = response
			.into_body()
			.collect()
			.await
			.unwrap_or_else(|source| panic!("body collection failed: {source}"))
			.to_bytes();
		serde_json::from_slice(&bytes)
			.unwrap_or_else(|source| panic!("body was not JSON: {source}"))
	}

	async fn content_type_of(response: &Response) -> String {
		response
			.headers()
			.get(header::CONTENT_TYPE)
			.unwrap_or_else(|| panic!("missing content type"))
			.to_str()
			.unwrap_or_else(|source| panic!("header not utf8: {source}"))
			.to_string()
	}

	#[tokio::test]
	async fn unknown_miss_answers_the_c_shape_404() {
		let response = ApiMiss::Unknown.response();
		assert_eq!(response.status(), StatusCode::NOT_FOUND);
		assert_eq!(
			content_type_of(&response).await,
			"application/problem+json",
			"API outages stay JSON (frontend shell.spec.ts contract)"
		);
		let payload = body_json(response).await;
		assert_eq!(payload["title"], API_UNKNOWN_TITLE);
		assert_eq!(payload["status"], 404);
		assert_eq!(
			payload["type"], "https://tools.ietf.org/html/rfc9110#section-15.5.5",
			"the type link mirrors the ASP.NET default"
		);
	}

	#[tokio::test]
	async fn unported_miss_answers_the_c_shape_501() {
		let response = ApiMiss::NotYetPorted.response();
		assert_eq!(response.status(), StatusCode::NOT_IMPLEMENTED);
		assert_eq!(
			content_type_of(&response).await,
			"application/problem+json",
			"the shape stays stable between 404 and 501"
		);
		let payload = body_json(response).await;
		assert_eq!(payload["title"], API_UNPORTED_TITLE);
		assert_eq!(payload["status"], 501);
		assert_eq!(
			payload["type"], "https://tools.ietf.org/html/rfc9110#section-15.6.2",
			"the type link mirrors the ASP.NET default"
		);
	}
}
