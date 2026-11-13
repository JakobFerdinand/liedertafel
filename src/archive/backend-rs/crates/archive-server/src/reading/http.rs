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
		.parse::<mime::Mime>()
		.map_err(|_| bad())?;
	let mut result = FormData::default();
	match content.essence_str() {
		"multipart/form-data" => {
			if content
				.get_param("boundary")
				.is_none_or(|b| b.as_str().len() > 128)
			{
				return Err(bad());
			}
			// Multer normalizes whitespace and overwrites duplicate headers. Keep
			// bounded raw header metadata for the source's wire limits/classifier;
			// multer remains the only parser of section BODY/boundary semantics.
			let boundary = content.get_param("boundary").ok_or_else(bad)?.as_str();
			let (parts, body) = request.into_parts();
			let bytes = axum::body::to_bytes(body, FORM_LIMIT).await.map_err(|_| {
				fail(
					StatusCode::PAYLOAD_TOO_LARGE,
					"Der Anfrageinhalt ist zu groß.",
				)
			})?;
			let mut wire = SectionHeaders::new(&bytes, boundary).map_err(|_| bad())?;
			let request = Request::from_parts(parts, axum::body::Body::from(bytes.clone()));
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
				// FormFeature counts EVERY section, including files and ignored
				// dispositions, before classifying its Content-Disposition.
				count += 1;
				if count > 1024 {
					return Err(bad());
				}
				let headers = wire.read().map_err(|_| bad())?;
				let disposition = section_disposition(&headers).map_err(|_| bad())?;
				let Some((key, file)) = disposition else {
					let ignored = part.bytes().await.map_err(|error| {
						if error.status() == StatusCode::PAYLOAD_TOO_LARGE {
							fail(
								StatusCode::PAYLOAD_TOO_LARGE,
								"Der Anfrageinhalt ist zu groß.",
							)
						} else {
							bad()
						}
					})?;
					wire.consumed(ignored.len()).map_err(|_| bad())?;
					continue;
				};
				if !file {
					let content_type = headers
						.get("content-type")
						.and_then(|v| v.to_str().ok())
						.and_then(|v| v.parse::<mime::Mime>().ok());
					let charset = content_type
						.as_ref()
						.and_then(|t| t.get_param("charset"))
						.map(|p| p.as_str());
					// .NET's built-in UTF-7 aliases throw rather than falling back.
					if charset.is_some_and(|c| {
						matches!(
							c.to_ascii_lowercase().as_str(),
							"utf-7"
								| "csunicode11utf7" | "unicode-1-1-utf-7"
								| "unicode-2-0-utf-7" | "x-unicode-1-1-utf-7"
								| "x-unicode-2-0-utf-7"
						)
					}) {
						return Err(bad());
					}
					let page = charset.and_then(encoding_code_page).unwrap_or(65001);
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
					wire.consumed(bytes.len()).map_err(|_| bad())?;
					result.insert(key, decode_form_value(page, &bytes));
					continue;
				}
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
				wire.consumed(bytes.len()).map_err(|_| bad())?;
				result.files.push((key, bytes.to_vec()));
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
			// ASP.NET UrlDecoder rejects a percent-decoded NUL before Int32
			// binding. Multipart values deliberately retain the legacy tail NUL.
			if bytes.windows(3).any(|w| w == b"%00") {
				return Err(bad());
			}
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

/// Raw section-header cursor only. Body lengths come from multer's completed
/// fields, never a second boundary search through file bytes. This retains OWS
/// and duplicates that multer's HeaderMap discards. Whole form is already capped.
struct SectionHeaders<'a> {
	bytes: &'a [u8],
	delimiter: Vec<u8>,
	at: usize,
}
impl<'a> SectionHeaders<'a> {
	fn new(bytes: &'a [u8], boundary: &str) -> Result<Self, ()> {
		let delimiter = format!("--{boundary}").into_bytes();
		let prefix = &bytes[..bytes.len().min(16384 + delimiter.len() + 2)];
		let at = prefix
			.windows(delimiter.len())
			.position(|w| w == delimiter)
			.ok_or(())?;
		Ok(Self {
			bytes,
			delimiter,
			at,
		})
	}
	fn read(&mut self) -> Result<axum::http::HeaderMap, ()> {
		let rest = self
			.bytes
			.get(self.at..)
			.and_then(|b| b.strip_prefix(self.delimiter.as_slice()))
			.ok_or(())?;
		// Both MultipartReaderStream and multer permit linear transport padding
		// on boundary lines. The frozen ReadLineAsync budget includes its CRLF.
		let padding = rest
			.iter()
			.position(|b| !matches!(b, b' ' | b'\t'))
			.unwrap_or(rest.len());
		if padding + 2 > 100 {
			return Err(());
		}
		let rest = rest[padding..].strip_prefix(b"\r\n").ok_or(())?;
		let scan = &rest[..rest.len().min(16452)];
		let end = scan.windows(4).position(|w| w == b"\r\n\r\n").ok_or(())?;
		let mut headers = axum::http::HeaderMap::new();
		let mut total = 0;
		for line in scan[..end].split(|b| *b == b'\n') {
			let line = line.strip_suffix(b"\r").unwrap_or(line);
			// BufferedReadStream.ReadLineAsync must still fit its CRLF, including
			// the final empty line. The aggregate uses the decoded UTF16 length.
			if line.len() + 2 > 16384 - total {
				return Err(());
			}
			total += String::from_utf8_lossy(line).encode_utf16().count();
			let colon = line.iter().position(|b| *b == b':').ok_or(())?;
			let name = axum::http::HeaderName::from_bytes(&line[..colon]).map_err(|_| ())?;
			let value = &line[colon + 1..];
			let first = value
				.iter()
				.position(|b| !b.is_ascii_whitespace())
				.unwrap_or(value.len());
			let last = value
				.iter()
				.rposition(|b| !b.is_ascii_whitespace())
				.map_or(first, |n| n + 1);
			let value = axum::http::HeaderValue::from_bytes(&value[first..last]).map_err(|_| ())?;
			headers.append(name, value);
			if headers.keys().count() > 16 {
				return Err(());
			}
		}
		if total + 2 > 16384 {
			return Err(());
		}
		self.at = self.bytes.len() - rest.len() + end + 4;
		Ok(headers)
	}
	fn consumed(&mut self, length: usize) -> Result<(), ()> {
		self.at = self
			.at
			.checked_add(length)
			.and_then(|at| at.checked_add(2))
			.filter(|at| *at <= self.bytes.len())
			.ok_or(())?;
		Ok(())
	}
}

fn decode_form_value(page: u16, bytes: &[u8]) -> String {
	// StreamReader detectEncodingFromByteOrderMarks:true: BOM overrides the
	// accepted charset, including UTF32 before its overlapping UTF16LE prefix.
	for (prefix, detected) in [
		(b"\xff\xfe\0\0".as_slice(), 12000),
		(b"\0\0\xfe\xff".as_slice(), 12001),
		(b"\xef\xbb\xbf".as_slice(), 65001),
		(b"\xff\xfe".as_slice(), 1200),
		(b"\xfe\xff".as_slice(), 1201),
	] {
		if let Some(rest) = bytes.strip_prefix(prefix) {
			return decode_header_name(detected, rest);
		}
	}
	decode_header_name(page, bytes)
}

// Multer3.1.0's private metadata extractor explicitly has a case-sensitive
// parameter-name TODO and no filename*. Keep its BODY parser, adapting only
// the already parsed/bounded section header to actual ASP.NET FormFeature rules.
fn section_disposition(headers: &axum::http::HeaderMap) -> Result<Option<(String, bool)>, ()> {
	if headers.get_all("content-disposition").iter().count() != 1 {
		return Err(());
	}
	let raw = headers.get("content-disposition").ok_or(())?;
	let raw = std::str::from_utf8(raw.as_bytes()).map_err(|_| ())?;
	let mut pieces = Vec::new();
	let mut start = 0;
	let mut quoted = false;
	let mut escaped = false;
	for (index, byte) in raw.bytes().enumerate() {
		if escaped {
			escaped = false;
			continue;
		}
		if quoted && byte == b'\\' {
			escaped = true;
			continue;
		}
		if byte == b'"' {
			quoted = !quoted;
		}
		if byte == b';' && !quoted {
			pieces.push(&raw[start..index]);
			start = index + 1;
		}
	}
	if quoted || escaped {
		return Err(());
	}
	pieces.push(&raw[start..]);
	fn trim(s: &str) -> &str {
		s.trim_matches([' ', '\t'])
	}
	let kind = trim(pieces.first().copied().ok_or(())?);
	if !header_token(kind) {
		return Err(());
	}
	let mut parameters = Vec::new();
	for part in pieces.into_iter().skip(1) {
		let part = trim(part);
		if part.is_empty() {
			continue;
		}
		let (key, value) = part
			.split_once('=')
			.map(|(k, v)| (trim(k), Some(trim(v))))
			.unwrap_or((part, None));
		if !header_token(key) {
			return Err(());
		}
		if let Some(value) = value
			&& !(header_token(value)
				|| (value.starts_with('"') && value.ends_with('"') && value.len() >= 2))
		{
			return Err(());
		}
		parameters.push((key, value));
	}
	if !kind.eq_ignore_ascii_case("form-data") {
		return Ok(None);
	}
	let parameter = |name: &str| {
		parameters
			.iter()
			.find(|(key, _)| key.eq_ignore_ascii_case(name))
			.and_then(|(_, value)| *value)
	};
	let name = parameter("name").map(disposition_name).unwrap_or_default();
	let filename = parameter("filename")
		.map(disposition_name)
		.unwrap_or_default();
	let extended = parameter("filename*").is_some_and(|raw| {
		let parts = raw.split('\'').collect::<Vec<_>>();
		parts.len() == 3 && encoding_code_page(parts[0]).is_some() && !parts[2].is_empty()
	});
	Ok(Some((name, !filename.is_empty() || extended)))
}
fn disposition_name(raw: &str) -> String {
	let Some(value) = raw.strip_prefix('"').and_then(|s| s.strip_suffix('"')) else {
		return raw.into();
	};
	// Frozen ContentDispositionHeaderValue.GetName: quoted MIME base64 only;
	// failed/unknown MIME decoding falls back to the unquoted ORIGINAL value.
	let parts = value.split('?').collect::<Vec<_>>();
	if parts.len() == 5 && parts[0] == "=" && parts[4] == "=" && parts[2].eq_ignore_ascii_case("b")
	{
		use base64::Engine;
		let data = parts[3]
			.bytes()
			.filter(|b| !b" \t\r\n".contains(b))
			.collect::<Vec<_>>();
		// Convert.FromBase64String accepts unused trailing bits, but still
		// requires padding/length. This engine is ONLY for quoted MIME names.
		let decoder = base64::engine::general_purpose::GeneralPurpose::new(
			&base64::alphabet::STANDARD,
			base64::engine::general_purpose::GeneralPurposeConfig::new()
				.with_decode_allow_trailing_bits(true),
		);
		if let Some(page) = encoding_code_page(parts[1])
			&& let Ok(bytes) = decoder.decode(data)
		{
			return decode_header_name(page, &bytes);
		}
	}
	value.into()
}
fn decode_header_name(page: u16, bytes: &[u8]) -> String {
	match page {
		65001 => String::from_utf8_lossy(bytes).into_owned(),
		20127 => bytes
			.iter()
			.map(|b| if b.is_ascii() { char::from(*b) } else { '?' })
			.collect(),
		28591 => bytes.iter().copied().map(char::from).collect(),
		1200 | 1201 => {
			let mut units = Vec::new();
			for pair in bytes.chunks(2) {
				units.push(if pair.len() == 2 {
					if page == 1200 {
						u16::from_le_bytes([pair[0], pair[1]])
					} else {
						u16::from_be_bytes([pair[0], pair[1]])
					}
				} else {
					0xfffd
				});
			}
			String::from_utf16_lossy(&units)
		}
		12000 | 12001 => bytes
			.chunks(4)
			.map(|chunk| {
				let Ok(value) = <[u8; 4]>::try_from(chunk) else {
					return '\u{fffd}';
				};
				char::from_u32(if page == 12000 {
					u32::from_le_bytes(value)
				} else {
					u32::from_be_bytes(value)
				})
				.unwrap_or('\u{fffd}')
			})
			.collect(),
		_ => String::new(),
	}
}
fn header_token(value: &str) -> bool {
	!value.is_empty()
		&& value
			.bytes()
			.all(|b| b.is_ascii_alphanumeric() || b"!#$%&'*+-.^_`|~".contains(&b))
}
// Actual .NET10 built-in aliases (UTF-7 is disabled). EncodingData.cs, MIT,
// .NET Foundation; existing notice: tests/oracles/dotnet-runtime-LICENSE.txt.
// https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Text/EncodingData.cs
fn encoding_code_page(name: &str) -> Option<u16> {
	match name.to_ascii_lowercase().as_str() {
		"ansi_x3.4-1968" | "ansi_x3.4-1986" | "ascii" | "cp367" | "csascii" | "ibm367"
		| "iso-ir-6" | "iso646-us" | "iso_646.irv:1991" | "us" | "us-ascii" => Some(20127),
		"cp819" | "csisolatin1" | "ibm819" | "iso-8859-1" | "iso-ir-100" | "iso8859-1"
		| "iso_8859-1" | "iso_8859-1:1987" | "l1" | "latin1" => Some(28591),
		"iso-10646-ucs-2" | "ucs-2" | "unicode" | "utf-16" | "utf-16le" => Some(1200),
		"unicodefffe" | "utf-16be" => Some(1201),
		"utf-32" | "utf-32le" => Some(12000),
		"utf-32be" => Some(12001),
		"unicode-1-1-utf-8"
		| "unicode-2-0-utf-8"
		| "utf-8"
		| "x-unicode-1-1-utf-8"
		| "x-unicode-2-0-utf-8" => Some(65001),
		_ => None,
	}
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
