use super::{
	Auth, AuthError,
	mail::code_message,
	security::{code_hash, normalize_email, opaque},
	session::roles,
};
use axum::{
	Json,
	http::StatusCode,
	response::{IntoResponse, Response},
};
use chrono::{DateTime, Duration, Utc};
use rand::Rng;
use serde_json::{Value, json};
use sqlx::{PgConnection, Row};
use subtle::ConstantTimeEq;
use tracing::Instrument;
use uuid::Uuid;

const INVALID: &str = "Der Code ist ungültig oder abgelaufen.";
const LIMITED: &str = "Zu viele Anfragen. Bitte später erneut versuchen.";

impl Auth {
	pub(super) async fn request_code(
		&self,
		body: &Value,
		ip_hash: &str,
	) -> Result<Response, AuthError> {
		let normalized = normalize_email(body["email"].as_str()).ok_or(AuthError::Problem(
			StatusCode::BAD_REQUEST,
			"Die E-Mail-Adresse ist ungültig.",
		))?;
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		// Serializes same-email and same-IP counters across replicas; never raw identifiers in logs.
		lock_abuse(&mut tx, ip_hash, &normalized).await?;
		let email_count: i64 = sqlx::query_scalar("SELECT count(*) FROM auth_request_log WHERE \"NormalizedEmail\"=$1 AND \"Kind\"=0 AND \"OccurredAt\">=$2").bind(&normalized).bind(now-Duration::hours(1)).fetch_one(&mut *tx).await?;
		let ip_count: i64 = sqlx::query_scalar("SELECT count(*) FROM auth_request_log WHERE \"IpHash\"=$1 AND \"Kind\"=0 AND \"OccurredAt\">=$2").bind(ip_hash).bind(now-Duration::hours(1)).fetch_one(&mut *tx).await?;
		let limited = email_count >= self.config.max_email_requests
			|| ip_count >= self.config.max_ip_requests;
		log(&mut tx, &normalized, ip_hash, 0, !limited, now).await?;
		if limited {
			tx.commit().await?;
			self.failures.add(1, &[]);
			tracing::Span::current().record("auth.result", "rate_limited");
			return Err(AuthError::Problem(StatusCode::TOO_MANY_REQUESTS, LIMITED));
		}
		let latest: Option<DateTime<Utc>> = sqlx::query_scalar("SELECT \"LastSentAt\" FROM auth_sign_in_codes WHERE \"NormalizedEmail\"=$1 AND \"ConsumedAt\" IS NULL AND \"ExpiresAt\">$2 ORDER BY \"CreatedAt\" DESC LIMIT 1").bind(&normalized).bind(now).fetch_optional(&mut *tx).await?.flatten();
		let user = sqlx::query("SELECT \"Id\", \"Email\", \"LockoutEnabled\", \"LockoutEnd\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 FOR UPDATE").bind(&normalized).fetch_optional(&mut *tx).await?;
		let mut send = None;
		let suppressed = latest.is_some_and(|sent| sent + self.config.resend_cooldown > now);
		if !suppressed && let Some(user) = user {
			let end: Option<DateTime<Utc>> = user.try_get("LockoutEnd")?;
			if !user.try_get::<bool, _>("LockoutEnabled")? || !end.is_some_and(|end| end > now) {
				let code = format!("{:06}", rand::rng().random_range(0..1_000_000));
				let salt = rand::rng().random::<[u8; 16]>();
				let id = Uuid::new_v4();
				sqlx::query("INSERT INTO auth_sign_in_codes(\"Id\",\"UserId\",\"NormalizedEmail\",\"CodeHash\",\"Salt\",\"CreatedAt\",\"ExpiresAt\",\"LastSentAt\",\"RowVersion\") VALUES($1,$2,$3,$4,$5,$6,$7,$6,0)").bind(id).bind(user.try_get::<Uuid,_>("Id")?).bind(&normalized).bind(code_hash(&code,&salt)).bind(salt.as_slice()).bind(now).bind(now+self.config.code_lifetime).execute(&mut *tx).instrument(tracing::info_span!("archive.auth.challenge")).await?;
				send = Some((
					id,
					code_message(
						user.try_get::<Option<String>, _>("Email")?
							.unwrap_or(normalized.clone()),
						&code,
						self.config.code_lifetime.num_minutes(),
					),
				));
			}
		}
		tx.commit().await?;
		if let Some((id, message)) = send
			&& self
				.mail
				.send(message)
				.instrument(tracing::info_span!(
					"archive.mail.send",
					otel.kind = "client"
				))
				.await
				.is_err()
		{
			// Exact challenge, not the latest (a different replica may already have resent).
			sqlx::query(
				"UPDATE auth_sign_in_codes SET \"ExpiresAt\"=$2 WHERE \"Id\"=$1 AND \"ConsumedAt\" IS NULL",
			)
			.bind(id)
			.bind(now)
			.execute(&self.pool)
			.await?;
			return Err(AuthError::Mail);
		}
		self.requests.add(1, &[]);
		tracing::Span::current().record(
			"auth.result",
			if suppressed {
				"resent_suppressed"
			} else {
				"requested"
			},
		);
		tracing::info!("Sign-in code requested");
		Ok((StatusCode::ACCEPTED,Json(json!({"message":"Falls die Adresse eingeladen ist, wurde ein Code gesendet. Bitte das Postfach prüfen."}))).into_response())
	}

	pub(super) async fn verify_code(
		&self,
		body: &Value,
		ip_hash: &str,
	) -> Result<(Value, String), AuthError> {
		let invalid = || AuthError::Problem(StatusCode::BAD_REQUEST, INVALID);
		let normalized = normalize_email(body["email"].as_str()).ok_or_else(invalid)?;
		let code: String = body["code"]
			.as_str()
			.unwrap_or_default()
			.chars()
			.filter(char::is_ascii_digit)
			.collect();
		if code.len() != 6 {
			return Err(invalid());
		}
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		lock_abuse(&mut tx, ip_hash, &normalized).await?;
		let failures:i64 = sqlx::query_scalar("SELECT count(*) FROM auth_request_log WHERE \"NormalizedEmail\"=$1 AND \"Kind\"=2 AND \"OccurredAt\">=$2").bind(&normalized).bind(now-Duration::minutes(10)).fetch_one(&mut *tx).await?;
		if failures >= self.config.max_verify_failures {
			self.failures.add(1, &[]);
			tracing::Span::current().record("auth.result", "rate_limited");
			return Err(AuthError::Problem(StatusCode::TOO_MANY_REQUESTS, LIMITED));
		}
		let user =
			sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 FOR UPDATE")
				.bind(&normalized)
				.fetch_optional(&mut *tx)
				.await?;
		let mut valid = false;
		let mut account = None;
		if let Some(user) = user {
			let id: Uuid = user.try_get("Id")?;
			let lockout: Option<DateTime<Utc>> = user.try_get("LockoutEnd")?;
			if !user.try_get::<bool, _>("LockoutEnabled")? || !lockout.is_some_and(|end| end > now)
			{
				let challenge = sqlx::query("SELECT * FROM auth_sign_in_codes WHERE \"UserId\"=$1 AND \"ConsumedAt\" IS NULL AND \"ExpiresAt\">$2 ORDER BY \"CreatedAt\" DESC LIMIT 1 FOR UPDATE").bind(id).bind(now).fetch_optional(&mut *tx).await?;
				if let Some(challenge) = challenge
					&& challenge.try_get::<i32, _>("AttemptCount")? < self.config.max_code_attempts
				{
					let hash = code_hash(&code, &challenge.try_get::<Vec<u8>, _>("Salt")?);
					valid = bool::from(hash.ct_eq(&challenge.try_get::<Vec<u8>, _>("CodeHash")?));
					sqlx::query("UPDATE auth_sign_in_codes SET \"AttemptCount\"=\"AttemptCount\"+$2, \"ConsumedAt\"=$3, \"RowVersion\"=\"RowVersion\"+1 WHERE \"Id\"=$1").bind(challenge.try_get::<Uuid,_>("Id")?).bind(if valid {0}else{1}).bind(valid.then_some(now)).execute(&mut *tx).await?;
				}
				if valid {
					sqlx::query("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=true, \"AccessFailedCount\"=0, \"ConcurrencyStamp\"=$2 WHERE \"Id\"=$1").bind(id).bind(opaque()).execute(&mut *tx).await?;
					let roles = roles(&mut tx, id).await?;
					valid = !roles.is_empty();
					if valid {
						sqlx::query("UPDATE member_invitations SET \"AcceptedAt\"=$2 WHERE \"UserId\"=$1 AND \"AcceptedAt\" IS NULL").bind(id).bind(now).execute(&mut *tx).await?;
						account = Some((
							id,
							json!({"message":"Anmeldung erfolgreich.", "accountId":id, "displayName":user.try_get::<Option<String>,_>("DisplayName")?.or(user.try_get("Email")?),"roles":roles}),
						));
					}
				} else {
					sqlx::query("UPDATE \"AspNetUsers\" SET \"AccessFailedCount\"=CASE WHEN \"AccessFailedCount\">=19 THEN 0 ELSE \"AccessFailedCount\"+1 END, \"LockoutEnd\"=CASE WHEN \"AccessFailedCount\">=19 THEN $2 ELSE \"LockoutEnd\" END, \"ConcurrencyStamp\"=$3 WHERE \"Id\"=$1").bind(id).bind(now+Duration::minutes(15)).bind(opaque()).execute(&mut *tx).await?;
				}
			}
		}
		log(
			&mut tx,
			&normalized,
			ip_hash,
			if valid { 1 } else { 2 },
			valid,
			now,
		)
		.await?;
		let result = if let Some((id, body)) = account {
			Some((body, self.issue(&mut tx, id, "email_code").await?))
		} else {
			None
		};
		tx.commit().await?;
		if let Some(result) = result {
			self.verified.add(1, &[]);
			tracing::Span::current().record("auth.result", "verified");
			tracing::info!("Sign-in code verified");
			Ok(result)
		} else {
			self.failures.add(1, &[]);
			tracing::Span::current().record("auth.result", "failed");
			tracing::info!("Sign-in code verification failed");
			Err(invalid())
		}
	}
}
async fn lock_abuse(tx: &mut PgConnection, ip: &str, email: &str) -> Result<(), AuthError> {
	// Fixed ordering: IP then email on request AND verify avoids lock-order inversions.
	sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,221))")
		.bind(format!("ip:{ip}"))
		.execute(&mut *tx)
		.await?;
	sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,221))")
		.bind(format!("email:{email}"))
		.execute(tx)
		.await?;
	Ok(())
}
async fn log(
	tx: &mut PgConnection,
	email: &str,
	ip: &str,
	kind: i32,
	succeeded: bool,
	now: DateTime<Utc>,
) -> Result<(), AuthError> {
	sqlx::query("INSERT INTO auth_request_log(\"NormalizedEmail\",\"IpHash\",\"Kind\",\"Succeeded\",\"OccurredAt\") VALUES($1,$2,$3,$4,$5)").bind(email).bind(ip).bind(kind).bind(succeeded).bind(now).execute(tx).await?;
	Ok(())
}
