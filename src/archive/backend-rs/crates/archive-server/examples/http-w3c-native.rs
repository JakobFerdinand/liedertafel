//! Offline sampler fixture: product Router + actual non-Development telemetry,
//! no auth/mail/storage/provider construction or credentials. Loopback only.
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
	let defaults = archive_telemetry::ServiceDefaults::init(false, "http-w3c-native")?;
	let port: u16 = std::env::var("PORT")?.parse()?;
	let origin = std::sync::Arc::new(archive_server::Origin::new(archive_server::Settings::new(
		false,
		Default::default(),
		port,
	)));
	let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await?;
	axum::serve(listener, archive_server::build_router(origin))
		.with_graceful_shutdown(async {
			if let Ok(mut signal) =
				tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
			{
				signal.recv().await;
			}
		})
		.await?;
	if !defaults.finish_finite_job() {
		return Err("offline terminal telemetry failure".into());
	}
	Ok(())
}
