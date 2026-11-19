//! Finite operations and Development-only diagnostics.
mod cleanup;
pub(crate) mod diagnostics;
mod local;

use tokio_util::sync::CancellationToken;

#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error("Local service commands require Development.")]
	Development,
	#[error("Archive job was cancelled.")]
	Cancelled,
	#[error(transparent)]
	Settings(#[from] crate::settings::SettingsError),
	#[error(transparent)]
	Local(#[from] local::Error),
	#[error(transparent)]
	Cleanup(#[from] cleanup::Error),
}

pub async fn run(
	command: &str,
	args: &[String],
	development: bool,
	cancel: &CancellationToken,
) -> Result<(), Error> {
	if command != "--cleanup-dropped-blobs" && !development {
		return Err(Error::Development);
	}
	if command == "--cleanup-papierkorb" && crate::settings::finite_maintenance()? {
		tracing::info!(target: "Archive.Papierkorb", "Papierkorb cleanup paused: maintenance mode is enabled");
		return Ok(());
	}
	match command {
		"--initialize-local-storage" => local::initialize(cancel).await?,
		"--worker-smoke" => local::exercise(cancel, None).await?,
		_ => {
			let dry_run = args.iter().any(|a| a == "--dry-run");
			tokio::select! {
				biased;
				_ = cancel.cancelled() => return Err(Error::Cancelled),
				result = cleanup::run(command, dry_run, development) => result?,
			}
		}
	}
	Ok(())
}
