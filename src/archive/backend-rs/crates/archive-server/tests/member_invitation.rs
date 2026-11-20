//! MemberInvitationTests translations at the real HTTP/PG and delivery-only mail seams.
mod support;
use serde_json::json;

// Only the externally visible delivery boundary is replaced; independent PG
// reads here prove a recipient cannot receive mail before the state commits.
struct ObserveCommitted {
	pool: sqlx::PgPool,
	capture: std::sync::Arc<support::CaptureMail>,
}
impl archive_server::auth::Mail for ObserveCommitted {
	fn send<'a>(
		&'a self,
		message: archive_server::auth::MailMessage,
	) -> std::pin::Pin<
		Box<
			dyn std::future::Future<Output = Result<(), archive_server::auth::MailError>>
				+ Send
				+ 'a,
		>,
	> {
		Box::pin(async move {
			let count: Result<i64, sqlx::Error> = if message.subject
				== "Einladung zum Liedertafel-Archiv"
			{
				sqlx::query_scalar("SELECT count(*) FROM member_invitations i JOIN \"AspNetUsers\" u ON u.\"Id\"=i.\"UserId\" JOIN \"AspNetUserRoles\" r ON r.\"UserId\"=u.\"Id\" WHERE u.\"Email\"=$1 AND NOT u.\"EmailConfirmed\" AND i.\"MailStatus\"=0").bind(&message.to).fetch_one(&self.pool).await
			} else {
				sqlx::query_scalar("SELECT count(*) FROM member_email_changes WHERE \"NormalizedNewEmail\"=$1 AND \"ConsumedAt\" IS NULL").bind(message.to.to_uppercase()).fetch_one(&self.pool).await
			};
			if !matches!(count, Ok(1)) {
				return Err(archive_server::auth::MailError::Delivery);
			}
			archive_server::auth::Mail::send(self.capture.as_ref(), message).await
		})
	}
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn invitation_and_email_change_state_commit_before_external_delivery() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let mail = std::sync::Arc::new(ObserveCommitted {
		pool: app.pool.clone(),
		capture: app.mail.clone(),
	});
	let auth = archive_server::auth::Auth::new(
		app.pool.clone(),
		archive_server::auth::AuthConfig::development(),
		mail,
		app.time.clone(),
	)
	.unwrap();
	admin.router = archive_server::build_router(std::sync::Arc::new(
		archive_server::Origin::new(archive_server::Settings::new(
			true,
			std::path::PathBuf::new(),
			0,
		))
		.with_auth(auth),
	));
	let invited = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"committed@liedertafel.test","role":"Member"}),
		)
		.await;
	assert_eq!(invited.0, 201);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/email/request",
				json!({"accountId":invited.1["accountId"],"newEmail":"committed-new@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn duplicates_resend_conflicts_validation_and_retryable_delivery() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	for (email, role) in [
		("keine-mail", "Member"),
		("ok@liedertafel.test", "Superadmin"),
		("", "Member"),
	] {
		assert_eq!(
			admin
				.post("/api/admin/invitations", json!({"email":email,"role":role}))
				.await
				.0,
			400
		);
	}
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations",
				json!({"email":"ok@liedertafel.test","role":"Member","displayName":"a".repeat(201)})
			)
			.await
			.0,
		400
	);
	let active = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"mitglied@liedertafel.test","role":"Editor"}),
		)
		.await;
	assert_eq!(active.0, 409);
	assert_eq!(
		active.1["title"],
		"Diese Adresse ist bereits registriert. Es wurde keine neue Einladung erstellt und die Rolle nicht geändert."
	);
	let first = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"doppel@liedertafel.test","role":"Member","displayName":"Doppel"}),
		)
		.await;
	assert_eq!(first.0, 201);
	let again = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"DOPPEL@liedertafel.test","role":"Member","displayName":"Nicht überschreiben"}),
		)
		.await;
	assert_eq!(again.0, 200);
	assert_eq!(again.1["accountId"], first.1["accountId"]);
	assert_eq!(again.1["invitationId"], first.1["invitationId"]);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations",
				json!({"email":"doppel@liedertafel.test","role":"Editor"})
			)
			.await
			.0,
		409
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations/resend",
				json!({"email":"doppel@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations/resend",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await
			.0,
		409
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations/resend",
				json!({"email":"unknown@liedertafel.test"})
			)
			.await
			.0,
		404
	);
	app.mail.1.store(true, std::sync::atomic::Ordering::Relaxed);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations",
				json!({"email":"fehler@liedertafel.test","role":"Member"})
			)
			.await
			.0,
		502
	);
	let rows = admin.get("/api/admin/members").await.1;
	let failed = rows["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["email"] == "fehler@liedertafel.test")
		.unwrap()
		.clone();
	assert_eq!(failed["invitationMailStatus"], "failed");
	assert!(failed["lastInvitationSentAt"].is_null());
	app.mail
		.1
		.store(false, std::sync::atomic::Ordering::Relaxed);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations/resend",
				json!({"email":"fehler@liedertafel.test"})
			)
			.await
			.0,
		200
	);
	let rows = admin.get("/api/admin/members").await.1;
	let retried = rows["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["email"] == "fehler@liedertafel.test")
		.unwrap();
	assert_eq!(retried["invitationId"], failed["invitationId"]);
	assert_eq!(retried["invitationMailStatus"], "sent");
	assert!(!retried["lastInvitationSentAt"].is_null());
	assert_eq!(
		admin
			.post(
				"/api/admin/members/deactivate",
				json!({"accountId":first.1["accountId"]})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations",
				json!({"email":"doppel@liedertafel.test","role":"Member"})
			)
			.await
			.0,
		409
	);
	assert_eq!(
		admin
			.post(
				"/api/admin/invitations/resend",
				json!({"email":"doppel@liedertafel.test"})
			)
			.await
			.0,
		409
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn admin_policy_csrf_freshness_and_current_membership_on_all_mutations() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let routes = [
		"/api/admin/invitations",
		"/api/admin/invitations/resend",
		"/api/admin/members/deactivate",
		"/api/admin/members/reactivate",
		"/api/admin/members/role",
		"/api/admin/members/email/request",
		"/api/admin/members/email/confirm",
	];
	let mut anonymous = app.client();
	assert_eq!(anonymous.get("/api/admin/members").await.0, 401);
	for route in routes {
		assert_eq!(
			anonymous.post(route, json!({})).await.1["title"],
			"Ungültiger Sicherheitstoken."
		);
	}
	anonymous.csrf().await;
	for route in routes {
		assert_eq!(anonymous.post(route, json!({})).await.0, 401);
	}
	for email in ["mitglied@liedertafel.test", "redaktion@liedertafel.test"] {
		let mut caller = app.sign_in(email).await;
		assert_eq!(caller.get("/api/admin/members").await.0, 403);
		for route in routes {
			let result = caller.post(route, json!({})).await;
			assert_eq!(result.0, 403);
			assert_eq!(
				result.1["title"],
				"Keine Berechtigung für die Mitgliederverwaltung."
			);
		}
	}
	let token = admin.token.clone();
	admin.token = "forged".into();
	for route in routes {
		assert_eq!(
			admin.post(route, json!({})).await.1["title"],
			"Ungültiger Sicherheitstoken."
		);
	}
	admin.token = token;
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	assert_eq!(
		admin.get("/api/admin/members").await.0,
		200,
		"list does not require freshness"
	);
	for route in routes {
		let result = admin.post(route, json!({})).await;
		assert_eq!(result.0, 403);
		assert_eq!(
			result.1["title"],
			"Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich."
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn concurrent_duplicate_invitations_share_stable_identity_and_first_role() {
	let app = support::App::new().await;
	let mut a = app.admin().await;
	let mut b = app.sign_in("verwaltung@liedertafel.test").await;
	let (a, b) = tokio::join!(
		a.post(
			"/api/admin/invitations",
			json!({"email":"race@liedertafel.test","role":"Member"})
		),
		b.post(
			"/api/admin/invitations",
			json!({"email":"RACE@liedertafel.test","role":"Member"})
		)
	);
	assert!(matches!(
		(a.0.as_u16(), b.0.as_u16()),
		(201, 200) | (200, 201)
	));
	assert_eq!(a.1["accountId"], b.1["accountId"]);
	assert_eq!(a.1["invitationId"], b.1["invitationId"]);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn admin_invites_new_member_with_role_mail_and_acceptance_attribution() {
	let app = support::App::new().await;
	let mut admin = app.member().await;
	assert_eq!(admin.post("/api/auth/logout", json!({})).await.0, 200);
	admin.csrf().await;
	assert_eq!(
		admin
			.post(
				"/api/auth/code/request",
				json!({"email":"verwaltung@liedertafel.test"})
			)
			.await
			.0,
		202
	);
	assert_eq!(
		admin
			.post(
				"/api/auth/code/verify",
				json!({"email":"verwaltung@liedertafel.test","code":app.mail.code()})
			)
			.await
			.0,
		200
	);
	admin.csrf().await;
	let admin_id = admin.get("/api/auth/me").await.1["accountId"].clone();
	let (status, invite) = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"neu@liedertafel.test","displayName":"Neue Stimme","role":"Editor"}),
		)
		.await;
	assert_eq!(status, 201, "{invite}");
	assert_eq!(
		invite["message"],
		"Einladung erstellt. Die E-Mail wurde zum Versand angenommen; die Zustellung wird nicht bestätigt."
	);
	assert_eq!(invite["status"], "invited");
	assert_eq!(invite["mailStatus"], "sent");
	let list = admin.get("/api/admin/members").await;
	assert_eq!(list.0, 200);
	let row = list.1["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["accountId"] == invite["accountId"])
		.unwrap();
	assert_eq!(row["roles"], json!(["Editor"]));
	assert_eq!(row["invitedByAccountId"], admin_id);
	assert_eq!(row["acceptedAt"], json!(null));
	assert_eq!(row["invitationId"], invite["invitationId"]);
	let mut member = app.client();
	member.csrf().await;
	assert_eq!(
		member
			.post(
				"/api/auth/code/request",
				json!({"email":"neu@liedertafel.test"})
			)
			.await
			.0,
		202
	);
	assert_eq!(
		member
			.post(
				"/api/auth/code/verify",
				json!({"email":"neu@liedertafel.test","code":app.mail.code()})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		member.get("/api/auth/me").await.1["accountId"],
		invite["accountId"]
	);
	let rows = admin.get("/api/admin/members").await.1;
	let accepted = rows["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["accountId"] == invite["accountId"])
		.unwrap();
	assert_eq!(accepted["status"], "active");
	assert!(!accepted["acceptedAt"].is_null());
	app.close().await;
}
