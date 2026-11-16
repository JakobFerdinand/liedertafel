//! The pinned AGUI.Abstractions1.0.0 request grammar, not browser authority.
//! Generated CLR records bind case-insensitively and validate EACH occurrence;
//! custom message/content discriminators and user properties use last EXACT
//! JsonElement lookups. Keep raw occurrences until those distinct rules apply.
use serde::{
	Deserialize,
	de::{MapAccess, Visitor},
};
use serde_json::value::RawValue;

pub(super) struct Input {
	pub thread_id: String,
	pub run_id: String,
	pub parent_run_id: Option<String>,
	pub question: String,
}
struct Object(Vec<(String, Box<RawValue>)>);
impl<'de> Deserialize<'de> for Object {
	fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
		struct ObjectVisitor;
		impl<'de> Visitor<'de> for ObjectVisitor {
			type Value = Object;
			fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
				f.write_str("a protocol object")
			}
			fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Object, A::Error> {
				let mut fields = Vec::new();
				while let Some(key) = map.next_key::<String>()? {
					fields.push((key, map.next_value()?));
				}
				Ok(Object(fields))
			}
		}
		d.deserialize_map(ObjectVisitor)
	}
}
type Bound<T> = Result<T, ()>;
fn object(raw: &str) -> Bound<Object> {
	serde_json::from_str(raw).map_err(|_| ())
}
fn string(raw: &str) -> Bound<Option<String>> {
	serde_json::from_str(raw).map_err(|_| ())
}
fn array(raw: &str) -> Bound<Option<Vec<Box<RawValue>>>> {
	serde_json::from_str(raw).map_err(|_| ())
}
fn null(raw: &str) -> bool {
	raw.trim() == "null"
}
impl Object {
	fn exact(&self, key: &str) -> Option<&str> {
		self.0
			.iter()
			.rev()
			.find(|(k, _)| k == key)
			.map(|(_, v)| v.get())
	}
	fn last(&self, key: &str) -> Option<&str> {
		self.0
			.iter()
			.rev()
			.find(|(k, _)| k.eq_ignore_ascii_case(key))
			.map(|(_, v)| v.get())
	}
	fn strings(&self, keys: &[&str]) -> Bound<()> {
		for (key, value) in &self.0 {
			if keys.iter().any(|k| key.eq_ignore_ascii_case(k)) {
				string(value.get())?;
			}
		}
		Ok(())
	}
	fn text(&self, key: &str) -> Bound<Option<String>> {
		self.last(key).map(string).transpose().map(Option::flatten)
	}
}

pub(super) fn parse(bytes: &[u8]) -> Bound<Input> {
	let root: Object = serde_json::from_slice(bytes).map_err(|_| ())?;
	root.strings(&["threadId", "runId", "parentRunId", "protocolVersion"])?;
	let mut messages = None;
	for (key, value) in &root.0 {
		match key.to_ascii_lowercase().as_str() {
			"messages" => {
				messages = array(value.get())?
					.map(|list| {
						list.into_iter()
							.map(|v| message(v.get()))
							.collect::<Bound<Vec<_>>>()
					})
					.transpose()?;
			}
			"tools" | "context" | "resume" => {
				if let Some(list) = array(value.get())? {
					for item in list {
						ambient(&key.to_ascii_lowercase(), item.get())?;
					}
				}
			}
			_ => {} // state/forwardedProps/unknown values are opaque, never grounding.
		}
	}
	let question = messages
		.ok_or(())?
		.into_iter()
		.rev()
		.find_map(|(role, text)| (role == "user").then_some(text))
		.ok_or(())?
		.trim()
		.to_owned();
	// A null runId makes the frozen converter emit invalid strict-client SSE.
	// Refuse before paid work, rather than reproducing that unsafe oracle output.
	if root.last("runId").is_some_and(null) {
		return Err(());
	}
	Ok(Input {
		thread_id: root.text("threadId")?.unwrap_or_default(),
		run_id: root.text("runId")?.unwrap_or_default(),
		parent_run_id: root.text("parentRunId")?,
		question,
	})
}
fn ambient(kind: &str, raw: &str) -> Bound<()> {
	if null(raw) && kind == "context" {
		return Ok(());
	}
	let obj = object(raw)?;
	match kind {
		"tools" => {
			obj.strings(&["name", "description"])?;
			if obj.text("name")?.is_none_or(|n| n.is_empty()) {
				return Err(());
			}
		}
		"context" => obj.strings(&["description", "value"])?,
		"resume" => {
			obj.strings(&["interruptId", "status"])?;
			if obj.text("interruptId")?.is_none_or(|s| s.is_empty()) {
				return Err(());
			}
		}
		_ => return Err(()),
	}
	Ok(())
}
fn message(raw: &str) -> Bound<(String, String)> {
	let obj = object(raw)?;
	let role = string(obj.exact("role").ok_or(())?)?.ok_or(())?;
	let text = match role.as_str() {
		"user" => {
			for key in ["id", "name", "encryptedValue", "subagentRunId"] {
				if let Some(value) = obj.exact(key) {
					string(value)?;
				}
			}
			obj.exact("content")
				.map(content)
				.transpose()?
				.unwrap_or_default()
		}
		"assistant" | "system" | "developer" | "tool" => {
			let mut keys = vec!["id", "subagentRunId", "encryptedValue"];
			if matches!(role.as_str(), "assistant" | "system" | "developer") {
				keys.push("name");
			}
			if role == "tool" {
				keys.extend(["toolCallId", "error"]);
			}
			obj.strings(&keys)?;
			let mut text = String::new();
			for (key, value) in &obj.0 {
				if key.eq_ignore_ascii_case("content") {
					text = if role == "tool" {
						content(value.get())?
					} else {
						string(value.get())?.unwrap_or_default()
					};
				}
				if role == "assistant"
					&& key.eq_ignore_ascii_case("toolCalls")
					&& let Some(list) = array(value.get())?
				{
					for item in list {
						tool_call(item.get())?;
					}
				}
			}
			text
		}
		_ => return Err(()),
	};
	Ok((role, text))
}
fn tool_call(raw: &str) -> Bound<()> {
	let obj = object(raw)?;
	obj.strings(&["id", "type", "encryptedValue"])?;
	if obj.last("id").is_some_and(null) {
		return Err(());
	}
	for (key, value) in &obj.0 {
		if key.eq_ignore_ascii_case("function") {
			let function = object(value.get())?;
			function.strings(&["name", "arguments"])?;
			if function.last("name").is_some_and(null) {
				return Err(());
			}
			if let Some(args) = function.text("arguments")?
				&& !args.is_empty()
			{
				let args = object(&args)?;
				drop(args);
			}
		}
	}
	Ok(())
}
fn content(raw: &str) -> Bound<String> {
	if raw.trim_start().starts_with('"') {
		return string(raw)?.ok_or(());
	}
	let parts = array(raw)?.ok_or(())?;
	let mut text = String::new();
	for part in parts {
		let obj = object(part.get())?;
		let kind = string(obj.exact("type").ok_or(())?)?.ok_or(())?;
		obj.strings(&["id"])?;
		match kind.as_str() {
			"text" => {
				obj.strings(&["text"])?;
				text.push_str(&obj.text("text")?.unwrap_or_default());
			}
			"image" | "audio" | "video" | "document" => {
				for (key, value) in &obj.0 {
					if key.eq_ignore_ascii_case("source") && !null(value.get()) {
						source(value.get())?;
					}
				}
				if obj.last("source").is_none_or(null) {
					return Err(());
				}
			}
			_ => return Err(()),
		}
	}
	Ok(text)
}
fn source(raw: &str) -> Bound<()> {
	let obj = object(raw)?;
	let kind = string(obj.exact("type").ok_or(())?)?.ok_or(())?;
	match kind.as_str() {
		"data" | "url" => obj.strings(&["value", "mimeType"])?,
		"file" => obj.strings(&["value", "mimeType", "provider"])?,
		_ => return Err(()),
	}
	let value = obj.text("value")?.ok_or(())?;
	if kind == "data" {
		use base64::Engine;
		base64::engine::general_purpose::STANDARD
			.decode(&value)
			.map_err(|_| ())?;
		if obj.text("mimeType")?.is_none() {
			return Err(());
		}
	}
	Ok(())
}
