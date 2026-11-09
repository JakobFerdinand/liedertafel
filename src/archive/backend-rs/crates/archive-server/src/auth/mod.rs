//! Shared authentication/access boundary. Identity rows remain canonical.
mod allocation;
mod code;
mod config;
mod csrf;
pub mod mail;
mod passkey;
mod security;
mod seed;
mod session;

pub use config::{AuthConfig, Clock, SystemClock};
pub use mail::{Mail, MailError, MailMessage};
pub use security::{normalize_email, verify_identity_password};
pub use session::CurrentSession;

use crate::{Origin, api::problem};
use axum::{
	Json,
	body::to_bytes,
	extract::{Request, State},
	http::{HeaderMap, StatusCode},
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use chrono::Duration;
use opentelemetry::{global, metrics::Counter};
use serde_json::{Value, json};
use sqlx::PgPool;
use std::sync::Arc;
use tracing::Instrument;

pub const INVALID_CSRF: &str = "Ungültiger Sicherheitstoken.";
pub const UNAUTHORIZED: &str = "Anmeldung erforderlich.";
pub const FORBIDDEN: &str = "Keine Berechtigung für das Liedverzeichnis.";
pub const NOT_FRESH: &str = "Die Anmeldung ist zu alt. Bitte erneut anmelden.";
pub(super) const LIMITED: &str = "Zu viele Anfragen. Bitte später erneut versuchen.";

pub struct Auth {
	pub pool: PgPool,
	pub config: AuthConfig,
	pub clock: Arc<dyn Clock>,
	mail: Arc<dyn Mail>,
	requests: Counter<u64>,
	verified: Counter<u64>,
	failures: Counter<u64>,
	allocations_limited: Counter<u64>,
}
impl std::fmt::Debug for Auth {
	fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("Auth { redacted }")
	}
}
impl Auth {
	pub fn new(
		pool: PgPool,
		config: AuthConfig,
		mail: Arc<dyn Mail>,
		clock: Arc<dyn Clock>,
	) -> Result<Self, AuthError> {
		config.validate()?;
		let meter = global::meter(archive_telemetry::ACTIVITY_SOURCE);
		Ok(Self {
			pool,
			config,
			mail,
			clock,
			requests: meter.u64_counter("archive.auth.requests").build(),
			verified: meter.u64_counter("archive.auth.verified").build(),
			failures: meter.u64_counter("archive.auth.failures").build(),
			allocations_limited: meter
				.u64_counter("archive.auth.allocations_limited")
				.build(),
		})
	}

	/// Shared member-route boundary for later waves. Mutations validate CSRF
	/// before authorization/domain work; membership is never inferred from cookies.
	pub async fn authorize(
		&self,
		headers: &HeaderMap,
		policy: AccessPolicy,
		mutation: bool,
	) -> Result<RequestAccess, AuthError> {
		if mutation
			&& cookie(headers, "archive.csrf")
				.and_then(security::token_digest)
				.is_none()
		{
			return Err(AuthError::Problem(StatusCode::BAD_REQUEST, INVALID_CSRF));
		}
		let lookup = self.session(cookie(headers, "archive.auth")).await?;
		if mutation {
			self.validate_csrf(headers, lookup.current.as_ref()).await?;
		}
		match policy {
			AccessPolicy::Member => require_member(lookup.current.as_ref())?,
			AccessPolicy::Editor => require_editor(lookup.current.as_ref())?,
			AccessPolicy::Administrator => require_administrator(lookup.current.as_ref())?,
		};
		Ok(RequestAccess {
			current: lookup
				.current
				.ok_or(AuthError::Problem(StatusCode::UNAUTHORIZED, UNAUTHORIZED))?,
			renew: lookup
				.renew
				.then(|| cookie(headers, "archive.auth").map(str::to_owned))
				.flatten(),
			secure: self.secure(headers),
		})
	}
}

#[derive(Clone, Copy)]
pub enum AccessPolicy {
	Member,
	Editor,
	Administrator,
}
/// Owns response cache/renewal policy as well as the current access decision.
pub struct RequestAccess {
	pub current: CurrentSession,
	renew: Option<String>,
	secure: bool,
}
impl RequestAccess {
	pub fn respond(self, mut response: Response) -> Response {
		if let Some(token) = self.renew {
			set_cookie(
				&mut response,
				"archive.auth",
				&token,
				self.secure,
				Some(Duration::days(30)),
			);
		}
		no_store(response)
	}
}

/// Safe error messages only; never retain SQL/provider payloads (which may contain secrets).
#[derive(Debug, thiserror::Error)]
pub enum AuthError {
	#[error(
		"invalid authentication configuration; check Authentication__PasskeyRelyingPartyId, PasskeyOrigins, bounded authentication limits and Mail settings (production requires keyless Azure)"
	)]
	Configuration,
	#[error("authentication database operation failed")]
	Database,
	#[error("mail delivery failed")]
	Mail,
	#[error("{1}")]
	Problem(StatusCode, &'static str),
}
impl From<sqlx::Error> for AuthError {
	fn from(_: sqlx::Error) -> Self {
		Self::Database
	}
}
impl IntoResponse for AuthError {
	fn into_response(self) -> Response {
		let response = match self {
			Self::Problem(status, title) => problem(status, title),
			_ => {
				tracing::error!("Authentication operation failed");
				problem(
					StatusCode::INTERNAL_SERVER_ERROR,
					"Ein unerwarteter Fehler ist aufgetreten.",
				)
			}
		};
		no_store(response)
	}
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Route {
	Csrf,
	Me,
	RequestCode,
	VerifyCode,
	Logout,
	RegisterOptions,
	RegisterVerify,
	LoginOptions,
	LoginVerify,
	Passkeys,
	Rename,
	Remove,
	Seed,
}
pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	// One route registry: paths/methods occur here only. Dispatch uses typed
	// operations, never parses request paths to choose security policy.
	[
		("/api/antiforgery", Route::Csrf, true),
		("/api/auth/me", Route::Me, true),
		("/api/auth/code/request", Route::RequestCode, false),
		("/api/auth/code/verify", Route::VerifyCode, false),
		("/api/auth/logout", Route::Logout, false),
		("/api/auth/passkeys", Route::Passkeys, true),
		(
			"/api/auth/passkeys/register/options",
			Route::RegisterOptions,
			false,
		),
		(
			"/api/auth/passkeys/register/verify",
			Route::RegisterVerify,
			false,
		),
		(
			"/api/auth/passkeys/login/options",
			Route::LoginOptions,
			false,
		),
		("/api/auth/passkeys/login/verify", Route::LoginVerify, false),
		("/api/auth/passkeys/rename", Route::Rename, false),
		("/api/auth/passkeys/remove", Route::Remove, false),
		("/api/dev/auth/seed", Route::Seed, false),
	]
	.into_iter()
	.map(|(path, route, read)| {
		let handler = move |State(origin): State<Arc<Origin>>, request: Request| async move {
			dispatch(&origin, request, route).await
		};
		(
			path,
			if read { get(handler) } else { post(handler) }.fallback(crate::api::api_miss),
		)
	})
	.collect()
}

async fn dispatch(origin: &Origin, request: Request, route: Route) -> Response {
	let result = dispatch_inner(origin, request, route).await;
	no_store(match result {
		Ok(response) => response,
		Err(error) => error.into_response(),
	})
}
async fn dispatch_inner(
	origin: &Origin,
	request: Request,
	route: Route,
) -> Result<Response, AuthError> {
	let get = matches!(route, Route::Csrf | Route::Me | Route::Passkeys);
	if route == Route::Seed && !origin.settings.development {
		return Ok(crate::api::ApiMiss::Unknown.response());
	}
	let headers = request.headers().clone();
	// Same one trusted-ingress hop as Program.cs. Production container is only
	// reachable through managed ingress; never deploy this origin on a public socket.
	let forwarded = headers
		.get("x-forwarded-for")
		.and_then(|v| v.to_str().ok())
		.and_then(|v| v.rsplit(',').next())
		.and_then(|v| v.trim().parse::<std::net::IpAddr>().ok());
	let ip = forwarded
		.or_else(|| {
			request
				.extensions()
				.get::<axum::extract::ConnectInfo<std::net::SocketAddr>>()
				.map(|peer| peer.0.ip())
		})
		.map(|ip| ip.to_string());
	let address_digest = security::digest(ip.as_deref().unwrap_or("unknown"));
	let auth = origin.auth.as_deref();
	// Missing/forged mutations fail before any dependency or domain effect.
	let mutation = !get && route != Route::LoginOptions;
	if mutation
		&& (cookie(&headers, "archive.csrf")
			.and_then(security::token_digest)
			.is_none()
			|| headers.get("X-CSRF-TOKEN").is_none())
	{
		return Err(AuthError::Problem(StatusCode::BAD_REQUEST, INVALID_CSRF));
	}
	let Some(auth) = auth else {
		return if route == Route::Me {
			Ok(Json(json!({"authenticated":false})).into_response())
		} else if mutation {
			Err(AuthError::Problem(StatusCode::BAD_REQUEST, INVALID_CSRF))
		} else {
			Err(AuthError::Database)
		};
	};
	let lookup = auth.session(cookie(&headers, "archive.auth")).await?;
	let current = lookup.current;
	if mutation {
		auth.validate_csrf(&headers, current.as_ref()).await?;
	}
	let mut response = if get {
		match route {
			Route::Csrf => {
				auth.csrf(&headers, current.as_ref(), &address_digest)
					.await?
			}
			Route::Me => {
				let body = match current.as_ref() {
					Some(current) => {
						let mut body =
							serde_json::to_value(current).map_err(|_| AuthError::Database)?;
						body["authenticated"] = json!(true);
						body
					}
					None => json!({"authenticated":false}),
				};
				Json(body).into_response()
			}
			Route::Passkeys => auth.list_passkeys(current.as_ref()).await?,
			_ => crate::api::ApiMiss::Unknown.response(),
		}
	} else {
		let body = to_bytes(request.into_body(), 65_536).await.map_err(|_| {
			AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig.")
		})?;
		let body: Value = if body.is_empty() {
			json!({})
		} else {
			serde_json::from_slice(&body).map_err(|_| {
				AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig.")
			})?
		};
		// Only a SHA256 digest of the effective client IP enters the abuse log.
		let ip_hash = address_digest
			.iter()
			.map(|b| format!("{b:02X}"))
			.collect::<String>();
		match route {
			Route::RequestCode => {
				auth.request_code(&body, &ip_hash)
					.instrument(tracing::info_span!(
						"archive.auth.request",
						"auth.result" = tracing::field::Empty
					))
					.await?
			}
			Route::VerifyCode => {
				let (body, token) = auth
					.verify_code(&body, &ip_hash)
					.instrument(tracing::info_span!(
						"archive.auth.verify",
						"auth.result" = tracing::field::Empty
					))
					.await?;
				let mut response = Json(body).into_response();
				set_cookie(
					&mut response,
					"archive.auth",
					&token,
					auth.secure(&headers),
					Some(Duration::days(30)),
				);
				response
			}
			Route::Logout => {
				let user = require_member(current.as_ref())?;
				let mut tx = auth.pool.begin().await?;
				Auth::revoke_sessions(&mut tx, user.account_id).await?;
				tx.commit().await?;
				let mut response =
					Json(json!({"message":"Abmeldung erfolgreich."})).into_response();
				set_cookie(
					&mut response,
					"archive.auth",
					"",
					auth.secure(&headers),
					Some(Duration::zero()),
				);
				response
			}
			Route::Seed => Json(auth.seed().await?).into_response(),
			Route::LoginOptions => {
				auth.passkey_options(&headers, current.as_ref(), "login", &address_digest)
					.await?
			}
			Route::RegisterOptions => {
				auth.passkey_options(&headers, current.as_ref(), "register", &address_digest)
					.await?
			}
			Route::LoginVerify | Route::RegisterVerify => {
				let operation = if route == Route::RegisterVerify {
					"register"
				} else {
					"login"
				};
				let result = auth
					.verify_passkey(&headers, current.as_ref(), &body, operation)
					.await;
				if result.is_err() {
					auth.failures.add(1, &[]);
					tracing::info!("Passkey ceremony rejected");
				}
				result?
			}
			Route::Rename => auth.manage_passkey(current.as_ref(), &body, true).await?,
			Route::Remove => auth.manage_passkey(current.as_ref(), &body, false).await?,
			_ => crate::api::ApiMiss::Unknown.response(),
		}
	};
	if lookup.renew
		&& !response
			.headers()
			.get_all("set-cookie")
			.iter()
			.any(|v| v.as_bytes().starts_with(b"archive.auth="))
		&& let Some(token) = cookie(&headers, "archive.auth")
	{
		set_cookie(
			&mut response,
			"archive.auth",
			token,
			auth.secure(&headers),
			Some(Duration::days(30)),
		);
	}
	Ok(response)
}

pub fn require_member(current: Option<&CurrentSession>) -> Result<&CurrentSession, AuthError> {
	current.ok_or(AuthError::Problem(StatusCode::UNAUTHORIZED, UNAUTHORIZED))
}
pub fn require_editor(current: Option<&CurrentSession>) -> Result<&CurrentSession, AuthError> {
	let current = require_member(current)?;
	if !current.is_editor() {
		return Err(AuthError::Problem(StatusCode::FORBIDDEN, FORBIDDEN));
	}
	Ok(current)
}
pub fn require_administrator(
	current: Option<&CurrentSession>,
) -> Result<&CurrentSession, AuthError> {
	let current = require_member(current)?;
	if !current.is_administrator() {
		return Err(AuthError::Problem(StatusCode::FORBIDDEN, FORBIDDEN));
	}
	Ok(current)
}

pub fn no_store(mut response: Response) -> Response {
	response.headers_mut().insert(
		"cache-control",
		axum::http::HeaderValue::from_static("no-store"),
	);
	response
}
pub(super) fn cookie<'a>(headers: &'a HeaderMap, name: &str) -> Option<&'a str> {
	let mut values = headers
		.get_all("cookie")
		.iter()
		.filter_map(|v| v.to_str().ok())
		.flat_map(|v| v.split(';'))
		.filter_map(|v| v.trim().split_once('='))
		.filter(|(key, _)| *key == name);
	let (_, value) = values.next()?;
	if values.next().is_some() {
		None
	} else {
		Some(value)
	}
}
pub(super) fn set_cookie(
	response: &mut Response,
	name: &str,
	value: &str,
	secure: bool,
	lifetime: Option<Duration>,
) {
	let mut cookie = format!("{name}={value}; Path=/; HttpOnly; SameSite=Strict");
	if secure {
		cookie.push_str("; Secure");
	}
	if let Some(lifetime) = lifetime {
		cookie.push_str(&format!("; Max-Age={}", lifetime.num_seconds()));
		if lifetime.is_zero() {
			cookie.push_str("; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
		}
	}
	if let Ok(value) = cookie.parse() {
		response.headers_mut().append("set-cookie", value);
	}
}
