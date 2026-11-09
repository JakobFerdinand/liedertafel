//! MemberRevocationTests: stable identity, attribution, next-request access.
mod support;
use serde_json::json;
use sqlx::Row;

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn role_changes_sync_without_relogin_and_same_role_has_one_audit() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let mut member = app.sign_in("mitglied@liedertafel.test").await;
	let before = member.get("/api/auth/me").await.1;
	let id = before["accountId"].clone();
	for role in ["Editor", "Editor", "Administrator"] {
		let result = admin
			.post(
				"/api/admin/members/role",
				json!({"accountId":id,"role":role}),
			)
			.await;
		assert_eq!(result.0, 200);
		let me = member.get("/api/auth/me").await.1;
		assert_eq!(me["roles"], json!([role]));
		assert_eq!(me["verifiedAt"], before["verifiedAt"]);
		assert_eq!(me["authMethod"], before["authMethod"]);
	}
	assert_eq!(member.get("/api/admin/members").await.0, 200);
	assert_eq!(
		admin
			.post(
				"/api/admin/members/role",
				json!({"accountId":id,"role":"Member"})
			)
			.await
			.0,
		200
	);
	assert_eq!(member.get("/api/admin/members").await.0, 403);
	assert_eq!(member.get("/api/auth/me").await.1["authenticated"], true);
	let count: i64 =
		sqlx::query_scalar("SELECT count(*) FROM member_admin_actions WHERE \"Action\"=2")
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(count, 3);
	for path in [
		"/api/admin/members/deactivate",
		"/api/admin/members/reactivate",
		"/api/admin/members/role",
	] {
		assert_eq!(
			admin.post(path, json!({"accountId":"invalid"})).await.0,
			400
		);
		assert_eq!(
			admin
				.post(path, json!({"accountId":uuid::Uuid::nil(),"role":"Member"}))
				.await
				.0,
			400
		);
		assert_eq!(
			admin
				.post(
					path,
					json!({"accountId":uuid::Uuid::new_v4(),"role":"Member"})
				)
				.await
				.0,
			404
		);
	}
	assert_eq!(
		admin
			.post(
				"/api/admin/members/role",
				json!({"accountId":id,"role":"Superadmin"})
			)
			.await
			.0,
		400
	);
	for path in [
		"/api/admin/members/deactivate",
		"/api/admin/members/reactivate",
	] {
		assert_eq!(admin.post(path, json!({"accountId":id})).await.0, 200);
		assert_eq!(admin.post(path, json!({"accountId":id})).await.0, 409);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn last_active_administrator_decisions_are_atomic_across_replicas() {
	for second_operation in ["/api/admin/members/deactivate", "/api/admin/members/role"] {
		let app = support::App::new().await;
		let mut a = app.admin().await;
		let aid = a.get("/api/auth/me").await.1["accountId"].clone();
		assert_eq!(
			a.post("/api/admin/members/deactivate", json!({"accountId":aid}))
				.await
				.0,
			409
		);
		assert_eq!(
			a.post(
				"/api/admin/members/role",
				json!({"accountId":aid,"role":"Member"})
			)
			.await
			.0,
			409
		);
		let bid = a
			.post(
				"/api/admin/invitations",
				json!({"email":"second@liedertafel.test","role":"Administrator"}),
			)
			.await
			.1["accountId"]
			.clone();
		let mut b = app.sign_in("second@liedertafel.test").await;
		b.router = support::App::router(
			app.pool.clone(),
			app.mail.clone(),
			app.time.clone(),
			archive_server::auth::AuthConfig::development(),
		);
		let (a, b) = tokio::join!(
			a.post("/api/admin/members/deactivate", json!({"accountId":aid})),
			b.post(second_operation, json!({"accountId":bid,"role":"Member"}))
		);
		assert!(
			matches!((a.0.as_u16(), b.0.as_u16()), (200, 409) | (409, 200)),
			"{a:?} {b:?}"
		);
		let count:i64=sqlx::query_scalar("SELECT count(*) FROM \"AspNetUsers\" u JOIN \"AspNetUserRoles\" ur ON ur.\"UserId\"=u.\"Id\" JOIN \"AspNetRoles\" r ON r.\"Id\"=ur.\"RoleId\" WHERE r.\"Name\"='Administrator' AND u.\"EmailConfirmed\" AND (u.\"LockoutEnd\" IS NULL OR u.\"LockoutEnd\"<now())").fetch_one(&app.pool).await.unwrap();
		assert_eq!(count, 1);
		app.close().await;
	}
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn queued_mutation_rechecks_actor_role_revocation_and_original_freshness() {
	for mode in ["demote", "revoke", "stale"] {
		let app = support::App::new().await;
		let mut caller = app.admin().await;
		let id: uuid::Uuid = caller.get("/api/auth/me").await.1["accountId"]
			.as_str()
			.unwrap()
			.parse()
			.unwrap();
		let mut tx = app.pool.begin().await.unwrap();
		sqlx::query("SELECT pg_advisory_xact_lock(222,1)")
			.execute(&mut *tx)
			.await
			.unwrap();
		let pending = tokio::spawn(async move {
			caller
				.post(
					"/api/admin/invitations",
					json!({"email":"queued@liedertafel.test","role":"Member"}),
				)
				.await
		});
		tokio::time::timeout(std::time::Duration::from_secs(5),async{loop{let waiting:bool=sqlx::query_scalar("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory' AND query='SELECT pg_advisory_xact_lock(222,1)')").fetch_one(&app.pool).await.unwrap();if waiting{break;}tokio::task::yield_now().await;}}).await.unwrap();
		match mode {
			"demote" => {
				sqlx::query("DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\"=$1")
					.bind(id)
					.execute(&mut *tx)
					.await
					.unwrap();
				sqlx::query("INSERT INTO \"AspNetUserRoles\" SELECT $1,\"Id\" FROM \"AspNetRoles\" WHERE \"Name\"='Member'").bind(id).execute(&mut *tx).await.unwrap();
			}
			"revoke" => {
				sqlx::query(
					"UPDATE \"AspNetUsers\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=$1",
				)
				.bind(id)
				.execute(&mut *tx)
				.await
				.unwrap();
			}
			_ => {
				*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
			}
		}
		tx.commit().await.unwrap();
		let result = pending.await.unwrap();
		assert_eq!(result.0, if mode == "revoke" { 401 } else { 403 });
		assert_eq!(
			sqlx::query_scalar::<_, i64>("SELECT count(*) FROM member_invitations")
				.fetch_one(&app.pool)
				.await
				.unwrap(),
			0
		);
		app.close().await;
	}
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn freshness_is_rechecked_after_waiting_for_the_target_account_lock() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let mut member = app.sign_in("mitglied@liedertafel.test").await;
	let id: uuid::Uuid = member.get("/api/auth/me").await.1["accountId"]
		.as_str()
		.unwrap()
		.parse()
		.unwrap();
	let mut tx = app.pool.begin().await.unwrap();
	sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE")
		.bind(id)
		.fetch_one(&mut *tx)
		.await
		.unwrap();
	let pending = tokio::spawn(async move {
		admin
			.post(
				"/api/admin/members/role",
				json!({"accountId":id,"role":"Editor"}),
			)
			.await
	});
	tokio::time::timeout(std::time::Duration::from_secs(5),async{loop{let waiting:bool=sqlx::query_scalar("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event IN ('transactionid','tuple') AND query='SELECT * FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE')").fetch_one(&app.pool).await.unwrap();if waiting{break;}tokio::task::yield_now().await;}}).await.unwrap();
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(11);
	tx.commit().await.unwrap();
	assert_eq!(pending.await.unwrap().0, 403);
	assert_eq!(
		member.get("/api/auth/me").await.1["roles"],
		json!(["Member"])
	);
	assert_eq!(
		sqlx::query_scalar::<_, i64>("SELECT count(*) FROM member_admin_actions")
			.fetch_one(&app.pool)
			.await
			.unwrap(),
		0
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn deactivate_reactivate_preserve_history_and_never_revive_old_session() {
	let app = support::App::new().await;
	let mut admin = app.admin().await;
	let admin_id = admin.get("/api/auth/me").await.1["accountId"].clone();
	let invite = admin
		.post(
			"/api/admin/invitations",
			json!({"email":"neu@liedertafel.test","role":"Member"}),
		)
		.await;
	assert_eq!(invite.0, 201);
	let id = invite.1["accountId"].clone();
	let mut member = app.sign_in("neu@liedertafel.test").await;
	let before = admin.get("/api/admin/members").await.1;
	let row = before["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|r| r["accountId"] == id)
		.unwrap()
		.clone();
	let changed = admin
		.post("/api/admin/members/deactivate", json!({"accountId":id}))
		.await;
	assert_eq!(changed.0, 200, "{changed:?}");
	assert_eq!(changed.1["status"], "deactivated");
	assert_eq!(
		member.get("/api/auth/me").await.1,
		json!({"authenticated":false})
	);
	assert_eq!(member.get("/api/admin/members").await.0, 401);
	let after = admin.get("/api/admin/members").await.1;
	let after = after["members"]
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
		assert_eq!(row[key], after[key]);
	}
	assert_eq!(
		admin
			.post("/api/admin/members/reactivate", json!({"accountId":id}))
			.await
			.1["status"],
		"active"
	);
	assert_eq!(member.get("/api/auth/me").await.1["authenticated"], false);
	let mut fresh = app.sign_in("neu@liedertafel.test").await;
	assert_eq!(fresh.get("/api/auth/me").await.1["accountId"], id);
	let audits = sqlx::query("SELECT * FROM member_admin_actions ORDER BY \"Action\"")
		.fetch_all(&app.pool)
		.await
		.unwrap();
	assert_eq!(audits.len(), 2);
	for (i, audit) in audits.iter().enumerate() {
		assert_eq!(audit.get::<i32, _>("Action"), i as i32);
		assert_eq!(
			audit.get::<uuid::Uuid, _>("ActorAccountId").to_string(),
			admin_id.as_str().unwrap()
		);
		assert_eq!(audit.get::<String, _>("OldRoles"), "Member");
	}
	app.close().await;
}
