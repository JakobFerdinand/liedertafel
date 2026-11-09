//! AuthApiTests bootstrap/seed and MemberEmailChangeTests repair at native CLI seam.
mod support;
use serde_json::json;
use sqlx::{ConnectOptions, Row};
use std::process::Output;

// Failure to start/finish a bounded fixture process is an assertion failure.
#[allow(clippy::unwrap_used)]
async fn command(app: &support::App, development: bool, args: &[&str]) -> Output {
	let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
	let endpoint = format!("http://{}", listener.local_addr().unwrap());
	let receiver = axum::Router::new().fallback(|_body: axum::body::Bytes| async {
		axum::http::Response::builder()
			.header("content-type", "application/grpc")
			.header("grpc-status", "0")
			.body(axum::body::Body::from(vec![0, 0, 0, 0, 0]))
			.unwrap()
	});
	let collector = tokio::spawn(async move {
		axum::serve(listener, receiver).await.unwrap();
	});
	let output = tokio::time::timeout(
		std::time::Duration::from_secs(20),
		tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
			.args(args)
			.env(
				"ARCHIVE_ENVIRONMENT",
				if development {
					"Development"
				} else {
					"Production"
				},
			)
			.env("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint)
			.env("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")
			.env("Archive__OperatorToken", "synthetic-operator-token")
			.env(
				"ConnectionStrings__archive-migrations",
				app.pool.connect_options().to_url_lossy().as_str(),
			)
			.env(
				"ConnectionStrings__archive-db",
				"invalid-fallback-must-not-be-used",
			)
			.kill_on_drop(true)
			.output(),
	)
	.await
	.unwrap()
	.unwrap();
	collector.abort();
	let _ = collector.await;
	output
}
#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn bootstrap_creates_first_confirmed_passwordless_admin_and_refuses_any_existing_account() {
	let app = support::App::new().await;
	let args = [
		"--bootstrap-admin",
		"--email",
		"first@liedertafel.test",
		"--name",
		"Erste Verwaltung",
	];
	let missing = command(&app, false, &args).await;
	assert!(!missing.status.success());
	let mut authorized = args.to_vec();
	authorized.extend(["--operator-token", "synthetic-operator-token"]);
	let first = command(&app, false, &authorized).await;
	assert!(
		first.status.success(),
		"{}",
		String::from_utf8_lossy(&first.stderr)
	);
	let user = sqlx::query("SELECT * FROM \"AspNetUsers\"")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert!(user.get::<bool, _>("EmailConfirmed"));
	assert!(user.get::<Option<String>, _>("PasswordHash").is_none());
	assert_eq!(
		user.get::<Option<String>, _>("DisplayName").as_deref(),
		Some("Erste Verwaltung")
	);
	assert!(!command(&app, true, &args).await.status.success());
	for output in [&first, &missing] {
		let log = format!(
			"{}{}",
			String::from_utf8_lossy(&output.stdout),
			String::from_utf8_lossy(&output.stderr)
		);
		assert!(!log.contains("first@liedertafel.test"));
		assert!(!log.contains("synthetic-operator-token"));
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn repair_creates_promotes_moves_revokes_compromised_keys_but_noop_keeps_them() {
	let app = support::App::new().await;
	let create = command(
		&app,
		true,
		&[
			"--repair-admin",
			"--email",
			"repair@liedertafel.test",
			"--name",
			"Reparatur",
			"--operator",
			"Wartung",
		],
	)
	.await;
	assert!(
		create.status.success(),
		"{}",
		String::from_utf8_lossy(&create.stderr)
	);
	*app.time.0.lock().unwrap() = chrono::Utc::now();
	let id: uuid::Uuid = sqlx::query_scalar("SELECT \"Id\" FROM \"AspNetUsers\"")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	let invitation = sqlx::query("SELECT * FROM member_invitations")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert!(
		invitation
			.get::<Option<chrono::DateTime<chrono::Utc>>, _>("AcceptedAt")
			.is_some()
	);
	assert!(
		invitation
			.get::<Option<uuid::Uuid>, _>("InvitedByAccountId")
			.is_none()
	);
	sqlx::query(
		"INSERT INTO \"AspNetUserPasskeys\"(\"UserId\",\"CredentialId\",\"Data\") VALUES($1,$2,$3)",
	)
	.bind(id)
	.bind(b"repair-test-key".as_slice())
	.bind(json!({"Name":"kept-until-changed"}))
	.execute(&app.pool)
	.await
	.unwrap();
	let mut signed = app.sign_in("repair@liedertafel.test").await;
	let noop = command(
		&app,
		true,
		&["--repair-admin", "--email", "repair@liedertafel.test"],
	)
	.await;
	assert!(noop.status.success());
	assert_eq!(signed.get("/api/auth/me").await.1["authenticated"], true);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM member_admin_actions")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		1
	);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM \"AspNetUserPasskeys\"")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		1
	);
	let moved = command(
		&app,
		true,
		&[
			"--repair-admin",
			"--account-id",
			&id.to_string(),
			"--email",
			"moved@liedertafel.test",
			"--operator",
			"Wartung",
		],
	)
	.await;
	assert!(
		moved.status.success(),
		"{}",
		String::from_utf8_lossy(&moved.stderr)
	);
	assert_eq!(signed.get("/api/auth/me").await.1["authenticated"], false);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM \"AspNetUserPasskeys\"")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		0
	);
	let audits = sqlx::query("SELECT * FROM member_admin_actions")
		.fetch_all(&app.pool)
		.await
		.unwrap();
	assert_eq!(audits.len(), 2);
	for row in audits {
		assert_eq!(
			row.get::<uuid::Uuid, _>("ActorAccountId"),
			uuid::Uuid::nil()
		);
		assert_eq!(row.get::<i32, _>("Action"), 4);
		assert_eq!(
			row.get::<String, _>("Note"),
			"Betreiber-Reparatur durch Wartung"
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn repair_existing_member_confirms_promotes_retains_roles_and_rejects_collisions() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let mut member = app.sign_in("mitglied@liedertafel.test").await;
	let id = member.get("/api/auth/me").await.1["accountId"].clone();
	let id: uuid::Uuid = id.as_str().unwrap().parse().unwrap();
	assert_eq!(
		admin
			.post("/api/admin/members/deactivate", json!({"accountId":id}))
			.await
			.0,
		200
	);
	sqlx::query("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=false WHERE \"Id\"=$1")
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(
		"INSERT INTO \"AspNetUserPasskeys\"(\"UserId\",\"CredentialId\",\"Data\") VALUES($1,$2,$3)",
	)
	.bind(id)
	.bind(b"compromised".as_slice())
	.bind(json!({"Name":"revoked"}))
	.execute(&app.pool)
	.await
	.unwrap();
	let repair = command(
		&app,
		true,
		&[
			"--repair-admin",
			"--email",
			"mitglied@liedertafel.test",
			"--operator",
			"Wartung",
		],
	)
	.await;
	assert!(
		repair.status.success(),
		"{}",
		String::from_utf8_lossy(&repair.stderr)
	);
	*app.time.0.lock().unwrap() = chrono::Utc::now();
	let mut fresh = app.sign_in("mitglied@liedertafel.test").await;
	let me = fresh.get("/api/auth/me").await.1;
	assert_eq!(me["accountId"], id.to_string());
	assert_eq!(me["roles"], json!(["Administrator", "Member"]));
	assert_eq!(
		sqlx::query_scalar::<_, i64>(
			"SELECT count(*) FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1"
		)
		.bind(id)
		.fetch_one(&app.pool)
		.await
		.unwrap(),
		0
	);
	for args in [
		vec![
			"--repair-admin",
			"--accountId",
			"invalid",
			"--email",
			"valid@liedertafel.test",
		],
		vec![
			"--repair-admin",
			"--accountId",
			"00000000-0000-0000-0000-000000000001",
			"--email",
			"valid@liedertafel.test",
		],
		vec![
			"--repair-admin",
			"--accountId",
			&id.to_string(),
			"--email",
			"verwaltung@liedertafel.test",
		],
	] {
		assert!(!command(&app, true, &args).await.status.success());
	}
	assert_eq!(
		fresh.get("/api/auth/me").await.1["email"],
		"mitglied@liedertafel.test"
	);
	let missing = command(
		&app,
		false,
		&["--repair-admin", "--email", "production@liedertafel.test"],
	)
	.await;
	assert!(!missing.status.success());
	let allowed = command(
		&app,
		false,
		&[
			"--repair-admin",
			"--email",
			"production@liedertafel.test",
			"--operator-token",
			"synthetic-operator-token",
		],
	)
	.await;
	assert!(allowed.status.success());
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn development_seed_is_idempotent_and_race_safe_with_cli_and_http() {
	let app = support::App::new().await;
	let mut client = app.client();
	client.csrf().await;
	let (cli, http) = tokio::join!(
		command(&app, true, &["--seed-dev-auth"]),
		client.post("/api/dev/auth/seed", json!({}))
	);
	assert!(cli.status.success());
	assert_eq!(http.0, 200);
	assert_eq!(client.post("/api/dev/auth/seed", json!({})).await, http);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM \"AspNetUsers\"")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		3
	);
	assert_eq!(sqlx::query_scalar::<_,i64>("SELECT count(*) FROM \"AspNetUsers\" WHERE \"PasswordHash\" IS NOT NULL OR NOT \"EmailConfirmed\"").fetch_one(&app.pool).await.unwrap(),0);
	assert!(
		!command(&app, false, &["--seed-dev-auth"])
			.await
			.status
			.success()
	);
	// Bootstrap refuses even when accounts exist but no Administrator does.
	sqlx::query("DELETE FROM \"AspNetUserRoles\" WHERE \"RoleId\" IN(SELECT \"Id\" FROM \"AspNetRoles\" WHERE \"Name\"='Administrator')").execute(&app.pool).await.unwrap();
	assert!(
		!command(
			&app,
			true,
			&["--bootstrap-admin", "--email", "new@liedertafel.test"]
		)
		.await
		.status
		.success()
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn concurrent_bootstraps_allow_only_one_first_account() {
	let app = support::App::new().await;
	let (one, two) = tokio::join!(
		command(
			&app,
			true,
			&["--bootstrap-admin", "--email", "first@liedertafel.test"]
		),
		command(
			&app,
			true,
			&["--bootstrap-admin", "--email", "second@liedertafel.test"]
		)
	);
	assert_ne!(one.status.success(), two.status.success());
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM \"AspNetUsers\"")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		1
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn promotion_only_repair_preserves_address_spelling_and_unrelated_identity_state() {
	let app = support::App::new().await;
	let _admin = app.admin().await;
	let expired = chrono::Utc::now() - chrono::Duration::days(1);
	sqlx::query("UPDATE \"AspNetUsers\" SET \"Email\"='Mitglied@liedertafel.test',\"UserName\"='Mitglied@liedertafel.test',\"AccessFailedCount\"=3,\"LockoutEnd\"=$1 WHERE \"NormalizedEmail\"='MITGLIED@LIEDERTAFEL.TEST'").bind(expired).execute(&app.pool).await.unwrap();
	assert!(
		command(
			&app,
			true,
			&["--repair-admin", "--email", "mitglied@liedertafel.test"]
		)
		.await
		.status
		.success()
	);
	let row = sqlx::query(
		"SELECT * FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"='MITGLIED@LIEDERTAFEL.TEST'",
	)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_eq!(row.get::<String, _>("Email"), "Mitglied@liedertafel.test");
	assert_eq!(
		row.get::<String, _>("UserName"),
		"Mitglied@liedertafel.test"
	);
	assert_eq!(row.get::<i32, _>("AccessFailedCount"), 3);
	assert!(
		row.get::<Option<chrono::DateTime<chrono::Utc>>, _>("LockoutEnd")
			.is_some()
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn finite_operator_reports_failed_otlp_flush_without_rolling_back_committed_work() {
	let app = support::App::new().await;
	let output = tokio::time::timeout(
		std::time::Duration::from_secs(35),
		tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
			.args(["--bootstrap-admin", "--email", "flush@liedertafel.test"])
			.env("ARCHIVE_ENVIRONMENT", "Development")
			.env("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
			.env("OTEL_EXPORTER_OTLP_TIMEOUT", "1000")
			.env(
				"ConnectionStrings__archive-migrations",
				app.pool.connect_options().to_url_lossy().as_str(),
			)
			.kill_on_drop(true)
			.output(),
	)
	.await
	.unwrap()
	.unwrap();
	assert!(
		!output.status.success(),
		"finite jobs fail visibly when configured OTLP cannot flush"
	);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM \"AspNetUsers\"")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		1
	);
	app.close().await;
}
