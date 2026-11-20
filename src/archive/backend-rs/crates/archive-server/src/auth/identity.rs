//! Canonical Identity writes shared by member administration, seed and operators.
use super::{AuthError, security::opaque};
use chrono::{DateTime, Utc};
use sqlx::PgConnection;
use uuid::Uuid;

pub(super) const ROLES: [&str; 3] = ["Member", "Editor", "Administrator"];

/// Serialize member/role/address creation and last-admin decisions across replicas.
/// Always before account locks. Auth ceremonies only take account locks and never
/// acquire this lock, so sign-in cannot invert the ordering.
pub(super) async fn lock(tx: &mut PgConnection) -> Result<(), AuthError> {
	sqlx::query("SELECT pg_advisory_xact_lock(222,1)")
		.execute(tx)
		.await?;
	Ok(())
}
pub(super) async fn add_role(tx: &mut PgConnection, id: Uuid, role: &str) -> Result<(), AuthError> {
	sqlx::query("INSERT INTO \"AspNetRoles\"(\"Id\",\"Name\",\"NormalizedName\",\"ConcurrencyStamp\") VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING").bind(Uuid::new_v4()).bind(role).bind(role.to_uppercase()).bind(opaque()).execute(&mut *tx).await?;
	sqlx::query("INSERT INTO \"AspNetUserRoles\"(\"UserId\",\"RoleId\") SELECT $1,\"Id\" FROM \"AspNetRoles\" WHERE \"NormalizedName\"=$2 ON CONFLICT DO NOTHING").bind(id).bind(role.to_uppercase()).execute(tx).await?;
	Ok(())
}
pub(super) async fn create_user(
	tx: &mut PgConnection,
	email: &str,
	normalized: &str,
	name: Option<&str>,
	confirmed: bool,
) -> Result<Uuid, AuthError> {
	let id = Uuid::new_v4();
	sqlx::query("INSERT INTO \"AspNetUsers\"(\"Id\",\"DisplayName\",\"Email\",\"NormalizedEmail\",\"UserName\",\"NormalizedUserName\",\"EmailConfirmed\",\"PhoneNumberConfirmed\",\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\",\"SecurityStamp\",\"ConcurrencyStamp\") VALUES($1,$2,$3,$4,$3,$4,$5,false,false,true,0,$6,$7)").bind(id).bind(name).bind(email).bind(normalized).bind(confirmed).bind(opaque()).bind(opaque()).execute(tx).await?;
	Ok(id)
}

pub(super) async fn audit(
	tx: &mut PgConnection,
	target: Uuid,
	actor: Uuid,
	action: i32,
	(old, new): (&[String], &[String]),
	now: DateTime<Utc>,
	note: Option<&str>,
) -> Result<(), AuthError> {
	sqlx::query("INSERT INTO member_admin_actions(\"Id\",\"TargetUserId\",\"ActorAccountId\",\"Action\",\"OldRoles\",\"NewRoles\",\"OccurredAt\",\"Note\") VALUES($1,$2,$3,$4,$5,$6,$7,$8)").bind(Uuid::new_v4()).bind(target).bind(actor).bind(action).bind(old.join(",")).bind(new.join(",")).bind(now).bind(note).execute(tx).await?;
	Ok(())
}

pub(super) async fn invalidate_challenges(
	tx: &mut PgConnection,
	id: Uuid,
	now: DateTime<Utc>,
) -> Result<(), AuthError> {
	sqlx::query("DELETE FROM auth_sign_in_codes WHERE \"UserId\"=$1")
		.bind(id)
		.execute(&mut *tx)
		.await?;
	sqlx::query("UPDATE member_email_changes SET \"ConsumedAt\"=$2 WHERE \"UserId\"=$1 AND \"ConsumedAt\" IS NULL").bind(id).bind(now).execute(&mut *tx).await?;
	sqlx::query("DELETE FROM archive_ceremonies WHERE user_id=$1")
		.bind(id)
		.execute(tx)
		.await?;
	Ok(())
}
