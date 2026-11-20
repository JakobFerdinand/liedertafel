//! Auth portion of ArchiveAccessTests. Resource visibility belongs to later waves.
mod support;
use archive_server::auth::{AccessPolicy, Auth, AuthConfig};
use axum::{Json, extract::Request, response::IntoResponse, routing::any};
use serde_json::json;
use std::sync::Arc;

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn member_editor_administrator_gates_use_current_database_roles_and_csrf_first() {
	let app = support::App::new().await;
	let auth = Arc::new(
		Auth::new(
			app.pool.clone(),
			AuthConfig::development(),
			app.mail.clone(),
			app.time.clone(),
		)
		.unwrap(),
	);
	let mut router = app.router.clone();
	for (path, policy) in [
		("/test/member", AccessPolicy::Member),
		("/test/editor", AccessPolicy::Editor),
		("/test/admin", AccessPolicy::Administrator),
	] {
		let auth = auth.clone();
		router = router.route(
			path,
			any(move |request: Request| {
				let auth = auth.clone();
				async move {
					let result = auth
						.authorize(request.headers(), policy, request.method() == "POST")
						.await;
					match result {
						Ok(access) => access.respond(Json(json!({"allowed":true})).into_response()),
						Err(error) => error.into_response(),
					}
				}
			}),
		);
	}
	let mut anon = app.client();
	anon.router = router.clone();
	assert_eq!(anon.get("/test/member").await.0, 401);
	assert_eq!(anon.post("/test/editor", json!({})).await.0, 400);
	anon.csrf().await;
	assert_eq!(anon.post("/test/editor", json!({})).await.0, 401);
	let mut member = app.member().await;
	member.router = router.clone();
	assert_eq!(member.get("/test/member").await.0, 200);
	assert_eq!(
		member.get("/test/editor").await.1["title"],
		"Keine Berechtigung für das Liedverzeichnis."
	);
	assert_eq!(member.post("/test/admin", json!({})).await.0, 403);
	let id = member.get("/api/auth/me").await.1["accountId"]
		.as_str()
		.unwrap()
		.parse::<uuid::Uuid>()
		.unwrap();
	sqlx::query("INSERT INTO \"AspNetUserRoles\" SELECT $1,\"Id\" FROM \"AspNetRoles\" WHERE \"Name\"='Administrator'").bind(id).execute(&app.pool).await.unwrap();
	assert_eq!(member.post("/test/admin", json!({})).await.0, 200);
	sqlx::query("UPDATE \"AspNetUsers\" SET \"LockoutEnd\"='2099-01-01' WHERE \"Id\"=$1")
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(member.get("/test/member").await.0, 401);
	sqlx::query("UPDATE \"AspNetUsers\" SET \"LockoutEnd\"=NULL WHERE \"Id\"=$1")
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(
		member.get("/test/member").await.0,
		401,
		"old session stays revoked after reactivation"
	);
	app.close().await;
}
