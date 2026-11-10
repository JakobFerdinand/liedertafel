use super::{Error, Usage};
use serde_json::{Value, value::RawValue};
use std::sync::LazyLock;
use uuid::Uuid;

// Derived scalar facts from the actual frozen AIJsonUtilities encoder on
// .NET10.0.14, NOT a claim about arbitrary JsonSerializer settings/types.
// AiTransportReviewOracle.cs exhaustively probes BMP scalars (except surrogate
// code units); its encoder-blocklist observation regenerates this sorted table.
static ENCODER_BMP_RANGES: LazyLock<Result<Vec<[u32; 2]>, serde_json::Error>> =
	LazyLock::new(|| serde_json::from_str(include_str!("encoder-bmp-ranges.json")));

/// Internal operation/account attribution is deliberately not Serialize.
#[derive(Clone, Debug)]
pub struct Operation {
	pub feature: String,
	pub operation_id: Uuid,
	pub account_id: Option<Uuid>,
	pub max_output_tokens: Option<i32>,
	pub non_text_input_tokens: Option<i64>,
}
impl Operation {
	pub fn start(feature: &str, account_id: Option<Uuid>) -> Self {
		Self {
			feature: feature.into(),
			operation_id: Uuid::now_v7(),
			account_id,
			max_output_tokens: None,
			non_text_input_tokens: None,
		}
	}
}
/// Preserve JsonElement.GetRawText length/serialization when a schema or a
/// caller already owns serialized JSON. Strings remain unquoted estimates.
#[derive(Clone, Debug)]
pub enum Payload {
	Text(String),
	Json(Box<RawValue>),
}
impl Payload {
	pub fn json(raw: &str) -> Result<Self, Error> {
		Ok(Self::Json(
			serde_json::from_str(raw).map_err(|_| Error::Configuration)?,
		))
	}
	pub fn from_value(value: &Value) -> Result<Self, Error> {
		// Match the frozen encoder's blocked BMP scalars and non-BMP UTF-16
		// pairs, while preserving German/other allowed letters. Raw JSON uses
		// Payload::json and is never re-encoded by this supported-Value helper.
		let pretty = serde_json::to_string_pretty(value).map_err(|_| Error::Configuration)?;
		let ranges = ENCODER_BMP_RANGES
			.as_ref()
			.map_err(|_| Error::Configuration)?;
		let mut out = String::new();
		let mut chars = pretty.chars();
		while let Some(c) = chars.next() {
			// Serde already emitted JSON escapes for controls / quote / slash.
			// Uppercase generated \uXXXX hex without mistaking a user's escaped
			// literal backslash-u sequence for an encoder-generated escape.
			if c == '\\' {
				out.push(c);
				let escaped = chars.next().ok_or(Error::Configuration)?;
				out.push(escaped);
				if escaped == 'u' {
					for _ in 0..4 {
						out.push(
							chars
								.next()
								.ok_or(Error::Configuration)?
								.to_ascii_uppercase(),
						);
					}
				}
				continue;
			}
			let scalar = c as u32;
			let blocked = ranges
				.binary_search_by(|&[start, end]| {
					if scalar < start {
						std::cmp::Ordering::Greater
					} else if scalar > end {
						std::cmp::Ordering::Less
					} else {
						std::cmp::Ordering::Equal
					}
				})
				.is_ok();
			if scalar > 0xffff || blocked {
				use std::fmt::Write;
				for unit in c.encode_utf16(&mut [0; 2]) {
					write!(&mut out, "\\u{unit:04X}").map_err(|_| Error::Configuration)?;
				}
			} else {
				out.push(c);
			}
		}
		Self::json(&out)
	}
	pub fn raw(&self) -> &str {
		match self {
			Self::Text(s) => s,
			Self::Json(v) => v.get(),
		}
	}
}
#[derive(Clone, Debug)]
pub enum Content {
	Text(String),
	ImageUrl(String),
	HostedFile(String),
	FunctionCall {
		id: String,
		name: String,
		arguments: Payload,
	},
	FunctionResult {
		id: String,
		result: Payload,
	},
}
#[derive(Clone, Debug)]
pub struct Message {
	pub role: String,
	pub contents: Vec<Content>,
}
impl Message {
	pub fn text(role: &str, text: &str) -> Self {
		Self {
			role: role.into(),
			contents: vec![Content::Text(text.into())],
		}
	}
}
#[derive(Clone, Debug)]
pub struct Tool {
	pub name: String,
	pub description: String,
	pub schema: Box<RawValue>,
}
#[derive(Clone, Debug)]
pub struct ResponseFormat {
	pub name: String,
	pub description: Option<String>,
	pub schema: Box<RawValue>,
	pub strict: Option<bool>,
}
#[derive(Clone, Debug, Default)]
pub struct CallOptions {
	pub model_id: Option<String>,
	pub max_output_tokens: Option<i32>,
	pub instructions: Option<String>,
	pub reasoning_effort: Option<String>,
	pub tools: Vec<Tool>,
	pub response_format: Option<ResponseFormat>,
	pub temperature: Option<f32>,
}
#[derive(Clone, Debug)]
pub struct Request {
	pub messages: Vec<Message>,
	pub options: CallOptions,
	pub operation: Option<Operation>,
	pub agent: Option<String>,
}
impl Request {
	pub fn new(messages: Vec<Message>) -> Self {
		Self {
			messages,
			options: CallOptions::default(),
			operation: None,
			agent: None,
		}
	}
}
/// Provider-visible normalized request: no operation/account/agent markers.
#[derive(Clone, Debug)]
pub struct ProviderRequest {
	pub messages: Vec<Message>,
	pub options: CallOptions,
	pub streaming: bool,
}
#[derive(Clone, Debug, Default)]
pub struct ToolDelta {
	pub index: usize,
	pub id: Option<String>,
	pub name: Option<String>,
	pub arguments: String,
}
#[derive(Clone, Debug, Default)]
pub struct Update {
	pub text: String,
	pub tools: Vec<ToolDelta>,
	pub usage: Usage,
	pub finish_reason: Option<String>,
}
impl Update {
	pub fn text(text: &str) -> Self {
		Self {
			text: text.into(),
			..Default::default()
		}
	}
	pub fn usage(usage: Usage) -> Self {
		Self {
			usage,
			..Default::default()
		}
	}
}

/// Bounded indexed wire assembly only, not a tool executor or loop. Later
/// consumers must still validate tool names, JSON schema and authorization.
#[derive(Default)]
pub struct ToolCalls {
	entries: std::collections::BTreeMap<usize, ToolDelta>,
	bytes: usize,
}
impl ToolCalls {
	pub fn observe(&mut self, deltas: &[ToolDelta]) -> Result<(), Error> {
		for delta in deltas {
			if delta.index >= 128 {
				return Err(Error::Protocol);
			}
			self.bytes = self
				.bytes
				.checked_add(delta.arguments.len())
				.and_then(|v| v.checked_add(delta.id.as_ref().map_or(0, String::len)))
				.and_then(|v| v.checked_add(delta.name.as_ref().map_or(0, String::len)))
				.ok_or(Error::Protocol)?;
			if self.bytes > 1024 * 1024 {
				return Err(Error::Protocol);
			}
			let entry = self.entries.entry(delta.index).or_default();
			entry.index = delta.index;
			if let Some(id) = &delta.id {
				entry.id.get_or_insert_with(String::new).push_str(id);
			}
			if let Some(name) = &delta.name {
				entry.name.get_or_insert_with(String::new).push_str(name);
			}
			entry.arguments.push_str(&delta.arguments);
		}
		Ok(())
	}
	pub fn finish(self) -> Result<Vec<Content>, Error> {
		self.entries
			.into_values()
			.map(|entry| {
				Ok(Content::FunctionCall {
					id: entry.id.filter(|v| !v.is_empty()).ok_or(Error::Protocol)?,
					name: entry
						.name
						.filter(|v| !v.is_empty())
						.ok_or(Error::Protocol)?,
					arguments: Payload::json(&entry.arguments).map_err(|_| Error::Protocol)?,
				})
			})
			.collect()
	}
}
/// Frozen estimator counts UTF-16 units, not bytes or Unicode scalar values.
pub fn estimate_text(messages: &[Message], options: &CallOptions) -> Result<(i64, bool), Error> {
	let len = |s: &str| i64::try_from(s.encode_utf16().count()).map_err(|_| Error::MoneyOverflow);
	let mut chars = len(options.instructions.as_deref().unwrap_or(""))?;
	let mut non_text = false;
	for message in messages {
		for content in &message.contents {
			let length = match content {
				Content::Text(v) => len(v)?,
				Content::FunctionCall {
					name, arguments, ..
				} => len(name)?
					.checked_add(len(arguments.raw())?)
					.ok_or(Error::MoneyOverflow)?,
				Content::FunctionResult { result, .. } => len(result.raw())?,
				Content::ImageUrl(_) | Content::HostedFile(_) => {
					non_text = true;
					0
				}
			};
			chars = chars.checked_add(length).ok_or(Error::MoneyOverflow)?;
		}
	}
	for tool in &options.tools {
		for value in [&*tool.name, &*tool.description, tool.schema.get()] {
			chars = chars.checked_add(len(value)?).ok_or(Error::MoneyOverflow)?;
		}
	}
	let overhead = i64::try_from(messages.len())
		.map_err(|_| Error::MoneyOverflow)?
		.checked_mul(8)
		.ok_or(Error::MoneyOverflow)?;
	Ok((
		chars
			.checked_add(2)
			.and_then(|v| (v / 3).checked_add(overhead))
			.ok_or(Error::MoneyOverflow)?,
		non_text,
	))
}

/// Retained dormant guard only: no embedding provider, vectors, registration or
/// semantic search. Offline siblings reserve/settle this spec through Ledger.
pub fn dormant_embedding_call(
	values: &[String],
	operation: Option<Operation>,
	model: &str,
	requested_model: Option<&str>,
) -> Result<super::CallSpec, Error> {
	let operation = operation.ok_or(Error::Unavailable)?;
	if requested_model
		.is_some_and(|v| !v.trim().is_empty() && !v.trim().eq_ignore_ascii_case(model))
	{
		return Err(Error::Unavailable);
	}
	let chars = values
		.iter()
		.try_fold(0_i64, |sum, v| {
			sum.checked_add(i64::try_from(v.encode_utf16().count()).ok()?)
		})
		.ok_or(Error::MoneyOverflow)?;
	Ok(super::CallSpec {
		feature: operation.feature,
		operation_id: operation.operation_id,
		account_id: operation.account_id,
		model: model.into(),
		estimated_input: chars.checked_add(2).ok_or(Error::MoneyOverflow)? / 3,
		max_output: 0,
	})
}
