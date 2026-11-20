//! MemberEmailChangeTests HTTP translations. Operator cases use the native CLI suite.
mod support;
use serde_json::json;
use sqlx::Row;

// Missing capture is a fixture assertion failure, not a production panic path.
#[allow(clippy::unwrap_used)]
fn change_code(app: &support::App) -> String {
	app.mail
		.0
		.lock()
		.unwrap()
		.last()
		.unwrap()
		.text
		.split("Ihr Bestätigungscode lautet: ")
		.nth(1)
		.unwrap()[..6]
		.to_owned()
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn own_address_change_revokes_acting_admin_and_obsolete_signin_codes() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let id = admin.get("/api/auth/me").await.1["accountId"].clone();
	// A second outstanding sign-in code at the old address is explicitly invalidated.
	let mut old = app.client();
	old.csrf().await;
	assert_eq!(
		old.post(
			"/api/auth/code/request",
			json!({"email":"verwaltung@liedertafel.test"})
		)
		.await
		.0,
		202
	);
	let stale = app.mail.code();
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"new-admin@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let code = change_code(&app);
	let messages = app.mail.0.lock().unwrap().len();
	assert_eq!(
		old.post(
			"/api/auth/code/request",
			json!({"email":"new-admin@liedertafel.test"})
		)
		.await
		.0,
		202
	);
	assert_eq!(app.mail.0.lock().unwrap().len(), messages);
	assert_eq!(
		old.post(
			"/api/auth/code/verify",
			json!({"email":"new-admin@liedertafel.test","code":code})
		)
		.await
		.0,
		400
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"new-admin@liedertafel.test","code":code})
			)
			.await
			.0,
		200
	);
	assert_eq!(admin.get("/api/auth/me").await.1["authenticated"], false);
	assert_eq!(
		old.post(
			"/api/auth/code/verify",
			json!({"email":"verwaltung@liedertafel.test","code":stale})
		)
		.await
		.0,
		400
	);
	let mut new = app.sign_in("new-admin@liedertafel.test").await;
	let me = new.get("/api/auth/me").await.1;
	assert_eq!(me["accountId"], id);
	assert_eq!(me["roles"], json!(["Administrator"]));
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn email_change_validation_attempt_expiry_supersession_and_delivery_retry() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let mut member = app.sign_in("mitglied@liedertafel.test").await;
	let id = member.get("/api/auth/me").await.1["accountId"].clone();
	for (id, email, status) in [
		(json!("invalid"), "neu@liedertafel.test", 400),
		(json!(uuid::Uuid::new_v4()), "neu@liedertafel.test", 404),
		(id.clone(), "invalid", 400),
		(id.clone(), "mitglied@liedertafel.test", 400),
		(id.clone(), "redaktion@liedertafel.test", 409),
	] {
		assert_eq!(
			admin
				.post(
					"/api/admin/members/email/request",
					json!({"accountId":id,"newEmail":email})
				)
				.await
				.0,
			status
		);
	}
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM member_email_changes")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		0
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"neu@liedertafel.test","code":"123456"})
			)
			.await
			.0,
		400
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"neu@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let code = change_code(&app);
	let wrong = if code == "000000" { "111111" } else { "000000" };
	for _ in 0..5 {
		assert_eq!(
			admin
				.post(
					"/api/admin/members/email/confirm",
					json!({"accountId":id,"newEmail":"neu@liedertafel.test","code":wrong})
				)
				.await
				.0,
			400
		);
	}
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"neu@liedertafel.test","code":code})
			)
			.await
			.0,
		400
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"expired@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let expired = change_code(&app);
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	// Reauthentication is needed to reach the expired-code decision.
	let mut admin = app.sign_in("verwaltung@liedertafel.test").await;
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"expired@liedertafel.test","code":expired})
			)
			.await
			.0,
		400
	);
	app.mail.1.store(true, std::sync::atomic::Ordering::Relaxed);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"retry@liedertafel.test"})
			)
			.await
			.0,
		502
	);
	assert_eq!(
		sqlx::query_scalar::<_, i64>(
			"SELECT count(*) FROM member_email_changes WHERE \"ConsumedAt\" IS NULL"
		)
		.fetch_one(&app.pool)
		.await
		.unwrap(),
		1
	);
	app.mail
		.1
		.store(false, std::sync::atomic::Ordering::Relaxed);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"retry@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let superseded = change_code(&app);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"fresh@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let fresh = change_code(&app);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"retry@liedertafel.test","code":superseded})
			)
			.await
			.0,
		400
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"fresh@liedertafel.test","code":fresh})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"fresh@liedertafel.test","code":fresh})
			)
			.await
			.0,
		400
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn change_codes_bind_account_and_address_recheck_collision_and_are_consumed_once() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let mut member = app.sign_in("mitglied@liedertafel.test").await;
	let id = member.get("/api/auth/me").await.1["accountId"].clone();
	let other = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"other@liedertafel.test","role":"Member"}),
		)
		.await
		.1["accountId"]
		.clone();
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"free@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let code = change_code(&app);
	for (target, email) in [
		(other.clone(), "free@liedertafel.test"),
		(id.clone(), "wrong@liedertafel.test"),
	] {
		assert_eq!(
			admin
				.post(
					"/api/admin/members/email/confirm",
					json!({"accountId":target,"newEmail":email,"code":code})
				)
				.await
				.0,
			400
		);
	}
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations",
				json!({"email":"free@liedertafel.test","role":"Member"})
			)
			.await
			.0,
		201
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"free@liedertafel.test","code":code})
			)
			.await
			.0,
		409
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/confirm",
				json!({"accountId":id,"newEmail":"free@liedertafel.test","code":code})
			)
			.await
			.0,
		400,
		"collision consumes challenge"
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":id,"newEmail":"race@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let code = change_code(&app);
	let mut replica = app.sign_in("verwaltung@liedertafel.test").await;
	let (one, two) = tokio::join!(
		admin.post(
			"/api/admin/members/email/confirm",
			json!({"accountId":id,"newEmail":"race@liedertafel.test","code":code})
		),
		replica.post(
			"/api/admin/members/email/confirm",
			json!({"accountId":id,"newEmail":"race@liedertafel.test","code":code})
		)
	);
	assert!(matches!(
		(one.0.as_u16(), two.0.as_u16()),
		(200, 400) | (400, 200)
	));
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn email_change_retains_identity_history_passkeys_and_rotates_sessions() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let invite = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"neu@liedertafel.test","role":"Member"}),
		)
		.await;
	assert_eq!(invite.0, 201);
	let id = invite.1["accountId"].clone();
	let uuid: uuid::Uuid = id.as_str().unwrap().parse().unwrap();
	let mut member = app.sign_in("neu@liedertafel.test").await;
	sqlx::query(
		"INSERT INTO \"AspNetUserPasskeys\"(\"UserId\",\"CredentialId\",\"Data\") VALUES($1,$2,$3)",
	)
	.bind(uuid)
	.bind(b"email-change-test-key".as_slice())
	.bind(json!({"Name":"kept","SignCount":5}))
	.execute(&app.pool)
	.await
	.unwrap();
	let before = admin.get("/api/admin/members").await.1;
	let change = admin
		.post(
			"/api/admin/members/email/request",
			json!({"accountId":id,"newEmail":"neu2@liedertafel.test"}),
		)
		.await;
	assert_eq!(change.0, 200, "{change:?}");
	assert_eq!(
		change.1["message"],
		"Bestätigungscode an die neue Adresse gesendet. Die Änderung wird erst mit dem Code wirksam."
	);
	let code = app
		.mail
		.0
		.lock()
		.unwrap()
		.last()
		.unwrap()
		.text
		.split("Ihr Bestätigungscode lautet: ")
		.nth(1)
		.unwrap()[..6]
		.to_owned();
	let row = sqlx::query("SELECT * FROM member_email_changes WHERE \"UserId\"=$1")
		.bind(uuid)
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(row.get::<Vec<u8>, _>("CodeHash").len(), 32);
	assert_eq!(row.get::<Vec<u8>, _>("Salt").len(), 16);
	assert_ne!(row.get::<Vec<u8>, _>("CodeHash"), code.as_bytes());
	let confirm = admin
		.post(
			"/api/admin/members/email/confirm",
			json!({"accountId":id,"newEmail":"neu2@liedertafel.test","code":code}),
		)
		.await;
	assert_eq!(confirm.0, 200, "{confirm:?}");
	assert_eq!(confirm.1["email"], "neu2@liedertafel.test");
	assert_eq!(member.get("/api/auth/me").await.1["authenticated"], false);
	assert_eq!(admin.get("/api/auth/me").await.1["authenticated"], true);
	let after = admin.get("/api/admin/members").await.1;
	let old = before["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["accountId"] == id)
		.unwrap();
	let new = after["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["accountId"] == id)
		.unwrap();
	for key in [
		"accountId",
		"roles",
		"invitationId",
		"acceptedAt",
		"invitedByAccountId",
	] {
		assert_eq!(old[key], new[key]);
	}
	let mut fresh = app.sign_in("neu2@liedertafel.test").await;
	assert_eq!(fresh.get("/api/auth/me").await.1["accountId"], id);
	let count: i64 =
		sqlx::query_scalar("SELECT count(*) FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1")
			.bind(uuid)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(count, 1);
	app.close().await;
}
