use super::Error;
use rust_decimal::{Decimal, prelude::ToPrimitive};
use std::{collections::BTreeMap, str::FromStr};

#[derive(Clone, Debug)]
pub struct Price {
	pub input: Decimal,
	pub output: Decimal,
}
impl Price {
	pub fn new(input: &str, output: &str) -> Result<Self, Error> {
		Ok(Self {
			input: Decimal::from_str(input).map_err(|_| Error::Configuration)?,
			output: Decimal::from_str(output).map_err(|_| Error::Configuration)?,
		})
	}
}

/// All money stays decimal until its checked, integral micro-EUR conversion.
/// Rounding is per physical gateway call, not per operation or month.
pub fn cost_micro_eur(price: &Price, input: i64, output: i64) -> Result<i64, Error> {
	price
		.input
		.checked_mul(Decimal::from(input.max(0)))
		.and_then(|v| {
			price
				.output
				.checked_mul(Decimal::from(output.max(0)))
				.and_then(|o| v.checked_add(o))
		})
		.and_then(|v| v.ceil().to_i64())
		.ok_or(Error::MoneyOverflow)
}
pub fn micro_eur(eur: Decimal) -> Result<i64, Error> {
	eur.checked_mul(Decimal::from(1_000_000))
		.and_then(|v| v.floor().to_i64())
		.ok_or(Error::MoneyOverflow)
}
pub fn eur(micro: i64) -> Decimal {
	Decimal::new(micro, 6)
}

#[derive(Clone, Debug)]
pub struct Options {
	pub monthly_cap_eur: Decimal,
	pub max_output_tokens: i32,
	pub max_input_tokens: i64,
	pub models: BTreeMap<String, Price>,
	pub notice_thresholds_eur: String,
}
impl Default for Options {
	fn default() -> Self {
		Self {
			monthly_cap_eur: Decimal::from(15),
			max_output_tokens: 6000,
			max_input_tokens: 200_000,
			models: BTreeMap::new(),
			notice_thresholds_eur: String::new(),
		}
	}
}
impl Options {
	pub fn shipped(development: bool) -> Self {
		let mut result = Self::default();
		result.models.insert(
			"gpt-6-luna".into(),
			Price {
				input: Decimal::new(1056, 4),
				output: Decimal::new(528, 3),
			},
		);
		if development {
			result.models.insert(
				"scripted".into(),
				Price {
					input: Decimal::new(83, 2),
					output: Decimal::new(495, 2),
				},
			);
		}
		result
	}
	pub fn from_env(development: bool) -> Result<Self, Error> {
		let mut result = Self::shipped(development);
		for (key, value) in std::env::vars() {
			// .NET's ENV provider normalizes '__' to ':' and binds section /
			// property names case-insensitively. Only AI option names are folded;
			// the configured deployment/model identity is never lowercased.
			let normalized = key.replace("__", ":");
			let parts = normalized.split(':').collect::<Vec<_>>();
			if parts.len() < 3
				|| !parts[0].eq_ignore_ascii_case("Archive")
				|| !parts[1].eq_ignore_ascii_case("Ai")
			{
				continue;
			}
			match &parts[2..] {
				[setting] if setting.eq_ignore_ascii_case("MonthlyCapEur") => {
					result.monthly_cap_eur =
						value.trim().parse().map_err(|_| Error::Configuration)?
				}
				[setting] if setting.eq_ignore_ascii_case("MaxOutputTokensPerCall") => {
					result.max_output_tokens =
						value.trim().parse().map_err(|_| Error::Configuration)?
				}
				[setting] if setting.eq_ignore_ascii_case("MaxInputTokensPerCall") => {
					result.max_input_tokens =
						value.trim().parse().map_err(|_| Error::Configuration)?
				}
				[setting] if setting.eq_ignore_ascii_case("NoticeThresholdsEur") => {
					result.notice_thresholds_eur = value
				}
				[section, model, field] if section.eq_ignore_ascii_case("Models") => {
					let model = result
						.models
						.keys()
						.find(|key| key.eq_ignore_ascii_case(model))
						.cloned()
						.unwrap_or_else(|| (*model).to_owned());
					let price = result.models.entry(model).or_insert(Price {
						input: Decimal::ZERO,
						output: Decimal::ZERO,
					});
					match *field {
						field if field.eq_ignore_ascii_case("InputPricePerMillionEur") => {
							price.input = value.trim().parse().map_err(|_| Error::Configuration)?
						}
						field if field.eq_ignore_ascii_case("OutputPricePerMillionEur") => {
							price.output = value.trim().parse().map_err(|_| Error::Configuration)?
						}
						_ => {}
					}
				}
				_ => {}
			}
		}
		result.cap_micro_eur()?;
		Ok(result)
	}
	pub fn find_price(&self, model: &str) -> Option<&Price> {
		self.models
			.iter()
			.find(|(key, _)| key.eq_ignore_ascii_case(model))
			.map(|(_, v)| v)
			.filter(|p| p.input > Decimal::ZERO && p.output > Decimal::ZERO)
	}
	pub fn cap_micro_eur(&self) -> Result<i64, Error> {
		micro_eur(self.monthly_cap_eur)
	}
	pub fn levels(&self) -> Vec<Decimal> {
		let mut result = Vec::new();
		for entry in self
			.notice_thresholds_eur
			.split(',')
			.map(str::trim)
			.filter(|s| !s.is_empty())
		{
			match threshold_number(entry) {
				Some(v) if v > Decimal::ZERO => result.push(v),
				_ => tracing::error!(
					entry,
					"KI-Budget: Der Schwellwert-Eintrag ist nicht lesbar; er ist vorerst ausgeschaltet."
				),
			}
		}
		if !result.contains(&self.monthly_cap_eur) {
			result.push(self.monthly_cap_eur);
		}
		result.retain(|v| *v > Decimal::ZERO);
		result.sort();
		result
	}
}

/// Invariant NumberStyles.Number for an already comma-split, Unicode-trimmed
/// threshold entry. Commas are list separators here, NOT numeric grouping.
/// BCL permits one leading OR trailing sign, ASCII numeric whitespace before a
/// trailing sign, a decimal point and trailing NULs. It rejects exponents,
/// currency, parentheses, underscores and interior Unicode whitespace. Decimal
/// precision reduction is ties-to-even (unlike rust_decimal's FromStr rounding).
/// The committed actual BCL matrix pins this deliberately narrow grammar.
fn threshold_number(entry: &str) -> Option<Decimal> {
	let numeric_white = |c: char| matches!(c, ' ' | '\t'..='\r');
	let mut text = entry.trim_end_matches('\0').trim_end_matches(numeric_white);
	let mut negative = false;
	let leading = text.starts_with(['+', '-']);
	if leading {
		negative = text.starts_with('-');
		text = &text[1..];
	}
	if text.ends_with(['+', '-']) {
		if leading {
			return None;
		}
		negative = text.ends_with('-');
		text = text[..text.len() - 1].trim_end_matches(numeric_white);
	}
	let mut point = false;
	let mut fraction = 0_usize;
	let mut digits = String::new();
	for byte in text.bytes() {
		match byte {
			b'0'..=b'9' => {
				digits.push(char::from(byte));
				if point {
					fraction += 1;
				}
			}
			b'.' if !point => point = true,
			_ => return None,
		}
	}
	if digits.is_empty() {
		return None;
	}
	let significant = digits.trim_start_matches('0').len();
	let initial_drop = fraction
		.saturating_sub(28)
		.max(significant.saturating_sub(29));
	for drop in initial_drop..=fraction {
		let split = digits.len().checked_sub(drop)?;
		let retained = digits[..split].trim_start_matches('0');
		let mut coefficient = if retained.is_empty() {
			0
		} else {
			retained.parse::<i128>().ok()?
		};
		let removed = &digits.as_bytes()[split..];
		if let Some(&first) = removed.first()
			&& (first > b'5'
				|| (first == b'5'
					&& (removed[1..].iter().any(|&b| b != b'0') || coefficient % 2 != 0)))
		{
			coefficient = coefficient.checked_add(1)?;
		}
		if coefficient > Decimal::MAX.mantissa() {
			continue;
		}
		let scale = u32::try_from(fraction - drop).ok()?;
		return Decimal::try_from_i128_with_scale(
			if negative { -coefficient } else { coefficient },
			scale,
		)
		.ok();
	}
	None
}

pub fn month_key(instant: chrono::DateTime<chrono::Utc>) -> String {
	instant
		.with_timezone(&chrono_tz::Europe::Vienna)
		.format("%Y-%m")
		.to_string()
}
