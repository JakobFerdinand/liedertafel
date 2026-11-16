use crate::ai;

#[derive(Clone)]
pub struct Options {
	pub enabled: bool,
	pub disabled: bool,
	pub max_tool_calls: i32,
	pub no_token_seconds: i32,
	pub overall_seconds: i32,
	pub max_question_chars: i32,
	pub max_answer_chars: i32,
}
impl Default for Options {
	fn default() -> Self {
		Self {
			enabled: false,
			disabled: false,
			max_tool_calls: 5,
			no_token_seconds: 30,
			overall_seconds: 120,
			max_question_chars: 2000,
			max_answer_chars: 8000,
		}
	}
}
impl Options {
	pub fn from_env() -> Result<Self, ai::Error> {
		Self::from_configuration(|key| std::env::var(format!("Archive__Chat__{key}")).ok())
	}
	fn from_configuration(get: impl Fn(&str) -> Option<String>) -> Result<Self, ai::Error> {
		let mut result = Self::default();
		for (name, target) in [
			("Enabled", &mut result.enabled),
			("Disabled", &mut result.disabled),
		] {
			if let Some(value) = get(name) {
				*target = value
					.trim()
					.to_ascii_lowercase()
					.parse()
					.map_err(|_| ai::Error::Configuration)?;
			}
		}
		for (name, target) in [
			("MaxToolCalls", &mut result.max_tool_calls),
			("NoTokenSeconds", &mut result.no_token_seconds),
			("OverallSeconds", &mut result.overall_seconds),
			("MaxQuestionChars", &mut result.max_question_chars),
			("MaxAnswerChars", &mut result.max_answer_chars),
		] {
			if let Some(value) = get(name) {
				// Same captured BCL Int32/hex grammar as the accepted form readers.
				*target = crate::reading::configuration_integer(&value, false)
					.and_then(|v| i32::try_from(v).ok())
					.ok_or(ai::Error::Configuration)?;
			}
		}
		Ok(result)
	}
}

#[cfg(test)]
mod tests {
	use super::*;
	#[test]
	fn configuration_keeps_defaults_and_binds_both_gates_and_every_signed_bound() {
		let defaults = Options::from_configuration(|_| None).unwrap();
		assert!(!defaults.enabled && !defaults.disabled);
		assert_eq!(
			(
				defaults.max_tool_calls,
				defaults.no_token_seconds,
				defaults.overall_seconds,
				defaults.max_question_chars,
				defaults.max_answer_chars
			),
			(5, 30, 120, 2000, 8000)
		);
		let configured = Options::from_configuration(|key| {
			Some(
				match key {
					"Enabled" | "Disabled" => " TrUe ",
					"MaxToolCalls" => "0",
					"NoTokenSeconds" => "-1",
					"OverallSeconds" => "0x10",
					"MaxQuestionChars" => "#7d0",
					_ => "&h1F40",
				}
				.into(),
			)
		})
		.unwrap();
		assert!(configured.enabled && configured.disabled);
		assert_eq!(
			(
				configured.max_tool_calls,
				configured.no_token_seconds,
				configured.overall_seconds,
				configured.max_question_chars,
				configured.max_answer_chars
			),
			(0, -1, 16, 2000, 8000)
		);
		for invalid in ["", "1_000", "2147483648", "garbage"] {
			assert!(
				Options::from_configuration(|key| (key == "MaxToolCalls").then(|| invalid.into()))
					.is_err()
			);
		}
		assert!(
			Options::from_configuration(|key| (key == "Disabled").then(|| "1".into())).is_err()
		);
	}
}
