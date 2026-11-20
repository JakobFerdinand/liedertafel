// HTTP-only maintenance prerequisite: storage actions must not race migration.
#![allow(clippy::unwrap_used)]
use archive_server::{Origin, Settings, build_router};
use axum::{body::Body, http::Request, response::Response};
use http_body_util::BodyExt;
use serde_json::{Value, json};
use std::{path::PathBuf, sync::Arc};
use tower::ServiceExt;

const MAINTENANCE: &str = "Wartungsarbeiten: Das Archiv ist vorübergehend nicht verfügbar. Bitte versuchen Sie es später erneut.";

async fn payload(response: Response) -> Value {
	serde_json::from_slice(&response.into_body().collect().await.unwrap().to_bytes()).unwrap()
}

// The public ARC-012 cases from the frozen MaintenanceTests (Production).
#[tokio::test]
async fn maintenance_status_and_release_probes_remain_available() {
	for (active, message) in [(false, "Das Archiv ist verfügbar."), (true, MAINTENANCE)] {
		let mut settings = Settings::new(false, PathBuf::new(), 0);
		settings.maintenance = active;
		let router = build_router(Arc::new(Origin::new(settings)));
		for path in ["/alive", "/api/build", "/api/maintenance"] {
			let response = router
				.clone()
				.oneshot(Request::get(path).body(Body::empty()).unwrap())
				.await
				.unwrap();
			assert_eq!(response.status(), 200, "{path} in maintenance={active}");
			if path == "/api/maintenance" {
				assert_eq!(response.headers()["cache-control"], "no-store");
				assert_eq!(
					payload(response).await,
					json!({"maintenance":active,"message":message})
				);
			}
		}
	}
}

#[tokio::test]
async fn maintenance_returns_a_german_problem_before_auth_csrf_or_api_fallback() {
	let mut settings = Settings::new(false, PathBuf::new(), 0);
	settings.maintenance = true;
	let router = build_router(Arc::new(Origin::new(settings)));
	for (method, path) in [
		("GET", "/api/antiforgery"),
		("POST", "/api/auth/code/request"),
		("GET", "/api/auth/me"),
		("GET", "/api/nonexistent-arc012-probe"),
	] {
		let response = router
			.clone()
			.oneshot(
				Request::builder()
					.method(method)
					.uri(path)
					.body(Body::empty())
					.unwrap(),
			)
			.await
			.unwrap();
		assert_eq!(response.status(), 503, "{method} {path}");
		assert_eq!(
			response.headers()["content-type"],
			"application/problem+json"
		);
		assert_eq!(response.headers()["retry-after"], "600");
		assert_eq!(payload(response).await["title"], MAINTENANCE);
	}
}
#[tokio::test]
async fn maintenance_pauses_catalogue_reads_and_tier_before_dependencies_and_csrf() {
	let mut settings = Settings::new(true, PathBuf::new(), 0);
	settings.maintenance = true;
	let router = build_router(Arc::new(Origin::new(settings)));
	for (method, path) in [
		("GET", "/api/lieder"),
		("GET", "/api/lieder/00000000-0000-0000-0000-000000000001"),
		(
			"GET",
			"/api/dateien/00000000-0000-0000-0000-000000000001/access",
		),
		(
			"POST",
			"/api/dateien/00000000-0000-0000-0000-000000000001/storage-tier",
		),
	] {
		let response = router
			.clone()
			.oneshot(
				Request::builder()
					.method(method)
					.uri(path)
					.body(Body::empty())
					.unwrap(),
			)
			.await
			.unwrap();
		assert_eq!(response.status(), 503);
		assert_eq!(response.headers()["retry-after"], "600");
		assert_eq!(payload(response).await["title"], MAINTENANCE);
	}
}
