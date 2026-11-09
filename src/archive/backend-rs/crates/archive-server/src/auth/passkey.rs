//! The only expert-core adapter. Upstream owns ALL ceremony/signature validation.
//! Application owns old Guid-text handle resolution, Identity JSON, one-shot state,
//! current membership and atomic counter persistence. See auth-parity.md.
use super::{
	Auth, AuthError, CurrentSession, NOT_FRESH, cookie, require_member,
	security::{digest, opaque, token_digest},
	session::roles,
	set_cookie,
};
use axum::{
	Json,
	http::{HeaderMap, StatusCode},
	response::{IntoResponse, Response},
};
use base64::{
	Engine,
	engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD},
};
use chrono::{DateTime, Duration, Utc};
use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use sqlx::{PgConnection, Row};
use uuid::Uuid;
use webauthn_rs_core::{WebauthnCore, proto::*};

fn invalid(operation: &str) -> AuthError {
	AuthError::Problem(
		StatusCode::BAD_REQUEST,
		if operation == "register" {
			"Die Passkey-Registrierung ist ungültig."
		} else {
			"Die Passkey-Anmeldung ist ungültig."
		},
	)
}
fn codec_error() -> AuthError {
	invalid("login")
}

impl Auth {
	fn core(&self, client_data: Option<&[u8]>, operation: &str) -> Result<WebauthnCore, AuthError> {
		let mut origins = self.config.origins.clone();
		// Dev-only loopback policy from the oracle. The ceremony client-data origin
		// selects one exact loopback origin, never a request Host/Origin header.
		if self.config.development && origins.is_empty() {
			if let Some(data) = client_data {
				let data: Value = serde_json::from_slice(data).map_err(|_| invalid(operation))?;
				let url =
					url::Url::parse(data["origin"].as_str().ok_or_else(|| invalid(operation))?)
						.map_err(|_| invalid(operation))?;
				if !matches!(
					url.host_str(),
					Some("localhost" | "127.0.0.1" | "[::1]" | "::1")
				) || !matches!(url.scheme(), "http" | "https")
					|| url.path() != "/"
					|| url.query().is_some()
					|| url.fragment().is_some()
					|| !url.username().is_empty()
					|| url.password().is_some()
				{
					return Err(invalid(operation));
				}
				origins.push(url);
			} else {
				origins.push(
					url::Url::parse("http://localhost").map_err(|_| AuthError::Configuration)?,
				);
			}
		}
		// Defaults retain strict counter validation, no cross-origin/subdomain/port
		// relaxation, no backup-eligibility upgrade, and no invented CA trust.
		Ok(WebauthnCore::new_unsafe_experts_only(
			"Liedertafel Archiv",
			&self.config.rp_id,
			origins,
			std::time::Duration::from_secs(300),
			Some(false),
			Some(false),
		))
	}

	pub(super) async fn passkey_options(
		&self,
		headers: &HeaderMap,
		current: Option<&CurrentSession>,
		operation: &str,
	) -> Result<Response, AuthError> {
		let core = self.core(None, operation)?;
		let (options, state) = if operation == "register" {
			let current = require_member(current)?;
			self.fresh(current)?;
			let existing: Vec<Vec<u8>> = sqlx::query_scalar(
				"SELECT \"CredentialId\" FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1",
			)
			.bind(current.account_id)
			.fetch_all(&self.pool)
			.await?;
			if existing.len() as i64 >= self.config.max_passkeys {
				return Err(AuthError::Problem(
					StatusCode::CONFLICT,
					"Maximale Anzahl an Passkeys erreicht.",
				));
			}
			let handle = current.account_id.to_string();
			let name = current.email.as_deref().unwrap_or(&handle);
			let display = current.display_name.as_deref().unwrap_or(name);
			let builder = core
				.new_challenge_register_builder(handle.as_bytes(), name, display)
				.map_err(|_| invalid(operation))?
				.user_verification_policy(UserVerificationPolicy::Required)
				.require_resident_key(true)
				.credential_algorithms(vec![COSEAlgorithm::ES256])
				.exclude_credentials(Some(existing.into_iter().map(Into::into).collect()));
			let (options, state) = core
				.generate_challenge_register(builder)
				.map_err(|_| invalid(operation))?;
			(
				json!({"creationOptions":serde_json::to_string(&options.public_key).map_err(|_|AuthError::Database)?}),
				serde_json::to_value(state).map_err(|_| AuthError::Database)?,
			)
		} else {
			let builder = core
				.new_challenge_authenticate_builder(vec![], Some(UserVerificationPolicy::Required))
				.map_err(|_| invalid(operation))?
				.allow_backup_eligible_upgrade(false);
			let (options, state) = core
				.generate_challenge_authenticate(builder)
				.map_err(|_| invalid(operation))?;
			(
				json!({"requestOptions":serde_json::to_string(&options.public_key).map_err(|_|AuthError::Database)?}),
				serde_json::to_value(state).map_err(|_| AuthError::Database)?,
			)
		};
		let browser = cookie(headers, "archive.ceremony")
			.filter(|v| token_digest(v).is_some())
			.map(str::to_owned)
			.unwrap_or_else(opaque);
		let mut tx = self.pool.begin().await?;
		// Cleanup is bounded by expiry indexes; no in-memory replica-local state.
		self.cleanup(&mut tx).await?;
		let registration = current.filter(|_| operation == "register");
		sqlx::query("INSERT INTO archive_ceremonies(browser_digest,operation,session_digest,user_id,state,expires_at) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(browser_digest,operation) DO UPDATE SET session_digest=excluded.session_digest,user_id=excluded.user_id,state=excluded.state,expires_at=excluded.expires_at")
			.bind(digest(&browser)).bind(operation).bind(registration.map(|s|s.token_digest.as_slice())).bind(registration.map(|s|s.account_id)).bind(state).bind(self.clock.now()+Duration::minutes(5)).execute(&mut *tx).await?;
		tx.commit().await?;
		let mut response = Json(options).into_response();
		set_cookie(
			&mut response,
			"archive.ceremony",
			&browser,
			self.secure(headers),
			Some(Duration::minutes(5)),
		);
		Ok(response)
	}
	fn fresh(&self, current: &CurrentSession) -> Result<(), AuthError> {
		if !current.is_fresh(self.clock.now(), self.config.fresh_window) {
			return Err(AuthError::Problem(StatusCode::FORBIDDEN, NOT_FRESH));
		}
		Ok(())
	}

	/// Claim commits before verification. Malformed, expired, failed, cancelled or
	/// replayed responses never leave a usable challenge. Account/session-bound.
	async fn consume<T: DeserializeOwned>(
		&self,
		headers: &HeaderMap,
		current: Option<&CurrentSession>,
		operation: &str,
	) -> Result<T, AuthError> {
		let browser = cookie(headers, "archive.ceremony")
			.and_then(token_digest)
			.ok_or_else(|| invalid(operation))?;
		let registration = current.filter(|_| operation == "register");
		let state:Option<Value>=sqlx::query_scalar("DELETE FROM archive_ceremonies WHERE browser_digest=$1 AND operation=$2 AND expires_at>$3 AND session_digest IS NOT DISTINCT FROM $4 AND user_id IS NOT DISTINCT FROM $5 RETURNING state")
			.bind(browser).bind(operation).bind(self.clock.now()).bind(registration.map(|s|s.token_digest.as_slice())).bind(registration.map(|s|s.account_id)).fetch_optional(&self.pool).await?;
		serde_json::from_value(state.ok_or_else(|| invalid(operation))?)
			.map_err(|_| invalid(operation))
	}

	pub(super) async fn verify_passkey(
		&self,
		headers: &HeaderMap,
		current: Option<&CurrentSession>,
		body: &Value,
		operation: &str,
	) -> Result<Response, AuthError> {
		if operation == "register" {
			let user = require_member(current)?;
			self.fresh(user)?;
			let count: i64 = sqlx::query_scalar(
				"SELECT count(*) FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1",
			)
			.bind(user.account_id)
			.fetch_one(&self.pool)
			.await?;
			if count >= self.config.max_passkeys {
				return Err(AuthError::Problem(
					StatusCode::CONFLICT,
					"Maximale Anzahl an Passkeys erreicht.",
				));
			}
		}
		let raw = body["credential"]
			.as_str()
			.filter(|s| s.len() <= 60_000)
			.ok_or_else(|| invalid(operation))?;
		if operation == "register" {
			let state: RegistrationState = self.consume(headers, current, operation).await?;
			let reg: RegisterPublicKeyCredential =
				serde_json::from_str(raw).map_err(|_| invalid(operation))?;
			let name = body["name"].as_str().unwrap_or_default().trim();
			if name.encode_utf16().count() > self.config.max_passkey_name {
				return Err(AuthError::Problem(
					StatusCode::BAD_REQUEST,
					"Der Name des Passkeys ist zu lang.",
				));
			}
			let credential = self
				.core(Some(&reg.response.client_data_json), operation)?
				.register_credential(&reg, &state, None)
				.map_err(|_| invalid(operation))?;
			if credential.cred_id.is_empty()
				|| credential.cred_id.len() > 1024
				|| credential.cred_id != reg.raw_id
				|| reg.type_ != "public-key"
				|| URL_SAFE_NO_PAD.decode(&reg.id).ok().as_deref() != Some(reg.raw_id.as_slice())
			{
				return Err(invalid(operation));
			}
			let current = require_member(current)?;
			let mut tx = self.pool.begin().await?;
			self.lock_active(&mut tx, current.account_id)
				.await?
				.ok_or(AuthError::Problem(StatusCode::FORBIDDEN, NOT_FRESH))?;
			self.fresh(current)?;
			// Revalidate session while holding account lock, so logout/revocation wins.
			self.check_session(&mut tx, current).await?;
			let count: i64 = sqlx::query_scalar(
				"SELECT count(*) FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1",
			)
			.bind(current.account_id)
			.fetch_one(&mut *tx)
			.await?;
			if count >= self.config.max_passkeys {
				return Err(AuthError::Problem(
					StatusCode::CONFLICT,
					"Maximale Anzahl an Passkeys erreicht.",
				));
			}
			let data = identity_data(&credential, &reg, name, self.clock.now())?;
			let written=sqlx::query("INSERT INTO \"AspNetUserPasskeys\"(\"CredentialId\",\"UserId\",\"Data\") VALUES($1,$2,$3) ON CONFLICT DO NOTHING").bind(credential.cred_id.as_slice()).bind(current.account_id).bind(data).execute(&mut *tx).await?;
			if written.rows_affected() != 1 {
				return Err(invalid(operation));
			}
			audit(&mut tx, current.account_id, 5, None, self.clock.now()).await?;
			tx.commit().await?;
			Ok(Json(json!({"registered":true})).into_response())
		} else {
			let mut state: AuthenticationState = self.consume(headers, current, operation).await?;
			let rsp: PublicKeyCredential =
				serde_json::from_str(raw).map_err(|_| invalid(operation))?;
			if rsp.raw_id.is_empty()
				|| rsp.raw_id.len() > 1024
				|| rsp.type_ != "public-key"
				|| URL_SAFE_NO_PAD.decode(&rsp.id).ok().as_deref() != Some(rsp.raw_id.as_slice())
			{
				return Err(invalid(operation));
			}
			let mut tx = self.pool.begin().await?;
			// Lookup by credential, NOT by the unverified handle. Account locks are
			// taken before passkey locks throughout the module to avoid inversion.
			let id: Option<Uuid> = sqlx::query_scalar(
				"SELECT \"UserId\" FROM \"AspNetUserPasskeys\" WHERE \"CredentialId\"=$1",
			)
			.bind(rsp.raw_id.as_slice())
			.fetch_optional(&mut *tx)
			.await?;
			let id = id.ok_or_else(|| invalid(operation))?;
			let user = self
				.lock_active(&mut tx, id)
				.await?
				.ok_or_else(|| invalid(operation))?;
			if rsp.get_user_unique_id() != Some(id.to_string().as_bytes()) {
				return Err(invalid(operation));
			}
			let mut data:Value=sqlx::query_scalar("SELECT \"Data\" FROM \"AspNetUserPasskeys\" WHERE \"CredentialId\"=$1 AND \"UserId\"=$2 FOR UPDATE").bind(rsp.raw_id.as_slice()).bind(id).fetch_optional(&mut *tx).await?.ok_or_else(||invalid(operation))?;
			let credential = import_identity(&rsp.raw_id, &data)?;
			// Upstream's supported resident-login hook: only the credential resolved
			// from Identity and bound to its Guid-text owner above enters the state.
			// The server-generated challenge/UV/strict-BE policy remains untouched.
			state.set_allowed_credentials(vec![credential]);
			let result = self
				.core(Some(&rsp.response.client_data_json), operation)?
				.authenticate_credential(&rsp, &state)
				.map_err(|_| invalid(operation))?;
			// Required UV, strict counter and unchanged BE are enforced upstream;
			// persist only verified counter/backup changes, leaving all old JSON intact.
			data["SignCount"] = json!(result.counter());
			data["IsBackedUp"] = json!(result.backup_state());
			data["IsBackupEligible"] = json!(result.backup_eligible());
			sqlx::query("UPDATE \"AspNetUserPasskeys\" SET \"Data\"=$2 WHERE \"CredentialId\"=$1")
				.bind(rsp.raw_id.as_slice())
				.bind(data)
				.execute(&mut *tx)
				.await?;
			let roles = roles(&mut tx, id).await?;
			let token = self.issue(&mut tx, id, "passkey").await?;
			tx.commit().await?;
			self.verified.add(1, &[]);
			tracing::info!("Passkey login verified");
			let mut response=Json(json!({"message":"Anmeldung erfolgreich.","accountId":id,"displayName":user.try_get::<Option<String>,_>("DisplayName")?.or(user.try_get("Email")?),"roles":roles})).into_response();
			set_cookie(
				&mut response,
				"archive.auth",
				&token,
				self.secure(headers),
				Some(Duration::days(30)),
			);
			Ok(response)
		}
	}
	async fn check_session(
		&self,
		tx: &mut PgConnection,
		current: &CurrentSession,
	) -> Result<(), AuthError> {
		let exists:bool=sqlx::query_scalar("SELECT EXISTS(SELECT FROM archive_sessions s JOIN \"AspNetUsers\" u ON u.\"Id\"=s.user_id WHERE s.token_digest=$1 AND s.user_id=$2 AND s.security_stamp IS NOT DISTINCT FROM u.\"SecurityStamp\" AND s.expires_at>$3)").bind(&current.token_digest).bind(current.account_id).bind(self.clock.now()).fetch_one(tx).await?;
		if !exists {
			return Err(AuthError::Problem(
				StatusCode::UNAUTHORIZED,
				super::UNAUTHORIZED,
			));
		}
		Ok(())
	}
	async fn lock_active(
		&self,
		tx: &mut PgConnection,
		id: Uuid,
	) -> Result<Option<sqlx::postgres::PgRow>, AuthError> {
		let user = sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE")
			.bind(id)
			.fetch_optional(&mut *tx)
			.await?;
		let Some(user) = user else {
			return Ok(None);
		};
		let lockout: Option<DateTime<Utc>> = user.try_get("LockoutEnd")?;
		if !user.try_get::<bool, _>("EmailConfirmed")?
			|| (user.try_get::<bool, _>("LockoutEnabled")?
				&& lockout.is_some_and(|end| end > self.clock.now()))
			|| roles(tx, id).await?.is_empty()
		{
			return Ok(None);
		}
		Ok(Some(user))
	}
	pub(super) async fn list_passkeys(
		&self,
		current: Option<&CurrentSession>,
	) -> Result<Response, AuthError> {
		let user = require_member(current)?;
		let rows=sqlx::query("SELECT \"CredentialId\",\"Data\" FROM \"AspNetUserPasskeys\" WHERE \"UserId\"=$1 ORDER BY \"CredentialId\"").bind(user.account_id).fetch_all(&self.pool).await?;
		let mut keys = vec![];
		for row in rows {
			let data: Value = row.try_get("Data")?;
			keys.push(json!({"credentialId":STANDARD.encode(row.try_get::<Vec<u8>,_>("CredentialId")?),"name":data["Name"],"createdAt":data["CreatedAt"]}));
		}
		Ok(Json(json!({"passkeys":keys})).into_response())
	}
	pub(super) async fn manage_passkey(
		&self,
		current: Option<&CurrentSession>,
		body: &Value,
		rename: bool,
	) -> Result<Response, AuthError> {
		let user = require_member(current)?;
		let id = body["credentialId"]
			.as_str()
			.filter(|s| s.len() <= 2048)
			.and_then(|s| {
				STANDARD
					.decode(
						s.chars()
							.filter(|c| !matches!(c, ' ' | '\t' | '\r' | '\n'))
							.collect::<String>(),
					)
					.ok()
			})
			.filter(|id| !id.is_empty() && id.len() <= 1024)
			.ok_or(AuthError::Problem(
				StatusCode::BAD_REQUEST,
				"Der Passkey wurde nicht gefunden.",
			))?;
		let name = body["name"].as_str().unwrap_or_default().trim();
		if rename && (name.is_empty() || name.encode_utf16().count() > self.config.max_passkey_name)
		{
			return Err(AuthError::Problem(
				StatusCode::BAD_REQUEST,
				"Der Name des Passkeys ist ungültig.",
			));
		}
		let mut tx = self.pool.begin().await?;
		self.lock_active(&mut tx, user.account_id)
			.await?
			.ok_or(AuthError::Problem(
				StatusCode::UNAUTHORIZED,
				super::UNAUTHORIZED,
			))?;
		self.check_session(&mut tx, user).await?;
		let data:Option<Value>=sqlx::query_scalar("SELECT \"Data\" FROM \"AspNetUserPasskeys\" WHERE \"CredentialId\"=$1 AND \"UserId\"=$2 FOR UPDATE").bind(&id).bind(user.account_id).fetch_optional(&mut *tx).await?;
		let mut data = data.ok_or(AuthError::Problem(
			StatusCode::NOT_FOUND,
			"Der Passkey wurde nicht gefunden.",
		))?;
		if rename {
			data["Name"] = json!(name);
			sqlx::query("UPDATE \"AspNetUserPasskeys\" SET \"Data\"=$2 WHERE \"CredentialId\"=$1")
				.bind(&id)
				.bind(data)
				.execute(&mut *tx)
				.await?;
		} else {
			sqlx::query("DELETE FROM \"AspNetUserPasskeys\" WHERE \"CredentialId\"=$1")
				.bind(&id)
				.execute(&mut *tx)
				.await?;
		}
		audit(
			&mut tx,
			user.account_id,
			if rename { 7 } else { 6 },
			rename.then_some(name),
			self.clock.now(),
		)
		.await?;
		tx.commit().await?;
		Ok(Json(if rename {
			json!({"renamed":true})
		} else {
			json!({"removed":true})
		})
		.into_response())
	}
	pub(super) async fn cleanup(&self, tx: &mut PgConnection) -> Result<(), AuthError> {
		let now = self.clock.now();
		for query in [
			"DELETE FROM archive_sessions WHERE token_digest IN (SELECT token_digest FROM archive_sessions WHERE expires_at<=$1 LIMIT 100)",
			"DELETE FROM archive_csrf WHERE browser_digest IN (SELECT browser_digest FROM archive_csrf WHERE expires_at<=$1 LIMIT 100)",
			"DELETE FROM archive_ceremonies WHERE (browser_digest,operation) IN (SELECT browser_digest,operation FROM archive_ceremonies WHERE expires_at<=$1 LIMIT 100)",
		] {
			sqlx::query(query).bind(now).execute(&mut *tx).await?;
		}
		Ok(())
	}
}

fn import_identity(id: &[u8], data: &Value) -> Result<Credential, AuthError> {
	let raw = STANDARD
		.decode(data["PublicKey"].as_str().ok_or_else(codec_error)?)
		.map_err(|_| codec_error())?;
	let cbor: serde_cbor_2::Value = serde_cbor_2::from_slice(&raw).map_err(|_| codec_error())?;
	let cred = COSEKey::try_from(&cbor).map_err(|_| codec_error())?;
	// Inventory proved existing ES256 only. No relabelling/fallback for other algorithms.
	if cred.type_ != COSEAlgorithm::ES256
		|| !matches!(&cred.key,COSEKeyType::EC_EC2(key) if key.curve==ECDSACurve::SECP256R1 && key.x.len()==32 && key.y.len()==32)
	{
		return Err(codec_error());
	}
	let boolean = |key: &str| data[key].as_bool().ok_or_else(codec_error);
	Ok(Credential {
		cred_id: id.to_vec().into(),
		cred,
		counter: data["SignCount"]
			.as_u64()
			.and_then(|n| n.try_into().ok())
			.ok_or_else(codec_error)?,
		transports: None,
		user_verified: boolean("IsUserVerified")?,
		backup_eligible: boolean("IsBackupEligible")?,
		backup_state: boolean("IsBackedUp")?,
		registration_policy: UserVerificationPolicy::Required,
		extensions: RegisteredExtensions::none(),
		attestation: ParsedAttestation::default(),
		attestation_format: AttestationFormat::None,
	})
}
fn identity_data(
	credential: &Credential,
	reg: &RegisterPublicKeyCredential,
	name: &str,
	now: DateTime<Utc>,
) -> Result<Value, AuthError> {
	// Encoding only: upstream has already verified the complete ceremony/key.
	// Identity's canonical PublicKey is COSE CBOR, NOT Rust JSON or SPKI.
	use serde_cbor_2::Value as C;
	let COSEKeyType::EC_EC2(key) = &credential.cred.key else {
		return Err(invalid("register"));
	};
	if credential.cred.type_ != COSEAlgorithm::ES256 || key.curve != ECDSACurve::SECP256R1 {
		return Err(invalid("register"));
	}
	let cose = C::Map(
		[
			(C::Integer(1), C::Integer(2)),
			(C::Integer(3), C::Integer(-7)),
			(C::Integer(-1), C::Integer(1)),
			(C::Integer(-2), C::Bytes(key.x.to_vec())),
			(C::Integer(-3), C::Bytes(key.y.to_vec())),
		]
		.into_iter()
		.collect(),
	);
	let raw = serde_cbor_2::to_vec(&cose).map_err(|_| AuthError::Database)?;
	Ok(
		json!({"PublicKey":STANDARD.encode(raw),"Name":if name.is_empty(){None}else{Some(name)},"CreatedAt":now,"SignCount":credential.counter,"Transports":reg.response.transports,"IsUserVerified":credential.user_verified,"IsBackupEligible":credential.backup_eligible,"IsBackedUp":credential.backup_state,"AttestationObject":STANDARD.encode(&reg.response.attestation_object),"ClientDataJson":STANDARD.encode(&reg.response.client_data_json)}),
	)
}
async fn audit(
	tx: &mut PgConnection,
	id: Uuid,
	action: i32,
	note: Option<&str>,
	now: DateTime<Utc>,
) -> Result<(), AuthError> {
	sqlx::query("INSERT INTO member_admin_actions(\"Id\",\"TargetUserId\",\"ActorAccountId\",\"Action\",\"OldRoles\",\"NewRoles\",\"Note\",\"OccurredAt\") VALUES($1,$2,$2,$3,'','',$4,$5)").bind(Uuid::new_v4()).bind(id).bind(action).bind(note).bind(now).execute(tx).await?;
	Ok(())
}
