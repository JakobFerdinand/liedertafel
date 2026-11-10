//! Approved head equivalents, NOT execution of obsolete EF transforms.
//! See docs/historical-migration-disposition.md for per-file assertions excluded.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
use sqlx::{Connection, PgConnection};
use uuid::Uuid;

async fn migrate(url: &str) -> std::process::Output {
	tokio::time::timeout(
		std::time::Duration::from_secs(30),
		tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
			.arg("--migrate")
			.env("ARCHIVE_ENVIRONMENT", "Production")
			.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
			.env("ConnectionStrings__archive-migrations", url)
			.kill_on_drop(true)
			.output(),
	)
	.await
	.unwrap()
	.unwrap()
}
async fn snapshot(db: &mut PgConnection) -> serde_json::Value {
	// JSON snapshots include complete synthetic rows, not only counts. No oracle
	// schema is derived from the Rust migration or feature implementation.
	sqlx::query_scalar(r#"SELECT jsonb_build_object('users',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM "AspNetUsers" t),'roles',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM "AspNetRoles" t),'memberships',(SELECT jsonb_agg(to_jsonb(t)) FROM "AspNetUserRoles" t),'passkeys',(SELECT jsonb_agg(to_jsonb(t)) FROM "AspNetUserPasskeys" t),'lieder',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM lieder t),'dateien',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM dateien t),'events',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM events t),'programmes',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM programmes t),'items',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Position") FROM programme_items t),'recordings',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM recordings t),'marks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM recording_passages t),'uploads',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "Id") FROM upload_sessions t),'ef',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "MigrationId") FROM "__EFMigrationsHistory" t))"#).fetch_one(db).await.unwrap()
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL; native frozen-head equivalent"]
async fn programme_and_old_model_head_adoption_preserves_all_current_rows_and_rejects_old_history()
{
	let server = std::env::var("ARCHIVE_TEST_POSTGRES").unwrap();
	let mut url = url::Url::parse(&server).unwrap();
	assert!(matches!(
		url.host_str(),
		Some("localhost" | "127.0.0.1" | "[::1]")
	));
	let name = format!("history_{}", Uuid::new_v4().simple());
	let mut admin = PgConnection::connect(&server).await.unwrap();
	sqlx::raw_sql(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
		.execute(&mut admin)
		.await
		.unwrap();
	url.set_path(&name);
	let mut db = PgConnection::connect(url.as_str()).await.unwrap();
	sqlx::raw_sql("DROP SCHEMA public")
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql(include_str!("../../archive-db/baseline/neon-schema.sql"))
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql(include_str!("../../archive-db/baseline/ef-history.sql"))
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql("SET search_path=public")
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql(r#"
	INSERT INTO "AspNetUsers" ("Id","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount") VALUES ('00000000-0000-0000-0000-00000000000a',true,false,false,false,0);
	INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName") VALUES ('00000000-0000-0000-0000-00000000000b','Editor','EDITOR');
	INSERT INTO "AspNetUserRoles" ("UserId","RoleId") VALUES ('00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-00000000000b');
	INSERT INTO "AspNetUserPasskeys" ("CredentialId","UserId","Data") VALUES ('\x0102','00000000-0000-0000-0000-00000000000a','{}');
	INSERT INTO lieder ("Id","Title","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ('20000000-0000-0000-0000-000000000001','Erstes Lied',now(),'00000000-0000-0000-0000-00000000000a',now(),'00000000-0000-0000-0000-00000000000a',1),('20000000-0000-0000-0000-000000000002','Zweites Lied',now(),'00000000-0000-0000-0000-00000000000a',now(),'00000000-0000-0000-0000-00000000000a',2);
	INSERT INTO events ("Id","Title","Kind","DateApproximate","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ('30000000-0000-0000-0000-000000000001','Adventkonzert','concert',false,now(),'00000000-0000-0000-0000-00000000000a',now(),'00000000-0000-0000-0000-00000000000a',3);
	INSERT INTO programmes ("Id","EventId","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") VALUES ('30000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000001',now(),'00000000-0000-0000-0000-00000000000a',now(),'00000000-0000-0000-0000-00000000000a',4);
	INSERT INTO programme_items ("Id","ProgrammeId","Position","LiedId","Note") VALUES ('cc000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000002',1,'20000000-0000-0000-0000-000000000002','Eröffnung'),('cc000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000002',2,'20000000-0000-0000-0000-000000000001',NULL);
	INSERT INTO dateien ("Id","EventId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","CreatedByAccountId","CreatedAt","RowVersion") VALUES ('30000000-0000-0000-0000-000000000008','30000000-0000-0000-0000-000000000001','aufnahme',false,'dateien/aufnahme','audio/mpeg',10,'00000000-0000-0000-0000-00000000000a',now(),1);
	INSERT INTO dateien ("Id","LiedId","Kind","IsPartitur","BlobName","ContentType","SizeBytes","CreatedByAccountId","CreatedAt","RowVersion") VALUES ('20000000-0000-0000-0000-000000000008','20000000-0000-0000-0000-000000000001','noten',true,'dateien/noten','application/pdf',20,'00000000-0000-0000-0000-00000000000a',now(),2);
	INSERT INTO recordings ("Id","EventId","Label","Kind","DownloadEnabled","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion","OriginalDateiId") VALUES ('30000000-0000-0000-0000-00000000000a','30000000-0000-0000-0000-000000000001','Konzert','audio',false,now(),'00000000-0000-0000-0000-00000000000a',now(),'00000000-0000-0000-0000-00000000000a',5,'30000000-0000-0000-0000-000000000008');
	INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion","PlaybackDateiId") VALUES ('40000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-00000000000a','cc000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000001',1,5,now(),'00000000-0000-0000-0000-00000000000a',now(),'00000000-0000-0000-0000-00000000000a',6,'30000000-0000-0000-0000-000000000008');
	INSERT INTO upload_sessions ("Id","LiedId","DateiKind","BlobName","ContentType","MaxSizeBytes","UploadTicketExpiresAt","State","CreatedByAccountId","CreatedAt") VALUES ('50000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','noten','pending/neu','application/pdf',10,now(),0,'00000000-0000-0000-0000-00000000000a',now());
	"#).execute(&mut db).await.unwrap();
	// #229 approved equivalent: already-Datei-keyed extraction/source and paid
	// reading progress survive frozen-head adoption. Obsolete revision->Datei
	// deletion/rekeying is deliberately NOT executed or claimed.
	sqlx::raw_sql(r#"INSERT INTO extraction_jobs ("DateiId","Status","Text","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion","TextSource") VALUES ('20000000-0000-0000-0000-000000000008',2,'Schon bezahlter Notentext',2,'00000000-0000-0000-0000-00000000000a',now(),now(),7,1);
	INSERT INTO reading_jobs ("DateiId","Status","PagesTotal","PagesRead","PageLimit","FieldsDone","FieldsApplied","FailureReason","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","RowVersion") VALUES ('20000000-0000-0000-0000-000000000008',2,35,10,20,false,3,NULL,0,'00000000-0000-0000-0000-00000000000a',now(),now(),8);"#).execute(&mut db).await.unwrap();
	let reading_before:serde_json::Value=sqlx::query_scalar(r#"SELECT jsonb_build_object('extraction',(SELECT jsonb_agg(to_jsonb(t)) FROM extraction_jobs t),'reading',(SELECT jsonb_agg(to_jsonb(t)) FROM reading_jobs t))"#).fetch_one(&mut db).await.unwrap();
	let before = snapshot(&mut db).await;
	// Both older-history frontiers must refuse before adoption; no transforms.
	for frontier in [
		"20261016170055_AddLiederAndDateien",
		"20261017185113_AuftrittDateien",
		"20261019102646_LiedEntwuerfe",
	] {
		let mut tx = db.begin().await.unwrap();
		let later:Vec<(String,String)>=sqlx::query_as(r#"DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId">$1 RETURNING "MigrationId","ProductVersion""#).bind(frontier).fetch_all(&mut *tx).await.unwrap();
		tx.commit().await.unwrap();
		let older = snapshot(&mut db).await;
		assert!(!migrate(url.as_str()).await.status.success());
		assert_eq!(
			older,
			snapshot(&mut db).await,
			"failed adoption never touches data/history"
		);
		let rows:serde_json::Value=sqlx::query_scalar(r#"SELECT jsonb_build_object('extraction',(SELECT jsonb_agg(to_jsonb(t)) FROM extraction_jobs t),'reading',(SELECT jsonb_agg(to_jsonb(t)) FROM reading_jobs t))"#).fetch_one(&mut db).await.unwrap();
		assert_eq!(rows, reading_before);
		for (id, version) in later {
			sqlx::query(r#"INSERT INTO "__EFMigrationsHistory" VALUES ($1,$2)"#)
				.bind(id)
				.bind(version)
				.execute(&mut db)
				.await
				.unwrap();
		}
	}
	// Schema-only old-model remnants are independently refused even with all
	// 44 history rows. These negative sentinels are not an EF upgrade fixture.
	for (create, drop) in [
		(
			"CREATE TABLE public.songs (sentinel text); INSERT INTO public.songs VALUES ('keep-old-data')",
			"DROP TABLE public.songs",
		),
		(
			"CREATE TABLE public.programme_revisions (sentinel text); INSERT INTO public.programme_revisions VALUES ('keep-old-data')",
			"DROP TABLE public.programme_revisions",
		),
	] {
		sqlx::raw_sql(create).execute(&mut db).await.unwrap();
		assert!(!migrate(url.as_str()).await.status.success());
		assert_eq!(before, snapshot(&mut db).await);
		let sentinel: String =
			sqlx::query_scalar(sqlx::AssertSqlSafe(if create.contains("public.songs") {
				"SELECT sentinel FROM public.songs"
			} else {
				"SELECT sentinel FROM public.programme_revisions"
			}))
			.fetch_one(&mut db)
			.await
			.unwrap();
		assert_eq!(sentinel, "keep-old-data");
		sqlx::raw_sql(drop).execute(&mut db).await.unwrap();
	}
	for _ in 0..2 {
		let output = migrate(url.as_str()).await;
		assert!(
			output.status.success(),
			"{}",
			String::from_utf8_lossy(&output.stderr)
		);
		assert_eq!(before, snapshot(&mut db).await);
	}
	let reading_after:serde_json::Value=sqlx::query_scalar(r#"SELECT jsonb_build_object('extraction',(SELECT jsonb_agg(to_jsonb(t)) FROM extraction_jobs t),'reading',(SELECT jsonb_agg(to_jsonb(t)) FROM reading_jobs t))"#).fetch_one(&mut db).await.unwrap();
	assert_eq!(reading_before, reading_after);
	for table in [
		"songs",
		"song_titles",
		"song_tags",
		"arrangements",
		"musical_versions",
		"assets",
		"file_revisions",
		"asset_revision_changes",
		"programme_revisions",
		"programme_confirmations",
		"performances",
	] {
		let exists:bool=sqlx::query_scalar("SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema='public' AND table_name=$1)").bind(table).fetch_one(&mut db).await.unwrap();
		assert!(!exists, "{table}");
	}
	for (table, column) in [
		("extraction_jobs", "RevisionId"),
		("extraction_jobs", "AssetId"),
		("reading_jobs", "RevisionId"),
		("reading_jobs", "AssetId"),
		("events", "PublishedAt"),
		("events", "PublishedByAccountId"),
		("programme_items", "RevisionId"),
		("programme_items", "SongId"),
		("programme_items", "ArrangementId"),
		("programme_items", "MusicalVersionId"),
		("recordings", "OriginalAssetId"),
		("recordings", "PlaybackAssetId"),
		("recording_passages", "PerformanceId"),
		("recording_passages", "PlaybackRevisionId"),
		("upload_sessions", "AssetId"),
		("upload_sessions", "FinalizedRevisionId"),
	] {
		let exists:bool=sqlx::query_scalar("SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2)").bind(table).bind(column).fetch_one(&mut db).await.unwrap();
		assert!(!exists, "{table}.{column}");
	}
	let error=sqlx::query(r#"INSERT INTO upload_sessions ("Id","BlobName","ContentType","MaxSizeBytes","UploadTicketExpiresAt","State","CreatedByAccountId","CreatedAt") VALUES (gen_random_uuid(),'pending/none','application/pdf',1,now(),0,gen_random_uuid(),now())"#).execute(&mut db).await.unwrap_err();
	assert_eq!(
		error.as_database_error().unwrap().constraint(),
		Some("CK_upload_sessions_owner")
	);
	db.close().await.unwrap();
	sqlx::raw_sql(sqlx::AssertSqlSafe(format!(
		"DROP DATABASE {name} WITH (FORCE)"
	)))
	.execute(&mut admin)
	.await
	.unwrap();
}
