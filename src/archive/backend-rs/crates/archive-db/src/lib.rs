//! Explicit schema ownership; never invoked by API startup.

mod configuration;

use sqlx::{ConnectOptions, Connection, PgConnection};
use std::time::Duration;

/// Byte-identical manual fallback, including its psql wrapper.
pub const RUNTIME_GRANTS: &str = include_str!("../runtime-grants.sql");

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
	let mut migrations = sqlx::migrate!("./migrations");
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
		// Commit the canonical broad grant and BOTH history revocations together.
		// Otherwise a failed second repair could leave Rust history writable.
		sqlx::raw_sql("REVOKE ALL ON TABLE public._sqlx_migrations FROM archive_runtime; COMMIT;")
			.execute(&mut *connection)
			.await
			.map_err(|_| MigrationError::Grants)?;
		tracing::info!("Runtime grants applied");
	} else {
		tracing::info!("Runtime grants skipped: not connected as archive_migrator");
	}
	Ok(())
}
