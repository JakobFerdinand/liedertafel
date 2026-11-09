use super::{Auth, AuthError, security::opaque};
use serde_json::{Value, json};
use uuid::Uuid;
impl Auth {
	/// Minimum local sign-in fixture, reused by #222's finite seed command later.
	pub async fn seed(&self) -> Result<Value, AuthError> {
		if !self.config.development {
			return Err(AuthError::Configuration);
		}
		let mut tx = self.pool.begin().await?;
		sqlx::query("SELECT pg_advisory_xact_lock(221,1)")
			.execute(&mut *tx)
			.await?;
		let mut accounts = vec![];
		for (email, name, role) in [
			("mitglied@liedertafel.test", "Testmitglied", "Member"),
			("redaktion@liedertafel.test", "Testredaktion", "Editor"),
			(
				"verwaltung@liedertafel.test",
				"Testverwaltung",
				"Administrator",
			),
		] {
			let normalized = email.to_uppercase();
			sqlx::query("INSERT INTO \"AspNetRoles\"(\"Id\",\"Name\",\"NormalizedName\",\"ConcurrencyStamp\") VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING").bind(Uuid::new_v4()).bind(role).bind(role.to_uppercase()).bind(opaque()).execute(&mut *tx).await?;
			sqlx::query("INSERT INTO \"AspNetUsers\"(\"Id\",\"DisplayName\",\"Email\",\"NormalizedEmail\",\"UserName\",\"NormalizedUserName\",\"EmailConfirmed\",\"PhoneNumberConfirmed\",\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\",\"SecurityStamp\",\"ConcurrencyStamp\") VALUES($1,$2,$3,$4,$3,$4,true,false,false,true,0,$5,$6) ON CONFLICT DO NOTHING").bind(Uuid::new_v4()).bind(name).bind(email).bind(&normalized).bind(opaque()).bind(opaque()).execute(&mut *tx).await?;
			let id:Uuid=sqlx::query_scalar("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=true WHERE \"NormalizedEmail\"=$1 RETURNING \"Id\"").bind(&normalized).fetch_one(&mut *tx).await?;
			sqlx::query("INSERT INTO \"AspNetUserRoles\"(\"UserId\",\"RoleId\") SELECT $1,\"Id\" FROM \"AspNetRoles\" WHERE \"NormalizedName\"=$2 ON CONFLICT DO NOTHING").bind(id).bind(role.to_uppercase()).execute(&mut *tx).await?;
			accounts.push(json!({"email":email,"role":role,"accountId":id}));
		}
		tx.commit().await?;
		Ok(json!({"accounts":accounts}))
	}
}
