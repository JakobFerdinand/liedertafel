// HTTP-only maintenance prerequisite: storage actions must not race migration.
#![allow(clippy::unwrap_used)]
use archive_server::{Origin, Settings, build_router};
use axum::{body::Body, http::Request};
use http_body_util::BodyExt;
use std::{path::PathBuf, sync::Arc};
use tower::ServiceExt;
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
		let body: serde_json::Value =
			serde_json::from_slice(&response.into_body().collect().await.unwrap().to_bytes())
				.unwrap();
		assert_eq!(
			body["title"],
			"Wartungsarbeiten: Das Archiv ist vorübergehend nicht verfügbar. Bitte versuchen Sie es später erneut."
		);
	}
	assert_eq!(
		router
			.clone()
			.oneshot(
				Request::builder()
					.uri("/api/build")
					.body(Body::empty())
					.unwrap()
			)
			.await
			.unwrap()
			.status(),
		200
	);
	let response = router
		.oneshot(
			Request::builder()
				.uri("/api/maintenance")
				.body(Body::empty())
				.unwrap(),
		)
		.await
		.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["cache-control"], "no-store");
}
