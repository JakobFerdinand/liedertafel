//! ACS Email 2023-03-31 keyless handoff; no endpoint/credential/recipient logging.
//! Small transport/token seams let #222 extend templates/ops without a second sender.
use super::{Mail, MailError, MailMessage};
use axum::http::HeaderMap;
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use std::{future::Future, pin::Pin, sync::Arc, time::Duration};
use url::Url;

pub trait AccessToken: Send + Sync {
	fn get(&self) -> Pin<Box<dyn Future<Output = Result<String, MailError>> + Send + '_>>;
}
pub struct AcsRequest {
	pub method: &'static str,
	pub url: Url,
	pub bearer: String,
	pub body: Value,
	pub headers: HeaderMap,
}
pub struct AcsResponse {
	pub status: u16,
	pub operation_location: Option<String>,
	pub retry_after: Duration,
	pub body: Value,
}
pub trait AcsTransport: Send + Sync {
	fn send(
		&self,
		request: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>>;
}

pub struct AzureMail {
	endpoint: Url,
	from: String,
	token: Arc<dyn AccessToken>,
	transport: Arc<dyn AcsTransport>,
	timeout: Duration,
	shared_key: Option<Vec<u8>>,
}
impl AzureMail {
	pub fn new(
		endpoint: Url,
		from: String,
		token: Arc<dyn AccessToken>,
		transport: Arc<dyn AcsTransport>,
		timeout: Duration,
	) -> Result<Self, super::super::AuthError> {
		if endpoint.scheme() != "https"
			|| endpoint.host_str().is_none()
			|| !endpoint.username().is_empty()
			|| endpoint.password().is_some()
			|| endpoint.query().is_some()
			|| endpoint.fragment().is_some()
			|| endpoint.path() != "/"
			|| timeout.is_zero()
			|| timeout > Duration::from_secs(120)
		{
			return Err(super::super::AuthError::Configuration);
		}
		Ok(Self {
			endpoint,
			from,
			token,
			transport,
			timeout,
			shared_key: None,
		})
	}
	/// Development-only caller guard lives in `from_env`; this constructor is
	/// also the offline delivery seam, never enabled for production configuration.
	pub fn with_connection_string(
		value: &str,
		from: String,
		transport: Arc<dyn AcsTransport>,
		timeout: Duration,
	) -> Result<Self, super::super::AuthError> {
		let mut endpoint = None;
		let mut key = None;
		for item in value.split(';').filter(|s| !s.trim().is_empty()) {
			let (name, value) = item
				.split_once('=')
				.ok_or(super::super::AuthError::Configuration)?;
			if name.trim().eq_ignore_ascii_case("endpoint") && endpoint.is_none() {
				endpoint = value.trim().parse().ok();
			} else if name.trim().eq_ignore_ascii_case("accesskey") && key.is_none() {
				key = STANDARD.decode(value.trim()).ok().filter(|k| !k.is_empty());
			} else {
				return Err(super::super::AuthError::Configuration);
			}
		}
		let mut sender = Self::new(
			endpoint.ok_or(super::super::AuthError::Configuration)?,
			from,
			Arc::new(NoToken),
			transport,
			timeout,
		)?;
		sender.shared_key = Some(key.ok_or(super::super::AuthError::Configuration)?);
		Ok(sender)
	}
	fn request(
		&self,
		method: &'static str,
		url: Url,
		bearer: String,
		body: Value,
	) -> Result<AcsRequest, MailError> {
		let mut headers = HeaderMap::new();
		if let Some(key) = &self.shared_key {
			use hmac::{Hmac, Mac};
			use sha2::{Digest, Sha256};
			let content = if method == "POST" {
				serde_json::to_vec(&body).map_err(|_| MailError::Delivery)?
			} else {
				vec![]
			};
			let hash = STANDARD.encode(Sha256::digest(content));
			let date = chrono::Utc::now()
				.format("%a, %d %b %Y %H:%M:%S GMT")
				.to_string();
			let host = &url[url::Position::BeforeHost..url::Position::AfterPort];
			let path = &url[url::Position::BeforePath..url::Position::AfterQuery];
			let string_to_sign = format!("{method}\n{path}\n{date};{host};{hash}");
			let mut mac = Hmac::<Sha256>::new_from_slice(key).map_err(|_| MailError::Delivery)?;
			mac.update(string_to_sign.as_bytes());
			let authorization = format!(
				"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={}",
				STANDARD.encode(mac.finalize().into_bytes())
			);
			for (name, value) in [
				("x-ms-date", date),
				("x-ms-content-sha256", hash),
				("authorization", authorization),
			] {
				let mut value: axum::http::HeaderValue =
					value.parse().map_err(|_| MailError::Delivery)?;
				// Defense in depth beneath the process-wide HTTP-log suppression:
				// authorization and mail-content digests must redact in Debug too.
				value.set_sensitive(name != "x-ms-date");
				headers.insert(name, value);
			}
		}
		Ok(AcsRequest {
			method,
			url,
			bearer,
			body,
			headers,
		})
	}
	async fn deliver(&self, message: MailMessage) -> Result<(), MailError> {
		let token = self.token.get().await?;
		let url = self
			.endpoint
			.join("/emails:send?api-version=2023-03-31")
			.map_err(|_| MailError::Delivery)?;
		let mut response=self.transport.send(self.request("POST",url,token.clone(),json!({"senderAddress":self.from,"recipients":{"to":[{"address":message.to.trim()}]},"content":{"subject":message.subject,"plainText":message.text},"userEngagementTrackingDisabled":true}))?).await?;
		if response.status != 202 {
			return Err(service_error(&response, None));
		}
		let mut poll = None;
		let mut operation_id = None;
		loop {
			operation_id = response.body["id"]
				.as_str()
				.and_then(|s| s.parse::<uuid::Uuid>().ok())
				.or(operation_id);
			match response.body["status"].as_str() {
				Some("Succeeded") => {
					tracing::info!(operation_id=?operation_id,"Mail accepted by ACS");
					return Ok(());
				}
				Some("NotStarted" | "Running") => {}
				_ => return Err(service_error(&response, operation_id)),
			}
			if let Some(location) = response.operation_location.take() {
				let next = Url::parse(&location).map_err(|_| MailError::Delivery)?;
				// Prevent token exfiltration / downgrade / userinfo, including redirects.
				if next.origin() != self.endpoint.origin()
					|| !next.username().is_empty()
					|| next.password().is_some()
					|| next.fragment().is_some()
					|| !next
						.path()
						.trim_start_matches('/')
						.starts_with("emails/operations/")
				{
					return Err(MailError::Delivery);
				}
				poll = Some(next);
			}
			let url = poll.clone().ok_or(MailError::Delivery)?;
			tokio::time::sleep(response.retry_after).await;
			response = self
				.transport
				.send(self.request("GET", url, token.clone(), Value::Null)?)
				.await?;
			if response.status != 200 {
				return Err(service_error(&response, operation_id));
			}
		}
	}
}
impl Mail for AzureMail {
	fn send<'a>(
		&'a self,
		message: MailMessage,
	) -> Pin<Box<dyn Future<Output = Result<(), MailError>> + Send + 'a>> {
		Box::pin(async move {
			match tokio::time::timeout(self.timeout, self.deliver(message)).await {
				Err(_) => {
					tracing::error!(error_code = "SendTimeout", "Azure mail timed out");
					Err(MailError::Timeout)
				}
				Ok(result) => {
					if matches!(result, Err(MailError::Delivery)) {
						tracing::error!("Azure mail delivery failed");
					}
					result
				}
			}
		})
	}
}

struct NoToken;
impl AccessToken for NoToken {
	fn get(&self) -> Pin<Box<dyn Future<Output = Result<String, MailError>> + Send + '_>> {
		Box::pin(async { Ok(String::new()) })
	}
}
fn service_error(response: &AcsResponse, operation_id: Option<uuid::Uuid>) -> MailError {
	let operation_id = response.body["id"]
		.as_str()
		.and_then(|s| s.parse().ok())
		.or(operation_id);
	// Only structured diagnostics, never service messages/URLs/content. Restrict
	// error-code syntax; operation IDs must parse as GUIDs before any logging.
	let code = response.body["error"]["code"]
		.as_str()
		.or(response.body["status"].as_str())
		.map(|code| match code {
			"SenderRejected"
			| "SenderNotAllowed"
			| "InvalidSenderAddress"
			| "InvalidRecipientAddress"
			| "Denied"
			| "Unauthorized"
			| "Forbidden"
			| "TooManyRequests"
			| "DomainNotLinked"
			| "Failed"
			| "Canceled" => code.to_owned(),
			_ => "ServiceError".to_owned(),
		});
	tracing::error!(status=response.status,error_code=?code,operation_id=?operation_id,"Azure mail handoff failed");
	MailError::Azure {
		status: response.status,
		error_code: code,
		operation_id,
	}
}

struct EntraToken(Arc<dyn azure_core::credentials::TokenCredential>);
impl AccessToken for EntraToken {
	fn get(&self) -> Pin<Box<dyn Future<Output = Result<String, MailError>> + Send + '_>> {
		Box::pin(async {
			Ok(self
				.0
				.get_token(&["https://communication.azure.com/.default"], None)
				.await
				.map_err(|_| MailError::Delivery)?
				.token
				.secret()
				.to_owned())
		})
	}
}
struct HttpTransport(reqwest::Client);
impl AcsTransport for HttpTransport {
	fn send(
		&self,
		request: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>> {
		Box::pin(async move {
			let method = if request.method == "POST" {
				reqwest::Method::POST
			} else {
				reqwest::Method::GET
			};
			let mut builder = self.0.request(method, request.url).headers(request.headers);
			if !request.bearer.is_empty() {
				builder = builder.bearer_auth(request.bearer);
			}
			if request.method == "POST" {
				builder = builder.json(&request.body);
			}
			let mut response = builder.send().await.map_err(|_| MailError::Delivery)?;
			let status = response.status().as_u16();
			let operation_location = response
				.headers()
				.get("operation-location")
				.and_then(|s| s.to_str().ok())
				.map(str::to_owned);
			let retry_after = Duration::from_secs(
				response
					.headers()
					.get("retry-after")
					.and_then(|s| s.to_str().ok())
					.and_then(|s| s.parse().ok())
					.unwrap_or(1),
			);
			let mut bytes = vec![];
			while let Some(chunk) = response.chunk().await.map_err(|_| MailError::Delivery)? {
				if bytes.len() + chunk.len() > 65_536 {
					return Err(MailError::Delivery);
				}
				bytes.extend_from_slice(&chunk);
			}
			let body = match serde_json::from_slice(&bytes) {
				Ok(body) => body,
				Err(_) if status != 200 && status != 202 => json!({}),
				Err(_) => return Err(MailError::Delivery),
			};
			Ok(AcsResponse {
				status,
				operation_location,
				retry_after,
				body,
			})
		})
	}
}

pub(super) fn from_env(development: bool) -> Result<Arc<dyn Mail>, super::super::AuthError> {
	use super::super::AuthError;
	let connection = std::env::var("Mail__AzureConnectionString")
		.ok()
		.filter(|s| !s.trim().is_empty());
	if !development && connection.is_some() {
		return Err(AuthError::Configuration);
	}
	let client = reqwest::Client::builder()
		.https_only(true)
		.redirect(reqwest::redirect::Policy::none())
		.retry(reqwest::retry::never())
		.connect_timeout(Duration::from_secs(10))
		.timeout(Duration::from_secs(120))
		.build()
		.map_err(|_| AuthError::Configuration)?;
	let from =
		std::env::var("Auth__MailFrom").unwrap_or_else(|_| "archiv@liedertafel-mining.at".into());
	let timeout = std::env::var("Mail__SendTimeout")
		.ok()
		.map(|s| super::super::config::parse_duration(&s))
		.transpose()?
		.filter(|d| *d > chrono::Duration::zero())
		.map(|d| d.to_std().map_err(|_| AuthError::Configuration))
		.transpose()?
		.unwrap_or(Duration::from_secs(45));
	if let Some(connection) = connection {
		return Ok(Arc::new(AzureMail::with_connection_string(
			&connection,
			from,
			Arc::new(HttpTransport(client)),
			timeout,
		)?));
	}
	let endpoint = std::env::var("Mail__AzureEndpoint")
		.ok()
		.and_then(|s| s.parse().ok())
		.ok_or(AuthError::Configuration)?;
	let credential: Arc<dyn azure_core::credentials::TokenCredential> = if development {
		azure_identity::DeveloperToolsCredential::new(None).map_err(|_| AuthError::Configuration)?
	} else {
		let options = azure_identity::ManagedIdentityCredentialOptions {
			user_assigned_id: std::env::var("AZURE_CLIENT_ID")
				.ok()
				.filter(|s| !s.is_empty())
				.map(azure_identity::UserAssignedId::ClientId),
			..Default::default()
		};
		azure_identity::ManagedIdentityCredential::new(Some(options))
			.map_err(|_| AuthError::Configuration)?
	};
	Ok(Arc::new(AzureMail::new(
		endpoint,
		from,
		Arc::new(EntraToken(credential)),
		Arc::new(HttpTransport(client)),
		timeout,
	)?))
}
