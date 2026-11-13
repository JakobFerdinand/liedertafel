//! CLR Guid.TryParse for STRING/domain/CLI/path inputs, never typed JSON Guid.
//! Grammar follows .NET 10 System.Private.CoreLib Guid.cs (MIT, .NET Foundation):
//! https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Guid.cs
//! Copyright (c) .NET Foundation and Contributors; full MIT notice:
//! ../../../tests/oracles/dotnet-runtime-LICENSE.txt
//! Independent actual-BCL fixtures/provenance live under tests/fixtures/guid-bcl.json.
use crate::auth::AuthError;
use axum::{
	extract::{FromRequestParts, Path, Request},
	http::{StatusCode, request::Parts},
	middleware::Next,
	response::Response,
};
use uuid::Uuid;

pub(crate) fn try_parse(raw: &str) -> Option<Uuid> {
	let text = raw.trim();
	let bytes = text.as_bytes();
	if bytes.len() < 32 {
		return None;
	}
	match bytes[0] {
		b'(' => {
			if bytes.len() != 38 || bytes[37] != b')' {
				return None;
			}
			d(&text[1..37])
		}
		b'{' if bytes.get(9) == Some(&b'-') => {
			if bytes.len() != 38 || bytes[37] != b'}' {
				return None;
			}
			d(&text[1..37])
		}
		b'{' => x(text),
		_ if bytes.get(8) == Some(&b'-') => d(text),
		_ => {
			if bytes.len() == 32 && bytes.iter().all(u8::is_ascii_hexdigit) {
				Uuid::parse_str(text).ok()
			} else {
				None
			}
		}
	}
}
// CLR's compatibility converter strips one '+', then one 0x, then arbitrary
// leading zeroes. Empty after these prefixes is zero; only >8 significant hex
// digits overflow UInt32. In X, B/C truncate uint32 to ushort (not range-u16).
fn hex(text: &str) -> Option<u32> {
	let text = text.strip_prefix('+').unwrap_or(text);
	let text = prefix(text).unwrap_or(text).trim_start_matches('0');
	if text.len() > 8 || !text.bytes().all(|c| c.is_ascii_hexdigit()) {
		return None;
	}
	if text.is_empty() {
		Some(0)
	} else {
		u32::from_str_radix(text, 16).ok()
	}
}
fn prefix(text: &str) -> Option<&str> {
	text.strip_prefix("0x").or_else(|| text.strip_prefix("0X"))
}
fn d(text: &str) -> Option<Uuid> {
	let b = text.as_bytes();
	if b.len() != 36 || !text.is_ascii() || [8, 13, 18, 23].iter().any(|i| b[*i] != b'-') {
		return None;
	}
	let a = hex(&text[..8])?;
	let b = hex(&text[9..13])?;
	let c = hex(&text[14..18])?;
	let de = hex(&text[19..23])?;
	let fg = hex(&text[24..28])?;
	let tail = &text[28..];
	if !tail.bytes().all(|c| c.is_ascii_hexdigit()) {
		return None;
	}
	Uuid::parse_str(&format!("{a:08x}-{b:04x}-{c:04x}-{de:04x}-{fg:04x}{tail}")).ok()
}
fn x(text: &str) -> Option<Uuid> {
	// CLR X removes Char.IsWhiteSpace everywhere, including inside tokens.
	let text = text
		.chars()
		.filter(|c| !c.is_whitespace())
		.collect::<String>();
	let inner = text.strip_prefix('{')?.strip_suffix('}')?;
	let mut fields = inner.splitn(4, ',');
	let component = |part: &str| {
		let value = prefix(part)?;
		if value.is_empty() { None } else { hex(value) }
	};
	let a = component(fields.next()?)?;
	let b = component(fields.next()?)? as u16;
	let c = component(fields.next()?)? as u16;
	let bytes = fields
		.next()?
		.strip_prefix('{')?
		.strip_suffix('}')?
		.split(',')
		.map(|v| u8::try_from(component(v)?).ok())
		.collect::<Option<Vec<_>>>()?;
	let bytes: [u8; 8] = bytes.try_into().ok()?;
	let tail = bytes[2..]
		.iter()
		.map(|v| format!("{v:02x}"))
		.collect::<String>();
	Uuid::parse_str(&format!(
		"{a:08x}-{b:04x}-{c:04x}-{:02x}{:02x}-{tail}",
		bytes[0], bytes[1]
	))
	.ok()
}

/// Unconstrained CLR Guid binding, with the port's controlled JSON 400 rejection.
pub(crate) struct GuidPath(pub Uuid);
impl<S: Send + Sync> FromRequestParts<S> for GuidPath {
	type Rejection = AuthError;
	async fn from_request_parts(parts: &mut Parts, state: &S) -> Result<Self, AuthError> {
		let bad = || AuthError::Problem(StatusCode::BAD_REQUEST, "Die Anfrage ist ungültig.");
		let Path(text) = Path::<String>::from_request_parts(parts, state)
			.await
			.map_err(|_| bad())?;
		try_parse(&text).map(Self).ok_or_else(bad)
	}
}
/// Attach only to existing :guid-constrained draft routes. Invalid spellings
/// select the API miss before authorization/body parsing; no universal 400.
pub(crate) async fn constrained(request: Request, next: Next) -> Response {
	let (mut parts, body) = request.into_parts();
	let valid = Path::<String>::from_request_parts(&mut parts, &())
		.await
		.ok()
		.is_some_and(|Path(text)| try_parse(&text).is_some());
	if !valid {
		return crate::api::api_miss().await;
	}
	next.run(Request::from_parts(parts, body)).await
}
