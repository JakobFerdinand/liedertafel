//! Preserve the existing operator environment seam, including Aspire's Npgsql
//! string. Parsing errors intentionally never contain values (passwords).

use super::MigrationError;
use sqlx::postgres::{PgConnectOptions, PgSslMode};

pub(super) fn parse(value: &str) -> Result<PgConnectOptions, MigrationError> {
	let value = value.trim();
	if value.starts_with("postgres://") || value.starts_with("postgresql://") {
		return value.parse().map_err(|_| MigrationError::InvalidConnection);
	}
	let mut options = PgConnectOptions::new_without_pgpass();
	let mut remaining = value;
	while !remaining.trim().trim_start_matches(';').trim().is_empty() {
		remaining = remaining.trim().trim_start_matches(';').trim_start();
		let (key, tail) = remaining
			.split_once('=')
			.ok_or(MigrationError::InvalidConnection)?;
		let (v, tail) = parse_value(tail.trim_start())?;
		remaining = tail;
		let key = key.trim().to_ascii_lowercase().replace(' ', "");
		options = match key.as_str() {
			"host" | "server" => options.host(&v),
			"port" => options.port(v.parse().map_err(|_| MigrationError::InvalidConnection)?),
			"database" => options.database(&v),
			"username" | "userid" | "user" => options.username(&v),
			"password" | "pwd" => options.password(&v),
			"sslmode" => options.ssl_mode(match v.to_ascii_lowercase().replace('-', "").as_str() {
				"disable" => PgSslMode::Disable,
				"allow" => PgSslMode::Allow,
				"prefer" => PgSslMode::Prefer,
				"require" => PgSslMode::Require,
				"verifyca" => PgSslMode::VerifyCa,
				"verifyfull" => PgSslMode::VerifyFull,
				_ => return Err(MigrationError::InvalidConnection),
			}),
			"rootcertificate" => options.ssl_root_cert(v),
			// These are enforced by our single, unpooled bounded connection, just
			// as DatabaseConfiguration overwrites them in C#.
			"maximumpoolsize" | "minimumpoolsize" | "timeout" | "commandtimeout" | "keepalive"
			| "includeerrordetail" | "pooling" => options,
			_ => return Err(MigrationError::InvalidConnection),
		};
	}
	Ok(options)
}

fn parse_value(value: &str) -> Result<(String, &str), MigrationError> {
	let Some(quote @ ('\'' | '"')) = value.chars().next() else {
		return Ok(match value.split_once(';') {
			Some((v, rest)) => (v.trim().to_owned(), rest),
			None => (value.trim().to_owned(), ""),
		});
	};
	let mut result = String::new();
	let mut chars = value[1..].char_indices().peekable();
	while let Some((i, c)) = chars.next() {
		if c != quote {
			result.push(c);
			continue;
		}
		if chars.peek().is_some_and(|(_, next)| *next == quote) {
			chars.next();
			result.push(quote);
			continue;
		}
		let rest = value[i + 2..].trim_start();
		if rest.is_empty() {
			return Ok((result, rest));
		}
		if let Some(rest) = rest.strip_prefix(';') {
			return Ok((result, rest));
		}
		return Err(MigrationError::InvalidConnection);
	}
	Err(MigrationError::InvalidConnection)
}
