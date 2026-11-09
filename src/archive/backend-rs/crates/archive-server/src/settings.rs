//! Origin configuration, read from the environment the hosts are handed
//! (Aspire injects `PORT`; the container carries `PORT` at cutover;
//! `ARCHIVE_*` variables carry the archive-specific settings).

use std::env;
use std::path::PathBuf;

/// `/api/build` payload: matches the ASP.NET informational version. The
/// container build stamps `ARCHIVE_IMAGE_VERSION` at compile time the way
/// C# `dotnet publish -p:Version=` does — the cutover wave (#236) wires the
/// Dockerfile; local builds fall back to the crate version.
pub fn build_version() -> &'static str {
	option_env!("ARCHIVE_IMAGE_VERSION").unwrap_or(env!("CARGO_PKG_VERSION"))
}

/// Environment names follow the ASPNETCORE_ENVIRONMENT vocabulary so exported
/// payloads and operator tooling read identically on both backends.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnvironmentName {
	Development,
	Production,
}

impl From<&str> for EnvironmentName {
	fn from(value: &str) -> Self {
		match value {
			"Development" => Self::Development,
			_ => Self::Production,
		}
	}
}

/// Environment of the origin process.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Settings {
	/// `ARCHIVE_ENVIRONMENT=Development` — mirrors ASPNETCORE_ENVIRONMENT.
	pub development: bool,
	/// Release pause shared by every API, including editor storage transitions.
	pub maintenance: bool,
	/// Root of the statically exported Next.js frontend (`out/` in the image,
	/// `wwwroot` as the C# container layout).
	pub static_root: PathBuf,
	/// HTTP port (container: 8080, local Aspire resource: injected `PORT`).
	pub port: u16,
}

/// A malformed environment fails startup loudly; silent fallbacks would serve
/// the wrong origin without anyone noticing.
#[derive(Debug, thiserror::Error)]
pub enum SettingsError {
	#[error("Archive__MaintenanceMode muss true oder false sein")]
	InvalidMaintenance,
	#[error("PORT muss eine Zahl sein, nicht {0}")]
	InvalidPort(String),
}

impl Settings {
	/// Reads the origin environment; missing values take defaults, a
	/// malformed `PORT` refuses to start.
	///
	/// # Errors
	/// When `PORT` cannot be parsed as a number.
	pub fn from_env() -> Result<Self, SettingsError> {
		let port = match env::var("PORT") {
			Ok(raw) if !raw.trim().is_empty() => raw.trim().parse().map_err(|_| {
				// `raw` explains the value; the parser detail stays invisible.
				SettingsError::InvalidPort(raw.clone())
			})?,
			_ => 8080,
		};

		let development = match env::var("ARCHIVE_ENVIRONMENT") {
			Ok(name) if !name.trim().is_empty() => {
				EnvironmentName::from(name.trim()) == EnvironmentName::Development
			}
			_ => false,
		};

		let static_root = match env::var("ARCHIVE_STATIC_ROOT") {
			Ok(root) if !root.trim().is_empty() => PathBuf::from(root.trim()),
			_ => PathBuf::from("wwwroot"),
		};

		Ok(Self {
			development,
			maintenance: match env::var("Archive__MaintenanceMode") {
				Ok(value) if !value.trim().is_empty() => value
					.trim()
					.to_ascii_lowercase()
					.parse()
					.map_err(|_| SettingsError::InvalidMaintenance)?,
				_ => false,
			},
			static_root,
			port,
		})
	}

	/// Direct construction for tests and embedding hosts.
	pub fn new(development: bool, static_root: PathBuf, port: u16) -> Self {
		Self {
			development,
			maintenance: false,
			static_root,
			port,
		}
	}
}

#[cfg(test)]
mod tests {
	use super::*;

	#[test]
	fn build_version_stays_constant_at_compile_time() {
		let version = build_version();
		assert!(!version.is_empty());
	}

	#[test]
	fn environment_names_match_aspnetcore_conventions() {
		assert_eq!(
			EnvironmentName::from("Development"),
			EnvironmentName::Development
		);
		assert_eq!(
			EnvironmentName::from("Production"),
			EnvironmentName::Production
		);
		assert_eq!(
			EnvironmentName::from("Staging"),
			EnvironmentName::Production,
			"every name but Development is production"
		);
	}

	#[test]
	fn direct_construction_keeps_fields() {
		let settings = Settings::new(false, PathBuf::from("out"), 18080);
		assert!(!settings.development);
		assert_eq!(settings.static_root, PathBuf::from("out"));
		assert_eq!(settings.port, 18080);
	}
}
