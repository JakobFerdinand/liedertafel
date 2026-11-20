//! Offline-only native reading cancellation gate. No hosted token/provider can
//! be selected: local test229 DB + loopback HTTP + a synthetic credential only.
use archive_server::{
	ai,
	auth::{Clock, SystemClock},
	reading,
	storage::{BlobStorage, StorageConfig},
};
use azure_core::credentials::{AccessToken, TokenCredential, TokenRequestOptions};
use std::sync::Arc;
use tracing::Instrument;
#[derive(Debug)]
struct Credential;
#[async_trait::async_trait]
impl TokenCredential for Credential {
	async fn get_token(
		&self,
		scopes: &[&str],
		_: Option<TokenRequestOptions<'_>>,
	) -> azure_core::Result<AccessToken> {
		assert_eq!(scopes, ["https://cognitiveservices.azure.com/.default"]);
		Ok(AccessToken::new(
			"synthetic-reading-token-229",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
	let _ = rustls::crypto::ring::default_provider().install_default();
	let defaults =
		archive_telemetry::ServiceDefaults::init(true, "archive-reading-cancel-offline")?;
	let url = std::env::var("ARCHIVE_TEST_READING_DATABASE")?;
	let parsed = url::Url::parse(&url)?;
	assert!(matches!(parsed.host_str(), Some("localhost" | "127.0.0.1")));
	assert!(parsed.path().starts_with("/test229_reading_"));
	let endpoint = std::env::var("ARCHIVE_TEST_READING_ENDPOINT")?;
	let endpoint_url = url::Url::parse(&endpoint)?;
	assert_eq!(endpoint_url.scheme(), "http");
	assert!(matches!(
		endpoint_url.host_str(),
		Some("localhost" | "127.0.0.1")
	));
	let pool = sqlx::PgPool::connect(&url).await?;
	let (_, rx) = tokio::sync::watch::channel(ai::Options::shipped(true));
	let clock: Arc<dyn Clock> = Arc::new(SystemClock);
	let ledger = ai::Ledger::new(
		Arc::new(ai::PostgresStore::new(pool.clone())),
		rx,
		clock.clone(),
	);
	let gateway = ai::Gateway::azure_with_credential(
		ledger,
		ai::AzureConfig {
			endpoint,
			deployment: "scripted".into(),
		},
		Arc::new(Credential),
	)?;
	let storage = Arc::new(BlobStorage::new(
		StorageConfig::from_env(true)?,
		clock.clone(),
	)?);
	let worker = reading::Worker::new(
		pool.clone(),
		storage,
		clock.clone(),
		reading::ScanReadingModel::new(gateway.clone(), reading::Options::default()),
	);
	let id = uuid::Uuid::parse_str(&std::env::var("ARCHIVE_TEST_READING_DATEI")?)?;
	let cancel = tokio_util::sync::CancellationToken::new();
	let signal = cancel.clone();
	let signals = tokio::spawn(async move {
		if let Ok(mut term) =
			tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
		{
			term.recv().await;
			signal.cancel();
		}
	});
	let result = worker
		.handle(
			&archive_server::queue::Message {
				id: "native-reading-cancel".into(),
				receipt: "offline-receipt".into(),
				body: archive_server::queue::envelope("reading", id),
				dequeue_count: 1,
			},
			clock.now() + chrono::Duration::minutes(1),
			&cancel,
		)
		.instrument(tracing::info_span!("reading-cancel-native"))
		.await;
	let healthy = gateway.drain().await;
	pool.close().await;
	signals.abort();
	let _ = signals.await;
	tracing::info!("Archive reading cancellation gate completed");
	if !defaults.finish_finite_job() || !healthy {
		return Err("finite accounting/telemetry failure".into());
	}
	if matches!(result, Err(reading::ReadingError::Cancelled)) {
		return Err("expected offline reading cancellation".into());
	}
	Err("offline cancellation was not exercised".into())
}
