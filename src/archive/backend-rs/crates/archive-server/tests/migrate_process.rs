//! Native command seam for #220; PostgreSQL checks use only a disposable server.
// Integration tests may panic/unwrap to report assertions (production may not).
#![allow(clippy::expect_used, clippy::panic, clippy::unwrap_used)]

use sqlx::{ConnectOptions, Connection, PgConnection};
use std::process::Stdio;
use std::time::Duration;

const BASELINE: &str = include_str!("../../archive-db/baseline/neon-schema.sql");

#[test]
fn embedded_runtime_grants_remain_the_canonical_script() {
	assert_eq!(
		archive_db::RUNTIME_GRANTS,
		include_str!("../../../../../../infrastructure/neon/runtime-grants.sql")
	);
}

fn command() -> tokio::process::Command {
	let mut command = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"));
	command
		.args(["--migrate"])
		.env("ARCHIVE_ENVIRONMENT", "Production")
		.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
		.env_remove("ConnectionStrings__archive-migrations")
		.env_remove("ConnectionStrings__archive-db")
		.stdout(Stdio::piped())
		.stderr(Stdio::piped())
		.kill_on_drop(true);
	command
}

#[tokio::test]
async fn migrate_without_connection_fails_finitely_instead_of_starting_http() {
	let output = tokio::time::timeout(Duration::from_secs(5), command().output())
		.await
		.expect("--migrate must be finite")
		.expect("binary runs");
	assert!(!output.status.success());
	assert!(
		String::from_utf8_lossy(&output.stderr).contains("ConnectionStrings__archive-migrations")
	);
}

#[tokio::test]
async fn migrate_requires_development_otlp_and_never_echoes_connection_secrets() {
	let output = command()
		.env("ARCHIVE_ENVIRONMENT", "Development")
		.output()
		.await
		.unwrap();
	assert!(!output.status.success());
	assert!(
		String::from_utf8_lossy(&output.stderr).contains("Development requires OTLP configuration")
	);
	for value in [
		"Host=localhost;Password=\"private-marker;unterminated",
		"Host=localhost;Password=private-marker;SSL Mode=trust-anything",
		"postgres://postgres:private-marker@127.0.0.1:1/archive?sslmode=disable",
	] {
		let output = run_migrate(value).await;
		assert!(!output.status.success());
		let all = format!(
			"{}{}",
			String::from_utf8_lossy(&output.stdout),
			String::from_utf8_lossy(&output.stderr)
		);
		assert!(
			!all.contains("private-marker"),
			"operator errors must redact credentials"
		);
	}
}

async fn run_migrate(connection: &str) -> std::process::Output {
	tokio::time::timeout(
		Duration::from_secs(30),
		command()
			.env("ConnectionStrings__archive-migrations", connection)
			.env(
				"ConnectionStrings__archive-db",
				"invalid-runtime-fallback-must-not-win",
			)
			.env("PORT", "not-an-http-command")
			.output(),
	)
	.await
	.expect("finite migration process")
	.expect("migration binary runs")
}

fn assert_success(output: &std::process::Output) {
	assert!(
		output.status.success(),
		"stdout: {}\nstderr: {}",
		String::from_utf8_lossy(&output.stdout),
		String::from_utf8_lossy(&output.stderr)
	);
}

/// Explicit gate: unlike an early return, an unconfigured DB never counts as a pass.
#[tokio::test]
#[ignore = "requires ARCHIVE_TEST_POSTGRES on a dedicated disposable PostgreSQL server and pg_dump 17"]
async fn postgres_migration_contract() {
	let server = std::env::var("ARCHIVE_TEST_POSTGRES").expect("dedicated throwaway server URL");
	let options: sqlx::postgres::PgConnectOptions = server.parse().expect("PostgreSQL URL");
	let mut admin = PgConnection::connect_with(&options)
		.await
		.expect("local server");
	// Refuse existing names: never destroy an unrelated database, even on a test server.
	sqlx::raw_sql("CREATE DATABASE archive")
		.execute(&mut admin)
		.await
		.expect("archive must not already exist");
	let archive_url = options
		.clone()
		.database("archive")
		.to_url_lossy()
		.to_string();
	let output = run_migrate(&archive_url).await;
	assert_success(&output);
	let mut db = PgConnection::connect_with(&options.clone().database("archive"))
		.await
		.unwrap();
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM public.\"__EFMigrationsHistory\"")
		.fetch_one(&mut db)
		.await
		.unwrap();
	assert_eq!(count, 44);
	let actual: String = sqlx::query_scalar("SELECT jsonb_agg(jsonb_build_object('MigrationId', \"MigrationId\", 'ProductVersion', \"ProductVersion\") ORDER BY \"MigrationId\" COLLATE \"C\")::text FROM public.\"__EFMigrationsHistory\"").fetch_one(&mut db).await.unwrap();
	let provenance: serde_json::Value =
		serde_json::from_str(include_str!("../../archive-db/baseline/provenance.json")).unwrap();
	assert_eq!(
		serde_json::from_str::<serde_json::Value>(&actual).unwrap(),
		provenance["ef_history"]
	);
	let history: i64 = sqlx::query_scalar("SELECT count(*) FROM public._sqlx_migrations")
		.fetch_one(&mut db)
		.await
		.unwrap();
	assert_eq!(history, 2);
	let before: String = sqlx::query_scalar(
		"SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM public._sqlx_migrations m",
	)
	.fetch_one(&mut db)
	.await
	.unwrap();
	assert_success(&run_migrate(&archive_url).await);
	let after: String = sqlx::query_scalar(
		"SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM public._sqlx_migrations m",
	)
	.fetch_one(&mut db)
	.await
	.unwrap();
	assert_eq!(before, after, "reruns must not rewrite Rust history");
	assert_success(
		&command()
			.env("ConnectionStrings__archive-db", &archive_url)
			.output()
			.await
			.unwrap(),
	);
	let (first, second) = tokio::join!(run_migrate(&archive_url), run_migrate(&archive_url));
	assert_success(&first);
	assert_success(&second);
	// Independent database restored from the reviewed Neon fixture, not Rust migrations.
	sqlx::raw_sql("CREATE DATABASE archive_oracle")
		.execute(&mut admin)
		.await
		.unwrap();
	let mut oracle = PgConnection::connect_with(&options.clone().database("archive_oracle"))
		.await
		.unwrap();
	sqlx::raw_sql("DROP SCHEMA public")
		.execute(&mut oracle)
		.await
		.unwrap();
	sqlx::raw_sql(BASELINE).execute(&mut oracle).await.unwrap();
	assert_eq!(
		dump(&options, "archive").await,
		dump(&options, "archive_oracle").await,
		"full frozen public schema parity, excluding Rust bookkeeping and the three additive #221 auth tables"
	);
	db.close().await.unwrap();
	oracle.close().await.unwrap();
	sqlx::raw_sql("DROP DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();
	sqlx::raw_sql("DROP DATABASE archive_oracle")
		.execute(&mut admin)
		.await
		.unwrap();

	// Already-EF-baselined databases must be adopted, not rebuilt. Synthetic
	// account/passkey data stands in for the records the reset ADR preserves.
	sqlx::raw_sql("CREATE DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();
	let mut db = PgConnection::connect_with(&options.clone().database("archive"))
		.await
		.unwrap();
	sqlx::raw_sql("DROP SCHEMA public")
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql(BASELINE).execute(&mut db).await.unwrap();
	sqlx::raw_sql(include_str!("../../archive-db/baseline/ef-history.sql"))
		.execute(&mut db)
		.await
		.unwrap();
	// The actual frozen EF migrations use IN literals. PG17's dump/restore
	// parser changes these three array cast trees without changing their meaning.
	// Keep the original EF shape here as well as the restored-dump shape below.
	sqlx::raw_sql(r#"
		ALTER TABLE public.dateien DROP CONSTRAINT "CK_dateien_kind";
		ALTER TABLE public.dateien ADD CONSTRAINT "CK_dateien_kind" CHECK ("Kind" IN ('noten','uebe','aufnahme','sonstiges'));
		ALTER TABLE public.dateien DROP CONSTRAINT "CK_dateien_storage_tier";
		ALTER TABLE public.dateien ADD CONSTRAINT "CK_dateien_storage_tier" CHECK ("StorageTier" IN ('hot','cold'));
		ALTER TABLE public.recordings DROP CONSTRAINT "CK_recordings_kind";
		ALTER TABLE public.recordings ADD CONSTRAINT "CK_recordings_kind" CHECK ("Kind" IN ('audio','video'));
	"#).execute(&mut db).await.unwrap();
	sqlx::raw_sql("INSERT INTO public.\"AspNetUsers\" (\"Id\", \"Email\", \"EmailConfirmed\", \"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\") VALUES ('00000000-0000-0000-0000-000000000001', 'mitglied@example.test', true, false, false, false, 0); INSERT INTO public.\"AspNetUserPasskeys\" VALUES ('\\x010203', '00000000-0000-0000-0000-000000000001', '{\"Name\":\"Testschlüssel\"}')").execute(&mut db).await.unwrap();
	let before = dump(&options, "archive").await;
	assert_success(&run_migrate(&archive_url).await);
	assert_eq!(before, dump(&options, "archive").await);
	let account: String = sqlx::query_scalar("SELECT \"Email\" FROM public.\"AspNetUsers\"")
		.fetch_one(&mut db)
		.await
		.unwrap();
	assert_eq!(account, "mitglied@example.test");
	let passkey: String =
		sqlx::query_scalar("SELECT \"Data\"->>'Name' FROM public.\"AspNetUserPasskeys\"")
			.fetch_one(&mut db)
			.await
			.unwrap();
	assert_eq!(passkey, "Testschlüssel");
	assert_success(&run_migrate(&archive_url).await);
	// Checksum drift must not be silently accepted on a rerun.
	sqlx::raw_sql("UPDATE public._sqlx_migrations SET checksum='\\x00'")
		.execute(&mut db)
		.await
		.unwrap();
	assert!(!run_migrate(&archive_url).await.status.success());
	db.close().await.unwrap();
	sqlx::raw_sql("DROP DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();

	// Missing EF entries and structural drift are not permission to rebuild.
	sqlx::raw_sql("CREATE DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();
	let mut db = PgConnection::connect_with(&options.clone().database("archive"))
		.await
		.unwrap();
	sqlx::raw_sql("DROP SCHEMA public")
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql(BASELINE).execute(&mut db).await.unwrap();
	sqlx::raw_sql(include_str!("../../archive-db/baseline/ef-history.sql"))
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql(
		"DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261028084325_ColdStorage'",
	)
	.execute(&mut db)
	.await
	.unwrap();
	assert!(!run_migrate(&archive_url).await.status.success());
	let history: i64 = sqlx::query_scalar("SELECT count(*) FROM public._sqlx_migrations")
		.fetch_one(&mut db)
		.await
		.unwrap();
	assert_eq!(
		history, 0,
		"rejected adoption does not mark the baseline as applied"
	);
	sqlx::raw_sql(
		"INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('20261028084325_ColdStorage','10.0.12')",
	)
	.execute(&mut db)
	.await
	.unwrap();
	for (change, repair) in [
		(
			"ALTER TABLE public.dateien ALTER COLUMN \"StorageTier\" DROP DEFAULT",
			"ALTER TABLE public.dateien ALTER COLUMN \"StorageTier\" SET DEFAULT 'hot'",
		),
		(
			"ALTER TABLE public.dateien DROP CONSTRAINT \"CK_dateien_owner\"",
			"ALTER TABLE public.dateien ADD CONSTRAINT \"CK_dateien_owner\" CHECK (((\"LiedId\" IS NOT NULL)::integer + (\"EventId\" IS NOT NULL)::integer) = 1)",
		),
		(
			"DROP INDEX public.\"IX_lieder_Title\"",
			"CREATE INDEX \"IX_lieder_Title\" ON public.lieder (\"Title\")",
		),
		(
			"ALTER SEQUENCE public.\"AspNetRoleClaims_Id_seq\" INCREMENT 2",
			"ALTER SEQUENCE public.\"AspNetRoleClaims_Id_seq\" INCREMENT 1",
		),
	] {
		sqlx::raw_sql(change).execute(&mut db).await.unwrap();
		assert!(
			!run_migrate(&archive_url).await.status.success(),
			"must reject schema drift: {change}"
		);
		sqlx::raw_sql(repair).execute(&mut db).await.unwrap();
	}
	assert_success(&run_migrate(&archive_url).await);
	db.close().await.unwrap();
	sqlx::raw_sql("DROP DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();
	// An unrelated/partially-created schema is not a fresh archive.
	sqlx::raw_sql("CREATE DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();
	let mut db = PgConnection::connect_with(&options.clone().database("archive"))
		.await
		.unwrap();
	sqlx::raw_sql(
		"CREATE TABLE public.unrelated (id integer); INSERT INTO public.unrelated VALUES (42)",
	)
	.execute(&mut db)
	.await
	.unwrap();
	assert!(!run_migrate(&archive_url).await.status.success());
	assert_eq!(
		sqlx::query_scalar::<_, i32>("SELECT id FROM public.unrelated")
			.fetch_one(&mut db)
			.await
			.unwrap(),
		42
	);
	db.close().await.unwrap();
	sqlx::raw_sql("DROP DATABASE archive")
		.execute(&mut admin)
		.await
		.unwrap();
	role_and_grant_contract(&options, &mut admin).await;
}

async fn role_and_grant_contract(
	options: &sqlx::postgres::PgConnectOptions,
	admin: &mut PgConnection,
) {
	// The canonical bootstrap runs as the local equivalent of the Neon owner.
	// Existing role/database names fail rather than being dropped speculatively.
	sqlx::raw_sql("CREATE ROLE archive_admin LOGIN CREATEROLE")
		.execute(&mut *admin)
		.await
		.unwrap();
	sqlx::raw_sql("CREATE DATABASE archive OWNER archive_admin")
		.execute(&mut *admin)
		.await
		.unwrap();
	let mut db = PgConnection::connect_with(&options.clone().database("archive"))
		.await
		.unwrap();
	sqlx::raw_sql("SET ROLE archive_admin")
		.execute(&mut db)
		.await
		.unwrap();
	let bootstrap = include_str!("../../../../../../infrastructure/neon/bootstrap-roles.sql")
		.lines()
		.filter(|l| !l.trim_start().starts_with('\\'))
		.collect::<Vec<_>>()
		.join("\n");
	sqlx::raw_sql(sqlx::AssertSqlSafe(bootstrap))
		.execute(&mut db)
		.await
		.unwrap();
	sqlx::raw_sql("RESET ROLE; ALTER ROLE archive_migrator PASSWORD 'mig;rate''quoted'; ALTER ROLE archive_runtime PASSWORD 'runtime-only'").execute(&mut db).await.unwrap();
	let connection = format!(
		"Host={};Port={};Database=archive;Username=archive_migrator;Password=\"mig;rate'quoted\";SSL Mode=Disable;Maximum Pool Size=5;Minimum Pool Size=0;Timeout=5;Command Timeout=10;Keepalive=0",
		options.get_host(),
		options.get_port()
	);
	assert!(
		!run_migrate(&connection.replace("SSL Mode=Disable", "SSL Mode=VerifyFull"))
			.await
			.status
			.success(),
		"VerifyFull must not downgrade on this non-TLS disposable server"
	);
	assert_success(&run_migrate(&connection).await);
	// The grants script's database-name guard stays a hard process failure.
	sqlx::raw_sql("CREATE DATABASE archive_wrong OWNER archive_migrator")
		.execute(&mut *admin)
		.await
		.unwrap();
	assert!(
		!run_migrate(&connection.replace("Database=archive;", "Database=archive_wrong;"))
			.await
			.status
			.success()
	);
	sqlx::raw_sql("DROP DATABASE archive_wrong")
		.execute(&mut *admin)
		.await
		.unwrap();
	let mut migrator = PgConnection::connect_with(
		&options
			.clone()
			.database("archive")
			.username("archive_migrator")
			.password("mig;rate'quoted"),
	)
	.await
	.unwrap();
	let mut runtime = PgConnection::connect_with(
		&options
			.clone()
			.database("archive")
			.username("archive_runtime")
			.password("runtime-only"),
	)
	.await
	.unwrap();
	for table in [
		"reading_jobs",
		"dateien",
		"lieder",
		"archive_sessions",
		"archive_csrf",
		"archive_ceremonies",
	] {
		let allowed: bool = sqlx::query_scalar(
			"SELECT has_table_privilege('archive_runtime', $1, 'SELECT, INSERT, UPDATE, DELETE')",
		)
		.bind(table)
		.fetch_one(&mut db)
		.await
		.unwrap();
		assert!(allowed, "runtime DML on {table}");
		let can_delegate: bool = sqlx::query_scalar(
			"SELECT has_table_privilege('archive_runtime', $1, 'SELECT WITH GRANT OPTION')",
		)
		.bind(table)
		.fetch_one(&mut db)
		.await
		.unwrap();
		assert!(!can_delegate, "runtime has no grant options");
	}
	// The additive tables use ordinary runtime DML; no runtime DDL or grants.
	sqlx::raw_sql("INSERT INTO \"AspNetUsers\"(\"Id\",\"EmailConfirmed\",\"PhoneNumberConfirmed\",\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\") VALUES('00000000-0000-0000-0000-000000000221',true,false,false,true,0); INSERT INTO archive_sessions(token_digest,user_id,verified_at,auth_method,renewed_at,expires_at) VALUES(decode(repeat('ab',32),'hex'),'00000000-0000-0000-0000-000000000221',now(),'passkey',now(),now()+interval '30 days'); INSERT INTO archive_csrf(browser_digest,request_token,session_digest,expires_at) VALUES(decode(repeat('cd',32),'hex'),repeat('x',43),decode(repeat('ab',32),'hex'),now()+interval '2 hours'); INSERT INTO archive_ceremonies(browser_digest,operation,state,expires_at) VALUES(decode(repeat('ef',32),'hex'),'login','{}',now()+interval '5 minutes'); UPDATE archive_sessions SET expires_at=now()+interval '30 days'; DELETE FROM archive_sessions; DELETE FROM archive_ceremonies; DELETE FROM \"AspNetUsers\" WHERE \"Id\"='00000000-0000-0000-0000-000000000221'").execute(&mut runtime).await.unwrap();
	let wrong_owner: bool = sqlx::query_scalar("SELECT EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner WHERE n.nspname='public' AND r.rolname <> 'archive_migrator')")
		.fetch_one(&mut db).await.unwrap();
	assert!(
		!wrong_owner,
		"migrator owns the migrated objects; runtime owns none"
	);
	for history in [
		"public.\"__EFMigrationsHistory\"",
		"public._sqlx_migrations",
	] {
		for privilege in [
			"SELECT",
			"INSERT",
			"UPDATE",
			"DELETE",
			"TRUNCATE",
			"REFERENCES",
			"TRIGGER",
		] {
			let allowed: bool =
				sqlx::query_scalar("SELECT has_table_privilege('archive_runtime', $1, $2)")
					.bind(history)
					.bind(privilege)
					.fetch_one(&mut db)
					.await
					.unwrap();
			assert!(!allowed, "runtime must not have {privilege} on {history}");
		}
	}
	// Default privileges cover future tables/sequences, but not public function execution.
	sqlx::raw_sql("CREATE TABLE public.later_feature (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, value text NOT NULL); CREATE FUNCTION public.future_function() RETURNS integer LANGUAGE sql AS 'SELECT 1'").execute(&mut migrator).await.unwrap();
	sqlx::raw_sql("INSERT INTO public.later_feature(value) VALUES ('Test'); UPDATE public.later_feature SET value='Geändert'; SELECT * FROM public.later_feature; DELETE FROM public.later_feature").execute(&mut runtime).await.unwrap();
	for forbidden in [
		"CREATE TABLE public.forbidden(id integer)",
		"ALTER TABLE public.later_feature ADD COLUMN forbidden integer",
		"DROP TABLE public.later_feature",
		"CREATE TEMP TABLE forbidden(id integer)",
		"CREATE ROLE forbidden",
		"SET ROLE archive_migrator",
		"SELECT * FROM public.\"__EFMigrationsHistory\"",
		"DELETE FROM public.\"__EFMigrationsHistory\"",
		"SELECT * FROM public._sqlx_migrations",
		"DELETE FROM public._sqlx_migrations",
		"SELECT public.future_function()",
	] {
		let error = sqlx::raw_sql(forbidden)
			.execute(&mut runtime)
			.await
			.expect_err(forbidden);
		assert_eq!(
			error.as_database_error().unwrap().code().as_deref(),
			Some("42501"),
			"{forbidden}"
		);
	}
	let elevated: bool = sqlx::query_scalar("SELECT bool_or(rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls) FROM pg_roles WHERE rolname IN ('archive_runtime', 'archive_migrator')").fetch_one(&mut db).await.unwrap();
	assert!(!elevated);
	// Repairs run after no-op migrations; they must again hide both histories.
	sqlx::raw_sql(
		"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO archive_runtime",
	)
	.execute(&mut migrator)
	.await
	.unwrap();
	assert_success(&run_migrate(&connection).await);
	assert!(
		sqlx::raw_sql("SELECT * FROM public._sqlx_migrations")
			.execute(&mut runtime)
			.await
			.is_err()
	);
	assert!(
		sqlx::raw_sql("SELECT * FROM public.\"__EFMigrationsHistory\"")
			.execute(&mut runtime)
			.await
			.is_err()
	);
	let runtime_connection = options
		.clone()
		.database("archive")
		.username("archive_runtime")
		.password("runtime-only")
		.to_url_lossy()
		.to_string();
	assert!(!run_migrate(&runtime_connection).await.status.success());
	// Default grants already exist on an EF estate at adoption time. Even a
	// failed baseline check must not expose freshly-created Rust bookkeeping.
	// Restore an EF-only fixture before testing failed *baseline* adoption again.
	sqlx::raw_sql("DROP TABLE public.archive_ceremonies, public.archive_csrf, public.archive_sessions; DROP TABLE public.later_feature; DROP FUNCTION public.future_function(); DROP TABLE public._sqlx_migrations; DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261028084325_ColdStorage'").execute(&mut migrator).await.unwrap();
	assert!(!run_migrate(&connection).await.status.success());
	assert!(
		sqlx::raw_sql("SELECT * FROM public._sqlx_migrations")
			.execute(&mut runtime)
			.await
			.is_err(),
		"failed adoption must not leak Rust history through existing defaults"
	);
	sqlx::raw_sql(
		"INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('20261028084325_ColdStorage','10.0.12')",
	)
	.execute(&mut migrator)
	.await
	.unwrap();
	assert_success(&run_migrate(&connection).await);
	db.close().await.unwrap();
	migrator.close().await.unwrap();
	runtime.close().await.unwrap();
	sqlx::raw_sql("DROP DATABASE archive")
		.execute(&mut *admin)
		.await
		.unwrap();
	sqlx::raw_sql("DROP ROLE archive_runtime, archive_migrator, archive_admin")
		.execute(&mut *admin)
		.await
		.unwrap();
}

async fn dump(options: &sqlx::postgres::PgConnectOptions, database: &str) -> String {
	let output = tokio::process::Command::new(
		std::env::var("ARCHIVE_TEST_PG_DUMP").unwrap_or_else(|_| "pg_dump".into()),
	)
	.args([
		"--schema-only",
		"--schema=public",
		"--no-owner",
		"--no-privileges",
		"--exclude-table=public._sqlx_migrations",
		"--exclude-table=public.archive_sessions",
		"--exclude-table=public.archive_csrf",
		"--exclude-table=public.archive_ceremonies",
	])
	.env("PGHOST", options.get_host())
	.env("PGPORT", options.get_port().to_string())
	.env("PGUSER", options.get_username())
	.env("PGDATABASE", database)
	// The wrapper/native client reads its test password from the caller's environment.
	.output()
	.await
	.expect("pg_dump 17 available");
	assert_success(&output);
	String::from_utf8(output.stdout)
		.unwrap()
		.lines()
		.filter(|line| !line.starts_with("\\restrict ") && !line.starts_with("\\unrestrict "))
		.collect::<Vec<_>>()
		.join("\n")
}
