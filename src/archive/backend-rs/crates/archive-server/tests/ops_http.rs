// Fixture failures should identify the exact assertion.
#![allow(clippy::unwrap_used)]
use archive_server::{Origin, Settings, build_router};
use axum::{body::Body, http::Request};
use http_body_util::BodyExt;
use std::sync::Arc;
use tower::ServiceExt;

#[tokio::test]
async fn diagnostics_are_development_only_and_validate_csrf_before_services() {
	for (method, path) in [
		("GET", "/api/dev/database"),
		("GET", "/api/dev/ai-budget"),
		("POST", "/api/dev/exercise"),
		("POST", "/api/dev/extraction-duplicate"),
	] {
		for (development, maintenance, expected) in [
			(false, false, 404),
			(true, true, 503),
			(true, false, if method == "POST" { 400 } else { 500 }),
		] {
			let mut settings = Settings::new(development, "missing".into(), 0);
			settings.maintenance = maintenance;
			let response = build_router(Arc::new(Origin::new(settings)))
				.oneshot(
					Request::builder()
						.method(method)
						.uri(path)
						.body(Body::empty())
						.unwrap(),
				)
				.await
				.unwrap();
			assert_eq!(response.status(), expected, "{method} {path}");
			if expected == 400 {
				assert!(response.headers().get("cache-control").is_none());
				let payload: serde_json::Value = serde_json::from_slice(
					&response.into_body().collect().await.unwrap().to_bytes(),
				)
				.unwrap();
				assert_eq!(payload["title"], "Ungültiger Sicherheitstoken.");
			} else if expected == 500 {
				assert_eq!(response.headers()["cache-control"], "no-cache,no-store");
			}
		}
	}
}

#[tokio::test]
async fn development_duplicate_binds_guid_before_csrf_like_frozen_minimal_api() {
	let origin = Arc::new(Origin::new(Settings::new(true, "missing".into(), 0)));
	for (query, code) in [
		("dateiId=", 500),
		("dateiId=not-a-guid", 500),
		(
			"dateiId=12345678-abcd-4321-89ab-cdef01234567&dateiId=12345678-abcd-4321-89ab-cdef01234567",
			500,
		),
		("dateiId=&DATEIID=12345678-abcd-4321-89ab-cdef01234567", 400),
		("DATEIID=12345678-abcd-4321-89ab-cdef01234567&dateiId=", 400),
	] {
		let response = build_router(origin.clone())
			.oneshot(
				Request::post(format!("/api/dev/extraction-duplicate?{query}"))
					.body(Body::empty())
					.unwrap(),
			)
			.await
			.unwrap();
		assert_eq!(response.status(), code, "{query}");
		let body: serde_json::Value =
			serde_json::from_slice(&response.into_body().collect().await.unwrap().to_bytes())
				.unwrap();
		assert_eq!(
			body["title"],
			if code == 500 {
				"Die Anfrage konnte nicht verarbeitet werden."
			} else {
				"Ungültiger Sicherheitstoken."
			}
		);
	}
}
