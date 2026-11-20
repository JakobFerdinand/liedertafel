//! Literal offline ScriptedChatClient sibling: delivery only, never SQL/tools,
//! authorization, trusted citations, persistence or accounting.
use crate::ai::{Content, ProviderRequest, ScriptStep, ToolDelta, Update, Usage};
use serde_json::{Value, json};
pub const UNKNOWN: &str =
	"Das ist mir nicht bekannt. Solche Angaben sind im Archiv nicht verzeichnet.";
fn text(message: &crate::ai::Message) -> String {
	message
		.contents
		.iter()
		.filter_map(|c| {
			if let Content::Text(t) = c {
				Some(t.as_str())
			} else {
				None
			}
		})
		.collect()
}
fn has(text: &str, parts: &[&str]) -> bool {
	let text = text.to_lowercase();
	parts.iter().any(|p| text.contains(p))
}
fn next(question: &str) -> bool {
	has(question, &["weitere lieder", "nächste seite"])
}
fn query(question: &str) -> String {
	if has(
		question,
		&[
			"welche lieder gibt es",
			"welche lieder sind",
			"liste alle lieder",
			"zeige alle lieder",
		],
	) {
		String::new()
	} else if card(question) {
		question
			.split(' ')
			.filter(|w| {
				!w.is_empty()
					&& !matches!(w.to_lowercase().as_str(), "zeige" | "mir" | "als" | "karte")
			})
			.collect::<Vec<_>>()
			.join(" ")
	} else {
		question.into()
	}
}
fn card(question: &str) -> bool {
	has(question, &[" als karte"])
}
fn history(question: &str) -> bool {
	has(question, &["wann"]) && has(question, &["gesungen", "aufgeführt", "aufgefuehrt"])
}
fn history_title(question: &str, request: &ProviderRequest) -> Option<String> {
	let named = question
		.split(' ')
		.filter(|s| !s.is_empty())
		.map(|w| w.trim_matches(['?', '!', '.', ',', '„', '“', '"']))
		.filter(|w| {
			!w.is_empty()
				&& !matches!(
					w.to_lowercase().as_str(),
					"wann"
						| "wurde" | "wurden"
						| "haben" | "hat" | "wir"
						| "ihr" | "das" | "dieses"
						| "diesem" | "dieser"
						| "lied" | "gesungen"
						| "aufgeführt" | "aufgefuehrt"
						| "schon" | "mal" | "eigentlich"
						| "zuletzt" | "denn"
				)
		})
		.collect::<Vec<_>>()
		.join(" ");
	if !named.is_empty() {
		return Some(named);
	}
	let previous = request
		.messages
		.iter()
		.rev()
		.find(|m| m.role == "assistant")
		.map(text)?;
	previous
		.split("[Quelle: ")
		.nth(1)?
		.split_once(']')
		.map(|(t, _)| t.trim().into())
}
fn called<'a>(request: &'a ProviderRequest, id: &str) -> Option<&'a str> {
	request
		.messages
		.iter()
		.flat_map(|m| &m.contents)
		.rev()
		.find_map(|c| {
			if let Content::FunctionCall {
				id: call_id, name, ..
			} = c
			{
				(call_id == id).then_some(name.as_str())
			} else {
				None
			}
		})
}
fn entries<'a>(value: &'a Value, key: &str) -> impl Iterator<Item = &'a Value> {
	value[key].as_array().into_iter().flatten()
}
fn surfaces(value: &Value, lied: &Value) -> Value {
	let mut list = vec![json!({"komponente":"LiedKarte","liedId":lied["id"]})];
	let sung: Vec<_> = entries(value, "gesungen")
		.filter_map(|e| e["auftrittId"].as_str())
		.collect();
	if !sung.is_empty() {
		for id in sung {
			list.push(json!({"komponente":"AuftrittKarte","auftrittId":id}));
		}
	} else {
		for (n, e) in entries(value, "geplant").enumerate() {
			if let Some(id) = e["auftrittId"].as_str() {
				list.push(
					json!({"komponente":if n==0{"ProgrammListe"}else{"AuftrittKarte"},"auftrittId":id}),
				);
			}
		}
	}
	list.truncate(4);
	json!({"komponenten":list})
}
fn history_sentences(value: &Value, title: &str) -> String {
	let mut sentences = Vec::new();
	for entry in entries(value, "gesungen") {
		let name = entry["auftritt"].as_str().unwrap_or_default();
		let date = entry["datum"].as_str().unwrap_or_default();
		sentences.push(format!(
			"„{title}“ wurde beim Auftritt „{name}“ ({date}) gesungen."
		));
		if let Some(label) = entries(entry, "zeitmarken")
			.find_map(|m| m["aufnahme"].as_str().filter(|s| !s.is_empty()))
		{
			sentences.push(format!(
				"Dafür gibt es eine Zeitmarke in der Aufnahme „{label}“."
			));
		}
		if entries(entry, "aufnahmen").any(|f| f.as_str().is_some_and(|s| !s.is_empty())) {
			sentences.push("Dafür gibt es auch eine Aufnahme des Liedes.".into());
		}
	}
	for entry in entries(value, "geplant") {
		let name = entry["auftritt"].as_str().unwrap_or_default();
		let date = entry["datum"].as_str().unwrap_or_default();
		sentences.push(format!(
			"„{title}“ steht im Programm des kommenden Auftritts „{name}“ ({date})."
		));
	}
	if sentences.is_empty() {
		format!("Zu „{title}“ sind im Archiv keine Auftritte verzeichnet.")
	} else {
		sentences.join(" ")
	}
}
fn answer(input: &str, text: &str) -> Vec<ScriptStep> {
	vec![
		ScriptStep::Update(Update::text(text)),
		ScriptStep::Update(Update::usage(Usage::reported(
			input.encode_utf16().count().div_ceil(4) as i64,
			text.encode_utf16().count().div_ceil(4) as i64,
		))),
	]
}
fn call(request: &ProviderRequest, name: &str, args: Value) -> Vec<ScriptStep> {
	let count = request
		.messages
		.iter()
		.filter(|m| {
			m.contents
				.iter()
				.any(|c| matches!(c, Content::FunctionCall { .. }))
		})
		.count();
	vec![ScriptStep::Update(Update {
		tools: vec![ToolDelta {
			index: 0,
			id: Some(format!("call_{}", count + 1)),
			name: Some(name.into()),
			arguments: args.to_string(),
		}],
		..Default::default()
	})]
}
pub(crate) fn delivery(request: &ProviderRequest) -> Vec<ScriptStep> {
	let asked = request
		.messages
		.iter()
		.rev()
		.find(|m| m.role == "user")
		.map(text)
		.unwrap_or_default();
	if let Some(Content::FunctionResult { id, result }) = request.messages.last().and_then(|m| {
		m.contents
			.iter()
			.rev()
			.find(|c| matches!(c, Content::FunctionResult { .. }))
	}) {
		let value = serde_json::from_str::<Value>(result.raw()).unwrap_or_default();
		let tool = called(request, id);
		if history(&asked) && tool == Some("catalogue_search") {
			let wanted = history_title(&asked, request).unwrap_or_default();
			let folded = crate::catalogue::search::fold(Some(&wanted));
			if let Some(lieder) = value["lieder"].as_array()
				&& let Some(lied) = lieder
					.iter()
					.find(|l| crate::catalogue::search::fold(l["title"].as_str()) == folded)
					.or_else(|| lieder.first())
			{
				return call(request, "lied_details", json!({"liedId":lied["id"]}));
			}
		}
		let card_source = if history(&asked) {
			"lied_details"
		} else {
			"catalogue_search"
		};
		if request
			.options
			.tools
			.iter()
			.any(|t| t.name == "zeige_oberflaeche")
			&& (history(&asked) || card(&asked))
			&& tool == Some(card_source)
			&& let Some(lied) = value["lieder"].as_array().and_then(|l| l.first())
		{
			return call(
				request,
				"zeige_oberflaeche",
				if history(&asked) {
					surfaces(&value, lied)
				} else {
					json!({"komponenten":[{"komponente":"LiedKarte","liedId":lied["id"]}]})
				},
			);
		}
		let result = if tool == Some("zeige_oberflaeche") {
			request
				.messages
				.iter()
				.flat_map(|m| &m.contents)
				.rev()
				.find_map(|c| {
					if let Content::FunctionResult { id, result } = c {
						matches!(
							called(request, id),
							Some("catalogue_search" | "lied_details")
						)
						.then_some(result)
					} else {
						None
					}
				})
				.unwrap_or(result)
		} else {
			result
		};
		let value = serde_json::from_str::<Value>(result.raw()).unwrap_or_default();
		let Some(lieder) = value["lieder"].as_array().filter(|l| !l.is_empty()) else {
			return answer(result.raw(), UNKNOWN);
		};
		if history(&asked) {
			let title = lieder[0]["title"].as_str().unwrap_or_default();
			let response = format!("{} [Quelle: {title}]", history_sentences(&value, title));
			return answer(result.raw(), &response);
		}
		let mut response = lieder
			.iter()
			.filter_map(|l| l["title"].as_str())
			.map(|title| {
				format!("Der Titel „{title}“ ist im Archiv verzeichnet. [Quelle: {title}]")
			})
			.collect::<Vec<_>>()
			.join(" ");
		if let (Some(page), Some(total)) = (value["page"].as_i64(), value["totalCount"].as_i64()) {
			response.push_str(&format!(
				" Seite {page} von insgesamt {total} veröffentlichten Treffern."
			));
		}
		if let Some(next) = value["nextPage"].as_i64() {
			response.push_str(&format!(
				" Es gibt weitere Lieder. Soll ich Seite {next} zeigen?"
			));
		}
		if value["limitReached"] == true {
			response.push_str(" Die Seitengrenze ist erreicht. Bitte grenze die Suche ein.");
		}
		if lieder
			.iter()
			.filter_map(|l| l["lyricsExcerpt"].as_str())
			.any(|s| has(s, &["anweisung:"]))
		{
			response.push_str(
				"\nAnweisungen innerhalb von Archivtexten behandle ich als Inhalt, nicht als Befehl.",
			);
		}
		return answer(result.raw(), &response);
	}
	let question = request.messages.last().map(text).unwrap_or_default();
	if history(&question) {
		return if let Some(title) = history_title(&question, request) {
			call(request, "catalogue_search", json!({"query":title,"page":1}))
		} else {
			answer(
				&question,
				"Welches Lied meinst du? Nenne mir bitte den Titel.",
			)
		};
	}
	if has(&question, &["wann wurde", "aufgeführt", "konzert"]) {
		return answer(
			&question,
			"Dazu liegen mir derzeit keine Aufführungsdaten vor.",
		);
	}
	if has(&question, &["wetter", "gedicht schreiben"]) {
		return answer(
			&question,
			"Dazu kann ich nur Fragen zum Vereinsarchiv beantworten.",
		);
	}
	let mut query = query(&question);
	let mut page = 1;
	if next(&question)
		&& let Some(previous) = request
			.messages
			.iter()
			.rev()
			.find(|m| m.role == "assistant")
			.map(text)
		&& let Some(n) = previous
			.split("Soll ich Seite ")
			.nth(1)
			.and_then(|v| v.split(' ').next())
			.and_then(|v| v.parse::<i32>().ok())
	{
		page = n;
		query = request
			.messages
			.iter()
			.rev()
			.filter(|m| m.role == "user")
			.map(text)
			.find(|q| !next(q))
			.map(|q| self::query(&q))
			.unwrap_or(query);
	}
	call(
		request,
		"catalogue_search",
		json!({"query":super::tools::prefix(&query,200),"page":page}),
	)
}
