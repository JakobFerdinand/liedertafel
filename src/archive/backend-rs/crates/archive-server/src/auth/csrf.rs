use super::{
	Auth, AuthError, CurrentSession, INVALID_CSRF, cookie,
	security::{digest, opaque, token_digest},
	set_cookie,
};
use axum::{
	Json,
	http::{HeaderMap, StatusCode},
	response::{IntoResponse, Response},
};
use chrono::Duration;
use serde_json::json;
use sqlx::Row;
use subtle::ConstantTimeEq;

impl Auth {
	pub(super) fn secure(&self, headers: &HeaderMap) -> bool {
		!self.config.development
			|| headers
				.get("x-forwarded-proto")
				.and_then(|v| v.to_str().ok())
				.and_then(|v| v.rsplit(',').next())
				.is_some_and(|v| v.trim().eq_ignore_ascii_case("https"))
	}
	pub(super) async fn csrf(
		&self,
		headers: &HeaderMap,
		current: Option<&CurrentSession>,
	) -> Result<Response, AuthError> {
		let browser = cookie(headers, "archive.csrf")
			.filter(|v| token_digest(v).is_some())
			.map(str::to_owned)
			.unwrap_or_else(opaque);
		let browser_digest = digest(&browser);
		let session = current.map(|s| s.token_digest.as_slice());
		let now = self.clock.now();
		let mut tx = self.pool.begin().await?;
		self.cleanup(&mut tx).await?;
		let token:String=sqlx::query_scalar("INSERT INTO archive_csrf(browser_digest,request_token,session_digest,expires_at) VALUES($1,$2,$3,$4) ON CONFLICT(browser_digest) DO UPDATE SET request_token=CASE WHEN archive_csrf.session_digest IS NOT DISTINCT FROM excluded.session_digest AND archive_csrf.expires_at>$5 THEN archive_csrf.request_token ELSE excluded.request_token END, session_digest=excluded.session_digest, expires_at=excluded.expires_at RETURNING request_token")
			.bind(&browser_digest).bind(opaque()).bind(session).bind(now+Duration::hours(2)).bind(now).fetch_one(&mut *tx).await?;
		tx.commit().await?;
		let mut response = Json(json!({"token":token})).into_response();
		set_cookie(
			&mut response,
			"archive.csrf",
			&browser,
			self.secure(headers),
			None,
		);
		Ok(response)
	}
	pub(super) async fn validate_csrf(
		&self,
		headers: &HeaderMap,
		current: Option<&CurrentSession>,
	) -> Result<(), AuthError> {
		let bad = || AuthError::Problem(StatusCode::BAD_REQUEST, INVALID_CSRF);
		let browser = cookie(headers, "archive.csrf")
			.and_then(token_digest)
			.ok_or_else(bad)?;
		let token = headers
			.get("X-CSRF-TOKEN")
			.and_then(|v| v.to_str().ok())
			.filter(|s| s.len() == 43)
			.ok_or_else(bad)?;
		let row=sqlx::query("SELECT request_token, session_digest FROM archive_csrf WHERE browser_digest=$1 AND expires_at>$2").bind(browser).bind(self.clock.now()).fetch_optional(&self.pool).await?.ok_or_else(bad)?;
		let expected: String = row.try_get("request_token")?;
		let binding: Option<Vec<u8>> = row.try_get("session_digest")?;
		if !bool::from(expected.as_bytes().ct_eq(token.as_bytes()))
			|| binding.as_deref() != current.map(|s| s.token_digest.as_slice())
		{
			return Err(bad());
		}
		Ok(())
	}
}
