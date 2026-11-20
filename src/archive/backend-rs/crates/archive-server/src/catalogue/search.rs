//! Shared pure search contract (LiedSearch/CatalogueText, #192).
use super::Lied;
use std::collections::HashMap;

pub fn fold(text: Option<&str>) -> String {
	text.unwrap_or_default()
		.chars()
		.map(|c| {
			let mut lower = c.to_lowercase();
			let first = lower.next().unwrap_or(c);
			if lower.next().is_some() { c } else { first }
		})
		.collect::<String>()
		.replace('ä', "ae")
		.replace('ö', "oe")
		.replace('ü', "ue")
		.replace('ß', "ss")
		.replace("ae", "a")
		.replace("oe", "o")
		.replace("ue", "u")
}
pub fn tokens_of(query: &str) -> Vec<String> {
	query
		.split(' ')
		.map(str::trim)
		.map(|s| fold(Some(s)))
		.filter(|s| !s.is_empty())
		.collect()
}
pub struct SearchField {
	pub label: &'static str,
	pub folded: String,
}
pub struct SearchMatch<'a> {
	pub lied: &'a Lied,
	pub rank: u8,
	pub fields: Vec<SearchField>,
}
pub fn fields_of(lied: &Lied, noten: Option<&str>) -> Vec<SearchField> {
	[
		("im Titel", Some(lied.title.as_str())),
		("beim Komponisten", lied.composer.as_deref()),
		("beim Bearbeiter", lied.arranger.as_deref()),
		("beim Texter", lied.lyricist.as_deref()),
		("im Liedtext", lied.lyrics.as_deref()),
		("in den Noten", noten),
	]
	.into_iter()
	.filter_map(|(label, text)| {
		let folded = fold(text);
		(!folded.is_empty()).then_some(SearchField { label, folded })
	})
	.collect()
}
pub fn labels(tokens: &[String], fields: &[SearchField]) -> Vec<&'static str> {
	fields
		.iter()
		.filter(|f| tokens.iter().any(|t| f.folded.contains(t)))
		.map(|f| f.label)
		.collect()
}
pub fn matching<'a>(
	tokens: &[String],
	rows: &'a [Lied],
	noten: &HashMap<Uuid, String>,
	query: &str,
) -> Vec<SearchMatch<'a>> {
	let mut matches: Vec<_> = rows
		.iter()
		.filter_map(|lied| {
			let fields = fields_of(lied, noten.get(&lied.id).map(String::as_str));
			if !tokens
				.iter()
				.all(|t| fields.iter().any(|f| f.folded.contains(t)))
			{
				return None;
			}
			let title = fold(Some(&lied.title));
			let rank = if tokens.is_empty() {
				2
			} else if title == fold(Some(query)) {
				0
			} else if tokens.iter().all(|t| title.contains(t)) {
				1
			} else {
				2
			};
			Some(SearchMatch { lied, rank, fields })
		})
		.collect();
	matches.sort_by(|a, b| {
		a.rank
			.cmp(&b.rank)
			.then_with(|| ordinal(&fold(Some(&a.lied.title)), &fold(Some(&b.lied.title))))
			.then(a.lied.id.cmp(&b.lied.id))
	});
	matches
}
use uuid::Uuid;
pub fn ordinal(a: &str, b: &str) -> std::cmp::Ordering {
	a.encode_utf16().cmp(b.encode_utf16())
}
pub fn optional_ordinal_ignore_case(a: Option<&str>, b: Option<&str>) -> std::cmp::Ordering {
	fn upper(s: &str) -> String {
		s.chars()
			.map(|c| {
				let mut chars = c.to_uppercase();
				let first = chars.next().unwrap_or(c);
				if chars.next().is_some() || matches!(c, '\u{0131}' | '\u{017f}') {
					c
				} else {
					first
				}
			})
			.collect()
	}
	match (a, b) {
		(None, None) => std::cmp::Ordering::Equal,
		(None, Some(_)) => std::cmp::Ordering::Less,
		(Some(_), None) => std::cmp::Ordering::Greater,
		(Some(a), Some(b)) => ordinal(&upper(a), &upper(b)),
	}
}
pub fn requested_page(page: Option<i32>) -> i32 {
	page.unwrap_or(1).max(1)
}
pub fn page<'a, 'b>(
	matches: &'b [SearchMatch<'a>],
	number: i32,
	size: usize,
) -> &'b [SearchMatch<'a>] {
	// Preserve frozen unchecked Int32 arithmetic before LINQ Skip; changing
	// extreme pages to a saturation policy would be a product change, not a port.
	let signed_size = i32::try_from(size).unwrap_or(i32::MAX);
	let start = number
		.wrapping_sub(1)
		.max(0)
		.wrapping_mul(signed_size)
		.max(0) as usize;
	let start = start.min(matches.len());
	&matches[start..start.saturating_add(size).min(matches.len())]
}

/// Completed embedded and scan-read text share this path. No tokens means no
/// extraction query; live Datei + live parent remain the canonical predicate.
pub async fn load_noten_texts(
	pool: &sqlx::PgPool,
	tokens: &[String],
) -> Result<HashMap<Uuid, String>, sqlx::Error> {
	let mut texts = HashMap::<Uuid, String>::new();
	if tokens.is_empty() {
		return Ok(texts);
	}
	for (lied, text) in queries::noten_texts(pool).await? {
		let entry = texts.entry(lied).or_default();
		if !entry.is_empty() {
			entry.push('\n');
		}
		entry.push_str(&fold(Some(&text)));
	}
	Ok(texts)
}
use super::queries;
