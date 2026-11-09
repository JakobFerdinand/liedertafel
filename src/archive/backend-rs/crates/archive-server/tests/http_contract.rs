//! HTTP contract tests: the seams the statically exported frontend and the
//! apphost depend on, translated from the C# behavior (parity oracle).
//! They run against the real router without a network — the process-level
//! cold start has its own file below.

// Integration-test binaries are not seen as `#[cfg(test)]` context by
// clippy's test allowances (clippy.toml), so these test plans carry the
// same loud-assert allowances explicitly (AGENTS.md rule: named reason).
#![allow(clippy::expect_used, clippy::panic, clippy::unwrap_used)]

use std::path::Path;
use std::sync::Arc;

use archive_server::{Origin, Settings, build_router};
use axum::body::Body;
use axum::http::{Request, StatusCode, header};
use http_body_util::BodyExt;
use tower::ServiceExt;

/// Builds a tiny static export fixture: the Next export shapes the origin
/// really serves — trailing-slash route directories plus their `index.html` /
/// `404.html` files.
fn write_export_fixture(root: &Path) {
	let pages: &[(&str, &str)] = &[
		("index.html", "Chorübersicht"),
		("404.html", "Seite nicht gefunden"),
		("system/status/index.html", "Systemstatus"),
		("lieder/mond/index.html", "Der Mond ist aufgegangen"),
	];
	for (page, title) in pages {
		let path = root.join(page);
		std::fs::create_dir_all(
			path.parent()
				.unwrap_or_else(|| panic!("fixture path without parent: {page}")),
		)
		.expect("fixture directories");
		std::fs::write(
			&path,
			format!("<html lang=\"de-AT\"><title>{title}</title></html>"),
		)
		.expect("fixture files");
	}
	std::fs::create_dir_all(root.join("assets")).expect("asset directory");
	std::fs::write(
		root.join("assets/favicon.svg"),
		"<svg xmlns=\"http://www.w3.org/2000/svg\"/>",
	)
	.expect("asset fixture");
}

fn origin(settings: Settings) -> Arc<Origin> {
	Arc::new(Origin::new(settings))
}

async fn run_once(
	router: &axum::Router,
	request: Request<Body>,
) -> (StatusCode, axum::http::HeaderMap, String) {
	let response = router.clone().oneshot(request).await.expect("router call");
	let headers = response.headers().clone();
	let status = response.status();
	let bytes = response
		.into_body()
		.collect()
		.await
		.expect("response body")
		.to_bytes();
	let body = String::from_utf8_lossy(&bytes).into_owned();
	(status, headers, body)
}

async fn get_once(
	router: &axum::Router,
	path: &str,
) -> (StatusCode, axum::http::HeaderMap, String) {
	run_once(
		router,
		Request::builder()
			.uri(path)
			.body(Body::empty())
			.expect("GET request"),
	)
	.await
}

#[tokio::test]
async fn build_probe_answers_with_the_c_shape_and_probe_cache_policy() {
	let settings = Settings::new(true, FixtureRoot::fixture(), 0);
	let router = build_router(origin(settings));

	let (status, headers, body) = get_once(&router, "/api/build").await;
	assert_eq!(status, StatusCode::OK);
	assert_eq!(
		headers.get(header::CACHE_CONTROL).expect("cache header"),
		"no-cache, no-store",
		"the probe cache policy is deliberate and different from no-store"
	);
	let payload: serde_json::Value = serde_json::from_str(&body).expect("build JSON");
	assert_eq!(payload["application"], "Liedertafel Archiv");
	assert_eq!(
		payload["version"],
		archive_server::settings::build_version()
	);
	assert_eq!(payload["development"], true);
}

#[tokio::test]
async fn build_probe_counts_and_refuses_unregistered_methods() {
	let settings = Settings::new(false, FixtureRoot::fixture(), 0);
	let router = build_router(origin(settings));

	let (_status, _headers, _body) = get_once(&router, "/api/build").await;
	// Unregistered methods answer the stable miss, not a bare 405 method
	// fallback (C# parity: any method without a mapping reaches MapFallback).
	let (post_status, post_headers, post_body) = run_once(
		&router,
		Request::builder()
			.method("POST")
			.uri("/api/build")
			.body(Body::empty())
			.expect("POST request"),
	)
	.await;
	assert_eq!(post_status, StatusCode::NOT_FOUND);
	assert_eq!(
		post_headers
			.get(header::CONTENT_TYPE)
			.expect("problem type"),
		"application/problem+json"
	);
	assert!(
		post_body.contains("API-Endpunkt nicht gefunden."),
		"stable miss title stays German and identical: {post_body}"
	);
}

#[tokio::test]
async fn every_other_api_call_answers_the_stable_404() {
	let settings = Settings::new(false, FixtureRoot::fixture(), 0);
	let router = build_router(origin(settings));

	for path in [
		"/api/liebe",
		"/api/dev/exercise",
		"/api/lieder/00000000-0000-0000-0000-00000000a001",
		"/api",
	] {
		let (status, headers, body) = get_once(&router, path).await;
		assert_eq!(status, StatusCode::NOT_FOUND, "{path} must answer 404");
		assert_eq!(
			headers.get(header::CONTENT_TYPE).expect("problem type"),
			"application/problem+json",
			"API outages stay JSON (frontend shell.spec.ts contract)"
		);
		let payload: serde_json::Value = serde_json::from_str(&body).expect("problem JSON");
		assert_eq!(payload["title"], "API-Endpunkt nicht gefunden.", "{path}");
		assert_eq!(payload["status"], 404, "{path}");
	}
}

#[tokio::test]
async fn liveness_says_healthy_without_dependencies() {
	let settings = Settings::new(false, FixtureRoot::fixture(), 0);
	let router = build_router(origin(settings));

	let (alive_status, alive_headers, alive_body) = get_once(&router, "/alive").await;
	assert_eq!(alive_status, StatusCode::OK);
	assert_eq!(alive_body, "Healthy");
	// Content type of the ASP.NET health-check response.
	assert!(
		alive_headers
			.get(header::CONTENT_TYPE)
			.expect("live type")
			.to_str()
			.expect("ascii header")
			.starts_with("text/plain"),
		"the health check answers text/plain like ASP.NET"
	);

	let (health_status, _, _) = get_once(&router, "/health").await;
	assert_eq!(
		health_status,
		StatusCode::NOT_FOUND,
		"/health only exists in Development"
	);
}

#[tokio::test]
async fn development_carries_the_readiness_probe_too() {
	let settings = Settings::new(true, FixtureRoot::fixture(), 0);
	let router = build_router(origin(settings));
	let (status, _, body) = get_once(&router, "/health").await;
	assert_eq!(status, StatusCode::OK);
	assert_eq!(body, "Healthy");
}

#[tokio::test]
async fn root_and_deep_links_serve_the_export() {
	let root = std::env::temp_dir().join("archive-server-fixture-index");
	write_export_fixture(&root);
	let settings = Settings::new(false, root, 0);
	let router = build_router(origin(settings));

	let (root_status, root_headers, root_body) = get_once(&router, "/").await;
	assert_eq!(root_status, StatusCode::OK);
	assert!(root_body.contains("Chorübersicht"));
	let content_type = root_headers
		.get(header::CONTENT_TYPE)
		.expect("html type")
		.to_str()
		.expect("ascii header");
	assert_eq!(content_type, "text/html; charset=utf-8");

	let (deep_status, _, deep_body) = get_once(&router, "/system/status/").await;
	assert_eq!(deep_status, StatusCode::OK);
	assert!(deep_body.contains("Systemstatus"));
}

#[tokio::test]
async fn unknown_static_route_answers_404_page_once_with_status_404() {
	let root = std::env::temp_dir().join("archive-server-fixture-404");
	write_export_fixture(&root);
	let settings = Settings::new(false, root, 0);
	let router = build_router(origin(settings));

	let (status, headers, body) = get_once(&router, "/gdzungal/lied/").await;
	assert_eq!(status, StatusCode::NOT_FOUND, "missing pages remain 404s");
	assert_eq!(
		headers.get(header::CONTENT_TYPE).expect("404 type"),
		"text/html; charset=utf-8",
		"404.html renders like the C# SendFileAsync path"
	);
	assert!(body.contains("Seite nicht gefunden"), "{body}");
}

#[tokio::test]
async fn static_assets_keep_their_media_type_and_api_routes_shadow_files() {
	let root = std::env::temp_dir().join("archive-server-fixture-assets");
	write_export_fixture(&root);
	let settings = Settings::new(false, root, 0);
	let router = build_router(origin(settings));

	let (asset_status, asset_headers, _) = get_once(&router, "/assets/favicon.svg").await;
	assert_eq!(asset_status, StatusCode::OK);
	assert_eq!(
		asset_headers.get(header::CONTENT_TYPE).expect("svg type"),
		"image/svg+xml"
	);

	// The API namespace is reserved: even a path spelled like a file is
	// answered by the API miss, never by a static file.
	let (api_status, api_headers, api_body) = get_once(&router, "/api/build.js").await;
	assert_eq!(api_status, StatusCode::NOT_FOUND);
	assert_eq!(
		api_headers.get(header::CONTENT_TYPE).expect("problem type"),
		"application/problem+json"
	);
	assert!(api_body.contains("API-Endpunkt nicht gefunden."));
}

struct FixtureRoot;

impl FixtureRoot {
	/// A shared temp directory the fixture-free tests never touch; the
	/// export root stays fully configurable per test.
	fn fixture() -> std::path::PathBuf {
		std::env::temp_dir().join("archive-server-fixture-shared")
	}
}
