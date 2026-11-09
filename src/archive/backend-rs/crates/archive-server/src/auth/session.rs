use super::{
	Auth, AuthError,
	security::{digest, opaque, token_digest},
};
use chrono::{DateTime, Duration, Utc};
use serde::Serialize;
use sqlx::{PgConnection, Row};
use uuid::Uuid;

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CurrentSession {
	pub account_id: Uuid,
	pub email: Option<String>,
	pub display_name: Option<String>,
	pub roles: Vec<String>,
	pub verified_at: DateTime<Utc>,
	pub auth_method: String,
	#[serde(skip)]
	pub token_digest: Vec<u8>,
}
impl CurrentSession {
	pub fn is_editor(&self) -> bool {
		self.roles
			.iter()
			.any(|r| r == "Editor" || r == "Administrator")
	}
	pub fn is_administrator(&self) -> bool {
		self.roles.iter().any(|r| r == "Administrator")
	}
	pub fn is_fresh(&self, now: DateTime<Utc>, window: Duration) -> bool {
		self.verified_at + window >= now
	}
}

pub(super) struct SessionLookup {
	pub current: Option<CurrentSession>,
	pub renew: bool,
}

impl Auth {
	/// Current membership/stamp is re-read on every request, on every replica.
	/// Refresh UPDATEs an existing token only: revocation can never be resurrected.
	pub(super) async fn session(&self, token: Option<&str>) -> Result<SessionLookup, AuthError> {
		let Some(token) = token.and_then(token_digest) else {
			return Ok(SessionLookup {
				current: None,
				renew: false,
			});
		};
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		// Lock account first, then take a *new statement snapshot* for session
		// state. A joined SELECT ... FOR UPDATE can otherwise read expiry before
		// waiting for another replica and delete that replica's committed refresh.
		let account:Option<Uuid>=sqlx::query_scalar("SELECT u.\"Id\" FROM \"AspNetUsers\" u WHERE u.\"Id\"=(SELECT user_id FROM archive_sessions WHERE token_digest=$1) FOR UPDATE OF u").bind(&token).fetch_optional(&mut *tx).await?;
		if account.is_none() {
			return Ok(SessionLookup {
				current: None,
				renew: false,
			});
		}
		let row = sqlx::query("SELECT s.*, u.\"Email\", u.\"DisplayName\", u.\"EmailConfirmed\", u.\"LockoutEnabled\", u.\"LockoutEnd\", u.\"SecurityStamp\" FROM archive_sessions s JOIN \"AspNetUsers\" u ON u.\"Id\" = s.user_id WHERE s.token_digest=$1 FOR UPDATE OF s")
			.bind(&token).fetch_optional(&mut *tx).await?;
		let Some(row) = row else {
			return Ok(SessionLookup {
				current: None,
				renew: false,
			});
		};
		let user_id: Uuid = row.try_get("user_id")?;
		let verified_at: DateTime<Utc> = row.try_get("verified_at")?;
		let expiry: DateTime<Utc> = row.try_get("expires_at")?;
		let stamp: Option<String> = row.try_get("security_stamp")?;
		let current_stamp: Option<String> = row.try_get("SecurityStamp")?;
		let lockout: Option<DateTime<Utc>> = row.try_get("LockoutEnd")?;
		let roles = roles(&mut tx, user_id).await?;
		let last_reset: Option<DateTime<Utc>> = sqlx::query_scalar("SELECT max(\"OccurredAt\") FROM member_admin_actions WHERE \"TargetUserId\"=$1 AND \"Action\" IN (1,3,4)").bind(user_id).fetch_one(&mut *tx).await?;
		if expiry <= now
			|| !row.try_get::<bool, _>("EmailConfirmed")?
			|| (row.try_get::<bool, _>("LockoutEnabled")? && lockout.is_some_and(|end| end > now))
			|| roles.is_empty()
			|| stamp != current_stamp
			|| last_reset.is_some_and(|reset| verified_at < reset)
		{
			sqlx::query("DELETE FROM archive_sessions WHERE token_digest=$1")
				.bind(&token)
				.execute(&mut *tx)
				.await?;
			tx.commit().await?;
			return Ok(SessionLookup {
				current: None,
				renew: false,
			});
		}
		let renewed_at: DateTime<Utc> = row.try_get("renewed_at")?;
		let renew = now - renewed_at > expiry - now;
		if renew {
			sqlx::query(
				"UPDATE archive_sessions SET renewed_at=$2, expires_at=$3 WHERE token_digest=$1",
			)
			.bind(&token)
			.bind(now)
			.bind(now + Duration::days(30))
			.execute(&mut *tx)
			.await?;
		}
		let current = CurrentSession {
			account_id: user_id,
			email: row.try_get("Email")?,
			display_name: row
				.try_get::<Option<String>, _>("DisplayName")?
				.or(row.try_get("Email")?),
			roles,
			verified_at,
			auth_method: row.try_get("auth_method")?,
			token_digest: token,
		};
		tx.commit().await?;
		Ok(SessionLookup {
			current: Some(current),
			renew,
		})
	}

	pub(super) async fn issue(
		&self,
		tx: &mut PgConnection,
		user_id: Uuid,
		method: &str,
	) -> Result<String, AuthError> {
		let token = opaque();
		let now = self.clock.now();
		sqlx::query("INSERT INTO archive_sessions(token_digest,user_id,security_stamp,verified_at,auth_method,renewed_at,expires_at) SELECT $1,\"Id\",\"SecurityStamp\",$3,$4,$3,$5 FROM \"AspNetUsers\" WHERE \"Id\"=$2")
			.bind(digest(&token)).bind(user_id).bind(now).bind(method).bind(now+Duration::days(30)).execute(tx).await?;
		Ok(token)
	}

	/// Call inside the same account-locked transaction as deactivation/repair/email change.
	/// The stamp also invalidates sessions issued by the frozen oracle during the port.
	pub async fn revoke_sessions(tx: &mut PgConnection, account_id: Uuid) -> Result<(), AuthError> {
		sqlx::query(
			"UPDATE \"AspNetUsers\" SET \"SecurityStamp\"=$2, \"ConcurrencyStamp\"=$3 WHERE \"Id\"=$1",
		)
		.bind(account_id)
		.bind(opaque())
		.bind(opaque())
		.execute(&mut *tx)
		.await?;
		sqlx::query("DELETE FROM archive_sessions WHERE user_id=$1")
			.bind(account_id)
			.execute(tx)
			.await?;
		Ok(())
	}
}
pub(super) async fn roles(tx: &mut PgConnection, id: Uuid) -> Result<Vec<String>, AuthError> {
	Ok(sqlx::query_scalar("SELECT DISTINCT r.\"Name\" FROM \"AspNetRoles\" r JOIN \"AspNetUserRoles\" ur ON ur.\"RoleId\"=r.\"Id\" WHERE ur.\"UserId\"=$1 AND r.\"Name\" IS NOT NULL ORDER BY r.\"Name\"").bind(id).fetch_all(tx).await?)
}
