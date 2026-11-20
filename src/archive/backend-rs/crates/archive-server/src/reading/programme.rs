//! Deterministic programme suggestions. No catalogue writes.
use std::sync::OnceLock;
use unicode_normalization::UnicodeNormalization;
use uuid::Uuid;
#[derive(Clone, Debug)]
pub struct MatchableLied {
	pub id: Uuid,
	pub title: String,
}
#[derive(Debug)]
pub struct ProgrammMatch {
	pub lied: Option<MatchableLied>,
	pub sure: bool,
	pub candidates: Vec<MatchableLied>,
}
pub(crate) fn invariant_lower(text: &str) -> String {
	text.chars()
		.map(|c| {
			if c == '\u{130}' {
				c
			} else {
				c.to_lowercase().next().unwrap_or(c)
			}
		})
		.collect()
}
#[derive(Debug, thiserror::Error)]
#[error("Ungültiger Unicode-Text für die Programmzuordnung.")]
pub struct NormalizationError;
pub fn normalize(title: &str, bare: bool) -> Result<String, NormalizationError> {
	// Actual frozen .NET10 exhaustive scalar oracle: U+FFFE alone throws;
	// U+FFFF and supplementary noncharacters do not. Never silently repair it.
	if title.contains('\u{fffe}') {
		return Err(NormalizationError);
	}
	Ok(normalized(title, bare))
}
fn normalized(title: &str, bare: bool) -> String {
	static MARK: OnceLock<regex::Regex> = OnceLock::new();
	static LETTER: OnceLock<regex::Regex> = OnceLock::new();
	let mark = MARK.get_or_init(|| {
		regex::Regex::new(r"^\p{Mn}$").unwrap_or_else(|_| unreachable!("literal Unicode category"))
	});
	let letter = LETTER.get_or_init(|| {
		regex::Regex::new(r"^[\pL\p{Nd}]$")
			.unwrap_or_else(|_| unreachable!("literal Unicode categories"))
	});
	let mut folded = title.replace(['ß', 'ẞ'], "ss");
	if !bare {
		for (from, to) in [
			('ä', "ae"),
			('ö', "oe"),
			('ü', "ue"),
			('Ä', "Ae"),
			('Ö', "Oe"),
			('Ü', "Ue"),
		] {
			folded = folded.replace(from, to);
		}
	}
	let mut result = String::new();
	let mut gap = false;
	for c in folded.nfd() {
		let mut buffer = [0; 4];
		let s = c.encode_utf8(&mut buffer);
		if c <= '\u{ffff}' && mark.is_match(s) {
			continue;
		}
		// The oracle deliberately iterates UTF-16 chars, NOT Unicode runes:
		// supplementary letters/digits are two non-letter surrogate chars.
		if c <= '\u{ffff}' && letter.is_match(s) {
			if gap && !result.is_empty() {
				result.push(' ');
			}
			gap = false;
			result.push(c.to_lowercase().next().unwrap_or(c));
		} else {
			gap = true;
		}
	}
	result
}
pub fn match_title(
	title: &str,
	lieder: &[MatchableLied],
) -> Result<ProgrammMatch, NormalizationError> {
	let wanted = normalize(title, false)?;
	if !wanted.is_empty() {
		for lied in lieder {
			normalize(&lied.title, false)?;
		}
	}
	Ok(match_valid_title(title, lieder))
}
fn match_valid_title(title: &str, lieder: &[MatchableLied]) -> ProgrammMatch {
	let wanted = normalized(title, false);
	let bare = normalized(title, true);
	let mut exact = lieder
		.iter()
		.filter(|l| {
			!wanted.is_empty()
				&& (normalized(&l.title, false) == wanted || normalized(&l.title, true) == bare)
		})
		.cloned()
		.collect::<Vec<_>>();
	if exact.len() == 1 {
		return ProgrammMatch {
			lied: exact.pop(),
			sure: true,
			candidates: vec![],
		};
	}
	exact.sort_by(|a, b| {
		crate::catalogue::search::ordinal(&a.title, &b.title).then(a.id.cmp(&b.id))
	});
	if !exact.is_empty() {
		exact.truncate(5);
		return ProgrammMatch {
			lied: None,
			sure: false,
			candidates: exact,
		};
	}
	if wanted.is_empty() {
		return ProgrammMatch {
			lied: None,
			sure: false,
			candidates: vec![],
		};
	}
	let mut scored = lieder
		.iter()
		.map(|l| (l, similarity(&wanted, &normalized(&l.title, false))))
		.filter(|(_, score)| *score >= 0.6)
		.collect::<Vec<_>>();
	scored.sort_by(|(a, x), (b, y)| {
		y.total_cmp(x)
			.then_with(|| crate::catalogue::search::ordinal(&a.title, &b.title))
			.then(a.id.cmp(&b.id))
	});
	let picked = scored.first().and_then(|(best, score)| {
		let lead = if scored.len() == 1 {
			*score
		} else {
			*score - scored[1].1
		};
		(*score >= 0.85 && lead >= 0.1).then(|| (*best).clone())
	});
	ProgrammMatch {
		lied: picked,
		sure: false,
		candidates: scored.into_iter().take(5).map(|(l, _)| l.clone()).collect(),
	}
}
fn similarity(left: &str, right: &str) -> f64 {
	let left = left.encode_utf16().collect::<Vec<_>>();
	let right = right.encode_utf16().collect::<Vec<_>>();
	if left.is_empty() || right.is_empty() {
		return 0.0;
	}
	let mut previous = (0..=right.len()).collect::<Vec<_>>();
	let mut current = vec![0; right.len() + 1];
	for (i, a) in left.iter().enumerate() {
		current[0] = i + 1;
		for (j, b) in right.iter().enumerate() {
			current[j + 1] = (current[j] + 1)
				.min(previous[j + 1] + 1)
				.min(previous[j] + usize::from(a != b));
		}
		std::mem::swap(&mut previous, &mut current);
	}
	1.0 - previous[right.len()] as f64 / left.len().max(right.len()) as f64
}

use super::{PdfPageRenderer, ReadField, ScanReadingModel};
use crate::{
	ai,
	extraction::{
		self,
		pdf::{self, Spool},
	},
};
use tokio_util::sync::CancellationToken;
#[derive(Clone, Debug, serde::Serialize)]
pub struct ProgrammFeld {
	#[serde(rename = "feld")]
	pub field: String,
	#[serde(rename = "wert")]
	pub value: String,
	#[serde(rename = "sicher")]
	pub sure: bool,
}
#[derive(Debug)]
pub struct ProgrammEintrag {
	pub text: String,
	pub sure: bool,
	pub matched: ProgrammMatch,
}
#[derive(Debug)]
pub struct ProgrammLesung {
	pub fields: Vec<ProgrammFeld>,
	pub programme: Vec<ProgrammEintrag>,
}
#[derive(Debug, thiserror::Error)]
pub enum ProgrammError {
	#[error("Programmtext nicht normalisierbar.")]
	Normalization(#[from] NormalizationError),
	#[error("Programmdatei nicht lesbar.")]
	Material(&'static str),
	#[error("PDF-Verarbeitung gescheitert.")]
	Pdf(#[from] pdf::Error),
	#[error("KI-Lesung gescheitert.")]
	Ai(#[from] ai::Error),
	#[error("Lieder konnten nicht geladen werden.")]
	Database(#[from] sqlx::Error),
}
pub fn is_pdf(bytes: &[u8]) -> bool {
	bytes.starts_with(b"%PDF-")
}
pub fn is_photo(bytes: &[u8]) -> bool {
	bytes.starts_with(&[0xff, 0xd8, 0xff])
		|| bytes.starts_with(b"\x89PNG\r\n\x1a\n")
		|| (bytes.starts_with(b"RIFF") && bytes.get(8..12) == Some(b"WEBP"))
}
pub struct ProgrammReader {
	pub model: ScanReadingModel,
	pub extraction: extraction::Options,
	pub pool: sqlx::PgPool,
}
impl ProgrammReader {
	pub async fn read(
		&self,
		bytes: &[u8],
		actor: Option<Uuid>,
		cancel: &CancellationToken,
	) -> Result<ProgrammLesung, ProgrammError> {
		if bytes.len() as u64 > self.model.options.max_pdf_bytes {
			return Err(ProgrammError::Material(super::worker::TOO_LARGE));
		}
		let text = if is_pdf(bytes) {
			self.pdf_text(bytes, actor, cancel).await?
		} else {
			if !is_photo(bytes) {
				return Err(ProgrammError::Material(super::worker::UNREADABLE));
			}
			self.model.programme_batch_size(1, 1)?;
			let owned = bytes.to_vec();
			let long = self.model.options.max_image_long_side;
			let quality = self.model.options.jpeg_quality;
			let token = cancel.clone();
			let page = tokio::task::spawn_blocking(move || {
				super::photo::downscale(&owned, long, quality, &token)
			})
			.await
			.map_err(|_| ProgrammError::Material(super::worker::UNREADABLE))??;
			self.model
				.transcribe_programme(&[page], actor, cancel)
				.await?
		};
		if text.trim().is_empty() {
			return Ok(ProgrammLesung {
				fields: vec![],
				programme: vec![],
			});
		}
		let reading = self.model.read_programme(&text, actor, cancel).await?;
		let normalized = normalize(&text, false)?;
		let fields = header(reading.fields.unwrap_or_default(), &normalized)?;
		// Frozen source intentionally projects ALL saved Lieder, including trash.
		// Do not replace this with member search/Visible semantics.
		let lieder=tokio::select! {biased; _=cancel.cancelled()=>return Err(ai::Error::Cancelled.into()), rows=sqlx::query_as::<_,(Uuid,String)>(r#"SELECT "Id","Title" FROM lieder"#).fetch_all(&self.pool)=>rows?}.into_iter().map(|(id,title)|MatchableLied{id,title}).collect::<Vec<_>>();
		let numbering = regex::Regex::new(r"^\s*[\p{Nd}&&\x{0000}-\x{FFFF}]{1,3}\s*[.)]\s+")
			.map_err(|_| ai::Error::Configuration)?;
		let mut programme = vec![];
		for entry in reading.entries.unwrap_or_default() {
			if programme.len() >= 60 {
				break;
			}
			let Some(title) = single(entry.title.as_deref(), 200) else {
				continue;
			};
			let title = numbering.replace(title, "").trim().to_owned();
			if title.is_empty() || !quote_stands(entry.quote.as_deref(), Some(&title), &normalized)?
			{
				continue;
			}
			programme.push(ProgrammEintrag {
				matched: match_title(&title, &lieder)?,
				text: title,
				sure: is_sure(entry.certainty.as_deref()),
			});
		}
		Ok(ProgrammLesung { fields, programme })
	}
	async fn pdf_text(
		&self,
		bytes: &[u8],
		actor: Option<Uuid>,
		cancel: &CancellationToken,
	) -> Result<String, ProgrammError> {
		let spool =
			Spool::from_bytes(bytes, &std::env::temp_dir(), || cancel.is_cancelled()).await?;
		let renderer = PdfPageRenderer::from_env()?;
		let engine = renderer.0.clone();
		let source = spool.clone();
		let token = cancel.clone();
		let extraction = self.extraction.clone();
		let outcome = tokio::task::spawn_blocking(move || {
			engine.extract(&source.0, &extraction, || token.is_cancelled())
		})
		.await
		.map_err(|_| pdf::Error::Io)??;
		match outcome {
			pdf::Outcome::Failure(reason) => Err(ProgrammError::Material(reason)),
			pdf::Outcome::Text(text) => Ok(text),
			pdf::Outcome::NoText => {
				let count = 4
					.min(self.model.options.images_per_call)
					.min(renderer.page_count(spool.clone(), cancel.clone()).await?);
				if count <= 0 {
					return Ok(String::new());
				}
				let count = self.model.programme_batch_size(1, count)?;
				let pages = renderer
					.render(
						spool,
						1,
						count,
						self.model.options.max_image_long_side,
						self.model.options.jpeg_quality,
						cancel.clone(),
					)
					.await
					.map_err(|error| match error {
						pdf::Error::Io => ProgrammError::Material(super::worker::UNREADABLE),
						_ => error.into(),
					})?;
				if pages.is_empty() {
					return Ok(String::new());
				}
				Ok(self
					.model
					.transcribe_programme(&pages, actor, cancel)
					.await?)
			}
		}
	}
}
fn single(raw: Option<&str>, max: usize) -> Option<&str> {
	raw.map(str::trim)
		.filter(|v| !v.is_empty() && v.encode_utf16().count() <= max && !v.contains('\n'))
}
fn is_sure(raw: Option<&str>) -> bool {
	raw.is_some_and(|s| s.trim().eq_ignore_ascii_case("sicher"))
}
fn quote_stands(
	quote: Option<&str>,
	value: Option<&str>,
	text: &str,
) -> Result<bool, NormalizationError> {
	let quote = normalize(quote.unwrap_or_default(), false)?;
	if quote.encode_utf16().count() < 3 || !text.contains(&quote) {
		return Ok(false);
	}
	Ok(match value {
		Some(v) => quote.contains(&normalize(v, false)?),
		None => true,
	})
}
fn header(read: Vec<ReadField>, text: &str) -> Result<Vec<ProgrammFeld>, NormalizationError> {
	let mut names = std::collections::HashMap::new();
	for field in &read {
		if let Some(name) = field.field.as_deref()
			&& field.value.as_deref().is_some_and(|s| !s.trim().is_empty())
		{
			names.entry(name).or_insert(field);
		}
	}
	let mut found = vec![];
	let mut add = |name: &str, value: String, sure: bool| {
		found.push(ProgrammFeld {
			field: name.into(),
			value,
			sure,
		})
	};
	if let Some(field) = names.get("title")
		&& let Some(value) = single(field.value.as_deref(), 200)
		&& quote_stands(field.quote.as_deref(), Some(value), text)?
	{
		add("title", value.into(), is_sure(field.certainty.as_deref()));
	}
	if let Some(field) = names.get("kind") {
		let value = invariant_lower(field.value.as_deref().unwrap_or_default().trim());
		if [
			"concert", "service", "wedding", "funeral", "festival", "other",
		]
		.contains(&value.as_str())
			&& quote_stands(field.quote.as_deref(), None, text)?
		{
			add("kind", value, is_sure(field.certainty.as_deref()));
		}
	}
	if let Some(field) = names.get("year")
		&& let Some(year) = field
			.value
			.as_deref()
			.and_then(|s| super::http::int32(s.trim()))
		&& (1800..=2100).contains(&year)
		&& quote_stands(field.quote.as_deref(), None, text)?
		&& normalize(field.quote.as_deref().unwrap_or_default(), false)?.contains(&year.to_string())
	{
		add(
			"jahr",
			year.to_string(),
			is_sure(field.certainty.as_deref()),
		);
		if let Some(field) = names.get("month")
			&& let Some(month) = field
				.value
				.as_deref()
				.and_then(|s| super::http::int32(s.trim()))
			&& (1..=12).contains(&month)
			&& quote_stands(field.quote.as_deref(), None, text)?
			&& month_stands(field.quote.as_deref().unwrap_or_default(), month)?
		{
			add(
				"monat",
				month.to_string(),
				is_sure(field.certainty.as_deref()),
			);
			if let Some(field) = names.get("day")
				&& let Some(day) = field
					.value
					.as_deref()
					.and_then(|s| super::http::int32(s.trim()))
				&& chrono::NaiveDate::from_ymd_opt(year, month as u32, day as u32).is_some()
				&& quote_stands(field.quote.as_deref(), None, text)?
				&& number_stands(field.quote.as_deref().unwrap_or_default(), day)
			{
				add("tag", day.to_string(), is_sure(field.certainty.as_deref()));
			}
		}
		if let Some(field) = names.get("approximate")
			&& field
				.value
				.as_deref()
				.is_some_and(|s| s.trim().eq_ignore_ascii_case("ja"))
			&& quote_stands(field.quote.as_deref(), None, text)?
		{
			add("unsicher", "true".into(), true);
		}
	}
	if let Some(field) = names.get("venue")
		&& let Some(value) = single(field.value.as_deref(), 200)
		&& quote_stands(field.quote.as_deref(), Some(value), text)?
	{
		add("venue", value.into(), is_sure(field.certainty.as_deref()));
	}
	Ok(found)
}
fn month_stands(quote: &str, month: i32) -> Result<bool, NormalizationError> {
	const MONTHS: [&str; 12] = [
		"januar",
		"februar",
		"marz",
		"april",
		"mai",
		"juni",
		"juli",
		"august",
		"september",
		"oktober",
		"november",
		"dezember",
	];
	Ok(
		normalize(quote, true)?.contains(MONTHS[(month - 1) as usize])
			|| regex::Regex::new(&format!(
				r"(?:^|[^\p{{Nd}}&&\x{{0000}}-\x{{FFFF}}])0?{month}\s*\."
			))
			.is_ok_and(|r| r.is_match(quote)),
	)
}
fn number_stands(quote: &str, number: i32) -> bool {
	regex::Regex::new(&format!(
		r"(?:^|[^\p{{Nd}}&&\x{{0000}}-\x{{FFFF}}]){number}(?:$|[^\p{{Nd}}&&\x{{0000}}-\x{{FFFF}}])"
	))
	.is_ok_and(|r| r.is_match(quote))
}
