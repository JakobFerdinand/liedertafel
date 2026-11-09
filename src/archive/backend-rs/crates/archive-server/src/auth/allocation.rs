//! Small shared budget for CSRF/ceremony allocation, not general HTTP throttling.
use super::{Auth, AuthError};
use axum::http::StatusCode;
use chrono::Duration;
use sqlx::PgConnection;

impl Auth {
	/// Every CSRF/ceremony upsert holds this lock until commit/rollback, across
	/// origins. No account locks are taken while holding it. Deletes can only
	/// release capacity. A rejected transaction leaves cleanup/budgets unchanged.
	pub(super) async fn lock_state_allocations(
		&self,
		tx: &mut PgConnection,
	) -> Result<(), AuthError> {
		sqlx::query("SELECT pg_advisory_xact_lock(221, 3)")
			.execute(&mut *tx)
			.await?;
		self.cleanup(tx).await
	}

	/// Called under lock_state_allocations, BEFORE any new state/budget insert.
	/// Valid CSRF reuse is exempt; a new anonymous challenge always spends budget,
	/// even if its browser already has a ceremony row. Both routes share slots.
	pub(super) async fn reserve_state_allocation(
		&self,
		tx: &mut PgConnection,
		address_digest: &[u8],
		new_row: bool,
		charge_anonymous: bool,
	) -> Result<(), AuthError> {
		if new_row {
			let outstanding: i64 = sqlx::query_scalar(
				"SELECT (SELECT count(*) FROM archive_csrf) + (SELECT count(*) FROM archive_ceremonies)",
			)
			.fetch_one(&mut *tx)
			.await?;
			if outstanding >= self.config.max_outstanding_auth_state {
				return Err(self.allocation_limited());
			}
		}
		if !charge_anonymous {
			return Ok(());
		}
		let now = self.clock.now();
		let current: Option<(i16, i16)> = sqlx::query_as("SELECT slot, allocations FROM archive_auth_allocation_budgets WHERE address_digest=$1 AND expires_at>$2")
			.bind(address_digest).bind(now).fetch_optional(&mut *tx).await?;
		let (slot, count) = if let Some((slot, count)) = current {
			if count >= 30 {
				return Err(self.allocation_limited());
			}
			(slot, count)
		} else {
			// Prefer this address's expired slot; otherwise reuse any free/expired
			// slot. The global lock prevents slot-stealing/UNIQUE races. At most
			// 1024 address records ever exist (also enforced by PK/CHECK in SQL).
			let slot: Option<i16> = sqlx::query_scalar("SELECT slot FROM archive_auth_allocation_budgets WHERE expires_at<=$2 ORDER BY (address_digest=$1) DESC NULLS LAST, slot LIMIT 1")
				.bind(address_digest).bind(now).fetch_optional(&mut *tx).await?;
			(slot.ok_or_else(|| self.allocation_limited())?, 0)
		};
		sqlx::query("UPDATE archive_auth_allocation_budgets SET address_digest=$2, allocations=$3, expires_at=CASE WHEN $3=1 THEN $4 ELSE expires_at END WHERE slot=$1")
			.bind(slot).bind(address_digest).bind(count + 1).bind(now + Duration::minutes(10)).execute(tx).await?;
		Ok(())
	}

	fn allocation_limited(&self) -> AuthError {
		self.failures.add(1, &[]);
		self.allocations_limited.add(1, &[]);
		// Neither address/digest, cookie nor credential becomes a log/tag/label.
		tracing::info!("Authentication state allocation limited");
		AuthError::Problem(StatusCode::TOO_MANY_REQUESTS, super::LIMITED)
	}
}
