//! Anonymous allocation budgets at the real HTTP/PostgreSQL boundary (#221).
// Integration-only assertions and synthetic request/fixture helpers fail loudly.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use axum::{
	Router,
	body::Body,
	extract::ConnectInfo,
	http::{Request, StatusCode},
	response::Response,
};
use http_body_util::BodyExt;
use serde_json::Value;
use tower::ServiceExt;

mod support;

async fn anonymous(router: &Router, route: &str, address: &str) -> Response {
	with_cookie(router, route, address, None).await
}

async fn with_cookie(
	router: &Router,
	route: &str,
	address: &str,
	cookie: Option<&str>,
) -> Response {
	let mut request = Request::builder()
		.method(if route == "/api/antiforgery" {
			"GET"
		} else {
			"POST"
		})
		.uri(route)
		// The untrusted leftmost value must never select a different budget.
		.header("x-forwarded-for", format!("203.0.113.99, {address}"))
		.body(Body::empty())
		.unwrap();
	if let Some(cookie) = cookie {
		request
			.headers_mut()
			.insert("cookie", cookie.parse().unwrap());
	}
	request.extensions_mut().insert(ConnectInfo(
		"127.0.0.1:54321".parse::<std::net::SocketAddr>().unwrap(),
	));
	router.clone().oneshot(request).await.unwrap()
}

async fn state(app: &support::App) -> Value {
	sqlx::query_scalar("SELECT jsonb_build_object('csrf',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY browser_digest),'[]'::jsonb) FROM archive_csrf s),'ceremonies',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY browser_digest,operation),'[]'::jsonb) FROM archive_ceremonies s),'budgets',(SELECT jsonb_agg(to_jsonb(s) ORDER BY slot) FROM archive_auth_allocation_budgets s))")
		.fetch_one(&app.pool).await.unwrap()
}

async fn limited(response: Response) {
	assert_eq!(response.status(), StatusCode::TOO_MANY_REQUESTS);
	assert_eq!(response.headers()["cache-control"], "no-store");
	assert!(response.headers().get("set-cookie").is_none());
	let body: Value =
		serde_json::from_slice(&response.into_body().collect().await.unwrap().to_bytes()).unwrap();
	assert_eq!(
		body["title"],
		"Zu viele Anfragen. Bitte später erneut versuchen."
	);
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn cookie_less_csrf_allocation_stops_without_persisting_rejected_requests() {
	let app = support::App::new().await;
	for _ in 0..30 {
		assert_eq!(
			anonymous(&app.router, "/api/antiforgery", "198.51.100.9")
				.await
				.status(),
			200
		);
	}
	assert_eq!(
		anonymous(
			&app.router,
			"/api/auth/passkeys/login/options",
			"198.51.100.10"
		)
		.await
		.status(),
		200
	);
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(6);
	let before = state(&app).await;
	assert_eq!(before["csrf"].as_array().unwrap().len(), 30);
	limited(anonymous(&app.router, "/api/antiforgery", "198.51.100.9").await).await;
	assert_eq!(
		state(&app).await,
		before,
		"rejection must not allocate, renew or clean up state"
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn cookie_less_login_options_spend_the_same_bounded_budget_without_csrf() {
	let app = support::App::new().await;
	for _ in 0..30 {
		assert_eq!(
			anonymous(
				&app.router,
				"/api/auth/passkeys/login/options",
				"198.51.100.9"
			)
			.await
			.status(),
			200
		);
	}
	let before = state(&app).await;
	assert_eq!(before["ceremonies"].as_array().unwrap().len(), 30);
	limited(
		anonymous(
			&app.router,
			"/api/auth/passkeys/login/options",
			"198.51.100.9",
		)
		.await,
	)
	.await;
	assert_eq!(state(&app).await, before);
	app.close().await;
}

fn replica(app: &support::App, cap: i64) -> Router {
	let mut config = archive_server::auth::AuthConfig::development();
	config.max_outstanding_auth_state = cap;
	support::App::router(app.pool.clone(), app.mail.clone(), app.time.clone(), config)
}

fn cookie(response: &Response) -> String {
	response.headers()["set-cookie"]
		.to_str()
		.unwrap()
		.split(';')
		.next()
		.unwrap()
		.into()
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn routes_and_replicas_share_the_trusted_address_budget_but_valid_csrf_reuse_is_free() {
	let app = support::App::new().await;
	let second = replica(&app, 4096);
	let first = anonymous(&app.router, "/api/antiforgery", "198.51.100.9").await;
	let csrf_cookie = cookie(&first);
	let token: Value =
		serde_json::from_slice(&first.into_body().collect().await.unwrap().to_bytes()).unwrap();
	for i in 1..30 {
		let router = if i % 2 == 0 { &app.router } else { &second };
		let route = if i % 3 == 0 {
			"/api/antiforgery"
		} else {
			"/api/auth/passkeys/login/options"
		};
		// Different untrusted hops cannot create a new trusted-address budget.
		assert_eq!(
			anonymous(router, route, &format!("198.18.0.{i}, 198.51.100.9"))
				.await
				.status(),
			200
		);
	}
	let before = state(&app).await;
	for router in [&app.router, &second] {
		for route in ["/api/antiforgery", "/api/auth/passkeys/login/options"] {
			limited(anonymous(router, route, "198.51.100.9").await).await;
		}
	}
	assert_eq!(state(&app).await, before);
	let mut requests = tokio::task::JoinSet::new();
	for i in 0..40 {
		let router = if i % 2 == 0 {
			app.router.clone()
		} else {
			second.clone()
		};
		let cookie = csrf_cookie.clone();
		requests.spawn(async move {
			let response =
				with_cookie(&router, "/api/antiforgery", "198.51.100.9", Some(&cookie)).await;
			assert_eq!(response.status(), 200);
			serde_json::from_slice::<Value>(
				&response.into_body().collect().await.unwrap().to_bytes(),
			)
			.unwrap()
		});
	}
	while let Some(result) = requests.join_next().await {
		assert_eq!(result.unwrap(), token);
	}
	assert_eq!(
		state(&app).await,
		before,
		"parallel reuse preserves the token and spends no extra budget"
	);
	// A distinct effective address is independent, despite the common untrusted hop.
	assert_eq!(
		anonymous(&second, "/api/antiforgery", "198.51.100.10")
			.await
			.status(),
		200
	);
	// The per-address window, not CSRF's two-hour expiry, releases rate capacity.
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(10);
	assert_eq!(
		anonymous(&second, "/api/antiforgery", "198.51.100.9")
			.await
			.status(),
		200
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn replacing_a_browser_challenge_is_rate_bounded_and_rejection_preserves_the_last_state() {
	let app = support::App::new().await;
	let first_router = replica(&app, 1);
	let second_router = replica(&app, 1);
	let first = anonymous(
		&first_router,
		"/api/auth/passkeys/login/options",
		"198.51.100.9",
	)
	.await;
	let browser = cookie(&first);
	for _ in 1..30 {
		assert_eq!(
			with_cookie(
				&second_router,
				"/api/auth/passkeys/login/options",
				"198.51.100.9",
				Some(&browser)
			)
			.await
			.status(),
			200
		);
	}
	let before = state(&app).await;
	assert_eq!(before["ceremonies"].as_array().unwrap().len(), 1);
	limited(
		with_cookie(
			&first_router,
			"/api/auth/passkeys/login/options",
			"198.51.100.9",
			Some(&browser),
		)
		.await,
	)
	.await;
	assert_eq!(state(&app).await, before);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn concurrent_new_addresses_cannot_exceed_the_combined_state_cap_and_expiry_releases_it() {
	let app = support::App::new().await;
	for cap in [0, 4097] {
		let mut config = archive_server::auth::AuthConfig::development();
		config.max_outstanding_auth_state = cap;
		assert!(
			archive_server::auth::Auth::new(
				app.pool.clone(),
				config,
				app.mail.clone(),
				app.time.clone()
			)
			.is_err()
		);
	}
	let first = replica(&app, 4);
	let second = replica(&app, 4);
	let csrf = anonymous(&first, "/api/antiforgery", "198.51.100.1").await;
	let csrf_cookie = cookie(&csrf);
	assert_eq!(
		anonymous(&second, "/api/auth/passkeys/login/options", "198.51.100.1")
			.await
			.status(),
		200
	);
	let mut requests = tokio::task::JoinSet::new();
	for i in 2..42 {
		let router = if i % 2 == 0 {
			first.clone()
		} else {
			second.clone()
		};
		requests.spawn(async move {
			anonymous(
				&router,
				if i % 2 == 0 {
					"/api/antiforgery"
				} else {
					"/api/auth/passkeys/login/options"
				},
				&format!("198.51.100.{i}"),
			)
			.await
			.status()
		});
	}
	let mut accepted = 0;
	while let Some(result) = requests.join_next().await {
		match result.unwrap() {
			StatusCode::OK => accepted += 1,
			StatusCode::TOO_MANY_REQUESTS => {}
			other => panic!("unexpected status {other}"),
		}
	}
	assert_eq!(accepted, 2);
	let before = state(&app).await;
	assert_eq!(
		before["csrf"].as_array().unwrap().len() + before["ceremonies"].as_array().unwrap().len(),
		4
	);
	for route in ["/api/antiforgery", "/api/auth/passkeys/login/options"] {
		limited(anonymous(&second, route, "198.51.100.200").await).await;
	}
	assert_eq!(state(&app).await, before);
	assert_eq!(
		with_cookie(
			&second,
			"/api/antiforgery",
			"198.51.100.1",
			Some(&csrf_cookie)
		)
		.await
		.status(),
		200,
		"valid reuse survives a full global cap"
	);
	assert_eq!(state(&app).await, before);
	// At least the explicitly allocated ceremony expires after five minutes;
	// the address rate window has NOT expired and no reset/restart is needed.
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(6);
	assert_eq!(
		anonymous(&first, "/api/auth/passkeys/login/options", "198.51.100.200")
			.await
			.status(),
		200
	);
	let after = state(&app).await;
	assert!(
		after["csrf"].as_array().unwrap().len() + after["ceremonies"].as_array().unwrap().len()
			<= 4
	);
	*app.time.0.lock().unwrap() += chrono::Duration::hours(2);
	assert_eq!(
		anonymous(&second, "/api/antiforgery", "198.51.100.201")
			.await
			.status(),
		200
	);
	let after = state(&app).await;
	assert_eq!(after["csrf"].as_array().unwrap().len(), 1);
	assert_eq!(after["ceremonies"].as_array().unwrap().len(), 0);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn address_ledger_itself_is_fixed_size_and_expired_slots_are_reused() {
	let app = support::App::new().await;
	for i in 0..1024 {
		assert_eq!(
			anonymous(
				&app.router,
				"/api/auth/passkeys/login/options",
				&format!("198.18.{}.{}", i / 256, i % 256)
			)
			.await
			.status(),
			200
		);
	}
	let before = state(&app).await;
	assert_eq!(before["budgets"].as_array().unwrap().len(), 1024);
	limited(anonymous(&app.router, "/api/antiforgery", "198.19.0.1").await).await;
	assert_eq!(state(&app).await, before);
	let invalid_slot =
		sqlx::query("INSERT INTO archive_auth_allocation_budgets(slot) VALUES(1024)")
			.execute(&app.pool)
			.await;
	assert!(
		matches!(invalid_slot, Err(sqlx::Error::Database(ref error)) if error.code().as_deref()==Some("23514")),
		"schema must forbid ledger growth beyond the fixed slot range"
	);
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	assert_eq!(
		anonymous(&app.router, "/api/antiforgery", "198.19.0.1")
			.await
			.status(),
		200
	);
	let after = state(&app).await;
	assert_eq!(after["budgets"].as_array().unwrap().len(), 1024);
	app.close().await;
}
