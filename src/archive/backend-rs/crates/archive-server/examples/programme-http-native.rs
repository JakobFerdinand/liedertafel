//! Offline-only process fixture for the programme HTTP lifetime. Real origin
//! routes/auth/Gateway/SQL; only model delivery is held. No credential factory,
//! hosted provider, private corpus, product command or persisted draft registry.
use archive_server::{Origin, Settings, ai, auth, reading};
use std::{sync::Arc, time::Duration};
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
	let _ = rustls::crypto::ring::default_provider().install_default();
	let defaults =
		archive_telemetry::ServiceDefaults::init(true, "archive-programme-http-offline")?;
	let url = std::env::var("ConnectionStrings__archive-db")?;
	let parsed = url::Url::parse(&url)?;
	assert!(
		matches!(parsed.host_str(), Some("localhost" | "127.0.0.1"))
			&& parsed.path().starts_with("/test231_programm_")
	);
	assert!(
		ai::AzureConfig::selected_from_env().is_none(),
		"offline fixture refuses configured provider"
	);
	let pool = sqlx::PgPool::connect(&url).await?;
	let clock = Arc::new(auth::SystemClock);
	let script = ai::Script::handler(|request| {
		if request
			.options
			.instructions
			.as_deref()
			.is_some_and(|s| s.contains("PROGRAMMTEXT-ANFANG"))
		{
			tracing::info!("Offline programme field delivery waiting");
			vec![ai::ScriptStep::Hold]
		} else {
			vec![ai::ScriptStep::Update(ai::Update {
				text: "Konzert\nprivate-programme-native-231\n1. Ave Maria".into(),
				usage: ai::Usage::reported(100, 20),
				..Default::default()
			})]
		}
	});
	let mut origin = Origin::new(Settings::from_env()?);
	origin.reading = reading::Options::from_env(true);
	origin.reading_enabled = true;
	origin.ai = Some(ai::Services::from_env_with_script(
		pool.clone(),
		clock.clone(),
		true,
		script,
	)?);
	origin = origin.with_auth(auth::Auth::new(
		pool.clone(),
		auth::AuthConfig::from_env(true)?,
		auth::mail::from_env(true)?,
		clock,
	)?);
	let origin = Arc::new(origin);
	let listener =
		tokio::net::TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, origin.settings.port))
			.await?;
	let host = origin.clone();
	let run = async {
		axum::serve(
			listener,
			archive_server::build_router(origin.clone())
				.into_make_service_with_connect_info::<std::net::SocketAddr>(),
		)
		.with_graceful_shutdown(async move {
			if let Ok(mut signal) =
				tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
			{
				signal.recv().await;
			}
			// Same shared host contract: signal BEFORE Axum's wait.
			host.shutdown.cancel();
		})
		.await?;
		let healthy = origin.drain_readings().await;
		pool.close().await;
		tracing::info!("Offline programme HTTP producers and accounting drained");
		Ok::<bool, Box<dyn std::error::Error>>(healthy)
	};
	let healthy = tokio::time::timeout(Duration::from_secs(60), run).await??;
	if !defaults.finish_finite_job() || !healthy {
		return Err("offline programme accounting/telemetry failure".into());
	}
	Ok(())
}
