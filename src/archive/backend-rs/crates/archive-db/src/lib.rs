//! Explicit schema ownership; never invoked by API startup.

mod configuration;

use sqlx::{ConnectOptions, Connection, PgConnection};
use std::time::Duration;

/// Byte-identical manual fallback, including its psql wrapper.
pub const RUNTIME_GRANTS: &str = include_str!("../runtime-grants.sql");
const MIGRATIONS: sqlx::migrate::Migrator = sqlx::migrate!("./migrations");

const STATUS_PREFIX: &str = "archive.sqlx.applied.v1:";

/// Read-only Development diagnostic. No history means all migrations are
/// pending. An existing history needs a projection from an explicit migrate.
pub async fn development_status(pool: &sqlx::PgPool) -> Result<(bool, Vec<String>), sqlx::Error> {
	// PostgreSQL exposes object comments to connected users, but only the
	// owner can edit this comment. No grants, role changes, SECURITY DEFINER
	// functions or history-row access are needed.
	let (value, has_history, projection): (i32, bool, Option<String>) = sqlx::query_as(
		"SELECT 1, history IS NOT NULL, pg_catalog.obj_description(history, 'pg_class')
		 FROM (SELECT pg_catalog.to_regclass('public._sqlx_migrations') AS history) AS state",
	)
	.fetch_one(pool)
	.await?;
	let versions: Vec<i64> = if has_history {
		let invalid =
			|| sqlx::Error::Protocol("Migration status requires explicit --migrate.".into());
		let payload = projection
			.as_deref()
			.and_then(|s| s.strip_prefix(STATUS_PREFIX))
			.ok_or_else(invalid)?;
		let versions = payload
			.split(',')
			.map(str::parse)
			.collect::<Result<Vec<i64>, _>>()
			.map_err(|_| invalid())?;
		if versions.windows(2).any(|pair| pair[0] >= pair[1]) {
			return Err(invalid());
		}
		versions
	} else {
		Vec::new()
	};
	let pending = MIGRATIONS
		.iter()
		.filter(|m| m.migration_type.is_up_migration() && !versions.contains(&m.version))
		.map(|m| format!("{:04}_{}", m.version, m.description.replace(' ', "_")))
		.collect();
	Ok((value == 1, pending))
}

/// Runtime uses only its own credentials, never the migration connection.
/// Lazy pool: probes/startup do not wake Neon; no periodic keep-alive.
pub fn runtime_pool(value: &str) -> Result<sqlx::PgPool, MigrationError> {
	let options = configuration::parse(value)?
		.application_name("archive-server")
		.options([("statement_timeout", "10000"), ("lock_timeout", "10000")])
		.disable_statement_logging();
	Ok(sqlx::postgres::PgPoolOptions::new()
		.min_connections(0)
		.max_connections(5)
		.acquire_timeout(Duration::from_secs(5))
		.idle_timeout(Some(Duration::from_secs(60)))
		.test_before_acquire(false)
		.connect_lazy_with(options))
}

/// Operator-safe errors: never print a connection string or database error detail.
#[derive(Debug, thiserror::Error)]
pub enum MigrationError {
	#[error(
		"ConnectionStrings__archive-migrations (or ConnectionStrings__archive-db for local admin) is required for --migrate"
	)]
	MissingConnection,
	#[error(
		"invalid PostgreSQL configuration; use a PostgreSQL URL or the documented Npgsql connection settings"
	)]
	InvalidConnection,
	#[error("PostgreSQL connection failed; check migration credentials and database availability")]
	Connection,
	#[error("PostgreSQL connection exceeded the five-second budget")]
	ConnectionTimeout,
	#[error("--migrate refuses archive_runtime; supply the separate migration connection")]
	RuntimeRole,
	#[error(
		"Rust migration failed; verify the frozen EF baseline, migration checksums and migrator privileges"
	)]
	Migration(#[source] sqlx::migrate::MigrateError),
	#[error("runtime grant repair failed; do not resume runtime")]
	Grants,
}

/// Runs only for an explicit operator process; a single connection, no pool,
/// no retry, and grants repaired even on a migration no-op.
pub async fn migrate_from_env() -> Result<(), MigrationError> {
	let value = [
		"ConnectionStrings__archive-migrations",
		"ConnectionStrings__archive-db",
	]
	.into_iter()
	.find_map(|key| std::env::var(key).ok().filter(|v| !v.trim().is_empty()))
	.ok_or(MigrationError::MissingConnection)?;
	let options = configuration::parse(&value)?
		.application_name("archive-migrate")
		.options([("statement_timeout", "10000"), ("lock_timeout", "10000")])
		.disable_statement_logging();
	let mut connection =
		tokio::time::timeout(Duration::from_secs(5), PgConnection::connect_with(&options))
			.await
			.map_err(|_| MigrationError::ConnectionTimeout)?
			.map_err(|_| MigrationError::Connection)?;
	let result = migrate(&mut connection).await;
	// Closing releases advisory locks on every error path too.
	let _ = connection.close().await;
	result
}

async fn migrate(connection: &mut PgConnection) -> Result<(), MigrationError> {
	use sqlx::migrate::Migrate;
	let role: String = sqlx::query_scalar("SELECT current_user")
		.fetch_one(&mut *connection)
		.await
		.map_err(|_| MigrationError::Grants)?;
	if role == "archive_runtime" {
		return Err(MigrationError::RuntimeRole);
	}
	// Hold SQLx's database-wide advisory lock through history setup, all pending
	// migrations AND grant repair. The single-use connection closes on failure.
	connection.lock().await.map_err(MigrationError::Migration)?;
	let mut transaction = connection
		.begin()
		.await
		.map_err(|_| MigrationError::Grants)?;
	transaction
		.ensure_migrations_table("public._sqlx_migrations")
		.await
		.map_err(MigrationError::Migration)?;
	// Existing EF databases already have broad default DML grants. Creation and
	// revocation must commit together, even if baseline validation later fails.
	sqlx::raw_sql("DO $$ BEGIN IF EXISTS (SELECT FROM pg_roles WHERE rolname='archive_runtime') THEN REVOKE ALL ON TABLE public._sqlx_migrations FROM archive_runtime; END IF; END $$")
		.execute(&mut *transaction).await.map_err(|_| MigrationError::Grants)?;
	transaction
		.commit()
		.await
		.map_err(|_| MigrationError::Grants)?;
	let mut migrations = MIGRATIONS;
	// This name is fixed from the very first Rust migration, not a history move.
	migrations.dangerous_set_table_name("public._sqlx_migrations");
	migrations.set_locking(false); // The encompassing lock above includes grant repair.
	migrations
		.run(&mut *connection)
		.await
		.map_err(MigrationError::Migration)?;
	if role == "archive_migrator" {
		let script = RUNTIME_GRANTS
			.lines()
			.filter(|line| !line.trim_start().starts_with('\\') && line.trim() != "COMMIT;")
			.collect::<Vec<_>>()
			.join("\n");
		// Only compiled-in, byte-checked SQL is converted; never external input.
		sqlx::raw_sql(sqlx::AssertSqlSafe(script))
			.execute(&mut *connection)
			.await
			.map_err(|_| MigrationError::Grants)?;
		// Commit the canonical grants, both history revocations and refreshed
		// projection together. A failed revocation must not expose history.
		sqlx::raw_sql("REVOKE ALL ON TABLE public._sqlx_migrations FROM archive_runtime;")
			.execute(&mut *connection)
			.await
			.map_err(|_| MigrationError::Grants)?;
		publish_development_status(connection).await?;
		sqlx::raw_sql("COMMIT")
			.execute(&mut *connection)
			.await
			.map_err(|_| MigrationError::Grants)?;
		tracing::info!("Runtime grants applied");
	} else {
		publish_development_status(connection).await?;
		tracing::info!("Runtime grants skipped: not connected as archive_migrator");
	}
	Ok(())
}

async fn publish_development_status(connection: &mut PgConnection) -> Result<(), MigrationError> {
	// Only version integers already validated by SQLx. Quote the value as
	// a SQL literal on the server; neither names nor SQL come from clients.
	sqlx::raw_sql(
		r#"
		DO $status$
		DECLARE versions text;
		BEGIN
			SELECT pg_catalog.string_agg(version::text, ',' ORDER BY version) INTO STRICT versions
			FROM public._sqlx_migrations WHERE success;
			IF versions IS NULL THEN
				RAISE EXCEPTION 'Cannot publish an empty applied migration list';
			END IF;
			EXECUTE pg_catalog.format('COMMENT ON TABLE public._sqlx_migrations IS %L',
				'archive.sqlx.applied.v1:' || versions);
		END
		$status$;
	"#,
	)
	.execute(connection)
	.await
	.map_err(|_| MigrationError::Grants)?;
	Ok(())
}
