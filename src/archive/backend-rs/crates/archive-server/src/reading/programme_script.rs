//! Delivery-only programme extension of the central scripted scan provider.
//! It cannot see images, choose catalogue identities, reserve or write anything.
use serde_json::{Value, json};
pub const SAMPLE: &str = "Sängerfest der Liedertafel Mining\n14. Juni 1925\nOrt: Gasthaus zur Post, Mining\n1. Ave Maria\n2. Der Mond ist aufgegangen\n3. Waldfahrt";
const MONTHS: [&str; 12] = [
	"Januar",
	"Februar",
	"März",
	"April",
	"Mai",
	"Juni",
	"Juli",
	"August",
	"September",
	"Oktober",
	"November",
	"Dezember",
];
fn field(name: &str, value: impl ToString, quote: &str) -> Value {
	json!({"feld":name,"wert":value.to_string(),"sicherheit":"sicher","zitat":quote})
}
pub fn fields(prompt: &str) -> String {
	let text = prompt
		.split_once("PROGRAMMTEXT-ANFANG")
		.and_then(|(_, s)| s.split_once("PROGRAMMTEXT-ENDE"))
		.map(|(s, _)| s)
		.unwrap_or_default();
	let mut fields = vec![];
	let mut entries = vec![];
	let mut title = None;
	let mut venue = None;
	let mut after_date = false;
	let year = regex::Regex::new(r"(?:^|[^\p{Nd}])(1[89][0-9]{2}|20[0-9]{2})(?:$|[^\p{Nd}])").ok();
	let day = regex::Regex::new(r"(?:^|[^\p{Nd}])([0-9]{1,2})\.\s*[A-Za-zÄÖÜäöü]").ok();
	let approximate = regex::Regex::new(r"(?i)\b(?:um|ca\.?|circa|etwa)\s").ok();
	let entry = regex::Regex::new(r"^\s*[\p{Nd}&&\x{0000}-\x{FFFF}]{1,3}\s*[.)]\s+(.+)$").ok();
	for line in text.lines().map(str::trim).filter(|s| !s.is_empty()) {
		if line.starts_with("===") {
			continue;
		}
		if let Some(found) = year.as_ref().and_then(|r| r.captures(line)) {
			fields.push(field("year", &found[1], line));
			if let Some(index) = MONTHS.iter().rposition(|month| {
				super::programme::invariant_lower(line)
					.contains(&super::programme::invariant_lower(month))
			}) {
				fields.push(field("month", index + 1, line));
				if let Some(found) = day.as_ref().and_then(|r| r.captures(line)) {
					fields.push(field("day", found[1].parse::<i32>().unwrap_or(0), line));
				}
			}
			if approximate.as_ref().is_some_and(|r| r.is_match(line)) {
				fields.push(field("approximate", "ja", line));
			}
			after_date = true;
			continue;
		}
		if line
			.get(..4)
			.is_some_and(|s| s.eq_ignore_ascii_case("Ort:"))
		{
			venue = Some(line);
			continue;
		}
		if let Some(found) = entry.as_ref().and_then(|r| r.captures(line)) {
			entries.push(json!({"titel":found[1].trim(),"sicherheit":"sicher","zitat":line}));
			after_date = false;
			continue;
		}
		if title.is_none() {
			title = Some(line);
		} else if after_date && venue.is_none() {
			venue = Some(line);
		}
		after_date = false;
	}
	if let Some(title) = title {
		fields.insert(0, field("title", title, title));
		let lower = super::programme::invariant_lower(title);
		let kind = [
			("wedding", &["hochzeit", "trauung"][..]),
			(
				"funeral",
				&["begräbnis", "beerdigung", "requiem", "trauerfeier"][..],
			),
			("service", &["gottesdienst", "messe", "andacht"][..]),
			("festival", &["fest"][..]),
			("concert", &["konzert", "liederabend"][..]),
		]
		.into_iter()
		.find(|(_, keys)| keys.iter().any(|key| lower.contains(key)))
		.map(|(kind, _)| kind);
		if let Some(kind) = kind {
			fields.insert(1, field("kind", kind, title));
		}
	}
	if let Some(venue) = venue {
		fields.push(field(
			"venue",
			if venue
				.get(..4)
				.is_some_and(|s| s.eq_ignore_ascii_case("Ort:"))
			{
				venue[4..].trim()
			} else {
				venue
			},
			venue,
		));
	}
	default_json(json!({"felder":fields,"punkte":entries}).to_string())
}
/// The frozen stand-in uses default System.Text.Json (BasicLatin), not the
/// provider encoder's relaxed Unicode policy. Preserve its synthetic UTF-16
/// response length/token cost as well as the generated JSON data.
fn default_json(raw: String) -> String {
	use std::fmt::Write;
	let mut result = String::new();
	let mut chars = raw.chars();
	while let Some(c) = chars.next() {
		if c == '\\' {
			if let Some(next) = chars.next() {
				if next == '"' {
					result.push_str("\\u0022");
				} else {
					result.push('\\');
					result.push(next);
				}
			}
		} else if !c.is_ascii() || matches!(c, '\'' | '&' | '+' | '<' | '>' | '`' | '\u{7f}') {
			for unit in c.encode_utf16(&mut [0; 2]) {
				let _ = write!(&mut result, "\\u{unit:04X}");
			}
		} else {
			result.push(c);
		}
	}
	result
}
