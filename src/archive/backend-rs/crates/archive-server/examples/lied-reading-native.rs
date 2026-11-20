//! Offline-only HTTP Lesung lifecycle fixture. Uses the PRODUCT router/readers,
//! registry, native renderer, shared host drain and real ledger. Only delivery is
//! fake: loopback HTTP plus a fixed synthetic credential, never hosted identity.
use archive_server::{
	Origin, Settings, ai,
	auth::{Auth, AuthConfig, Clock, SystemClock},
};
use azure_core::credentials::{AccessToken, TokenCredential, TokenRequestOptions};
use std::sync::Arc;
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
			"synthetic-lied-reading-token-230",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
fn local(url: &str) -> Result<url::Url, Box<dyn std::error::Error>> {
	let parsed = url::Url::parse(url)?;
	if !matches!(parsed.host_str(), Some("127.0.0.1" | "localhost")) {
		return Err("offline fixture refuses nonlocal endpoint".into());
	}
	Ok(parsed)
}
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
	let _ = rustls::crypto::ring::default_provider().install_default();
	let database = std::env::var("ARCHIVE_TEST_LIED_DATABASE")?;
	let parsed = local(&database)?;
	if !parsed.path().starts_with("/test230_lied_") {
		return Err("offline fixture refuses unrelated database".into());
	}
	let endpoint = std::env::var("ARCHIVE_TEST_LIED_ENDPOINT")?;
	if local(&endpoint)?.scheme() != "http" {
		return Err("offline fixture requires loopback HTTP".into());
	}
	if !matches!(
		std::env::var("Mail__Host").as_deref(),
		Ok("127.0.0.1" | "localhost")
	) {
		return Err("offline fixture requires local SMTP".into());
	}
	let defaults = archive_telemetry::ServiceDefaults::init(true, "archive-lied-reading-offline")?;
	let pool = sqlx::PgPool::connect(&database).await?;
	let clock: Arc<dyn Clock> = Arc::new(SystemClock);
	let (_, rx) = tokio::sync::watch::channel(ai::Options::shipped(true));
	let ledger = ai::Ledger::new(
		Arc::new(ai::PostgresStore::new(pool.clone())),
		rx,
		clock.clone(),
	);
	let gateway = ai::Gateway::azure_with_credential(
		ledger.clone(),
		ai::AzureConfig {
			endpoint,
			deployment: "scripted".into(),
		},
		Arc::new(Credential),
	)?;
	let mut origin = Origin::new(Settings::from_env()?).with_auth(Auth::new(
		pool.clone(),
		AuthConfig::development(),
		archive_server::auth::mail::from_env(true)?,
		clock,
	)?);
	origin.reading_enabled = true;
	origin.reading = archive_server::reading::Options::from_env(true);
	origin.ai = Some(ai::Services { ledger, gateway });
	let origin = Arc::new(origin);
	let listener = tokio::net::TcpListener::bind(("127.0.0.1", origin.settings.port)).await?;
	let cancel = origin.shutdown.clone();
	let signal = tokio::spawn(async move {
		let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
		tokio::select! {_=term.recv()=>{},_=tokio::signal::ctrl_c()=>{}}
		cancel.cancel();
		Ok::<(), std::io::Error>(())
	});
	let shutdown = origin.shutdown.clone();
	let result = axum::serve(
		listener,
		archive_server::build_router(origin.clone())
			.into_make_service_with_connect_info::<std::net::SocketAddr>(),
	)
	.with_graceful_shutdown(shutdown.cancelled_owned())
	.await;
	let healthy = origin.drain_readings().await;
	pool.close().await;
	let _ = signal.await?;
	tracing::info!("Offline Lied HTTP producers and accounting drained");
	if !defaults.finish_finite_job() || !healthy {
		return Err("offline HTTP accounting/telemetry failure".into());
	}
	result?;
	Ok(())
}
