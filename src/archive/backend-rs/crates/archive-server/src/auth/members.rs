//! Mitglied administration: HTTP policy, canonical state, then delivery outside
//! the committed transaction. No invitation/password/session shadow model.
use super::{AccessPolicy, Auth, AuthError, CurrentSession, identity, session::roles};
use crate::Origin;
use axum::{
	Json,
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use chrono::{DateTime, Utc};
use serde_json::{Value, json};
use sqlx::{PgConnection, Row};
use std::sync::Arc;
use tracing::Instrument;
use uuid::Uuid;

const ADMIN_FORBIDDEN: &str = "Keine Berechtigung für die Mitgliederverwaltung.";
const FRESH: &str = "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich.";
const INVITED: &str = "Einladung erstellt. Die E-Mail wurde zum Versand angenommen; die Zustellung wird nicht bestätigt.";
const RESENT: &str = "Einladung erneut zum Versand übergeben. Die Zustellung wird nicht bestätigt.";
const INVITE_FAILED: &str = "Die E-Mail konnte nicht übergeben werden. Die Einladung bleibt gespeichert und kann erneut gesendet werden.";
const ACTIVE: &str = "Diese Adresse ist bereits registriert. Es wurde keine neue Einladung erstellt und die Rolle nicht geändert.";
const ROLE_CONFLICT: &str = "Für diese Adresse liegt bereits eine Einladung mit einer anderen Rolle vor. Die Rolle wurde nicht geändert.";
const DEACTIVATED: &str = "Dieses Konto ist deaktiviert. Reaktivierung gehört zur Mitgliederverwaltung, nicht zur Einladung.";

#[derive(Clone, Copy, PartialEq)]
enum Operation {
	List,
	Invite,
	Resend,
	Deactivate,
	Reactivate,
	Role,
	EmailRequest,
	EmailConfirm,
}

pub(super) struct Metrics(
	std::collections::BTreeMap<&'static str, opentelemetry::metrics::Counter<u64>>,
);
impl Metrics {
	pub(super) fn new() -> Self {
		let meter = opentelemetry::global::meter(archive_telemetry::ACTIVITY_SOURCE);
		Self(
			[
				"archive.members.invited",
				"archive.members.resent",
				"archive.members.failures",
				"archive.members.deactivated",
				"archive.members.reactivated",
				"archive.members.role_changed",
				"archive.members.revocation_failures",
				"archive.members.email_change_requested",
				"archive.members.email_changed",
				"archive.members.email_change_failures",
			]
			.into_iter()
			.map(|name| (name, meter.u64_counter(name).build()))
			.collect(),
		)
	}
	fn record(&self, name: &'static str) {
		if let Some(counter) = self.0.get(name) {
			counter.add(1, &[]);
		}
	}
}
impl Operation {
	fn span(self) -> tracing::Span {
		match self {
			Self::List => tracing::info_span!("archive.members.list"),
			Self::Invite => tracing::info_span!("archive.members.invite"),
			Self::Resend => tracing::info_span!("archive.members.resend"),
			Self::Deactivate => tracing::info_span!("archive.members.deactivate"),
			Self::Reactivate => tracing::info_span!("archive.members.reactivate"),
			Self::Role => tracing::info_span!("archive.members.role_change"),
			Self::EmailRequest => tracing::info_span!("archive.members.email_change.request"),
			Self::EmailConfirm => tracing::info_span!("archive.members.email_change.confirm"),
		}
	}
}
pub(super) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	[
		("/api/admin/members", Operation::List),
		("/api/admin/invitations", Operation::Invite),
		("/api/admin/invitations/resend", Operation::Resend),
		("/api/admin/members/deactivate", Operation::Deactivate),
		("/api/admin/members/reactivate", Operation::Reactivate),
		("/api/admin/members/role", Operation::Role),
		("/api/admin/members/email/request", Operation::EmailRequest),
		("/api/admin/members/email/confirm", Operation::EmailConfirm),
	]
	.into_iter()
	.map(|(path, op)| {
		let handler = move |State(origin): State<Arc<Origin>>, request: Request| async move {
			super::no_store(
				match handle(&origin, request, op).instrument(op.span()).await {
					Ok(r) => r,
					Err(e) => e.into_response(),
				},
			)
		};
		(
			path,
			if op == Operation::List {
				get(handler)
			} else {
				post(handler)
			}
			.fallback(crate::api::api_miss),
		)
	})
	.collect()
}
async fn handle(origin: &Origin, request: Request, op: Operation) -> Result<Response, AuthError> {
	if op != Operation::List
		&& (super::cookie(request.headers(), "archive.csrf")
			.and_then(super::security::token_digest)
			.is_none()
			|| request.headers().get("X-CSRF-TOKEN").is_none())
	{
		return Err(bad(super::INVALID_CSRF));
	}
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		super::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(
			request.headers(),
			AccessPolicy::Member,
			op != Operation::List,
		)
		.await?;
	admin(&access.current, auth, op != Operation::List)?;
	let response = if op == Operation::List {
		Json(auth.members().await?).into_response()
	} else {
		let fields: &'static [&'static str] = match op {
			Operation::Invite => &["email", "displayName", "role"],
			Operation::Resend => &["email"],
			Operation::Deactivate | Operation::Reactivate => &["accountId"],
			Operation::Role => &["accountId", "role"],
			Operation::EmailRequest => &["accountId", "newEmail"],
			Operation::EmailConfirm => &["accountId", "newEmail", "code"],
			Operation::List => &[],
		};
		let body = super::bind_body(request, fields).await?;
		let result = match op {
			Operation::Invite | Operation::Resend => {
				auth.invite(&access.current, &body, op == Operation::Resend)
					.await
			}
			Operation::EmailRequest | Operation::EmailConfirm => {
				auth.change_email(&access.current, &body, op == Operation::EmailConfirm)
					.await
			}
			_ => auth.change_member(&access.current, &body, op).await,
		};
		if result.is_err() {
			auth.members_metrics.record(match op {
				Operation::Invite | Operation::Resend => "archive.members.failures",
				Operation::EmailRequest | Operation::EmailConfirm => {
					"archive.members.email_change_failures"
				}
				_ => "archive.members.revocation_failures",
			});
		}
		result?
	};
	Ok(access.respond(response))
}
fn bad(title: &'static str) -> AuthError {
	AuthError::Problem(StatusCode::BAD_REQUEST, title)
}
fn conflict(title: &'static str) -> AuthError {
	AuthError::Problem(StatusCode::CONFLICT, title)
}
fn admin(current: &CurrentSession, auth: &Auth, fresh: bool) -> Result<(), AuthError> {
	if !current.is_administrator() {
		return Err(AuthError::Problem(StatusCode::FORBIDDEN, ADMIN_FORBIDDEN));
	}
	if fresh && !current.is_fresh(auth.clock.now(), auth.config.fresh_window) {
		return Err(AuthError::Problem(StatusCode::FORBIDDEN, FRESH));
	}
	Ok(())
}
impl Auth {
	async fn change_email(
		&self,
		current: &CurrentSession,
		body: &Value,
		confirm: bool,
	) -> Result<Response, AuthError> {
		use rand::Rng;
		use subtle::ConstantTimeEq;
		const COLLISION: &str = "Diese Adresse gehört bereits zu einem anderen Konto. Es wurde nichts geändert und keine E-Mail gesendet.";
		const INVALID: &str = "Der Code ist ungültig oder abgelaufen.";
		let id = account_id(body)?;
		let raw = body["newEmail"].as_str().unwrap_or_default().trim();
		let normalized = super::normalize_email(Some(raw))
			.ok_or_else(|| bad("Die E-Mail-Adresse ist ungültig."))?;
		let code: String = body["code"]
			.as_str()
			.unwrap_or_default()
			.chars()
			.filter(char::is_ascii_digit)
			.collect();
		if confirm && code.len() != 6 {
			return Err(bad(INVALID));
		}
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		identity::lock(&mut tx).await?;
		self.member_actor(&mut tx, current).await?;
		let user = sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE")
			.bind(id)
			.fetch_optional(&mut *tx)
			.await?
			.ok_or(AuthError::Problem(
				StatusCode::NOT_FOUND,
				"Für diese Kennung wurde kein Mitglied gefunden.",
			))?;
		// The target can be held by a ceremony on another replica. Recheck the
		// shared session/freshness boundary after that final account-lock wait.
		self.member_actor(&mut tx, current).await?;
		if !confirm
			&& user
				.try_get::<Option<String>, _>("NormalizedEmail")?
				.as_deref() == Some(&normalized)
		{
			return Err(bad("Die neue Adresse ist mit der bisherigen identisch."));
		}
		let collision: bool = sqlx::query_scalar(
			"SELECT EXISTS(SELECT FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 AND \"Id\"<>$2)",
		)
		.bind(&normalized)
		.bind(id)
		.fetch_one(&mut *tx)
		.await?;
		if !confirm {
			if collision {
				return Err(conflict(COLLISION));
			}
			sqlx::query("UPDATE member_email_changes SET \"ConsumedAt\"=$2 WHERE \"UserId\"=$1 AND \"ConsumedAt\" IS NULL").bind(id).bind(now).execute(&mut *tx).await?;
			let code = format!("{:06}", rand::rng().random_range(0..1_000_000));
			let salt = rand::rng().random::<[u8; 16]>();
			sqlx::query("INSERT INTO member_email_changes(\"Id\",\"UserId\",\"NormalizedNewEmail\",\"CodeHash\",\"Salt\",\"CreatedAt\",\"ExpiresAt\",\"RowVersion\",\"RequestedByAccountId\") VALUES($1,$2,$3,$4,$5,$6,$7,0,$8)").bind(Uuid::new_v4()).bind(id).bind(&normalized).bind(super::security::code_hash(&code,&salt)).bind(salt.as_slice()).bind(now).bind(now+self.config.code_lifetime).bind(current.account_id).execute(&mut *tx).await?;
			tx.commit().await?;
			if self
				.mail
				.send(super::mail::email_change_message(
					raw.to_owned(),
					&code,
					self.config.code_lifetime.num_minutes(),
				))
				.instrument(tracing::info_span!(
					"archive.mail.email_change.send",
					otel.kind = "client"
				))
				.await
				.is_err()
			{
				return Err(AuthError::Problem(
					StatusCode::BAD_GATEWAY,
					"Die E-Mail konnte nicht übergeben werden. Die Änderung bleibt ausstehend und kann erneut angefordert werden.",
				));
			}
			self.members_metrics
				.record("archive.members.email_change_requested");
			return Ok(Json(json!({"message":"Bestätigungscode an die neue Adresse gesendet. Die Änderung wird erst mit dem Code wirksam.","accountId":id})).into_response());
		}
		let challenge=sqlx::query("SELECT * FROM member_email_changes WHERE \"UserId\"=$1 AND \"NormalizedNewEmail\"=$2 AND \"ConsumedAt\" IS NULL AND \"ExpiresAt\">$3 ORDER BY \"CreatedAt\" DESC LIMIT 1 FOR UPDATE").bind(id).bind(&normalized).bind(now).fetch_optional(&mut *tx).await?.ok_or_else(||bad(INVALID))?;
		if challenge.try_get::<i32, _>("AttemptCount")? >= self.config.max_code_attempts {
			return Err(bad(INVALID));
		}
		let challenge_id: Uuid = challenge.try_get("Id")?;
		let hash = super::security::code_hash(&code, &challenge.try_get::<Vec<u8>, _>("Salt")?);
		if !bool::from(hash.ct_eq(&challenge.try_get::<Vec<u8>, _>("CodeHash")?)) {
			sqlx::query("UPDATE member_email_changes SET \"AttemptCount\"=\"AttemptCount\"+1,\"RowVersion\"=\"RowVersion\"+1 WHERE \"Id\"=$1").bind(challenge_id).execute(&mut *tx).await?;
			tx.commit().await?;
			return Err(bad(INVALID));
		}
		sqlx::query("UPDATE member_email_changes SET \"ConsumedAt\"=$2,\"RowVersion\"=\"RowVersion\"+1 WHERE \"Id\"=$1").bind(challenge_id).bind(now).execute(&mut *tx).await?;
		if collision {
			tx.commit().await?;
			return Err(conflict(COLLISION));
		}
		sqlx::query("UPDATE \"AspNetUsers\" SET \"Email\"=$2,\"UserName\"=$2,\"NormalizedEmail\"=$3,\"NormalizedUserName\"=$3 WHERE \"Id\"=$1").bind(id).bind(raw).bind(&normalized).execute(&mut *tx).await?;
		sqlx::query("UPDATE member_invitations SET \"NormalizedEmail\"=$2 WHERE \"UserId\"=$1")
			.bind(id)
			.bind(&normalized)
			.execute(&mut *tx)
			.await?;
		identity::invalidate_challenges(&mut tx, id, now).await?;
		Auth::revoke_sessions(&mut tx, id).await?;
		let roles = roles(&mut tx, id).await?;
		identity::audit(
			&mut tx,
			id,
			current.account_id,
			3,
			(&roles, &roles),
			now,
			None,
		)
		.await?;
		tx.commit().await?;
		self.members_metrics.record("archive.members.email_changed");
		Ok(Json(json!({"message":"Adresse geändert. Frühere Sitzungen des Kontos wurden abgemeldet; bitte mit der neuen Adresse erneut per Code anmelden. Kennung und Verlauf bleiben erhalten.","accountId":id,"email":raw})).into_response())
	}
	async fn change_member(
		&self,
		current: &CurrentSession,
		body: &Value,
		operation: Operation,
	) -> Result<Response, AuthError> {
		let id = account_id(body)?;
		let role = body["role"].as_str().unwrap_or_default().trim();
		if operation == Operation::Role && !identity::ROLES.contains(&role) {
			return Err(bad("Die Rolle ist ungültig."));
		}
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		identity::lock(&mut tx).await?;
		self.member_actor(&mut tx, current).await?;
		let target = sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"Id\"=$1 FOR UPDATE")
			.bind(id)
			.fetch_optional(&mut *tx)
			.await?
			.ok_or(AuthError::Problem(
				StatusCode::NOT_FOUND,
				"Für diese Kennung wurde kein Mitglied gefunden.",
			))?;
		let old = roles(&mut tx, id).await?;
		self.member_actor(&mut tx, current).await?;
		let mut new = old.clone();
		let inactive = locked(&target, now)?;
		let confirmed: bool = target.try_get("EmailConfirmed")?;
		let (action, message, status) = match operation {
			Operation::Deactivate => {
				if inactive {
					return Err(conflict("Dieses Konto ist bereits deaktiviert."));
				}
				self.protect_last_admin(
					&mut tx,
					confirmed && old.iter().any(|r| r == "Administrator"),
				)
				.await?;
				sqlx::query(
					"UPDATE \"AspNetUsers\" SET \"LockoutEnabled\"=true,\"LockoutEnd\"=$2 WHERE \"Id\"=$1",
				)
				.bind(id)
				.bind(
					now.checked_add_months(chrono::Months::new(1200))
						.ok_or(AuthError::Database)?,
				)
				.execute(&mut *tx)
				.await?;
				Auth::revoke_sessions(&mut tx, id).await?;
				(
					0,
					"Mitglied deaktiviert. Bestehende Sitzungen werden abgemeldet; Inhalte und Verlauf bleiben erhalten.",
					Some("deactivated"),
				)
			}
			Operation::Reactivate => {
				if !inactive {
					return Err(conflict("Dieses Konto ist bereits aktiv."));
				}
				sqlx::query(
					"UPDATE \"AspNetUsers\" SET \"LockoutEnd\"=NULL,\"AccessFailedCount\"=0 WHERE \"Id\"=$1",
				)
				.bind(id)
				.execute(&mut *tx)
				.await?;
				Auth::revoke_sessions(&mut tx, id).await?;
				(
					1,
					"Mitglied reaktiviert. Eine erneute Anmeldung mit Code ist erforderlich; frühere Sitzungen bleiben ungültig.",
					Some(if confirmed { "active" } else { "invited" }),
				)
			}
			Operation::Role => {
				let message = "Rolle aktualisiert. Die Änderung gilt ab der nächsten Anfrage.";
				if old == [role] {
					return Ok(
						Json(json!({"message":message,"accountId":id,"roles":old})).into_response()
					);
				}
				self.protect_last_admin(
					&mut tx,
					confirmed
						&& !inactive && old.iter().any(|r| r == "Administrator")
						&& role != "Administrator",
				)
				.await?;
				sqlx::query("DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\"=$1")
					.bind(id)
					.execute(&mut *tx)
					.await?;
				identity::add_role(&mut tx, id, role).await?;
				sqlx::query("UPDATE \"AspNetUsers\" SET \"ConcurrencyStamp\"=$2 WHERE \"Id\"=$1")
					.bind(id)
					.bind(super::security::opaque())
					.execute(&mut *tx)
					.await?;
				new = vec![role.to_owned()];
				(2, message, None)
			}
			_ => return Err(AuthError::Database),
		};
		identity::audit(
			&mut tx,
			id,
			current.account_id,
			action,
			(&old, &new),
			now,
			None,
		)
		.await?;
		tx.commit().await?;
		let body = if let Some(status) = status {
			json!({"message":message,"accountId":id,"status":status})
		} else {
			json!({"message":message,"accountId":id,"roles":new})
		};
		self.members_metrics.record(match operation {
			Operation::Deactivate => "archive.members.deactivated",
			Operation::Reactivate => "archive.members.reactivated",
			_ => "archive.members.role_changed",
		});
		Ok(Json(body).into_response())
	}
	async fn protect_last_admin(
		&self,
		tx: &mut PgConnection,
		target_is_admin: bool,
	) -> Result<(), AuthError> {
		if target_is_admin {
			let count:i64=sqlx::query_scalar("SELECT count(DISTINCT u.\"Id\") FROM \"AspNetUsers\" u JOIN \"AspNetUserRoles\" ur ON ur.\"UserId\"=u.\"Id\" JOIN \"AspNetRoles\" r ON r.\"Id\"=ur.\"RoleId\" WHERE r.\"Name\"='Administrator' AND u.\"EmailConfirmed\" AND (NOT u.\"LockoutEnabled\" OR u.\"LockoutEnd\" IS NULL OR u.\"LockoutEnd\"<=$1)").bind(self.clock.now()).fetch_one(tx).await?;
			if count <= 1 {
				return Err(conflict(
					"Die letzte Administratorin oder der letzte Administrator kann nicht entfernt werden. Reparatur gehört zum Wartungsweg, nicht zur Selbstentsperrung.",
				));
			}
		}
		Ok(())
	}
	/// Re-check the shared session/membership facility after taking account locks:
	/// an administrator demoted/revoked while waiting cannot execute a stale write.
	async fn member_actor(
		&self,
		tx: &mut PgConnection,
		current: &CurrentSession,
	) -> Result<(), AuthError> {
		if self.lock_active(tx, current.account_id).await?.is_none() {
			return Err(AuthError::Problem(
				StatusCode::UNAUTHORIZED,
				super::UNAUTHORIZED,
			));
		}
		self.check_session(tx, current).await?;
		let mut current = current.clone();
		current.roles = roles(tx, current.account_id).await?;
		admin(&current, self, true)
	}
	async fn members(&self) -> Result<Value, AuthError> {
		let mut tx = self.pool.begin().await?;
		let rows=sqlx::query("SELECT u.*,i.\"Id\" AS invitation_id,i.\"InvitedAt\",i.\"InvitedByAccountId\",i.\"AcceptedAt\",i.\"LastSentAt\",i.\"MailStatus\" FROM \"AspNetUsers\" u LEFT JOIN member_invitations i ON i.\"UserId\"=u.\"Id\" ORDER BY u.\"Email\"").fetch_all(&mut *tx).await?;
		let mut members = vec![];
		for row in rows {
			let id: Uuid = row.try_get("Id")?;
			let status = if locked(&row, self.clock.now())? {
				"deactivated"
			} else if row.try_get::<bool, _>("EmailConfirmed")? {
				"active"
			} else {
				"invited"
			};
			let mail_status: Option<i32> = row.try_get("MailStatus")?;
			members.push(json!({"accountId":id,"email":row.try_get::<Option<String>,_>("Email")?.unwrap_or_default(),"displayName":row.try_get::<Option<String>,_>("DisplayName")?,"roles":roles(&mut tx,id).await?,"status":status,"invitationId":row.try_get::<Option<Uuid>,_>("invitation_id")?,"invitedAt":row.try_get::<Option<DateTime<Utc>>,_>("InvitedAt")?,"invitedByAccountId":row.try_get::<Option<Uuid>,_>("InvitedByAccountId")?,"acceptedAt":row.try_get::<Option<DateTime<Utc>>,_>("AcceptedAt")?,"lastInvitationSentAt":row.try_get::<Option<DateTime<Utc>>,_>("LastSentAt")?,"invitationMailStatus":match mail_status{None=>"none",Some(1)=>"sent",Some(2)=>"failed",_=>"pending"}}));
		}
		tx.commit().await?;
		Ok(json!({"members":members}))
	}
	async fn invite(
		&self,
		current: &CurrentSession,
		body: &Value,
		resend: bool,
	) -> Result<Response, AuthError> {
		let raw = body["email"].as_str().unwrap_or_default().trim();
		let normalized = super::normalize_email(Some(raw))
			.ok_or_else(|| bad("Die E-Mail-Adresse ist ungültig."))?;
		let role = body["role"].as_str().unwrap_or_default().trim();
		let name = body["displayName"]
			.as_str()
			.map(str::trim)
			.filter(|s| !s.is_empty());
		if !resend && !identity::ROLES.contains(&role) {
			return Err(bad("Die Rolle ist ungültig."));
		}
		if !resend && name.is_some_and(|s| s.encode_utf16().count() > 200) {
			return Err(bad("Der Name ist zu lang."));
		}
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		identity::lock(&mut tx).await?;
		self.member_actor(&mut tx, current).await?;
		let existing =
			sqlx::query("SELECT * FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"=$1 FOR UPDATE")
				.bind(&normalized)
				.fetch_optional(&mut *tx)
				.await?;
		self.member_actor(&mut tx, current).await?;
		let (id, is_new, email, name, role) = if let Some(user) = existing {
			let id: Uuid = user.try_get("Id")?;
			if locked(&user, now)? {
				return Err(conflict(DEACTIVATED));
			}
			if user.try_get::<bool, _>("EmailConfirmed")? {
				if resend {
					sqlx::query("UPDATE member_invitations SET \"AcceptedAt\"=$2 WHERE \"UserId\"=$1 AND \"AcceptedAt\" IS NULL").bind(id).bind(now).execute(&mut *tx).await?;
					tx.commit().await?;
				}
				return Err(conflict(if resend {
					"Diese Einladung wurde bereits angenommen. Es ist keine erneute Einladung nötig."
				} else {
					ACTIVE
				}));
			}
			let existing_roles = roles(&mut tx, id).await?;
			if !resend && !existing_roles.iter().any(|r| r == role) {
				return Err(conflict(ROLE_CONFLICT));
			}
			(
				id,
				false,
				user.try_get::<Option<String>, _>("Email")?
					.unwrap_or(normalized.clone()),
				user.try_get::<Option<String>, _>("DisplayName")?,
				existing_roles
					.first()
					.cloned()
					.unwrap_or_else(|| "Member".into()),
			)
		} else {
			if resend {
				return Err(AuthError::Problem(
					StatusCode::NOT_FOUND,
					"Für diese Adresse liegt keine Einladung vor.",
				));
			}
			let id = identity::create_user(&mut tx, raw, &normalized, name, false).await?;
			identity::add_role(&mut tx, id, role).await?;
			(
				id,
				true,
				raw.to_owned(),
				name.map(str::to_owned),
				role.to_owned(),
			)
		};
		sqlx::query("INSERT INTO member_invitations(\"Id\",\"UserId\",\"NormalizedEmail\",\"Role\",\"DisplayName\",\"InvitedByAccountId\",\"InvitedAt\",\"LastSentByAccountId\",\"MailStatus\") VALUES($1,$2,$3,$4,$5,$6,$7,$6,0) ON CONFLICT (\"UserId\") DO NOTHING").bind(Uuid::new_v4()).bind(id).bind(&normalized).bind(&role).bind(&name).bind(current.account_id).bind(now).execute(&mut *tx).await?;
		let invitation=sqlx::query("UPDATE member_invitations SET \"LastSentByAccountId\"=$2 WHERE \"UserId\"=$1 RETURNING \"Id\",\"Role\"").bind(id).bind(current.account_id).fetch_one(&mut *tx).await?;
		let invitation_id: Uuid = invitation.try_get("Id")?;
		let invitation_role: String = invitation.try_get("Role")?;
		tx.commit().await?;
		let delivered = self
			.mail
			.send(super::mail::invitation_message(
				email,
				name.as_deref(),
				&invitation_role,
			))
			.instrument(tracing::info_span!(
				"archive.mail.invite.send",
				otel.kind = "client"
			))
			.await
			.is_ok();
		sqlx::query("UPDATE member_invitations SET \"MailStatus\"=$2,\"LastError\"=$3,\"LastSentAt\"=CASE WHEN $2=1 THEN $4 ELSE \"LastSentAt\" END WHERE \"Id\"=$1").bind(invitation_id).bind(if delivered{1}else{2}).bind(if delivered{None}else{Some(INVITE_FAILED)}).bind(now).execute(&self.pool).await?;
		if !delivered {
			return Err(AuthError::Problem(StatusCode::BAD_GATEWAY, INVITE_FAILED));
		}
		self.members_metrics.record(if is_new {
			"archive.members.invited"
		} else {
			"archive.members.resent"
		});
		let mut response=(if is_new{StatusCode::CREATED}else{StatusCode::OK},Json(json!({"message":if is_new{INVITED}else{RESENT},"accountId":id,"invitationId":invitation_id,"status":"invited","mailStatus":"sent"}))).into_response();
		if is_new {
			response.headers_mut().insert(
				"location",
				axum::http::HeaderValue::from_static("/api/admin/members"),
			);
		}
		Ok(response)
	}
}
fn locked(user: &sqlx::postgres::PgRow, now: DateTime<Utc>) -> Result<bool, AuthError> {
	Ok(user.try_get::<bool, _>("LockoutEnabled")?
		&& user
			.try_get::<Option<DateTime<Utc>>, _>("LockoutEnd")?
			.is_some_and(|end| end > now))
}

fn account_id(body: &Value) -> Result<Uuid, AuthError> {
	body["accountId"]
		.as_str()
		.and_then(crate::clr_guid::try_parse)
		.filter(|id| !id.is_nil())
		.ok_or_else(|| bad("Die Kennung ist ungültig."))
}
