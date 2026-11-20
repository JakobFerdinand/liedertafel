#![allow(clippy::unwrap_used, clippy::expect_used)] // Native command assertions.
#[tokio::test]
async fn literal_reading_commands_pause_before_dependencies_and_exit_finitely() {
	for command in ["--read-queue", "--dispatch-reading"] {
		let output = tokio::time::timeout(
			std::time::Duration::from_secs(5),
			tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
				.arg(command)
				.env("ARCHIVE_ENVIRONMENT", "Production")
				.env("Archive__MaintenanceMode", "true")
				.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
				.env_remove("ConnectionStrings__archive-db")
				.kill_on_drop(true)
				.output(),
		)
		.await
		.unwrap()
		.unwrap();
		assert!(
			output.status.success(),
			"{command}: {}",
			String::from_utf8_lossy(&output.stderr)
		);
	}
}
