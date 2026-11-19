use crate::{
	auth::{Clock, SystemClock},
	catalogue::trash::{remove_datei, remove_owner},
	storage::{BlobStorage, Storage, StorageConfig, StorageError},
};
use chrono::{DateTime, Datelike, Duration, Utc};
use futures::TryStreamExt;
use sqlx::PgPool;
use std::{collections::HashSet, sync::Arc};
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

pub async fn run(command: &str, dry: bool, development: bool) -> Result<(), Error> {
	let clock = Arc::new(SystemClock);
	let connection = [
		"ConnectionStrings__archive-migrations",
		"ConnectionStrings__archive-db",
	]
	.into_iter()
	.find_map(|k| std::env::var(k).ok().filter(|v| !v.trim().is_empty()))
	.ok_or(Error::Configuration)?;
	let pool = archive_db::runtime_pool(&connection).map_err(|_| Error::Configuration)?;
	let storage = BlobStorage::new(StorageConfig::from_env(development)?, clock.clone())?;
	let result = match command {
		"--cleanup-uploads" => pending(&pool, &storage, clock.now()).await,
		"--cleanup-dropped-blobs" => dropped(&pool, &storage, dry).await,
		"--cleanup-papierkorb" => papierkorb(&pool, &storage, clock.now(), dry).await,
		_ => Err(Error::Configuration),
	};
	pool.close().await;
	result
}

async fn pending(pool: &PgPool, storage: &BlobStorage, now: DateTime<Utc>) -> Result<(), Error> {
	let grace = match std::env::var("Archive__Assets__ExpiryGrace") {
		Ok(value) => expiry_grace(&value).ok_or(Error::Configuration)?,
		Err(_) => Duration::hours(1),
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
fn expiry_grace(value: &str) -> Option<Duration> {
	fn number(s: &str) -> Option<i64> {
		if s.is_empty() || !s.bytes().all(|c| c.is_ascii_digit()) {
			return None;
		}
		let significant = s.trim_start_matches('0');
		if significant.is_empty() {
			Some(0)
		} else {
			significant.parse().ok()
		}
	}
	fn seconds(s: &str) -> Option<(i64, i64)> {
		let Some((whole, fraction)) = s.split_once('.') else {
			return Some((number(s)?, 0));
		};
		let fraction_number = number(fraction)?;
		if fraction_number > 9_999_999 {
			return None;
		}
		let mut ticks = 0;
		for i in 0..7 {
			ticks = ticks * 10 + i64::from(fraction.as_bytes().get(i).map_or(0, |b| b - b'0'));
		}
		if fraction.as_bytes().get(7).is_some_and(|b| *b >= b'5') {
			ticks += 1;
		}
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
) -> Result<(), Error> {
	let days: i32 = match std::env::var("Archive__Papierkorb__RetentionDays") {
		Ok(value) => value.trim().parse().map_err(|_| Error::Configuration)?,
		Err(_) => 7,
	};
	let cutoff = now
		.checked_sub_signed(Duration::days(i64::from(days)))
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
