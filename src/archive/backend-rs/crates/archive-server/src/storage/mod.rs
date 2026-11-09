//! Blob-scoped tickets, not an API file proxy. Azure clients remain the provider
//! boundary; callers authorize visible database rows before asking for a ticket.
mod shared_key;
use crate::auth::Clock;
use azure_core::credentials::Secret;
use azure_core::{
	credentials::TokenCredential,
	http::{ClientOptions, ExponentialRetryOptions, RequestContent, RetryOptions, Transport},
};
use azure_storage_blob::BlobServiceClient;
use azure_storage_blob::models::{
	AccessTier, BlobClientDownloadOptions, BlobClientGetPropertiesResultHeaders,
};
use azure_storage_common::models::UserDelegationKey;
use chrono::Duration;
use futures::future::BoxFuture;
use futures::{StreamExt, TryStreamExt, stream::BoxStream};
use std::sync::Arc;
use url::Url;

pub struct ObjectInfo {
	pub size_bytes: u64,
	pub content_type: Option<String>,
	etag: azure_core::http::Etag,
}
#[derive(Clone)]
pub struct ObjectMetadata {
	pub size_bytes: u64,
	pub content_type: Option<String>,
}
/// Forward-only, bounded SDK partitions. No claim of .NET's seekable OpenRead:
/// later random-access consumers must use ETag-pinned ranges, not collect a file.
pub type BlobRead = BoxStream<'static, Result<azure_core::Bytes, StorageError>>;

#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum StorageError {
	#[error("Kein Blob-Verbindungsstring konfiguriert.")]
	Configuration,
	#[error("Speicherdienst nicht erreichbar.")]
	Unavailable,
}
impl From<azure_core::Error> for StorageError {
	fn from(_: azure_core::Error) -> Self {
		Self::Unavailable
	}
}

pub trait Storage: Send + Sync {
	fn probe<'a>(
		&'a self,
		_blob: &'a str,
	) -> BoxFuture<'a, Result<Option<ObjectMetadata>, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn read_header<'a>(
		&'a self,
		_blob: &'a str,
		_length: usize,
	) -> BoxFuture<'a, Result<Option<Vec<u8>>, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn promote<'a>(
		&'a self,
		_source: &'a str,
		_target: &'a str,
	) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn upload_ticket<'a>(
		&'a self,
		_blob: &'a str,
		_lifetime: Duration,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn delete<'a>(&'a self, _blob: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn read_ticket<'a>(
		&'a self,
		blob: &'a str,
		lifetime: Duration,
		download: bool,
		content_type: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>>;
	fn set_tier<'a>(
		&'a self,
		blob: &'a str,
		tier: &'a str,
	) -> BoxFuture<'a, Result<(), StorageError>>;
}

#[derive(Clone)]
pub struct StorageConfig {
	pub container: String,
	pub read_lifetime: Duration,
	endpoint: Option<Url>,
	account: String,
	key: Option<Secret>,
	development: bool,
}
impl Default for StorageConfig {
	fn default() -> Self {
		Self {
			container: "archive-assets".into(),
			read_lifetime: Duration::minutes(15),
			endpoint: None,
			account: String::new(),
			key: None,
			development: false,
		}
	}
}
impl StorageConfig {
	pub fn select(
		connection: Option<&str>,
		service_uri: Option<&str>,
		container: Option<&str>,
		development: bool,
	) -> Result<Self, StorageError> {
		let container = container.unwrap_or("archive-assets");
		let mut config = if let Some(connection) = connection.filter(|s| !s.is_empty()) {
			Self::connection_string(connection, container)?
		} else if let Some(uri) = service_uri.filter(|s| !s.trim().is_empty()) {
			Self::service_uri(uri, container)?
		} else {
			Self {
				container: container.into(),
				..Self::default()
			}
		};
		config.development = development;
		Ok(config)
	}
	pub fn from_env(development: bool) -> Result<Self, StorageError> {
		let mut config = Self::select(
			std::env::var("ConnectionStrings__archive-blobs")
				.ok()
				.as_deref(),
			std::env::var("Archive__Assets__ServiceUri").ok().as_deref(),
			std::env::var("Archive__Assets__ContainerName")
				.ok()
				.as_deref(),
			development,
		)?;
		if let Ok(value) = std::env::var("Archive__Assets__ReadTicketLifetime") {
			config.read_lifetime = parse_lifetime(&value).ok_or(StorageError::Configuration)?;
		}
		Ok(config)
	}
	pub fn service_uri(value: &str, container: &str) -> Result<Self, StorageError> {
		let endpoint = Url::parse(value).map_err(|_| StorageError::Configuration)?;
		if endpoint.scheme() != "https"
			|| endpoint.host_str().is_none()
			|| !endpoint.username().is_empty()
			|| endpoint.password().is_some()
			|| endpoint.query().is_some()
			|| endpoint.fragment().is_some()
			|| endpoint.path() != "/"
		{
			return Err(StorageError::Configuration);
		}
		let account = endpoint
			.host_str()
			.and_then(|s| s.split('.').next())
			.ok_or(StorageError::Configuration)?
			.to_string();
		Ok(Self {
			endpoint: Some(endpoint),
			account,
			container: container.into(),
			..Self::default()
		})
	}
	pub fn connection_string(value: &str, container: &str) -> Result<Self, StorageError> {
		let fields: std::collections::HashMap<_, _> = value
			.split(';')
			.filter(|v| !v.is_empty())
			.map(|v| v.split_once('=').ok_or(StorageError::Configuration))
			.collect::<Result<_, _>>()?;
		let account = fields
			.get("AccountName")
			.ok_or(StorageError::Configuration)?
			.to_string();
		let key = fields
			.get("AccountKey")
			.ok_or(StorageError::Configuration)?;
		use base64::{Engine, engine::general_purpose::STANDARD};
		if account.is_empty()
			|| STANDARD
				.decode(key)
				.map_err(|_| StorageError::Configuration)?
				.is_empty()
		{
			return Err(StorageError::Configuration);
		}
		let mut endpoint = match fields.get("BlobEndpoint") {
			Some(v) => Url::parse(v),
			None => Url::parse(&format!(
				"{}://{account}.blob.{}",
				fields.get("DefaultEndpointsProtocol").unwrap_or(&"https"),
				fields.get("EndpointSuffix").unwrap_or(&"core.windows.net")
			)),
		}
		.map_err(|_| StorageError::Configuration)?;
		if !matches!(endpoint.scheme(), "http" | "https")
			|| endpoint.host_str().is_none()
			|| !endpoint.username().is_empty()
			|| endpoint.password().is_some()
			|| endpoint.query().is_some()
			|| endpoint.fragment().is_some()
		{
			return Err(StorageError::Configuration);
		}
		// Official client derivation appends path segments verbatim. Normalize
		// the connection-string service endpoint as .NET does, so a trailing
		// slash cannot introduce an empty container segment in an emulator URL.
		endpoint
			.path_segments_mut()
			.map_err(|_| StorageError::Configuration)?
			.pop_if_empty();
		Ok(Self {
			endpoint: Some(endpoint),
			account,
			key: Some(Secret::new(key.to_string())),
			container: container.into(),
			..Self::default()
		})
	}
}
pub struct BlobStorage {
	config: StorageConfig,
	clock: Arc<dyn Clock>,
	client: Option<BlobServiceClient>,
	delegation_key: tokio::sync::Mutex<Option<UserDelegationKey>>,
}
impl std::fmt::Debug for BlobStorage {
	fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
		f.write_str("BlobStorage { redacted }")
	}
}
impl BlobStorage {
	pub fn new(config: StorageConfig, clock: Arc<dyn Clock>) -> Result<Self, StorageError> {
		let credential: Option<Arc<dyn TokenCredential>> =
			if config.endpoint.is_some() && config.key.is_none() {
				Some(if config.development {
					azure_identity::DeveloperToolsCredential::new(None)
						.map_err(|_| StorageError::Configuration)?
				} else {
					azure_identity::ManagedIdentityCredential::new(Some(managed_identity_options(
						std::env::var("AZURE_CLIENT_ID").ok().as_deref(),
					)))
					.map_err(|_| StorageError::Configuration)?
				})
			} else {
				None
			};
		Self::construct(config, clock, credential, None)
	}
	/// Provider seam: only credential acquisition and HTTP delivery may be replaced.
	pub fn with_credential(
		config: StorageConfig,
		clock: Arc<dyn Clock>,
		credential: Arc<dyn TokenCredential>,
		transport: Option<Transport>,
	) -> Result<Self, StorageError> {
		if config.key.is_some() {
			return Err(StorageError::Configuration);
		}
		Self::construct(config, clock, Some(credential), transport)
	}
	fn construct(
		config: StorageConfig,
		clock: Arc<dyn Clock>,
		credential: Option<Arc<dyn TokenCredential>>,
		transport: Option<Transport>,
	) -> Result<Self, StorageError> {
		let mut options = azure_storage_blob::BlobServiceClientOptions {
			client_options: client_options(transport)?,
			..Default::default()
		};
		if let Some(key) = &config.key {
			options.version = shared_key::EMULATOR_VERSION.into();
			options.client_options.per_try_policies.push(Arc::new(
				shared_key::SharedKeyPolicy::new(
					config.account.clone(),
					key.clone(),
					clock.clone(),
				),
			));
		}
		let client = config
			.endpoint
			.clone()
			.map(|url| BlobServiceClient::new(url, credential, Some(options)))
			.transpose()?;
		Ok(Self {
			config,
			clock,
			client,
			delegation_key: tokio::sync::Mutex::new(None),
		})
	}
	#[tracing::instrument(name="archive.assets.upload_ticket",skip_all,err,fields(otel.kind="client"))]
	pub async fn upload_ticket(
		&self,
		blob: &str,
		lifetime: Duration,
	) -> Result<String, StorageError> {
		self.ticket(blob, lifetime, true, false, None).await
	}
	#[tracing::instrument(name="archive.assets.read_ticket",skip_all,err,fields(otel.kind="client"))]
	async fn read_ticket_inner(
		&self,
		blob: &str,
		lifetime: Duration,
		download: bool,
		content_type: Option<&str>,
	) -> Result<String, StorageError> {
		self.ticket(blob, lifetime, false, download, content_type)
			.await
	}
	pub fn service_client(&self) -> Result<&BlobServiceClient, StorageError> {
		self.client.as_ref().ok_or(StorageError::Configuration)
	}
	#[tracing::instrument(name="archive.assets.probe",skip_all,err,fields(otel.kind="client"))]
	pub async fn probe(&self, blob: &str) -> Result<Option<ObjectInfo>, StorageError> {
		match self
			.service_client()?
			.blob_client(&self.config.container, blob)
			.get_properties(None)
			.await
		{
			Ok(props) => Ok(Some(ObjectInfo {
				size_bytes: props.content_length()?.ok_or(StorageError::Unavailable)?,
				content_type: props.content_type()?,
				etag: props.etag()?.ok_or(StorageError::Unavailable)?,
			})),
			Err(e) if missing(&e) => Ok(None),
			Err(_) => Err(StorageError::Unavailable),
		}
	}
	#[tracing::instrument(name="archive.assets.read_header",skip_all,err,fields(otel.kind="client"))]
	pub async fn read_header(
		&self,
		blob: &str,
		length: usize,
	) -> Result<Option<Vec<u8>>, StorageError> {
		let Some(info) = self.probe(blob).await? else {
			return Ok(None);
		};
		let length = length.min(usize::try_from(info.size_bytes).unwrap_or(usize::MAX));
		if length == 0 {
			return Ok(Some(vec![]));
		}
		let mut stream = self.stream_range(blob, length as u64, info.etag)?;
		let mut header = Vec::with_capacity(length);
		while header.len() < length {
			let Some(bytes) = stream.next().await else {
				break;
			};
			let bytes = bytes?;
			header.extend_from_slice(&bytes[..bytes.len().min(length - header.len())]);
		}
		Ok(Some(header))
	}
	#[tracing::instrument(name="archive.assets.open_read",skip_all,err,fields(otel.kind="client"))]
	pub async fn open_read(&self, blob: &str) -> Result<Option<BlobRead>, StorageError> {
		let Some(info) = self.probe(blob).await? else {
			return Ok(None);
		};
		if info.size_bytes == 0 {
			return Ok(Some(Box::pin(futures::stream::empty())));
		}
		Ok(Some(self.stream_range(blob, info.size_bytes, info.etag)?))
	}
	fn stream_range(
		&self,
		blob: &str,
		length: u64,
		etag: azure_core::http::Etag,
	) -> Result<BlobRead, StorageError> {
		let client = self
			.service_client()?
			.blob_client(&self.config.container, blob);
		// Ask the official SDK for ONE bounded range at a time. Its managed
		// multi-part download spawns an initial task whose dropped handle does
		// not abort; full-object use would leave network work after cancellation.
		// A range <= partition_size has no background task or whole-file buffer.
		let ranges = futures::stream::try_unfold(
			(client, 0u64, length, etag),
			|(client, start, length, etag)| async move {
				if start >= length {
					return Ok::<_, StorageError>(None);
				}
				let end = start.saturating_add(8 * 1024 * 1024).min(length);
				let options = BlobClientDownloadOptions {
					range: Some((start..end).into()),
					if_match: Some(etag.clone()),
					parallel: std::num::NonZero::new(1),
					partition_size: std::num::NonZero::new(8 * 1024 * 1024),
					..Default::default()
				};
				let response = client.download(Some(options)).await?;
				Ok(Some((
					response.body.map_err(|_| StorageError::Unavailable),
					(client, end, length, etag),
				)))
			},
		);
		Ok(Box::pin(ranges.try_flatten()))
	}
	async fn ticket(
		&self,
		blob: &str,
		lifetime: Duration,
		upload: bool,
		download: bool,
		content_type: Option<&str>,
	) -> Result<String, StorageError> {
		let client = self.client.as_ref().ok_or(StorageError::Configuration)?;
		let expiry = self.clock.now() + lifetime;
		let token = if let Some(key) = self.config.key.as_ref() {
			shared_key::service_sas(
				(&self.config.account, key),
				&self.config.container,
				blob,
				expiry,
				upload,
				download,
				content_type,
			)?
		} else {
			let expiry = timestamp(expiry)?;
			let mut cache = self.delegation_key.lock().await;
			let now = timestamp(self.clock.now())?;
			if !cache.as_ref().is_some_and(|k| {
				k.signed_start.is_some_and(|s| s <= now)
					&& k.signed_expiry.is_some_and(|e| e >= expiry)
			}) {
				let info = azure_storage_blob::models::KeyInfo {
					start: Some(now - time::Duration::minutes(5)),
					expiry: Some(expiry),
					..Default::default()
				};
				let key = client
					.get_user_delegation_key(RequestContent::try_from(info)?, None)
					.await?
					.into_model()?;
				// Validate required fields with the official builder before caching.
				azure_storage_sas::SasBuilder::new(&self.config.account, &key, expiry)?;
				*cache = Some(key);
			}
			let key = cache.as_ref().ok_or(StorageError::Unavailable)?;
			let mut builder =
				azure_storage_sas::SasBuilder::new(&self.config.account, key, expiry)?
					.blob(&self.config.container, blob)
					.read();
			if upload {
				builder = builder.create().write();
			}
			if let Some(content) = content_type.filter(|s| !s.is_empty()) {
				builder = builder
					.content_type(content)
					.content_disposition(if download { "attachment" } else { "inline" });
			}
			builder.build()
		};
		let mut url = client
			.blob_client(&self.config.container, blob)
			.url()
			.clone();
		url.set_query(Some(&token));
		Ok(url.to_string())
	}
}
fn timestamp(value: chrono::DateTime<chrono::Utc>) -> Result<time::OffsetDateTime, StorageError> {
	// KeyInfo and the official SAS builder serialize whole seconds. Compare
	// cache coverage against that same signed window, not discarded fractions
	// (otherwise ordinary UtcNow would refresh the key for every ticket).
	time::OffsetDateTime::from_unix_timestamp(value.timestamp())
		.map_err(|_| StorageError::Unavailable)
}
pub(crate) fn parse_lifetime(value: &str) -> Option<Duration> {
	let value = value.trim();
	let negative = value.starts_with('-');
	let value = value.trim_start_matches('-');
	let parts: Vec<_> = value.split(':').collect();
	let [hours, minutes, seconds] = parts.as_slice() else {
		return None;
	};
	let (days, hours) = hours.split_once('.').unwrap_or(("0", hours));
	let (seconds, fraction) = seconds.split_once('.').unwrap_or((seconds, ""));
	let days = days.parse::<i64>().ok()?;
	let hours = hours.parse::<i64>().ok()?;
	let minutes = minutes.parse::<i64>().ok()?;
	let seconds = seconds.parse::<i64>().ok()?;
	if !(0..60).contains(&minutes)
		|| !(0..60).contains(&seconds)
		|| !(0..24).contains(&hours)
		|| days < 0
		|| fraction.len() > 7
		|| !fraction.bytes().all(|b| b.is_ascii_digit())
	{
		return None;
	}
	let total = days
		.checked_mul(86400)?
		.checked_add(hours * 3600 + minutes * 60 + seconds)?;
	let nanos = if fraction.is_empty() {
		0
	} else {
		fraction
			.parse::<i64>()
			.ok()?
			.checked_mul(10i64.pow(9 - fraction.len() as u32))?
	};
	let result = Duration::try_seconds(total)?.checked_add(&Duration::nanoseconds(nanos))?;
	if negative {
		result.checked_mul(-1)
	} else {
		Some(result)
	}
}

pub fn managed_identity_options(
	client_id: Option<&str>,
) -> azure_identity::ManagedIdentityCredentialOptions {
	azure_identity::ManagedIdentityCredentialOptions {
		user_assigned_id: client_id
			.filter(|s| !s.is_empty())
			.map(|s| azure_identity::UserAssignedId::ClientId(s.into())),
		..Default::default()
	}
}
fn client_options(transport: Option<Transport>) -> Result<ClientOptions, StorageError> {
	let transport = match transport {
		Some(t) => t,
		None => {
			let client = reqwest::Client::builder()
				.connect_timeout(std::time::Duration::from_secs(5))
				.read_timeout(std::time::Duration::from_secs(5))
				.redirect(reqwest::redirect::Policy::none())
				.no_gzip()
				.no_deflate()
				.no_brotli()
				.no_zstd()
				.build()
				.map_err(|_| StorageError::Unavailable)?;
			Transport::new(Arc::new(client))
		}
	};
	Ok(ClientOptions {
		per_try_policies: vec![Arc::new(NetworkDeadline)],
		retry: RetryOptions::exponential(ExponentialRetryOptions {
			max_retries: 2,
			..Default::default()
		}),
		transport: Some(transport),
		..Default::default()
	})
}
#[derive(Debug)]
struct NetworkDeadline;
#[async_trait::async_trait] // Official SDK's dyn Policy trait requires its macro.
impl azure_core::http::policies::Policy for NetworkDeadline {
	async fn send(
		&self,
		ctx: &azure_core::http::Context,
		request: &mut azure_core::http::Request,
		next: &[Arc<dyn azure_core::http::policies::Policy>],
	) -> azure_core::http::policies::PolicyResult {
		let policy = next.first().ok_or_else(|| {
			azure_core::Error::with_message(
				azure_core::error::ErrorKind::Other,
				"storage pipeline missing transport",
			)
		})?;
		tokio::time::timeout(
			std::time::Duration::from_secs(5),
			policy.send(ctx, request, &next[1..]),
		)
		.await
		.map_err(|_| {
			azure_core::Error::with_message(
				azure_core::error::ErrorKind::Io,
				"storage network timeout",
			)
		})?
	}
}
impl Storage for BlobStorage {
	fn upload_ticket<'a>(
		&'a self,
		blob: &'a str,
		lifetime: Duration,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(BlobStorage::upload_ticket(self, blob, lifetime))
	}
	fn probe<'a>(
		&'a self,
		blob: &'a str,
	) -> BoxFuture<'a, Result<Option<ObjectMetadata>, StorageError>> {
		Box::pin(async move {
			Ok(BlobStorage::probe(self, blob)
				.await?
				.map(|p| ObjectMetadata {
					size_bytes: p.size_bytes,
					content_type: p.content_type,
				}))
		})
	}
	fn read_header<'a>(
		&'a self,
		blob: &'a str,
		length: usize,
	) -> BoxFuture<'a, Result<Option<Vec<u8>>, StorageError>> {
		Box::pin(BlobStorage::read_header(self, blob, length))
	}
	fn promote<'a>(
		&'a self,
		source: &'a str,
		target: &'a str,
	) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(self.promote_inner(source, target))
	}
	fn delete<'a>(&'a self, blob: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(self.delete_inner(blob))
	}
	fn read_ticket<'a>(
		&'a self,
		blob: &'a str,
		lifetime: Duration,
		download: bool,
		content_type: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(self.read_ticket_inner(blob, lifetime, download, content_type))
	}
	fn set_tier<'a>(
		&'a self,
		blob: &'a str,
		tier: &'a str,
	) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(self.set_access_tier(blob, tier))
	}
}
impl BlobStorage {
	#[tracing::instrument(name="archive.assets.promote",skip_all,err,fields(otel.kind="client"))]
	async fn promote_inner(&self, source: &str, target: &str) -> Result<(), StorageError> {
		use azure_storage_blob::models::CopyStatus;
		let source = self
			.read_ticket_inner(source, Duration::seconds(60), false, None)
			.await?;
		let target = self
			.service_client()?
			.blob_client(&self.config.container, target);
		target.start_copy_from_url(source, None).await?;
		let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(30);
		while tokio::time::Instant::now() < deadline {
			match target.get_properties(None).await {
				Ok(props) => match props.copy_status()? {
					Some(CopyStatus::Success) => return Ok(()),
					Some(CopyStatus::Aborted | CopyStatus::Failed) => {
						return Err(StorageError::Unavailable);
					}
					_ => {}
				},
				Err(e) if missing(&e) => {}
				Err(_) => return Err(StorageError::Unavailable),
			}
			tokio::time::sleep(std::time::Duration::from_millis(250)).await;
		}
		Err(StorageError::Unavailable)
	}
	#[tracing::instrument(name="archive.assets.delete",skip_all,err,fields(otel.kind="client"))]
	async fn delete_inner(&self, blob: &str) -> Result<(), StorageError> {
		match self
			.service_client()?
			.blob_client(&self.config.container, blob)
			.delete(None)
			.await
		{
			Ok(_) => Ok(()),
			Err(e) if missing(&e) => Ok(()),
			Err(_) => Err(StorageError::Unavailable),
		}
	}
	#[tracing::instrument(name="archive.assets.set_access_tier",skip_all,err,fields(otel.kind="client"))]
	async fn set_access_tier(&self, blob: &str, tier: &str) -> Result<(), StorageError> {
		let tier = match tier {
			"hot" => AccessTier::Hot,
			"cold" => AccessTier::Cold,
			_ => return Err(StorageError::Unavailable),
		};
		self.service_client()?
			.blob_client(&self.config.container, blob)
			.set_tier(tier, None)
			.await?;
		Ok(())
	}
}
fn missing(error: &azure_core::Error) -> bool {
	matches!(error.kind(),azure_core::error::ErrorKind::HttpResponse{status,..} if *status==azure_core::http::StatusCode::NotFound)
}
