//! Local-only service SAS per Microsoft's REST spec (2020-12-06+ layout).
//! The official azure_storage_sas builder intentionally takes delegation keys,
//! not account keys. Do not manufacture a delegation key or change sv after signing.
use super::StorageError;
use azure_core::credentials::Secret;
use chrono::{DateTime, SecondsFormat, Utc};
pub const EMULATOR_VERSION: &str = "2025-11-05";

/// Official per-try extension: refresh date/signature on retries. This is
/// Blob/Queue Shared Key, not Table Shared Key or account/container SAS.
pub(super) struct SharedKeyPolicy {
	account: String,
	key: Secret,
	clock: std::sync::Arc<dyn crate::auth::Clock>,
}
impl std::fmt::Debug for SharedKeyPolicy {
	fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("SharedKeyPolicy { redacted }")
	}
}
impl SharedKeyPolicy {
	pub(super) fn new(
		account: String,
		key: Secret,
		clock: std::sync::Arc<dyn crate::auth::Clock>,
	) -> Self {
		Self {
			account,
			key,
			clock,
		}
	}
}
#[async_trait::async_trait] // Official SDK's dyn Policy trait requires its macro.
impl azure_core::http::policies::Policy for SharedKeyPolicy {
	async fn send(
		&self,
		ctx: &azure_core::http::Context,
		request: &mut azure_core::http::Request,
		next: &[std::sync::Arc<dyn azure_core::http::policies::Policy>],
	) -> azure_core::http::policies::PolicyResult {
		request.insert_header(
			"x-ms-date",
			self.clock
				.now()
				.format("%a, %d %b %Y %H:%M:%S GMT")
				.to_string(),
		);
		// Generated SDK operations set the Body, not Content-Length. The
		// transport adds the latter too late for Shared Key signing; sign and
		// send the same known length without reading/buffering a stream.
		if request
			.headers()
			.get_optional_str(&"content-length".into())
			.is_none()
			&& let Some(length) = request.body().len()
		{
			request.insert_header("content-length", length.to_string());
		}
		let get = |name: &str| {
			request
				.headers()
				.get_optional_str(&name.to_string().into())
				.unwrap_or_default()
				.to_owned()
		};
		let mut standard = vec![request.method().to_string()];
		for name in [
			"content-encoding",
			"content-language",
			"content-length",
			"content-md5",
			"content-type",
			"date",
			"if-modified-since",
			"if-match",
			"if-none-match",
			"if-unmodified-since",
			"range",
		] {
			let mut value = get(name);
			if name == "date" || (name == "content-length" && value == "0") {
				value.clear();
			}
			standard.push(value);
		}
		let mut ms: Vec<_> = request
			.headers()
			.iter()
			.filter(|(k, _)| k.as_str().starts_with("x-ms-"))
			.map(|(k, v)| (k.as_str(), v.as_str().trim()))
			.collect();
		ms.sort_by_key(|(k, _)| *k);
		let mut to_sign = standard.join("\n");
		to_sign.push('\n');
		for (name, value) in ms {
			to_sign.push_str(name);
			to_sign.push(':');
			to_sign.push_str(value);
			to_sign.push('\n');
		}
		// The URI path is already encoded. IP emulator paths contain the account
		// once already, hence the deliberate account/account duplication here.
		to_sign.push_str(&format!("/{}{}", self.account, request.url().path()));
		let mut query = std::collections::BTreeMap::<String, Vec<String>>::new();
		for (k, v) in request.url().query_pairs() {
			query
				.entry(k.to_ascii_lowercase())
				.or_default()
				.push(v.into_owned());
		}
		for (k, mut values) in query {
			values.sort();
			to_sign.push('\n');
			to_sign.push_str(&k);
			to_sign.push(':');
			to_sign.push_str(&values.join(","));
		}
		let signature = azure_core::hmac::hmac_sha256(&to_sign, &self.key)?;
		request.insert_header(
			"authorization",
			format!("SharedKey {}:{signature}", self.account),
		);
		let next_policy = next.first().ok_or_else(|| {
			azure_core::Error::with_message(
				azure_core::error::ErrorKind::Other,
				"storage pipeline missing transport",
			)
		})?;
		next_policy.send(ctx, request, &next[1..]).await
	}
}
pub(super) fn service_sas(
	credential: (&str, &Secret),
	container: &str,
	blob: &str,
	expiry: DateTime<Utc>,
	upload: bool,
	download: bool,
	content_type: Option<&str>,
) -> Result<String, StorageError> {
	let (account, key) = credential;
	let permission = if upload { "rcw" } else { "r" };
	let expiry = expiry.to_rfc3339_opts(SecondsFormat::Secs, true);
	let content_type = content_type.filter(|s| !s.is_empty()).unwrap_or_default();
	let disposition = if content_type.is_empty() {
		""
	} else if download {
		"attachment"
	} else {
		"inline"
	};
	let resource = format!("/blob/{account}/{container}/{blob}");
	let to_sign = [
		permission,
		"",
		&expiry,
		&resource,
		"",
		"",
		"",
		EMULATOR_VERSION,
		"b",
		"",
		"",
		"",
		disposition,
		"",
		"",
		content_type,
	]
	.join("\n");
	let signature = azure_core::hmac::hmac_sha256(&to_sign, key)?;
	let mut query = url::form_urlencoded::Serializer::new(String::new());
	query
		.append_pair("sv", EMULATOR_VERSION)
		.append_pair("sr", "b")
		.append_pair("sp", permission)
		.append_pair("se", &expiry);
	if !content_type.is_empty() {
		query
			.append_pair("rsct", content_type)
			.append_pair("rscd", disposition);
	}
	query.append_pair("sig", &signature);
	Ok(query.finish())
}
