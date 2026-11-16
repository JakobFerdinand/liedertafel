//! OFFLINE ONLY: product chat/router/Origin drain and real PostgreSQL accounting.
//! Script supplies delivery only; no hosted provider, credentials or auth bypass.
use archive_server::{
	Origin, Settings, ai,
	auth::{Auth, AuthConfig, SystemClock},
};
use std::sync::{
	Arc,
	atomic::{AtomicUsize, Ordering},
};
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
	let _ = rustls::crypto::ring::default_provider().install_default();
	let database = std::env::var("ARCHIVE_TEST_CHAT_DATABASE")?;
	let url = url::Url::parse(&database)?;
	if !matches!(url.host_str(), Some("127.0.0.1" | "localhost"))
		|| !url.path().starts_with("/test232_chat_lifetime_")
	{
		return Err("offline fixture refuses unrelated database".into());
	}
	if !matches!(
		std::env::var("Mail__Host").as_deref(),
		Ok("127.0.0.1" | "localhost")
	) || std::env::var("Archive__Chat__Provider").as_deref() != Ok("Scripted")
	{
		return Err("offline fixture requires local mail and scripted delivery".into());
	}
	let mode = std::env::var("ARCHIVE_TEST_CHAT_MODE")?;
	if !matches!(
		mode.as_str(),
		"hold-first" | "hold-second" | "paid" | "fail"
	) {
		return Err("unknown offline mode".into());
	}
	let defaults = archive_telemetry::ServiceDefaults::init(true, "archive-chat-offline")?;
	let pool = sqlx::PgPool::connect(&database).await?;
	let count = AtomicUsize::new(0);
	let script = ai::Script::handler(move |_| {
		let step = count.fetch_add(1, Ordering::SeqCst);
		tracing::info!(step, "Offline chat delivery started");
		match (mode.as_str(), step) {
			("hold-second", 0) => vec![ai::ScriptStep::Update(ai::Update {
				tools: vec![ai::ToolDelta {
					index: 0,
					id: Some("call_1".into()),
					name: Some("catalogue_search".into()),
					arguments: "{\"query\":\"\",\"page\":1}".into(),
				}],
				usage: ai::Usage::reported(100, 10),
				..Default::default()
			})],
			("paid", _) => vec![
				ai::ScriptStep::Update(ai::Update::text("synthetic232-private-answer")),
				ai::ScriptStep::Update(ai::Update::usage(ai::Usage::reported(100, 10))),
			],
			("fail", _) => vec![ai::ScriptStep::Fail],
			_ => vec![ai::ScriptStep::Hold],
		}
	});
	let mut origin = Origin::new(Settings::from_env()?).with_auth(Auth::new(
		pool.clone(),
		AuthConfig::development(),
		archive_server::auth::mail::from_env(true)?,
		Arc::new(SystemClock),
	)?);
	origin.chat = archive_server::chat::Options::from_env()?;
	origin.ai = Some(ai::Services::from_env_with_script(
		pool.clone(),
		Arc::new(SystemClock),
		true,
		script,
	)?);
	let origin = Arc::new(origin);
	let listener = tokio::net::TcpListener::bind(("127.0.0.1", origin.settings.port)).await?;
	let shutdown = origin.shutdown.clone();
	let signal = tokio::spawn(async move {
		let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
		tokio::select! {_=term.recv()=>{},_=tokio::signal::ctrl_c()=>{}}
		shutdown.cancel();
		Ok::<(), std::io::Error>(())
	});
	let shutdown = origin.shutdown.clone();
	axum::serve(
		listener,
		archive_server::build_router(origin.clone())
			.into_make_service_with_connect_info::<std::net::SocketAddr>(),
	)
	.with_graceful_shutdown(shutdown.cancelled_owned())
	.await?;
	let healthy = origin.drain_readings().await;
	pool.close().await;
	let _ = signal.await?;
	tracing::info!("Offline chat HTTP producers and accounting drained");
	if !defaults.finish_finite_job() || !healthy {
		return Err("offline chat accounting/telemetry failure".into());
	}
	Ok(())
}
