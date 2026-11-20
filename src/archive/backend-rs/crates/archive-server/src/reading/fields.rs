use crate::{
	auth::Clock,
	catalogue::{Lied, queries},
	extraction::score,
};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use uuid::Uuid;

pub const AUTOMATED_ORDER: [&str; 5] =
	["composer", "arranger", "lyricist", "musical_key", "lyrics"];
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ReadField {
	#[serde(rename = "feld")]
	pub field: Option<String>,
	#[serde(rename = "wert")]
	pub value: Option<String>,
	#[serde(rename = "sicherheit")]
	pub certainty: Option<String>,
	#[serde(rename = "zitat")]
	pub quote: Option<String>,
}
pub fn clean_value(field: &str, raw: Option<&str>) -> Option<String> {
	let value = raw?.replace("\r\n", "\n").trim().to_owned();
	if value.is_empty() {
		return None;
	}
	let count = value.encode_utf16().count();
	((field == "lyrics" && count <= 3500)
		|| (field != "lyrics" && count <= 150 && !value.contains('\n')))
	.then_some(value)
}
pub fn normalize(value: &str) -> String {
	let collapsed = value
		.replace('\u{ad}', "")
		.split_whitespace()
		.collect::<Vec<_>>()
		.join(" ");
	// Frozen .NET invariant casing is scalar/simple (no final-sigma context,
	// no full folding), with U+0130 explicitly preserved by ChangeCaseInvariant.
	// The actual frozen helper's exhaustive scalar + string-context oracle pins
	// this deliberately narrow policy; search/auth casing is not changed.
	collapsed
		.chars()
		.map(|c| {
			if c == '\u{130}' {
				c
			} else {
				c.to_lowercase().next().unwrap_or(c)
			}
		})
		.collect()
}
pub fn quote_is_verified(read: &ReadField, value: &str, text: &str) -> bool {
	let field = read.field.as_deref().unwrap_or_default();
	let normalized = normalize(text);
	let quote = normalize(read.quote.as_deref().unwrap_or_default());
	if quote.encode_utf16().count() < (if field == "lyrics" { 10 } else { 4 })
		|| !normalized.contains(&quote)
	{
		return false;
	}
	match field {
		"title" | "composer" | "lyricist" | "arranger" => quote.contains(&normalize(value)),
		"lyrics" => value
			.lines()
			.map(normalize)
			.filter(|s| !s.is_empty())
			.all(|line| normalized.contains(&line)),
		"musical_key" => {
			let compact = |s: &str| {
				normalize(s)
					.chars()
					.filter(|c| c.is_alphanumeric())
					.collect::<String>()
			};
			let key = compact(value);
			!key.is_empty() && compact(&quote).contains(&key)
		}
		_ => true,
	}
}
fn current<'a>(lied: &'a Lied, field: &str) -> Option<&'a str> {
	match field {
		"composer" => lied.composer.as_deref(),
		"arranger" => lied.arranger.as_deref(),
		"lyricist" => lied.lyricist.as_deref(),
		"musical_key" => lied.musical_key.as_deref(),
		"lyrics" => lied.lyrics.as_deref(),
		_ => None,
	}
}

/// The sole automated catalogue write. A single SQL statement compares the
/// CURRENT field and visible source/owner atomically; no stale model snapshot
/// can overwrite an editor. Only fixed catalogue columns enter SQL syntax.
pub async fn write(
	pool: &sqlx::PgPool,
	datei: Uuid,
	lied: Uuid,
	field: &str,
	value: &str,
	actor: Option<Uuid>,
	clock: &dyn Clock,
) -> Result<bool, sqlx::Error> {
	let Some(column) = crate::catalogue::writes::automated_column(field) else {
		return Ok(false);
	};
	let max = if field == "lyrics" { 5000 } else { 200 };
	let value = value.trim();
	if value.is_empty() || value.encode_utf16().count() > max {
		return Ok(false);
	}
	let statement = format!(
		r#"UPDATE lieder l SET "{column}"=$3,"UpdatedAt"=$4,"UpdatedByAccountId"=COALESCE($5,l."CreatedByAccountId"),"RowVersion"=l."RowVersion"+1 WHERE l."Id"=$1 AND l."DeletedAt" IS NULL AND (l."{column}" IS NULL OR btrim(l."{column}",$6)='') AND EXISTS (SELECT 1 FROM dateien d WHERE d."Id"=$2 AND d."LiedId"=l."Id" AND d."DeletedAt" IS NULL AND d."Kind"='noten')"#
	);
	let whitespace = "\u{9}\u{a}\u{b}\u{c}\u{d}\u{20}\u{85}\u{a0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}";
	Ok(sqlx::query(sqlx::AssertSqlSafe(statement))
		.bind(lied)
		.bind(datei)
		.bind(value)
		.bind(clock.now())
		.bind(actor)
		.bind(whitespace)
		.execute(pool)
		.await?
		.rows_affected()
		== 1)
}
pub struct Prefill<'a> {
	pub pool: &'a sqlx::PgPool,
	pub datei: Uuid,
	pub lied: Uuid,
	pub actor: Option<Uuid>,
	pub model: &'a super::ScanReadingModel,
	pub clock: &'a dyn Clock,
}
impl Prefill<'_> {
	pub async fn run(
		self,
		text: &str,
		applied: &mut i32,
		cancel: &tokio_util::sync::CancellationToken,
	) -> Result<(), super::worker::Error> {
		let Self {
			pool,
			datei,
			lied,
			actor,
			model,
			clock,
		} = self;
		let Some(lied_row) = queries::lied(pool, lied).await? else {
			return Ok(());
		};
		let mut open: HashSet<&str> = AUTOMATED_ORDER
			.into_iter()
			.filter(|f| current(&lied_row, f).is_none_or(|s| s.trim().is_empty()))
			.collect();
		if open.is_empty() || text.trim().is_empty() {
			return Ok(());
		}
		let facts = score::analyze(Some(text)).facts;
		for (field, value) in [
			("composer", facts.composer),
			("lyricist", facts.lyricist),
			("arranger", facts.arranger),
			("musical_key", facts.musical_key),
		] {
			if cancel.is_cancelled() {
				return Err(super::worker::Error::Cancelled);
			}
			if open.contains(field)
				&& let Some(value) = value
			{
				if write(pool, datei, lied, field, &value, actor, clock).await? {
					*applied += 1;
				}
				open.remove(field);
			}
		}
		if open.is_empty() {
			return Ok(());
		}
		let wanted = AUTOMATED_ORDER
			.into_iter()
			.filter(|f| open.contains(f))
			.collect::<Vec<_>>();
		let reading = model.read_fields(text, &wanted, actor, cancel).await?;
		let mut seen = HashSet::new();
		for read in reading {
			if cancel.is_cancelled() {
				return Err(super::worker::Error::Cancelled);
			}
			let Some(field) = read.field.as_deref() else {
				continue;
			};
			if !open.contains(field) || !seen.insert(field.to_owned()) {
				continue;
			}
			let Some(value) = clean_value(field, read.value.as_deref()) else {
				continue;
			};
			if !read
				.certainty
				.as_deref()
				.is_some_and(|c| c.trim().eq_ignore_ascii_case("sicher"))
				|| !quote_is_verified(&read, &value, text)
			{
				continue;
			}
			if write(pool, datei, lied, field, &value, actor, clock).await? {
				*applied += 1;
			}
		}
		Ok(())
	}
}
