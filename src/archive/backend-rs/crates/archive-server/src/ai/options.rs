use super::Error;
use rust_decimal::{Decimal, prelude::ToPrimitive};
use std::{collections::BTreeMap, str::FromStr};

#[derive(Clone, Debug)]
pub struct Price {
	pub input: Decimal,
	pub output: Decimal,
}
impl Price {
	/// External override strings use the accepted invariant configuration
	/// converter, not Rust Decimal's extended FromStr grammar. Usable/positive
	/// price checks remain with the caller, as for Options::find_price.
	pub fn from_configuration(input: &str, output: &str) -> Result<Self, Error> {
		Ok(Self {
			input: config_decimal(input)?,
			output: config_decimal(output)?,
		})
	}
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
fn config_decimal(value: &str) -> Result<Decimal, Error> {
	config_float(value).ok_or(Error::Configuration)
}

/// Actual invariant DecimalConverter ENV grammar: Unicode Trim, then Float
/// (leading sign/decimal point/exponent, NO trailing sign/grouping/underscores).
/// Reduce precision directly from the original digits, ties-to-even, retaining
/// scale and signed zero. Neither Rust FromStr's midpoint-up behavior nor the
/// notice's distinct Number grammar is suitable. Actual binder observations in
/// 228-ai-config-decimals.jsonl pin grammar, underflow and the 96-bit boundary.
fn config_float(value: &str) -> Option<Decimal> {
	let mut text = value.trim();
	let negative = text.starts_with('-');
	if text.starts_with(['+', '-']) {
		text = &text[1..];
	}
	let mut exponent = 0_i64;
	if let Some(index) = text.find(['e', 'E']) {
		let mut tail = &text[index + 1..];
		let exponent_negative = tail.starts_with('-');
		if tail.starts_with(['+', '-']) {
			tail = &tail[1..];
		}
		if tail.is_empty() || !tail.bytes().all(|b| b.is_ascii_digit()) {
			return None;
		}
		// Saturation handles arbitrarily long exponents without allocating
		// exponent-sized strings: nonzero huge positives overflow, negatives
		// underflow, and zero retains its clamped scale, just like the BCL.
		for byte in tail.bytes() {
			exponent = exponent
				.saturating_mul(10)
				.saturating_add(i64::from(byte - b'0'));
		}
		if exponent_negative {
			exponent = -exponent;
		}
		text = &text[..index];
	}
	let mut digits = String::new();
	let mut point = false;
	let mut fraction = 0_i64;
	for byte in text.bytes() {
		match byte {
			b'0'..=b'9' => {
				digits.push(char::from(byte));
				if point {
					fraction = fraction.saturating_add(1);
				}
			}
			b'.' if !point => point = true,
			_ => return None,
		}
	}
	if digits.is_empty() {
		return None;
	}
	let mut scale = fraction.saturating_sub(exponent);
	let digits = digits.trim_start_matches('0');
	if digits.is_empty() {
		let mut zero =
			Decimal::try_from_i128_with_scale(0, u32::try_from(scale.clamp(0, 28)).ok()?).ok()?;
		zero.set_sign_negative(negative);
		return Some(zero);
	}
	let expanded;
	let digits = if scale < 0 {
		let zeros = usize::try_from(-scale).ok()?;
		if digits.len().checked_add(zeros)? > 29 {
			return None;
		}
		expanded = format!("{digits}{}", "0".repeat(zeros));
		scale = 0;
		expanded.as_str()
	} else {
		digits
	};
	let length = i64::try_from(digits.len()).ok()?;
	let mut drop = scale
		.saturating_sub(28)
		.max(length.saturating_sub(29))
		.max(0);
	// At most 29 candidate scales, even for an enormous negative exponent.
	while drop <= scale {
		let split = length - drop;
		let mut coefficient = if split <= 0 {
			0
		} else {
			digits[..usize::try_from(split).ok()?]
				.parse::<i128>()
				.ok()?
		};
		if split >= 0 && split < length {
			let removed = &digits.as_bytes()[usize::try_from(split).ok()?..];
			if removed[0] > b'5'
				|| (removed[0] == b'5'
					&& (coefficient % 2 != 0 || removed[1..].iter().any(|&b| b != b'0')))
			{
				coefficient += 1;
			}
		}
		if coefficient <= Decimal::MAX.mantissa() {
			let mut amount =
				Decimal::try_from_i128_with_scale(coefficient, u32::try_from(scale - drop).ok()?)
					.ok()?;
			amount.set_sign_negative(negative);
			return Some(amount);
		}
		drop += 1;
	}
	None
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
					result.monthly_cap_eur = config_decimal(&value)?
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
							price.input = config_decimal(&value)?
						}
						field if field.eq_ignore_ascii_case("OutputPricePerMillionEur") => {
							price.output = config_decimal(&value)?
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
