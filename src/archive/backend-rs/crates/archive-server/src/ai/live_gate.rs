//! Shared absent-configuration gate for the THREE later live evaluations.
//! Passing this gate is configuration, never authorization for agent spending.
#[derive(Clone, Copy)]
pub enum LiveEvaluation {
	Chat,
	Scan,
	Programm,
}
impl LiveEvaluation {
	pub fn required(self) -> &'static [&'static str] {
		match self {
			Self::Chat => &["ARCHIVE_CHAT_ENDPOINT", "ARCHIVE_CHAT_DEPLOYMENT_NAME"],
			Self::Scan => &[
				"ARCHIVE_SCAN_EVAL_DIR",
				"ARCHIVE_CHAT_ENDPOINT",
				"ARCHIVE_CHAT_DEPLOYMENT_NAME",
			],
			Self::Programm => &[
				"ARCHIVE_PROGRAMM_EVAL_DIR",
				"ARCHIVE_CHAT_ENDPOINT",
				"ARCHIVE_CHAT_DEPLOYMENT_NAME",
			],
		}
	}
	pub fn configured(self, lookup: impl Fn(&str) -> Option<String>) -> bool {
		self.required()
			.iter()
			.all(|name| lookup(name).is_some_and(|v| !v.trim().is_empty()))
	}
	pub fn configured_from_env(self) -> bool {
		self.configured(|name| std::env::var(name).ok())
	}
}
