//! HTTP translations of AuthApiTests/PasskeyApiTests; real PG cases are explicit gates.
use archive_server::{Origin, Settings, build_router};
use axum::{body::Body, http::Request};
use http_body_util::BodyExt;
use serde_json::{Value, json};
use std::{path::PathBuf, sync::Arc};
use tower::ServiceExt;

mod support;

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn request_verify_me_logout_round_trip() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	let seed = client.post("/api/dev/auth/seed", json!({})).await;
	assert_eq!(seed.0, 200);
	let mut roles = seed.1["accounts"]
		.as_array()
		.unwrap()
		.iter()
		.map(|account| account["role"].as_str().unwrap())
		.collect::<Vec<_>>();
	roles.sort();
	assert_eq!(roles, vec!["Administrator", "Editor", "Member"]);
	assert_eq!(
		client.post("/api/dev/auth/seed", json!({})).await,
		seed,
		"seed retains stable account IDs on rerun"
	);
	let passwords: i64 = sqlx::query_scalar(
		"SELECT count(*) FROM \"AspNetUsers\" WHERE \"PasswordHash\" IS NOT NULL",
	)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_eq!(passwords, 0);
	let requested = client
		.post(
			"/api/auth/code/request",
			json!({"email":"mitglied@liedertafel.test"}),
		)
		.await;
	assert_eq!(requested.0, 202);
	assert_eq!(
		requested.1["message"],
		"Falls die Adresse eingeladen ist, wurde ein Code gesendet. Bitte das Postfach prüfen."
	);
	let code = app.mail.code();
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test", "code":code})
			)
			.await
			.0,
		200
	);
	let me = client.get("/api/auth/me").await.1;
	assert_eq!(me["authenticated"], true);
	assert_eq!(me["roles"], json!(["Member"]));
	assert_eq!(me["authMethod"], "email_code");
	client.csrf().await;
	assert_eq!(client.post("/api/auth/logout", json!({})).await.0, 200);
	assert_eq!(
		client.get("/api/auth/me").await.1,
		json!({"authenticated":false})
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn passkey_options_exact_policy_and_freshness() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	let options = client
		.post("/api/auth/passkeys/login/options", json!({}))
		.await;
	assert_eq!(options.0, 200);
	let options: Value =
		serde_json::from_str(options.1["requestOptions"].as_str().unwrap()).unwrap();
	assert_eq!(options["rpId"], "localhost");
	assert_eq!(options["userVerification"], "required");
	assert_eq!(
		client
			.post("/api/auth/passkeys/register/options", json!({}))
			.await
			.0,
		401
	);
	client.post("/api/dev/auth/seed", json!({})).await;
	client
		.post(
			"/api/auth/code/request",
			json!({"email":"mitglied@liedertafel.test"}),
		)
		.await;
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test","code":app.mail.code()})
			)
			.await
			.0,
		200
	);
	client.csrf().await;
	let options = client
		.post("/api/auth/passkeys/register/options", json!({}))
		.await;
	assert_eq!(options.0, 200);
	let options: Value =
		serde_json::from_str(options.1["creationOptions"].as_str().unwrap()).unwrap();
	assert_eq!(options["rp"]["id"], "localhost");
	assert_eq!(options["user"]["name"], "mitglied@liedertafel.test");
	assert_eq!(options["authenticatorSelection"]["residentKey"], "required");
	assert_eq!(
		options["authenticatorSelection"]["userVerification"],
		"required"
	);
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/register/verify",
				json!({"credential":"{}"})
			)
			.await
			.1["title"],
		"Die Passkey-Registrierung ist ungültig."
	);
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":"{not-json}"})
			)
			.await
			.1["title"],
		"Die Passkey-Anmeldung ist ungültig."
	);
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	let old = client
		.post("/api/auth/passkeys/register/options", json!({}))
		.await;
	assert_eq!(old.0, 403);
	assert_eq!(
		old.1["title"],
		"Die Anmeldung ist zu alt. Bitte erneut anmelden."
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn unknown_email_uniformity_expiry_reuse_attempts_and_resend_limits() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	client.post("/api/dev/auth/seed", json!({})).await;
	let known = client
		.post(
			"/api/auth/code/request",
			json!({"email":"mitglied@liedertafel.test"}),
		)
		.await;
	let unknown = client
		.post(
			"/api/auth/code/request",
			json!({"email":"fremd@liedertafel.test"}),
		)
		.await;
	assert_eq!(known, unknown);
	assert_eq!(app.mail.0.lock().unwrap().len(), 1);
	let code = app.mail.code();
	assert_eq!(
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await,
		known
	);
	assert_eq!(app.mail.0.lock().unwrap().len(), 1);
	let row: (Vec<u8>, Vec<u8>) =
		sqlx::query_as("SELECT \"Salt\",\"CodeHash\" FROM auth_sign_in_codes")
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(row.0.len(), 16);
	assert_eq!(row.1.len(), 32);
	assert_ne!(row.1, code.as_bytes());
	for email in ["fremd@liedertafel.test", "not-an-address"] {
		let response = client
			.post(
				"/api/auth/code/verify",
				json!({"email":email,"code":"123456"}),
			)
			.await;
		assert_eq!(response.0, 400);
		assert_eq!(
			response.1["title"],
			"Der Code ist ungültig oder abgelaufen."
		);
	}
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	client.csrf().await;
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test","code":code})
			)
			.await
			.0,
		400
	);
	client
		.post(
			"/api/auth/code/request",
			json!({"email":"mitglied@liedertafel.test"}),
		)
		.await;
	let code = app.mail.code();
	let wrong = if code == "000000" { "111111" } else { "000000" };
	for _ in 0..5 {
		assert_eq!(
			client
				.post(
					"/api/auth/code/verify",
					json!({"email":"mitglied@liedertafel.test","code":wrong})
				)
				.await
				.0,
			400
		);
	}
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test","code":code})
			)
			.await
			.0,
		400
	);
	for _ in 0..2 {
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"}),
			)
			.await;
	}
	assert_eq!(
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await
			.0,
		429
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn failed_mail_allows_immediate_retry_and_verification_failure_cap_is_shared() {
	use std::sync::atomic::Ordering;
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	client.post("/api/dev/auth/seed", json!({})).await;
	app.mail.1.store(true, Ordering::Relaxed);
	assert_eq!(
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await
			.0,
		500
	);
	app.mail.1.store(false, Ordering::Relaxed);
	assert_eq!(
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await
			.0,
		202
	);
	assert_eq!(app.mail.0.lock().unwrap().len(), 1);
	for _ in 0..10 {
		assert_eq!(
			client
				.post(
					"/api/auth/code/verify",
					json!({"email":"unknown@liedertafel.test","code":"123456"})
				)
				.await
				.0,
			400
		);
	}
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"unknown@liedertafel.test","code":"123456"})
			)
			.await
			.0,
		429
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn concurrent_code_verification_issues_one_session_and_replay_fails() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	client.post("/api/dev/auth/seed", json!({})).await;
	client
		.post(
			"/api/auth/code/request",
			json!({"email":"mitglied@liedertafel.test"}),
		)
		.await;
	let code = app.mail.code();
	let mut tasks = tokio::task::JoinSet::new();
	for _ in 0..5 {
		let mut other = app.client();
		other.cookies = client.cookies.clone();
		other.token = client.token.clone();
		let code = code.clone();
		tasks.spawn(async move {
			other
				.post(
					"/api/auth/code/verify",
					json!({"email":"mitglied@liedertafel.test","code":code}),
				)
				.await
				.0
		});
	}
	let mut successes = 0;
	while let Some(result) = tasks.join_next().await {
		let status = result.unwrap();
		assert!(status == 200 || status == 400);
		successes += usize::from(status == 200);
	}
	assert_eq!(successes, 1);
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test","code":code})
			)
			.await
			.0,
		400
	);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM archive_sessions")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn sessions_are_digest_only_shared_sliding_expiring_and_keep_original_freshness() {
	let app = support::App::new().await;
	let mut client = app.member().await;
	let initial = client.get("/api/auth/me").await.1;
	let token = client.cookies["archive.auth"].clone();
	let digest: Vec<u8> = sqlx::query_scalar("SELECT token_digest FROM archive_sessions")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(digest.len(), 32);
	assert_ne!(digest, token.as_bytes());
	// A separate pool/service knows nothing about the first process's memory.
	client.router = support::App::router(
		app.pool.clone(),
		app.mail.clone(),
		app.time.clone(),
		archive_server::auth::AuthConfig::development(),
	);
	assert_eq!(client.get("/api/auth/me").await.1, initial);
	*app.time.0.lock().unwrap() += chrono::Duration::days(16);
	let refreshed = client.get("/api/auth/me").await.1;
	assert_eq!(refreshed, initial);
	assert_eq!(client.cookies["archive.auth"], token);
	client.csrf().await;
	assert_eq!(
		client
			.post("/api/auth/passkeys/register/options", json!({}))
			.await
			.0,
		403,
		"sliding expiry is not fresh verification"
	);
	*app.time.0.lock().unwrap() += chrono::Duration::days(31);
	assert_eq!(client.get("/api/auth/me").await.1["authenticated"], false);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn membership_roles_and_reset_audits_apply_on_next_request_and_never_revive() {
	let app = support::App::new().await;
	let mut client = app.member().await;
	let initial = client.get("/api/auth/me").await.1;
	let id: uuid::Uuid = initial["accountId"].as_str().unwrap().parse().unwrap();
	sqlx::query("DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\"=$1")
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(
		"INSERT INTO \"AspNetUserRoles\" SELECT $1,\"Id\" FROM \"AspNetRoles\" WHERE \"Name\"='Editor'",
	)
	.bind(id)
	.execute(&app.pool)
	.await
	.unwrap();
	let changed = client.get("/api/auth/me").await.1;
	assert_eq!(changed["roles"], json!(["Editor"]));
	assert_eq!(changed["verifiedAt"], initial["verifiedAt"]);
	// Even a reset audit alone (without relying on stamp-validation timing) kills it.
	let reset_at = *app.time.0.lock().unwrap() + chrono::Duration::seconds(1);
	sqlx::query("INSERT INTO member_admin_actions(\"Id\",\"TargetUserId\",\"ActorAccountId\",\"Action\",\"OldRoles\",\"NewRoles\",\"OccurredAt\") VALUES($1,$2,$2,1,'','',$3)").bind(uuid::Uuid::new_v4()).bind(id).bind(reset_at).execute(&app.pool).await.unwrap();
	assert_eq!(client.get("/api/auth/me").await.1["authenticated"], false);
	sqlx::query("DELETE FROM member_admin_actions")
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		client.get("/api/auth/me").await.1["authenticated"],
		false,
		"reinstating account cannot revive a removed session"
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn csrf_pre_auth_transition_parallel_reuse_and_account_logout_revocation() {
	let app = support::App::new().await;
	let mut client = app.member().await;
	let token = client.token.clone();
	client.csrf().await;
	assert_eq!(
		client.token, token,
		"parallel page actions retain a reusable token"
	);
	let mut stolen = app.client();
	stolen.cookies = client.cookies.clone();
	stolen.token = client.token.clone();
	// Pre-auth token cannot cross into an authenticated identity.
	let mut anonymous = app.client();
	anonymous.csrf().await;
	let old_token = anonymous.token.clone();
	let old_cookie = anonymous.cookies["archive.csrf"].clone();
	anonymous.cookies.insert(
		"archive.auth".into(),
		client.cookies["archive.auth"].clone(),
	);
	assert_eq!(
		anonymous.post("/api/auth/logout", json!({})).await.1["title"],
		"Ungültiger Sicherheitstoken."
	);
	assert_eq!(anonymous.token, old_token);
	assert_eq!(anonymous.cookies["archive.csrf"], old_cookie);
	assert_eq!(client.post("/api/auth/logout", json!({})).await.0, 200);
	assert_eq!(
		stolen.get("/api/auth/me").await.1["authenticated"],
		false,
		"server-side logout, not browser-cookie deletion alone"
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn production_and_forwarded_https_keep_secure_strict_httponly_cookies_and_dev_seed_absent() {
	let app = support::App::new().await;
	let mut client = app.member().await;
	let mut config = archive_server::auth::AuthConfig::development();
	config.development = false;
	config.origins = vec!["https://localhost".parse().unwrap()];
	let auth = archive_server::auth::Auth::new(
		app.pool.clone(),
		config,
		app.mail.clone(),
		app.time.clone(),
	)
	.unwrap();
	let router = build_router(Arc::new(
		Origin::new(Settings::new(false, PathBuf::new(), 0)).with_auth(auth),
	));
	client.router = router.clone();
	let response = router
		.clone()
		.oneshot(
			Request::get("/api/antiforgery")
				.header("x-forwarded-proto", "https")
				.body(Body::empty())
				.unwrap(),
		)
		.await
		.unwrap();
	assert_eq!(response.status(), 200);
	let cookie = response.headers()["set-cookie"]
		.to_str()
		.unwrap()
		.to_lowercase();
	for flag in ["archive.csrf=", "httponly", "samesite=strict", "secure"] {
		assert!(cookie.contains(flag));
	}
	client.csrf().await;
	assert_eq!(
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await
			.0,
		202
	);
	let response = router
		.clone()
		.oneshot(
			Request::post("/api/auth/code/verify")
				.header("content-type", "application/json")
				.header(
					"cookie",
					client
						.cookies
						.iter()
						.map(|(k, v)| format!("{k}={v}"))
						.collect::<Vec<_>>()
						.join("; "),
				)
				.header("X-CSRF-TOKEN", &client.token)
				.body(Body::from(
					json!({"email":"mitglied@liedertafel.test","code":app.mail.code()}).to_string(),
				))
				.unwrap(),
		)
		.await
		.unwrap();
	assert_eq!(response.status(), 200);
	let cookie = response.headers()["set-cookie"]
		.to_str()
		.unwrap()
		.to_lowercase();
	for flag in [
		"archive.auth=",
		"httponly",
		"samesite=strict",
		"secure",
		"max-age=2592000",
	] {
		assert!(cookie.contains(flag));
	}
	assert_eq!(client.post("/api/dev/auth/seed", json!({})).await.0, 404);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn code_confirms_invitation_and_preserves_dotnet_german_email_normalization() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	client.post("/api/dev/auth/seed", json!({})).await;
	// Literal normalized value from the .NET fixture generator --normalization.
	let id:uuid::Uuid=sqlx::query_scalar("UPDATE \"AspNetUsers\" SET \"Email\"='straße@liedertafel.test', \"NormalizedEmail\"='STRAßE@LIEDERTAFEL.TEST', \"EmailConfirmed\"=false WHERE \"NormalizedEmail\"='MITGLIED@LIEDERTAFEL.TEST' RETURNING \"Id\"").fetch_one(&app.pool).await.unwrap();
	let now = *app.time.0.lock().unwrap();
	sqlx::query("INSERT INTO member_invitations(\"Id\",\"UserId\",\"NormalizedEmail\",\"Role\",\"InvitedAt\",\"MailStatus\") VALUES($1,$2,'STRAßE@LIEDERTAFEL.TEST','Member',$3,0)").bind(uuid::Uuid::new_v4()).bind(id).bind(now).execute(&app.pool).await.unwrap();
	assert_eq!(
		client
			.post(
				"/api/auth/code/request",
				json!({"email":"  straße@liedertafel.test  "})
			)
			.await
			.0,
		202
	);
	assert_eq!(app.mail.0.lock().unwrap().len(), 1);
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"straße@liedertafel.test","code":app.mail.code()})
			)
			.await
			.0,
		200
	);
	let confirmed: bool =
		sqlx::query_scalar("SELECT \"EmailConfirmed\" FROM \"AspNetUsers\" WHERE \"Id\"=$1")
			.bind(id)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert!(confirmed);
	let accepted: Option<chrono::DateTime<chrono::Utc>> =
		sqlx::query_scalar("SELECT \"AcceptedAt\" FROM member_invitations WHERE \"UserId\"=$1")
			.bind(id)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(accepted.unwrap().timestamp_micros(), now.timestamp_micros());
	app.close().await;
}

#[tokio::test]
async fn me_is_anonymous_envelope_without_cookie() {
	let router = build_router(Arc::new(Origin::new(Settings::new(
		true,
		PathBuf::new(),
		0,
	))));
	let response = router
		.oneshot(Request::get("/api/auth/me").body(Body::empty()).unwrap())
		.await
		.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["cache-control"], "no-store");
	let bytes = response.into_body().collect().await.unwrap().to_bytes();
	assert_eq!(
		serde_json::from_slice::<Value>(&bytes).unwrap(),
		json!({"authenticated": false})
	);
}

#[tokio::test]
async fn auth_mutations_reject_missing_or_forged_csrf_before_authentication() {
	let router = build_router(Arc::new(Origin::new(Settings::new(
		true,
		PathBuf::new(),
		0,
	))));
	for path in [
		"code/request",
		"code/verify",
		"logout",
		"passkeys/register/options",
		"passkeys/register/verify",
		"passkeys/login/verify",
		"passkeys/rename",
		"passkeys/remove",
	] {
		let response = router
			.clone()
			.oneshot(
				Request::post(format!("/api/auth/{path}"))
					.header("content-type", "application/json")
					.header("X-CSRF-TOKEN", "forged")
					.body(Body::from("{}"))
					.unwrap(),
			)
			.await
			.unwrap();
		assert_eq!(response.status(), 400, "{path}");
		let bytes = response.into_body().collect().await.unwrap().to_bytes();
		assert_eq!(
			serde_json::from_slice::<Value>(&bytes).unwrap()["title"],
			"Ungültiger Sicherheitstoken."
		);
	}
}

#[tokio::test]
async fn auth_routes_keep_json_api_miss_for_unregistered_methods() {
	let router = build_router(Arc::new(Origin::new(Settings::new(
		true,
		PathBuf::new(),
		0,
	))));
	for (method, path) in [
		("POST", "/api/auth/me"),
		("GET", "/api/auth/code/request"),
		("POST", "/api/antiforgery"),
		("PUT", "/api/auth/logout"),
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
		assert_eq!(response.status(), 404, "{method} {path}");
		assert_eq!(
			response.headers()["content-type"],
			"application/problem+json"
		);
	}
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn ip_request_cap_and_identity_lockout_are_enforced_without_disclosure() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	for index in 0..31 {
		let result = client
			.post(
				"/api/auth/code/request",
				json!({"email":format!("unknown-{index}@liedertafel.test")}),
			)
			.await;
		assert_eq!(result.0, if index < 30 { 202 } else { 429 });
	}
	assert!(app.mail.0.lock().unwrap().is_empty());
	client.post("/api/dev/auth/seed", json!({})).await;
	for round in 0..2 {
		if round > 0 {
			*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
		}
		for _ in 0..10 {
			assert_eq!(
				client
					.post(
						"/api/auth/code/verify",
						json!({"email":"mitglied@liedertafel.test","code":"123456"})
					)
					.await
					.0,
				400
			);
		}
	}
	let row:(i32,Option<chrono::DateTime<chrono::Utc>>)=sqlx::query_as("SELECT \"AccessFailedCount\",\"LockoutEnd\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"='MITGLIED@LIEDERTAFEL.TEST'").fetch_one(&app.pool).await.unwrap();
	assert_eq!(row.0, 0);
	assert!(row.1.unwrap() > *app.time.0.lock().unwrap());
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	// Still locked, but outside the verify-failure window; no mail and uniform 400.
	assert_eq!(
		client
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test","code":"123456"})
			)
			.await
			.0,
		400
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn session_validation_after_waiting_for_another_replica_reads_its_committed_refresh() {
	let app = support::App::new().await;
	let mut client = app.member().await;
	let before = client.get("/api/auth/me").await.1;
	let id: uuid::Uuid = before["accountId"].as_str().unwrap().parse().unwrap();
	let now = *app.time.0.lock().unwrap();
	sqlx::query("UPDATE archive_sessions SET expires_at=$1")
		.bind(now - chrono::Duration::seconds(1))
		.execute(&app.pool)
		.await
		.unwrap();
	// A real PG transaction models the earlier replica's in-flight refresh.
	// The HTTP request must wait on the account and then see committed session
	// state, rather than delete it using a pre-lock READ COMMITTED snapshot.
	let mut refresh = app.pool.begin().await.unwrap();
	sqlx::query("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE")
		.bind(id)
		.execute(&mut *refresh)
		.await
		.unwrap();
	sqlx::query("UPDATE archive_sessions SET expires_at=$1, renewed_at=$2")
		.bind(now + chrono::Duration::days(30))
		.bind(now)
		.execute(&mut *refresh)
		.await
		.unwrap();
	let request = tokio::spawn(async move { client.get("/api/auth/me").await });
	let mut waiting = false;
	for _ in 0..100 {
		waiting=sqlx::query_scalar("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND pid<>pg_backend_pid())").fetch_one(&app.pool).await.unwrap();
		if waiting {
			break;
		}
		tokio::time::sleep(std::time::Duration::from_millis(10)).await;
	}
	assert!(
		waiting,
		"HTTP request reached the real database lock boundary"
	);
	refresh.commit().await.unwrap();
	let response = request.await.unwrap();
	assert_eq!(response.0, 200);
	assert_eq!(response.1, before);
	app.close().await;
}
