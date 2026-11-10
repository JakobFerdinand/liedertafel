//! Hand-written dated AOAI Chat Completions REST/SSE. No OpenAI SDK wrappers.
use super::{
	Content, Error, Gateway, Ledger, ProviderRequest, ToolDelta, Update, Usage, gateway::Provider,
};
use azure_core::credentials::TokenCredential;
use futures::{
	StreamExt,
	stream::{self, BoxStream},
};
use serde_json::{Value, json};
use std::{collections::VecDeque, sync::Arc, time::Duration};

const MAX_EVENT: usize = 1024 * 1024;
const MAX_BODY: usize = 8 * 1024 * 1024;
const SCOPE: &str = "https://cognitiveservices.azure.com/.default";
#[derive(Clone)]
pub struct AzureConfig {
	pub endpoint: String,
	pub deployment: String,
}
impl AzureConfig {
	pub fn selected_from_env() -> Option<Self> {
		let provider = std::env::var("Archive__Chat__Provider").ok()?;
		let endpoint = std::env::var("Archive__Chat__Endpoint").ok()?;
		let deployment = std::env::var("Archive__Chat__DeploymentName").ok()?;
		(provider.trim() == "AzureOpenAI"
			&& !endpoint.trim().is_empty()
			&& !deployment.trim().is_empty())
		.then(|| Self {
			endpoint: endpoint.trim().into(),
			deployment: deployment.trim().into(),
		})
	}
}
#[derive(Clone)]
struct Azure {
	config: AzureConfig,
	client: reqwest::Client,
	credential: Arc<tokio::sync::OnceCell<Arc<dyn TokenCredential>>>,
}
impl Gateway {
	pub fn azure(ledger: Ledger, config: AzureConfig) -> Result<Self, Error> {
		Self::azure_inner(ledger, config, None)
	}
	/// Fake TokenCredential + local HTTP only; the real gateway/SQL stay above it.
	pub fn azure_with_credential(
		ledger: Ledger,
		config: AzureConfig,
		credential: Arc<dyn TokenCredential>,
	) -> Result<Self, Error> {
		Self::azure_inner(ledger, config, Some(credential))
	}
	fn azure_inner(
		ledger: Ledger,
		mut config: AzureConfig,
		credential: Option<Arc<dyn TokenCredential>>,
	) -> Result<Self, Error> {
		config.endpoint = config.endpoint.trim().into();
		config.deployment = config.deployment.trim().into();
		let endpoint = url::Url::parse(&config.endpoint).map_err(|_| Error::Configuration)?;
		if endpoint.scheme() == "http"
			&& (credential.is_none()
				|| !matches!(
					endpoint.host_str(),
					Some("localhost" | "127.0.0.1" | "::1" | "[::1]")
				)) {
			return Err(Error::Configuration);
		}
		if !matches!(endpoint.scheme(), "https" | "http")
			|| endpoint.host_str().is_none()
			|| !endpoint.username().is_empty()
			|| endpoint.password().is_some()
			|| endpoint.query().is_some()
			|| endpoint.fragment().is_some()
			|| config.deployment.is_empty()
		{
			return Err(Error::Configuration);
		}
		let client = reqwest::Client::builder()
			.retry(reqwest::retry::never())
			.redirect(reqwest::redirect::Policy::none())
			.build()
			.map_err(|_| Error::Configuration)?;
		let cell = tokio::sync::OnceCell::new();
		if let Some(credential) = credential {
			let _ = cell.set(credential);
		}
		Ok(Self::new(
			config.deployment.clone(),
			ledger,
			Arc::new(Azure {
				config,
				client,
				credential: Arc::new(cell),
			}),
		))
	}
}
impl Azure {
	async fn send(&self, request: &ProviderRequest) -> Result<reqwest::Response, Error> {
		let credential = self
			.credential
			.get_or_try_init(|| async {
				let has_client_id =
					std::env::var("AZURE_CLIENT_ID").is_ok_and(|s| !s.trim().is_empty());
				crate::storage::credential(true, !has_client_id)
					.map_err(|_| Error::Configuration)?
					.ok_or(Error::Configuration)
			})
			.await?;
		let token = credential
			.get_token(&[SCOPE], None)
			.await
			.map_err(|_| Error::Provider)?;
		let mut url = url::Url::parse(&self.config.endpoint).map_err(|_| Error::Configuration)?;
		url.path_segments_mut()
			.map_err(|_| Error::Configuration)?
			.pop_if_empty()
			.extend([
				"openai",
				"deployments",
				&self.config.deployment,
				"chat",
				"completions",
			]);
		url.query_pairs_mut()
			.append_pair("api-version", "2024-10-21");
		let body = wire_request(request, &self.config.deployment)?;
		// Frozen System.ClientModel production policy: initial + three retries
		// for 408/429/500/502/503/504, NOT 409 or malformed successful bodies.
		// These physical retries share ONE reservation, exactly as the SDK does;
		// no post-body/stream retries or invisible reqwest protocol retries.
		for attempt in 0..4 {
			let response = self
				.client
				.post(url.clone())
				.bearer_auth(token.token.secret())
				.json(&body)
				.send()
				.await;
			let retry = match &response {
				Ok(r) => matches!(r.status().as_u16(), 408 | 429 | 500 | 502 | 503 | 504),
				Err(_) => true,
			};
			if retry && attempt < 3 {
				let delay = response
					.as_ref()
					.ok()
					.and_then(|r| retry_delay(r.headers()))
					.unwrap_or_else(|| Duration::from_millis(800 * (1 << attempt)));
				drop(response);
				tokio::time::sleep(delay).await;
				continue;
			}
			let response = response.map_err(|_| Error::Provider)?;
			if !response.status().is_success() {
				return Err(Error::Provider);
			}
			return Ok(response);
		}
		Err(Error::Provider)
	}
}
fn retry_delay(headers: &reqwest::header::HeaderMap) -> Option<Duration> {
	for name in ["retry-after-ms", "x-ms-retry-after-ms"] {
		if let Some(ms) = headers
			.get(name)
			.and_then(|v| v.to_str().ok())
			.and_then(|v| v.parse::<u64>().ok())
		{
			return Some(Duration::from_millis(ms));
		}
	}
	headers
		.get("retry-after")
		.and_then(|v| v.to_str().ok())
		.and_then(|v| v.parse::<u64>().ok())
		.map(Duration::from_secs)
}
impl Provider for Azure {
	fn stream(&self, request: ProviderRequest) -> BoxStream<'static, Result<Update, Error>> {
		let client = self.clone();
		stream::unfold(
			Some(State {
				client,
				request,
				response: None,
				parser: Sse::default(),
				queue: VecDeque::new(),
				terminal: None,
				total: 0,
			}),
			|state| async move {
				let mut state = state?;
				let result = async {
					if state.response.is_none() {
						let response = state.client.send(&state.request).await?;
						let content_type = response
							.headers()
							.get(reqwest::header::CONTENT_TYPE)
							.and_then(|v| v.to_str().ok())
							.unwrap_or("");
						let expected = if state.request.streaming {
							"text/event-stream"
						} else {
							"application/json"
						};
						if !content_type
							.split(';')
							.next()
							.is_some_and(|v| v.trim().eq_ignore_ascii_case(expected))
						{
							return Err(Error::Protocol);
						}
						state.response = Some(response);
					}
					if !state.request.streaming {
						let mut bytes = Vec::new();
						let response = state.response.as_mut().ok_or(Error::Protocol)?;
						while let Some(chunk) =
							response.chunk().await.map_err(|_| Error::Provider)?
						{
							if bytes.len().saturating_add(chunk.len()) > MAX_BODY {
								return Err(Error::Protocol);
							}
							bytes.extend_from_slice(&chunk);
						}
						let value: Value =
							serde_json::from_slice(&bytes).map_err(|_| Error::Protocol)?;
						return Ok(Some(parse_update(&value, false)?));
					}
					loop {
						if let Some(update) = state.queue.pop_front() {
							return Ok(Some(update));
						}
						if let Some(error) = state.terminal.take() {
							return Err(error);
						}
						if state.parser.done {
							return Ok(None);
						}
						let chunk = state
							.response
							.as_mut()
							.ok_or(Error::Protocol)?
							.chunk()
							.await
							.map_err(|_| Error::Provider)?
							.ok_or(Error::Protocol)?;
						let accepted = chunk.len().min(MAX_BODY.saturating_sub(state.total));
						state.total += accepted;
						// A malformed later event must not erase completed events in
						// this HTTP chunk. Drain them in order before the terminal
						// error, without teaching the gateway about unconsumed usage.
						state.terminal = state
							.parser
							.feed(&chunk[..accepted], &mut state.queue)
							.err();
						if accepted < chunk.len() && state.terminal.is_none() {
							state.terminal = Some(Error::Protocol);
						}
					}
				}
				.await;
				match result {
					Ok(Some(update)) => {
						let next = state.request.streaming.then_some(state);
						Some((Ok(update), next))
					}
					Ok(None) => None,
					Err(error) => Some((Err(error), None)),
				}
			},
		)
		.boxed()
	}
}
struct State {
	client: Azure,
	request: ProviderRequest,
	response: Option<reqwest::Response>,
	parser: Sse,
	queue: VecDeque<Update>,
	terminal: Option<Error>,
	total: usize,
}
#[derive(Default)]
struct Sse {
	line: Vec<u8>,
	data: Vec<u8>,
	skip_lf: bool,
	done: bool,
}
impl Sse {
	fn feed(&mut self, bytes: &[u8], updates: &mut VecDeque<Update>) -> Result<(), Error> {
		for &byte in bytes {
			if self.done {
				break;
			}
			if self.skip_lf {
				self.skip_lf = false;
				if byte == b'\n' {
					continue;
				}
			}
			if matches!(byte, b'\n' | b'\r') {
				self.skip_lf = byte == b'\r';
				if self.line.is_empty() {
					if !self.data.is_empty() {
						self.data.pop();
						if self.data == b"[DONE]" {
							self.done = true;
						} else {
							let value: Value =
								serde_json::from_slice(&self.data).map_err(|_| Error::Protocol)?;
							if value.get("error").is_some() {
								return Err(Error::Provider);
							}
							updates.push_back(parse_update(&value, true)?);
						}
						self.data.clear();
					}
				} else if let Some(data) = self.line.strip_prefix(b"data:") {
					let data = data.strip_prefix(b" ").unwrap_or(data);
					if self.data.len().saturating_add(data.len()) > MAX_EVENT {
						return Err(Error::Protocol);
					}
					self.data.extend_from_slice(data);
					self.data.push(b'\n');
				}
				self.line.clear();
			} else {
				self.line.push(byte);
				if self.line.len() > MAX_EVENT {
					return Err(Error::Protocol);
				}
			}
		}
		Ok(())
	}
}
fn parse_update(value: &Value, streaming: bool) -> Result<Update, Error> {
	if value.get("error").is_some() {
		return Err(Error::Provider);
	}
	let mut update = Update::default();
	if let Some(usage) = value.get("usage").filter(|v| !v.is_null()) {
		if !usage.is_object() {
			return Err(Error::Protocol);
		}
		// The frozen SDK types this unused-for-pricing field as Int32 too.
		// A present malformed total cannot turn into successful paid usage.
		let _ = usage_count(usage, "total_tokens")?;
		update.usage = Usage::reported(
			usage_count(usage, "prompt_tokens")?,
			usage_count(usage, "completion_tokens")?,
		);
	}
	let choices = value
		.get("choices")
		.and_then(Value::as_array)
		.ok_or(Error::Protocol)?;
	for choice in choices {
		if choice.get("index").and_then(Value::as_u64) != Some(0) {
			continue;
		}
		let reason = choice.get("finish_reason").and_then(Value::as_str);
		// A content_filter finish is still a provider response (possibly with
		// billed usage), NOT a transport exception. Frozen SDK capture pins this.
		update.finish_reason = reason.map(str::to_owned);
		let delta = choice
			.get(if streaming { "delta" } else { "message" })
			.ok_or(Error::Protocol)?;
		if let Some(text) = delta.get("content").and_then(Value::as_str) {
			update.text.push_str(text);
		}
		if let Some(tools) = delta.get("tool_calls").and_then(Value::as_array) {
			if tools.len() > 128 {
				return Err(Error::Protocol);
			}
			for (position, tool) in tools.iter().enumerate() {
				let index = if streaming {
					tool.get("index")
						.and_then(Value::as_u64)
						.and_then(|v| usize::try_from(v).ok())
						.ok_or(Error::Protocol)?
				} else {
					position
				};
				if index >= 128 {
					return Err(Error::Protocol);
				}
				update.tools.push(ToolDelta {
					index,
					id: tool.get("id").and_then(Value::as_str).map(str::to_owned),
					name: tool
						.pointer("/function/name")
						.and_then(Value::as_str)
						.map(str::to_owned),
					arguments: tool
						.pointer("/function/arguments")
						.and_then(Value::as_str)
						.unwrap_or("")
						.into(),
				});
			}
		}
	}
	Ok(update)
}
fn usage_count(usage: &Value, key: &str) -> Result<i64, Error> {
	match usage.get(key) {
		None => Ok(0),
		Some(value) => value
			.as_i64()
			.and_then(|v| i32::try_from(v).ok())
			.map(i64::from)
			.ok_or(Error::Protocol),
	}
}
fn wire_request(request: &ProviderRequest, deployment: &str) -> Result<Value, Error> {
	let mut messages = Vec::new();
	if let Some(instructions) = &request.options.instructions {
		messages.push(json!({"role":"system","content":instructions}));
	}
	for message in &request.messages {
		let mut parts = Vec::new();
		let mut calls = Vec::new();
		for content in &message.contents {
			match content {
				Content::Text(text) => parts.push(json!({"type":"text","text":text})),
				Content::ImageUrl(url) => {
					parts.push(json!({"type":"image_url","image_url":{"url":url}}))
				}
				Content::HostedFile(file) => {
					parts.push(json!({"type":"file","file":{"file_id":file}}))
				}
				Content::FunctionCall {
					id,
					name,
					arguments,
				} => calls.push(
					json!({"id":id,"type":"function","function":{"name":name,"arguments":arguments.raw()}}),
				),
				Content::FunctionResult { id, result } => {
					messages.push(json!({"role":"tool","tool_call_id":id,"content":result.raw()}))
				}
			}
		}
		if !parts.is_empty() || !calls.is_empty() {
			let mut value = json!({"role":message.role});
			if !parts.is_empty() {
				value["content"] = if parts.len() == 1 && parts[0]["type"] == "text" {
					parts[0]["text"].clone()
				} else {
					json!(parts)
				};
			}
			if !calls.is_empty() {
				value["tool_calls"] = json!(calls);
			}
			messages.push(value);
		}
	}
	let options = &request.options;
	let mut value = json!({"model":deployment,"messages":messages,"reasoning_effort":options.reasoning_effort.as_deref().unwrap_or("none"),"max_completion_tokens":options.max_output_tokens});
	if request.streaming {
		value["stream"] = json!(true);
		value["stream_options"] = json!({"include_usage":true});
	}
	if let Some(temp) = options.temperature {
		value["temperature"] = json!(temp);
	}
	if !options.tools.is_empty() {
		value["tools"]=json!(options.tools.iter().map(|tool|json!({"type":"function","function":{"name":tool.name,"description":tool.description,"parameters":tool.schema}})).collect::<Vec<_>>());
		value["tool_choice"] = json!("auto");
	}
	if let Some(format) = &options.response_format {
		let mut schema = json!({"name":format.name,"schema":format.schema});
		if let Some(description) = &format.description {
			schema["description"] = json!(description);
		}
		if let Some(strict) = format.strict {
			schema["strict"] = json!(strict);
		}
		value["response_format"] = json!({"type":"json_schema","json_schema":schema});
	}
	Ok(value)
}
