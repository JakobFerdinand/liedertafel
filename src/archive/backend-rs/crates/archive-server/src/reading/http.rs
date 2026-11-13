//! Shared ASP.NET form semantics for the two Lesung consumers. Authorization
//! happens BEFORE consuming a body; identities are string Guid/Int32, not JSON.
use crate::auth::{AuthError, AuthError::Problem as fail};
use axum::{
	extract::{FromRequest, Multipart, Request},
	http::StatusCode,
};
use std::collections::HashMap;

// Frozen native Kestrel default, DISTINCT from Reading.MaxPdfBytes (32 MiB).
// TestServer has no such transport envelope; never silently enlarge production.
pub(crate) const FORM_LIMIT: usize = 30_000_000;
#[derive(Default)]
pub(crate) struct FormData {
	values: HashMap<String, Vec<String>>,
	files: Vec<(String, Vec<u8>)>,
}
impl FormData {
	pub fn value(&self, key: &str) -> String {
		self.values
			.get(&key.to_ascii_lowercase())
			.map(|v| {
				v.iter()
					.filter(|s| !s.is_empty())
					.map(String::as_str)
					.collect::<Vec<_>>()
					.join(",")
			})
			.unwrap_or_default()
	}
	pub fn file(&self, key: &str) -> Option<&[u8]> {
		self.files
			.iter()
			.find(|(k, _)| k.eq_ignore_ascii_case(key))
			.map(|(_, v)| v.as_slice())
	}
	fn insert(&mut self, key: String, value: String) {
		self.values
			.entry(key.to_ascii_lowercase())
			.or_default()
			.push(value);
	}
}
pub(crate) async fn read_form(
	request: Request,
	title: &'static str,
) -> Result<FormData, AuthError> {
	let bad = || fail(StatusCode::BAD_REQUEST, title);
	let content = request
		.headers()
		.get("content-type")
		.and_then(|v| v.to_str().ok())
		.unwrap_or_default()
		.split(';')
		.next()
		.unwrap_or_default()
		.trim()
		.to_ascii_lowercase();
	let mut result = FormData::default();
	match content.as_str() {
		"multipart/form-data" => {
			let mut form = Multipart::from_request(request, &())
				.await
				.map_err(|_| bad())?;
			let mut count = 0;
			while let Some(part) = form.next_field().await.map_err(|error| {
				if error.status() == StatusCode::PAYLOAD_TOO_LARGE {
					fail(
						StatusCode::PAYLOAD_TOO_LARGE,
						"Der Anfrageinhalt ist zu groß.",
					)
				} else {
					bad()
				}
			})? {
				count += 1;
				if count > 1024 {
					return Err(bad());
				}
				let key = part.name().unwrap_or_default().to_owned();
				let file = part.file_name().is_some();
				let bytes = part.bytes().await.map_err(|error| {
					if error.status() == StatusCode::PAYLOAD_TOO_LARGE {
						fail(
							StatusCode::PAYLOAD_TOO_LARGE,
							"Der Anfrageinhalt ist zu groß.",
						)
					} else {
						bad()
					}
				})?;
				if file {
					result.files.push((key, bytes.to_vec()));
				} else {
					result.insert(key, String::from_utf8_lossy(&bytes).into_owned());
				}
			}
		}
		"application/x-www-form-urlencoded" => {
			let bytes = axum::body::to_bytes(request.into_body(), FORM_LIMIT)
				.await
				.map_err(|_| {
					fail(
						StatusCode::PAYLOAD_TOO_LARGE,
						"Der Anfrageinhalt ist zu groß.",
					)
				})?;
			for (index, (key, value)) in url::form_urlencoded::parse(&bytes).enumerate() {
				if index >= 1024
					|| key.encode_utf16().count() > 2048
					|| value.encode_utf16().count() > 4 * 1024 * 1024
				{
					return Err(bad());
				}
				result.insert(key.into_owned(), value.into_owned());
			}
		}
		_ => return Err(bad()),
	}
	Ok(result)
}
pub(crate) fn identity(
	form: &FormData,
	title: &'static str,
) -> Result<(uuid::Uuid, i32), AuthError> {
	let bad = || fail(StatusCode::BAD_REQUEST, title);
	let draft = crate::clr_guid::try_parse(&form.value("entwurfId")).ok_or_else(bad)?;
	let number = int32(&form.value("anfrageId"))
		.filter(|n| *n > 0)
		.ok_or_else(bad)?;
	Ok((draft, number))
}
/// Request-drop cancellation is deliberately separate from a persisted registry
/// handle. The tracked consumer owns native/ledger completion after handler drop.
pub(crate) fn request_lifetime(
	parent: &tokio_util::sync::CancellationToken,
) -> (
	tokio_util::sync::CancellationToken,
	tokio_util::sync::DropGuard,
) {
	let cancel = parent.child_token();
	let guard = cancel.clone().drop_guard();
	(cancel, guard)
}
/// Int32.TryParse's integer grammar: ASCII numeric whitespace/sign/digits and
/// its legacy trailing NUL tolerance, not Rust's Unicode Trim or radix syntax.
pub(crate) fn int32(raw: &str) -> Option<i32> {
	let text = raw.trim_start_matches([' ', '\t', '\n', '\r', '\x0b', '\x0c']);
	let text = text
		.trim_end_matches('\0')
		.trim_end_matches([' ', '\t', '\n', '\r', '\x0b', '\x0c']);
	let digits = text.strip_prefix(['+', '-']).unwrap_or(text);
	if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
		return None;
	}
	text.parse().ok()
}
