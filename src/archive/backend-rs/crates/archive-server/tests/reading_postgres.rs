#![allow(clippy::unwrap_used, clippy::expect_used)] // Actual current PostgreSQL constraints; no historical waiver.
mod support;
use support::{App, catalogue::*};
#[tokio::test]
#[ignore = "explicit PostgreSQL; canonical reading constraints/FKs"]
async fn current_reading_rows_enforce_status_pages_keys_and_restrict_foreign_keys() {
	let app = App::new_scoped("reading229").await;
	let owner = lied(&app, "Constraint", serde_json::json!({})).await;
	let id = datei(&app, owner, false, "noten", "application/pdf").await;
	archive_server::reading::insert(&app.pool, id, uuid::Uuid::nil(), 20, chrono::Utc::now())
		.await
		.unwrap();
	for (statement, constraint) in [
		(
			r#"UPDATE reading_jobs SET "Status"=9"#,
			"CK_reading_jobs_status",
		),
		(
			r#"UPDATE reading_jobs SET "PagesRead"=-1"#,
			"CK_reading_jobs_pages",
		),
		(
			r#"UPDATE reading_jobs SET "PageLimit"=-1"#,
			"CK_reading_jobs_pages",
		),
	] {
		let error = sqlx::query(statement).execute(&app.pool).await.unwrap_err();
		assert_eq!(
			error.as_database_error().unwrap().constraint(),
			Some(constraint)
		);
	}
	let error = sqlx::query(r#"DELETE FROM dateien WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap_err();
	assert_eq!(
		error.as_database_error().unwrap().code().as_deref(),
		Some("23503")
	);
	let error = archive_server::reading::insert(
		&app.pool,
		uuid::Uuid::new_v4(),
		uuid::Uuid::nil(),
		20,
		chrono::Utc::now(),
	)
	.await
	.unwrap_err();
	assert_eq!(
		error.as_database_error().unwrap().constraint(),
		Some("FK_reading_jobs_dateien_DateiId")
	);
	let error=sqlx::query(r#"INSERT INTO extraction_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion","TextSource") VALUES ($1,0,0,$2,now(),now(),0,0)"#).bind(uuid::Uuid::new_v4()).bind(uuid::Uuid::nil()).execute(&app.pool).await.unwrap_err();
	assert_eq!(
		error.as_database_error().unwrap().code().as_deref(),
		Some("23503")
	);
	let error =
		archive_server::reading::insert(&app.pool, id, uuid::Uuid::nil(), 20, chrono::Utc::now())
			.await
			.unwrap_err();
	assert_eq!(
		error.as_database_error().unwrap().constraint(),
		Some("PK_reading_jobs")
	);
	app.close().await;
}
