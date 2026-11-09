//! Finite operator commands. No public recovery endpoint and no password creation.
use super::{Auth, AuthError, identity, normalize_email, session::roles};
use sqlx::{PgPool, Row};
use uuid::Uuid;

#[derive(Debug, thiserror::Error)]
pub enum OperatorError {
	#[error("{0}")]
	Auth(#[from] AuthError),
	#[error("{0}")]
	DatabaseConfiguration(#[from] archive_db::MigrationError),
	#[error("{0}")]
	Usage(&'static str),
}
fn option<'a>(args: &'a [String], name: &str) -> Option<&'a str> {
	args.windows(2)
		.find(|pair| pair[0].eq_ignore_ascii_case(name))
		.map(|pair| pair[1].as_str())
}
fn authorize(development: bool, args: &[String], repair: bool) -> Result<(), OperatorError> {
	use subtle::ConstantTimeEq;
	if !development {
		let expected = std::env::var("Archive__OperatorToken").unwrap_or_default();
		let provided = option(args, "--operator-token").unwrap_or_default();
		if expected.trim().is_empty()
			|| provided.trim().is_empty()
			|| !bool::from(expected.as_bytes().ct_eq(provided.as_bytes()))
		{
			return Err(OperatorError::Usage(if repair {
				"Repair outside Development requires Archive:OperatorToken and --operator-token."
			} else {
				"Bootstrap outside Development requires Archive:OperatorToken and --operator-token."
			}));
		}
	}
	Ok(())
}
pub async fn run(command: &str, args: &[String], development: bool) -> Result<(), OperatorError> {
	if command == "--send-test-mail" {
		let azure = std::env::var("Mail__Provider").is_ok_and(|s| s.eq_ignore_ascii_case("Azure"));
		validate_test_mail(development, args, azure)?;
		let mail = super::mail::from_env(development)?;
		return send_test_mail(development, args, azure, mail.as_ref()).await;
	}
	if command == "--seed-dev-auth" && !development {
		return Err(OperatorError::Usage(
			"Local service commands require Development.",
		));
	}
	if command != "--seed-dev-auth" {
		let email = option(args, "--email");
		if normalize_email(email).is_none() {
			return Err(OperatorError::Usage(if command == "--repair-admin" {
				"Repair requires --email <address>."
			} else {
				"Bootstrap requires --email <address>."
			}));
		}
		authorize(development, args, command == "--repair-admin")?;
	}
	let connection = [
		"ConnectionStrings__archive-migrations",
		"ConnectionStrings__archive-db",
	]
	.into_iter()
	.find_map(|key| std::env::var(key).ok().filter(|v| !v.trim().is_empty()))
	.ok_or(OperatorError::Usage(
		"Operator commands require a database connection.",
	))?;
	let pool = archive_db::runtime_pool(&connection)?;
	let result = match command {
		"--seed-dev-auth" => seed(&pool).await.map(|_| ()),
		"--repair-admin" => repair(&pool, args).await,
		_ => bootstrap(&pool, args).await,
	};
	pool.close().await;
	result
}

fn validate_test_mail(
	development: bool,
	args: &[String],
	azure: bool,
) -> Result<(), OperatorError> {
	if !development {
		return Err(OperatorError::Usage(
			"Local service commands require Development.",
		));
	}
	if !azure {
		return Err(OperatorError::Usage(
			"Der Azure-Versandtest erfordert Mail:Provider=Azure.",
		));
	}
	if args.len() != 1 || normalize_email(args.first().map(String::as_str)).is_none() {
		return Err(OperatorError::Usage(
			"Verwendung: --send-test-mail <empfaenger-adresse>.",
		));
	}
	Ok(())
}
/// Process-command delivery seam; callers can substitute only the mail boundary.
pub async fn send_test_mail(
	development: bool,
	args: &[String],
	azure: bool,
	mail: &dyn super::Mail,
) -> Result<(), OperatorError> {
	use tracing::Instrument;
	validate_test_mail(development, args, azure)?;
	mail.send(super::mail::test_message(args[0].clone()))
		.instrument(tracing::info_span!(
			"archive.mail.test.send",
			otel.kind = "client"
		))
		.await
		.map_err(|_| AuthError::Mail)?;
	tracing::info!("Azure test mail accepted for delivery");
	Ok(())
}

async fn repair(pool: &PgPool, args: &[String]) -> Result<(), OperatorError> {
	let email = option(args, "--email").unwrap_or_default().trim();
	let normalized = normalize_email(Some(email))
		.ok_or(OperatorError::Usage("Repair requires --email <address>."))?;
	let account = option(args, "--accountId")
		.or_else(|| option(args, "--account-id"))
		.filter(|s| !s.trim().is_empty());
	let target = account
		.map(|s| {
			s.parse::<Uuid>()
				.ok()
				.filter(|id| !id.is_nil())
				.ok_or(OperatorError::Usage(
					"Repair requires --accountId <guid> when moving an address.",
				))
		})
		.transpose()?;
	let name = option(args, "--name")
		.map(str::trim)
		.filter(|s| !s.is_empty());
	let note = format!(
		"Betreiber-Reparatur durch {}",
		option(args, "--operator").unwrap_or("Betreiber")
	);
	if note.encode_utf16().count() > 500 {
		return Err(OperatorError::Usage("Operator note is too long."));
	}
	let now = chrono::Utc::now();
	let mut tx = pool.begin().await.map_err(AuthError::from)?;
	identity::lock(&mut tx).await?;
	let existing = if let Some(id) = target {
		sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE")
			.bind(id)
			.fetch_optional(&mut *tx)
			.await
			.map_err(AuthError::from)?
			.ok_or(OperatorError::Usage(
				"Repair found no account for --accountId.",
			))
			.map(Some)?
	} else {
		sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 FOR UPDATE")
			.bind(&normalized)
			.fetch_optional(&mut *tx)
			.await
			.map_err(AuthError::from)?
	};
	let (id, old, new_user) = if let Some(user) = existing {
		let id: Uuid = user.try_get("Id").map_err(AuthError::from)?;
		let collision: bool = sqlx::query_scalar(
			"SELECT EXISTS(SELECT FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 AND \"Id\"<>$2)",
		)
		.bind(&normalized)
		.bind(id)
		.fetch_one(&mut *tx)
		.await
		.map_err(AuthError::from)?;
		if collision {
			return Err(OperatorError::Usage(
				"Repair address already belongs to another account.",
			));
		}
		let old = roles(&mut tx, id).await?;
		let confirmed = user
			.try_get::<bool, _>("EmailConfirmed")
			.map_err(AuthError::from)?;
		let locked = user
			.try_get::<bool, _>("LockoutEnabled")
			.map_err(AuthError::from)?
			&& user
				.try_get::<Option<chrono::DateTime<chrono::Utc>>, _>("LockoutEnd")
				.map_err(AuthError::from)?
				.is_some_and(|end| end > now);
		if target.is_none() && confirmed && !locked && old.iter().any(|r| r == "Administrator") {
			tx.commit().await.map_err(AuthError::from)?;
			tracing::info!(target_id=%id,"Repair found active administrator; no change");
			return Ok(());
		}
		if target.is_some() {
			sqlx::query("UPDATE \"AspNetUsers\" SET \"Email\"=$2,\"UserName\"=$2,\"NormalizedEmail\"=$3,\"NormalizedUserName\"=$3,\"EmailConfirmed\"=true,\"LockoutEnd\"=NULL,\"AccessFailedCount\"=0 WHERE \"Id\"=$1").bind(id).bind(email).bind(&normalized).execute(&mut *tx).await.map_err(AuthError::from)?;
		} else {
			// EnsureAdministrator does not move/recase an existing address. A
			// promotion alone also leaves expired lockout/failure state untouched.
			sqlx::query("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=true,\"LockoutEnd\"=CASE WHEN $2 THEN NULL ELSE \"LockoutEnd\" END,\"AccessFailedCount\"=CASE WHEN $3 THEN 0 ELSE \"AccessFailedCount\" END WHERE \"Id\"=$1").bind(id).bind(locked).bind(!confirmed || locked).execute(&mut *tx).await.map_err(AuthError::from)?;
		}
		(id, old, false)
	} else {
		(
			identity::create_user(&mut tx, email, &normalized, name, true).await?,
			vec![],
			true,
		)
	};
	identity::add_role(&mut tx, id, "Administrator").await?;
	if new_user {
		sqlx::query("INSERT INTO member_invitations(\"Id\",\"UserId\",\"NormalizedEmail\",\"Role\",\"DisplayName\",\"InvitedAt\",\"AcceptedAt\",\"MailStatus\") VALUES($1,$2,$3,'Administrator',$4,$5,$5,0)").bind(Uuid::new_v4()).bind(id).bind(&normalized).bind(name).bind(now).execute(&mut *tx).await.map_err(AuthError::from)?;
	} else {
		identity::invalidate_challenges(&mut tx, id, now).await?;
		sqlx::query("DELETE FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1")
			.bind(id)
			.execute(&mut *tx)
			.await
			.map_err(AuthError::from)?;
		Auth::revoke_sessions(&mut tx, id).await?;
		if target.is_some() {
			sqlx::query("UPDATE member_invitations SET \"NormalizedEmail\"=$2 WHERE \"UserId\"=$1")
				.bind(id)
				.bind(&normalized)
				.execute(&mut *tx)
				.await
				.map_err(AuthError::from)?;
		}
	}
	let new = roles(&mut tx, id).await?;
	identity::audit(&mut tx, id, Uuid::nil(), 4, (&old, &new), now, Some(&note)).await?;
	tx.commit().await.map_err(AuthError::from)?;
	tracing::info!(target_id=%id,"Repair ensured administrator; fresh sign-in required");
	Ok(())
}
async fn bootstrap(pool: &PgPool, args: &[String]) -> Result<(), OperatorError> {
	let email = option(args, "--email").unwrap_or_default().trim();
	let normalized = normalize_email(Some(email)).ok_or(OperatorError::Usage(
		"Bootstrap requires --email <address>.",
	))?;
	let name = option(args, "--name")
		.map(str::trim)
		.filter(|s| !s.is_empty());
	let mut tx = pool.begin().await.map_err(AuthError::from)?;
	identity::lock(&mut tx).await?;
	let exists: bool = sqlx::query_scalar("SELECT EXISTS(SELECT FROM \"AspNetUsers\")")
		.fetch_one(&mut *tx)
		.await
		.map_err(AuthError::from)?;
	if exists {
		return Err(OperatorError::Usage(
			"Archive already has accounts; use member administration instead of bootstrap.",
		));
	}
	let id = identity::create_user(&mut tx, email, &normalized, name, true).await?;
	identity::add_role(&mut tx, id, "Administrator").await?;
	tx.commit().await.map_err(AuthError::from)?;
	tracing::info!("Bootstrap administrator ensured");
	Ok(())
}

pub(super) async fn seed(pool: &PgPool) -> Result<serde_json::Value, OperatorError> {
	let mut tx = pool.begin().await.map_err(AuthError::from)?;
	identity::lock(&mut tx).await?;
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
		let normalized = normalize_email(Some(email)).ok_or(AuthError::Configuration)?;
		let existing: Option<(uuid::Uuid, Option<String>)> = sqlx::query_as(
			"SELECT \"Id\",\"Email\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 FOR UPDATE",
		)
		.bind(&normalized)
		.fetch_optional(&mut *tx)
		.await
		.map_err(AuthError::from)?;
		let (id, email) = if let Some((id, email)) = existing {
			sqlx::query(
				"UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=true,\"ConcurrencyStamp\"=$2 WHERE \"Id\"=$1 AND NOT \"EmailConfirmed\"",
			)
			.bind(id)
			.bind(super::security::opaque())
			.execute(&mut *tx)
			.await
			.map_err(AuthError::from)?;
			(id, email.unwrap_or_default())
		} else {
			(
				identity::create_user(&mut tx, email, &normalized, Some(name), true).await?,
				email.to_owned(),
			)
		};
		identity::add_role(&mut tx, id, role).await?;
		accounts.push(serde_json::json!({"email":email,"role":role,"accountId":id}));
	}
	tx.commit().await.map_err(AuthError::from)?;
	tracing::info!("Development test accounts ensured (3)");
	Ok(serde_json::json!({"accounts":accounts}))
}
