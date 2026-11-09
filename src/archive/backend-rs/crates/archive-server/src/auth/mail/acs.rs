//! ACS Email 2023-03-31 keyless handoff; no endpoint/credential/recipient logging.
//! Small transport/token seams let #222 extend templates/ops without a second sender.
use super::{Mail, MailError, MailMessage};
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
		})
	}
	async fn deliver(&self, message: MailMessage) -> Result<(), MailError> {
		let token = self.token.get().await?;
		let url = self
			.endpoint
			.join("emails:send?api-version=2023-03-31")
			.map_err(|_| MailError::Delivery)?;
		let mut response=self.transport.send(AcsRequest {method:"POST",url,bearer:token.clone(),body:json!({"senderAddress":self.from,"recipients":{"to":[{"address":message.to}]},"content":{"subject":message.subject,"plainText":message.text},"userEngagementTrackingDisabled":true})}).await?;
		if response.status != 202 {
			return Err(MailError::Delivery);
		}
		let mut poll = None;
		loop {
			match response.body["status"].as_str() {
				Some("Succeeded") => {
					tracing::info!("Mail accepted by ACS");
					return Ok(());
				}
				Some("NotStarted" | "Running") => {}
				_ => return Err(MailError::Delivery),
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
				.send(AcsRequest {
					method: "GET",
					url,
					bearer: token.clone(),
					body: Value::Null,
				})
				.await?;
			if response.status != 200 {
				return Err(MailError::Delivery);
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
			tokio::time::timeout(self.timeout, self.deliver(message))
				.await
				.map_err(|_| MailError::Timeout)?
		})
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
			let mut builder = self
				.0
				.request(method, request.url)
				.bearer_auth(request.bearer);
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
			let body = serde_json::from_slice(&bytes).map_err(|_| MailError::Delivery)?;
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
	// Connection-string dev authentication/ops is owned by #222. Keyless Azure
	// code sign-in uses the official SDK credential, constructed without network.
	if std::env::var("Mail__AzureConnectionString").is_ok_and(|s| !s.is_empty()) {
		return Err(AuthError::Configuration);
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
	let client = reqwest::Client::builder()
		.https_only(true)
		.redirect(reqwest::redirect::Policy::none())
		.retry(reqwest::retry::never())
		.connect_timeout(Duration::from_secs(10))
		.timeout(Duration::from_secs(45))
		.build()
		.map_err(|_| AuthError::Configuration)?;
	let from =
		std::env::var("Auth__MailFrom").unwrap_or_else(|_| "archiv@liedertafel-mining.at".into());
	let timeout = std::env::var("Mail__SendTimeout")
		.ok()
		.map(|s| super::super::config::parse_duration(&s))
		.transpose()?
		.map(|d| d.to_std().map_err(|_| AuthError::Configuration))
		.transpose()?
		.unwrap_or(Duration::from_secs(45));
	Ok(Arc::new(AzureMail::new(
		endpoint,
		from,
		Arc::new(EntraToken(credential)),
		Arc::new(HttpTransport(client)),
		timeout,
	)?))
}
