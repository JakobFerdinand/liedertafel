use crate::{
	auth::{Clock, SystemClock},
	catalogue::trash::{remove_datei, remove_owner},
	storage::{BlobStorage, Storage, StorageConfig, StorageError},
};
use chrono::{DateTime, Datelike, Duration, Utc};
use futures::TryStreamExt;
use sqlx::PgPool;
use std::{
	collections::{HashMap, HashSet},
	sync::Arc,
};
use uuid::Uuid;

#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error("Cleanup requires valid database, storage and retention configuration.")]
	Configuration,
	#[error("Cleanup database operation failed.")]
	Database(#[from] sqlx::Error),
	#[error(transparent)]
	Storage(#[from] StorageError),
	#[error("Papierkorb permanent removal failed.")]
	Removal(#[from] crate::auth::AuthError),
}

// Only the three cleanup commands read this overlay. The origin and other
// finite commands retain their existing configuration sources.
pub(super) struct Configuration<'a>(HashMap<String, &'a str>);

impl<'a> Configuration<'a> {
	pub(super) fn from_args(args: &'a [String]) -> Result<Self, Error> {
		let mut values = HashMap::new();
		let mut args = args.iter().map(String::as_str);
		while let Some(arg) = args.next() {
			let prefixed = arg.strip_prefix("--").or_else(|| arg.strip_prefix('/'));
			let (key, value) = if let Some(pair) = prefixed.unwrap_or(arg).split_once('=') {
				if prefixed.is_none() && arg.starts_with('-') {
					return Err(Error::Configuration);
				}
				pair
			} else if let Some(key) = prefixed {
				// The frozen provider consumes the next token even if it
				// looks like another switch (including after --dry-run).
				let Some(value) = args.next() else { break };
				(key, value)
			} else {
				continue;
			};
			values.insert(key.to_ascii_lowercase(), value);
		}
		Ok(Self(values))
	}

	fn get(&self, key: &str) -> Option<std::borrow::Cow<'_, str>> {
		self.0
			.get(&key.replace("__", ":").to_ascii_lowercase())
			.map(|v| (*v).into())
			.or_else(|| std::env::var(key).ok().map(Into::into))
	}

	pub(super) fn maintenance(&self) -> Result<bool, crate::settings::SettingsError> {
		let Some(value) = self.0.get("archive:maintenancemode") else {
			return crate::settings::finite_maintenance();
		};
		value
			.trim()
			.to_ascii_lowercase()
			.parse()
			.map_err(|_| crate::settings::SettingsError::InvalidMaintenance)
	}
}

pub(super) async fn run(
	command: &str,
	config: &Configuration<'_>,
	dry: bool,
	development: bool,
) -> Result<(), Error> {
	let clock = Arc::new(SystemClock);
	let connection = [
		"ConnectionStrings__archive-migrations",
		"ConnectionStrings__archive-db",
	]
	.into_iter()
	.find_map(|k| config.get(k).filter(|v| !v.trim().is_empty()))
	.ok_or(Error::Configuration)?;
	let pool = archive_db::runtime_pool(&connection).map_err(|_| Error::Configuration)?;
	let mut storage = StorageConfig::select(
		config.get("ConnectionStrings__archive-blobs").as_deref(),
		config.get("Archive__Assets__ServiceUri").as_deref(),
		config.get("Archive__Assets__ContainerName").as_deref(),
		development,
	)?;
	if let Some(lifetime) = config.get("Archive__Assets__ReadTicketLifetime") {
		storage.read_lifetime =
			crate::storage::parse_lifetime(&lifetime).ok_or(StorageError::Configuration)?;
	}
	let storage = BlobStorage::new(storage, clock.clone())?;
	let result = match command {
		"--cleanup-uploads" => {
			pending(
				&pool,
				&storage,
				clock.now(),
				config.get("Archive__Assets__ExpiryGrace").as_deref(),
			)
			.await
		}
		"--cleanup-dropped-blobs" => dropped(&pool, &storage, dry).await,
		"--cleanup-papierkorb" => {
			papierkorb(
				&pool,
				&storage,
				clock.now(),
				dry,
				config.get("Archive__Papierkorb__RetentionDays").as_deref(),
			)
			.await
		}
		_ => Err(Error::Configuration),
	};
	pool.close().await;
	result
}

async fn pending(
	pool: &PgPool,
	storage: &BlobStorage,
	now: DateTime<Utc>,
	grace: Option<&str>,
) -> Result<(), Error> {
	let grace = match grace {
		Some(value) => expiry_grace(value).ok_or(Error::Configuration)?,
		None => Duration::hours(1),
	};
	let cutoff = now
		.checked_sub_signed(grace)
		.filter(|cutoff| (1..=9999).contains(&cutoff.year()))
		.ok_or(Error::Configuration)?;
	let mut tx = pool.begin().await?;
	// Lock only expired Pending rows. A concurrent finalize/renew/cancel cannot
	// move one of those rows between selection and deleting its staging object.
	let expired: Vec<(Uuid, String)> = sqlx::query_as(
		r#"
		SELECT "Id","BlobName" FROM upload_sessions WHERE "State"=0 AND "UploadTicketExpiresAt" < $1
		ORDER BY "Id" FOR UPDATE"#,
	)
	.bind(cutoff)
	.fetch_all(&mut *tx)
	.await?;
	for (_, name) in &expired {
		if !name.starts_with("pending/") || storage.delete(name).await.is_err() {
			tracing::warn!(target: "Archive.Jobs", "Pending upload blob could not be deleted during cleanup");
		}
	}
	let ids = expired.iter().map(|(id, _)| *id).collect::<Vec<_>>();
	if !ids.is_empty() {
		sqlx::query(r#"UPDATE upload_sessions SET "State"=2 WHERE "Id"=ANY($1)"#)
			.bind(&ids)
			.execute(&mut *tx)
			.await?;
	}
	tx.commit().await?;
	tracing::info!(target: "Archive.Jobs", abandoned_count=ids.len(), "Cleanup marked {} upload sessions abandoned", ids.len());
	Ok(())
}

/// ExpiryGrace is bound as an invariant .NET TimeSpan, unlike storage's
/// ticket-duration settings. In particular a bare integer counts days, and
/// the sign applies to the whole interval (including zero).
/// Grammar and token/precision limits: .NET 10.0.14 TimeSpanParse.cs (MIT;
/// notice in tests/oracles/dotnet-runtime-LICENSE.txt). Binder oracle in
/// tests/oracles/ExpiryGraceOracle.cs.
fn expiry_grace(value: &str) -> Option<Duration> {
	fn number(s: &str) -> Option<i64> {
		if s.is_empty() || !s.bytes().all(|c| c.is_ascii_digit()) {
			return None;
		}
		let significant = s.trim_start_matches('0');
		if significant.is_empty() {
			Some(0)
		} else {
			significant
				.parse::<i64>()
				.ok()
				.filter(|&n| n <= 0x0fff_ffff)
		}
	}
	fn seconds(s: &str) -> Option<(i64, i64)> {
		let Some((whole, fraction)) = s.split_once('.') else {
			return Some((number(s)?, 0));
		};
		let fraction_number = number(fraction)?;
		let leading_zeroes = fraction.len() - fraction.trim_start_matches('0').len();
		let ticks = if fraction_number == 0 || leading_zeroes >= 8 {
			0
		} else if (leading_zeroes == 0 && fraction.len() > 7) || fraction.len() > 14 {
			// Without leading zeroes the BCL rejects precision >7. With
			// leading zeroes its rounding table supports at most 7 excess
			// places; beyond that the binder rejects the BCL indexing error.
			return None;
		} else if fraction.len() <= 7 {
			fraction_number * 10_i64.pow((7 - fraction.len()) as u32)
		} else {
			let scale = 10_i64.pow((fraction.len() - 7) as u32);
			(fraction_number + scale / 2) / scale
		};
		Some((if whole.is_empty() { 0 } else { number(whole)? }, ticks))
	}
	let value = value.trim();
	let (negative, unsigned) = value
		.strip_prefix('-')
		.map_or((false, value), |s| (true, s));
	let total = |d: i64, h, m, (s, fraction)| {
		if h > 23 || m > 59 || s > 59 {
			return None;
		}
		let ticks =
			(((i128::from(d) * 24 + i128::from(h)) * 60 + i128::from(m)) * 60 + i128::from(s))
				* 10_000_000 + i128::from(fraction);
		i64::try_from(if negative { -ticks } else { ticks }).ok()
	};
	let fields = unsigned.split(':').collect::<Vec<_>>();
	let ticks = match fields.as_slice() {
		[d] => total(number(d)?, 0, 0, (0, 0)),
		[h, m] => match h.split_once('.') {
			Some((d, h)) => total(number(d)?, number(h)?, number(m)?, (0, 0)),
			None => total(0, number(h)?, number(m)?, (0, 0)),
		},
		[first, second, third] => {
			if let Some((d, h)) = first.split_once('.') {
				total(number(d)?, number(h)?, number(second)?, seconds(third)?)
			} else {
				let (a, b) = (number(first)?, number(second)?);
				total(0, a, b, seconds(third)?).or_else(|| total(a, b, number(third)?, (0, 0)))
			}
		}
		[d, h, m, s] => total(number(d)?, number(h)?, number(m)?, seconds(s)?),
		_ => None,
	}?;
	Duration::try_seconds(ticks / 10_000_000)?
		.checked_add(&Duration::nanoseconds((ticks % 10_000_000) * 100))
}

#[cfg(test)]
#[path = "cleanup_grace_contract.rs"]
mod grace_contract;

async fn dropped(pool: &PgPool, storage: &BlobStorage, dry: bool) -> Result<(), Error> {
	let (mut revisions, mut staged, mut kept) = (0, 0, 0);
	let mut blobs = storage.list_prefix("revisions/")?;
	while let Some(name) = blobs.try_next().await? {
		if !dry {
			storage.delete(&name).await?;
		}
		revisions += 1;
	}
	let referenced = sqlx::query_scalar::<_, String>(r#"SELECT "BlobName" FROM upload_sessions"#)
		.fetch_all(pool)
		.await?
		.into_iter()
		.collect::<HashSet<_>>();
	let mut blobs = storage.list_prefix("pending/")?;
	while let Some(name) = blobs.try_next().await? {
		if referenced.contains(&name) {
			kept += 1;
		} else {
			if !dry {
				storage.delete(&name).await?;
			}
			staged += 1;
		}
	}
	let mut blobs = storage.list_prefix("dateien/")?;
	while blobs.try_next().await?.is_some() {
		kept += 1;
	}
	let mode = if dry { " (dry run)" } else { "" };
	let dry_label = if dry { "True" } else { "False" };
	tracing::info!(target: "Archive.Jobs", revisions, staged, kept, dry_run=dry,
		"Dropped-blob cleanup{mode}: {revisions} revision blobs, {staged} orphaned staged uploads, {kept} objects of the flat model kept");
	tracing::info!(target: "Archive.Jobs", "Dropped-blob cleanup finished: Result {{ Revisions = {revisions}, Staged = {staged}, Kept = {kept}, DryRun = {dry_label} }}");
	Ok(())
}

async fn papierkorb(
	pool: &PgPool,
	storage: &BlobStorage,
	now: DateTime<Utc>,
	dry: bool,
	retention_days: Option<&str>,
) -> Result<(), Error> {
	let days = match retention_days {
		Some(value) => {
			let value = value.trim().to_ascii_lowercase();
			let hex = value
				.strip_prefix('#')
				.or_else(|| value.strip_prefix("0x"))
				.or_else(|| value.strip_prefix("&h"));
			match hex {
				Some(hex) => {
					u32::from_str_radix(hex.strip_prefix('+').unwrap_or(hex), 16).map(|n| n as i32)
				}
				None => value.parse(),
			}
			.map_err(|_| Error::Configuration)?
		}
		None => 7,
	};
	let cutoff = now
		.checked_sub_signed(Duration::days(i64::from(days)))
		.filter(|cutoff| (1..=9999).contains(&cutoff.year()))
		.ok_or(Error::Configuration)?;
	let lieder: Vec<Uuid> = sqlx::query_scalar(r#"SELECT "Id" FROM lieder WHERE "DeletedAt" < $1"#)
		.bind(cutoff)
		.fetch_all(pool)
		.await?;
	let events: Vec<Uuid> = sqlx::query_scalar(r#"SELECT "Id" FROM events WHERE "DeletedAt" < $1"#)
		.bind(cutoff)
		.fetch_all(pool)
		.await?;
	let files: Vec<Uuid> = sqlx::query_scalar(r#"SELECT "Id" FROM dateien WHERE "DeletedAt" < $1"#)
		.bind(cutoff)
		.fetch_all(pool)
		.await?;
	if !dry {
		for id in &files {
			remove_datei(pool, storage, *id, now).await?;
		}
		for id in &lieder {
			remove_owner(pool, storage, *id, false, now).await?;
		}
		for id in &events {
			remove_owner(pool, storage, *id, true, now).await?;
		}
	}
	let (lieder, auftritte, dateien) = (lieder.len(), events.len(), files.len());
	let mode = if dry { " (dry run)" } else { "" };
	let dry_label = if dry { "True" } else { "False" };
	tracing::info!(target: "Archive.Papierkorb", lieder, auftritte, dateien, retention_days=days, dry_run=dry,
		"Papierkorb cleanup{mode}: {lieder} Lieder, {auftritte} Auftritte, {dateien} Dateien past {days} days");
	tracing::info!(target: "Archive.Papierkorb", "Papierkorb cleanup finished: Result {{ Lieder = {lieder}, Auftritte = {auftritte}, Dateien = {dateien}, RetentionDays = {days}, DryRun = {dry_label} }}");
	Ok(())
}
