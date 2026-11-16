//! The model supplies IDs only. Every display property is resolved here again
//! on reload; stored labels/URLs never become trusted output.
use crate::{auth::AuthError, catalogue::queries, clr_guid};
use serde_json::{Value, json};
const CATALOG: &str = "urn:liedertafel:archiv:a2ui:katalog:v1";
#[derive(Default)]
pub(super) struct Surfaces {
	messages: Vec<Value>,
	keys: std::collections::HashSet<String>,
}
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
	komponente: Option<String>,
	lied_id: Option<String>,
	lied_ids: Option<Vec<String>>,
	auftritt_id: Option<String>,
}
impl Surfaces {
	pub fn json(&self) -> Option<Value> {
		(!self.messages.is_empty()).then(|| json!(self.messages))
	}
	fn add(&mut self, props: Value) -> bool {
		let key = format!(
			"{}|{}|{}|{}",
			props["component"], props["liedId"], props["auftrittId"], props["quellen"]
		);
		if self.messages.len() >= 8 || !self.keys.insert(key) {
			return false;
		}
		let id = format!("s{}", self.messages.len() / 2 + 1);
		self.messages
			.push(json!({"version":"v0.9","createSurface":{"surfaceId":id,"catalogId":CATALOG}}));
		self.messages.push(
			json!({"version":"v0.9","updateComponents":{"surfaceId":id,"components":[props]}}),
		);
		true
	}
	pub async fn show(&mut self, pool: &sqlx::PgPool, args: &Value) -> Result<String, AuthError> {
		#[derive(serde::Deserialize)]
		struct Args {
			komponenten: Option<Vec<Request>>,
		}
		let Ok(args) = serde_json::from_value::<Args>(args.clone()) else {
			return Ok("{}".into());
		};
		let list = args.komponenten.unwrap_or_default();
		let mut dropped = list.len().saturating_sub(12);
		let mut shown = Vec::new();
		for request in list.into_iter().take(12) {
			if self.messages.len() >= 8 {
				dropped += 1;
				continue;
			}
			match resolve(pool, &request).await? {
				Some(props) if self.add(props.clone()) => {
					shown.push(json!({"komponente":props["component"]}))
				}
				_ => dropped += 1,
			}
		}
		Ok(crate::reading::programme_script::default_json(json!({"angezeigt":shown,"verworfen":dropped,"hinweis":if shown.is_empty(){"Es wurde keine Oberfläche angezeigt. Antworte nur mit Text und erwähne keine Karte."}else{"Die Oberfläche erscheint unter deiner Antwort. Beschreibe sie kurz im Text und wiederhole nicht ihren Inhalt."}}).to_string()))
	}
}
fn present(value: &Option<String>) -> bool {
	value.as_deref().is_some_and(|s| !s.trim().is_empty())
}
fn clip(text: &str, bound: usize) -> String {
	if text.encode_utf16().count() <= bound {
		text.into()
	} else {
		format!("{}…", super::tools::wire_prefix(text, bound - 1))
	}
}
fn optional(root: &mut Value, key: &str, value: Option<&str>) {
	if let Some(value) = value.filter(|s| !s.trim().is_empty()) {
		root[key] = json!(clip(value.trim(), 200));
	}
}
fn beginning(text: &str) -> String {
	let text = text
		.split('\n')
		.map(str::trim)
		.filter(|s| !s.is_empty())
		.take(2)
		.collect::<Vec<_>>()
		.join(" / ");
	if text.encode_utf16().count() <= 160 {
		return text;
	}
	// Frozen LastIndexOf(' ',159) includes the space AT index159.
	let cut = text
		.encode_utf16()
		.take(160)
		.enumerate()
		.filter_map(|(i, c)| (c == u16::from(b' ')).then_some(i))
		.last()
		.filter(|&i| i > 80)
		.unwrap_or(159);
	format!(
		"{}…",
		super::tools::wire_prefix(&text, cut).trim_end_matches([' ', ',', ';', '/'])
	)
}
async fn resolve(pool: &sqlx::PgPool, request: &Request) -> Result<Option<Value>, AuthError> {
	let kind = request.komponente.as_deref().unwrap_or_default();
	let (one, many, event) = match kind {
		"LiedKarte" => (true, false, false),
		"Quellen" => (false, true, false),
		"AuftrittKarte" | "ProgrammListe" => (false, false, true),
		_ => return Ok(None),
	};
	if present(&request.lied_id) != one
		|| present(&request.auftritt_id) != event
		|| request.lied_ids.as_ref().is_some_and(|v| !v.is_empty()) != many
	{
		return Ok(None);
	}
	if many {
		let mut ids = Vec::new();
		for raw in request.lied_ids.iter().flatten() {
			if let Some(id) = clr_guid::try_parse(raw)
				&& !ids.contains(&id)
			{
				ids.push(id);
				if ids.len() == 10 {
					break;
				}
			}
		}
		if ids.is_empty() {
			return Ok(None);
		}
		let lieder: Vec<crate::catalogue::Lied> =
			sqlx::query_as(r#"SELECT * FROM lieder WHERE "Id"=ANY($1) AND "DeletedAt" IS NULL"#)
				.bind(&ids)
				.fetch_all(pool)
				.await?;
		let sources: Vec<_> = ids
			.into_iter()
			.filter_map(|id| lieder.iter().find(|l| l.id == id))
			.map(|lied| {
				let mut source = json!({"liedId":lied.id,"titel":clip(&lied.title,200)});
				optional(&mut source, "komponist", lied.composer.as_deref());
				source
			})
			.collect();
		return Ok(
			(!sources.is_empty()).then(|| json!({"id":"root","component":kind,"quellen":sources}))
		);
	}
	if event {
		let Some(id) = request.auftritt_id.as_deref().and_then(clr_guid::try_parse) else {
			return Ok(None);
		};
		let Some(event) = crate::events::visible(pool)
			.await?
			.into_iter()
			.find(|e| e.id == id)
		else {
			return Ok(None);
		};
		let mut root =
			json!({"id":"root","component":kind,"auftrittId":id,"titel":clip(&event.title,200)});
		if kind == "AuftrittKarte" {
			root["art"] = json!(if crate::events::validate_kind(&event.kind).is_ok() {
				event.kind.as_str()
			} else {
				"other"
			});
			root["datum"] = json!(event.display());
			optional(&mut root, "ort", event.venue.as_deref());
		} else {
			let rows:Vec<(i32,uuid::Uuid,String,Option<String>)>=sqlx::query_as(r#"SELECT i."Position",i."LiedId",l."Title",i."Note" FROM programme_items i JOIN programmes p ON p."Id"=i."ProgrammeId" JOIN lieder l ON l."Id"=i."LiedId" WHERE p."EventId"=$1 AND l."DeletedAt" IS NULL ORDER BY i."Position",i."Id" LIMIT 60"#).bind(id).fetch_all(pool).await?;
			if rows.is_empty() {
				return Ok(None);
			}
			root["punkte"] = json!(
				rows.into_iter()
					.map(|(position, id, title, note)| {
						let mut item =
							json!({"position":position,"liedId":id,"titel":clip(&title,200)});
						if let Some(note) = note.filter(|s| !s.trim().is_empty()) {
							item["notiz"] = json!(clip(note.trim(), 500));
						}
						item
					})
					.collect::<Vec<_>>()
			);
		}
		return Ok(Some(root));
	}
	let Some(id) = request.lied_id.as_deref().and_then(clr_guid::try_parse) else {
		return Ok(None);
	};
	let Some(lied) = queries::lied(pool, id).await? else {
		return Ok(None);
	};
	let mut root =
		json!({"id":"root","component":"LiedKarte","liedId":id,"titel":clip(&lied.title,200)});
	optional(&mut root, "komponist", lied.composer.as_deref());
	optional(&mut root, "textdichter", lied.lyricist.as_deref());
	optional(
		&mut root,
		"textanfang",
		lied.lyrics.as_deref().map(beginning).as_deref(),
	);
	Ok(Some(root))
}
pub(super) async fn recheck(
	pool: &sqlx::PgPool,
	stored: Value,
) -> Result<Option<Value>, AuthError> {
	let Some(messages) = stored.as_array() else {
		return Ok(None);
	};
	let mut pairs: Vec<(String, Option<Value>)> = Vec::new();
	for message in messages {
		if message.as_object().is_none_or(|m| m.len() != 2) || message["version"] != "v0.9" {
			continue;
		}
		if message["createSurface"]["catalogId"] == CATALOG {
			if let Some(id) = message["createSurface"]["surfaceId"].as_str() {
				pairs.push((id.into(), None));
			}
		} else if let (Some(id), Some(components)) = (
			message["updateComponents"]["surfaceId"].as_str(),
			message["updateComponents"]["components"].as_array(),
		) && components.len() == 1
			&& components[0].is_object()
			&& let Some((_, slot)) = pairs
				.iter_mut()
				.find(|(key, slot)| key == id && slot.is_none())
		{
			*slot = Some(components[0].clone());
		}
	}
	let mut state = Surfaces::default();
	for (_, root) in pairs {
		let Some(root) = root.filter(|v| v["id"] == "root") else {
			continue;
		};
		let kind = root["component"].as_str().unwrap_or_default();
		let request = Request {
			komponente: Some(kind.into()),
			lied_id: (kind == "LiedKarte")
				.then(|| root["liedId"].as_str().map(str::to_owned))
				.flatten(),
			lied_ids: (kind == "Quellen")
				.then(|| {
					root["quellen"].as_array().map(|items| {
						items
							.iter()
							.map(|i| i["liedId"].as_str().unwrap_or_default().to_owned())
							.collect()
					})
				})
				.flatten(),
			auftritt_id: (["AuftrittKarte", "ProgrammListe"].contains(&kind))
				.then(|| root["auftrittId"].as_str().map(str::to_owned))
				.flatten(),
		};
		if let Some(props) = resolve(pool, &request).await? {
			state.add(props);
		}
	}
	Ok(state.json())
}
