use crate::{
	ai,
	catalogue::{Lied, queries, search},
};
use serde_json::{Value, json};

pub(super) fn definitions() -> Result<Vec<ai::Tool>, ai::Error> {
	#[derive(serde::Deserialize)]
	struct Definition {
		name: String,
		description: String,
		schema: Box<serde_json::value::RawValue>,
	}
	let definitions: Vec<Definition> =
		serde_json::from_str(include_str!("tools.json")).map_err(|_| ai::Error::Configuration)?;
	definitions
		.into_iter()
		.map(|d| {
			Ok(ai::Tool {
				name: d.name,
				description: d.description,
				schema: serde_json::from_str(&crate::reading::programme_script::default_json(
					d.schema.get().into(),
				))
				.map_err(|_| ai::Error::Configuration)?,
			})
		})
		.collect()
}
pub(super) async fn invoke(
	pool: &sqlx::PgPool,
	name: &str,
	args: &Value,
) -> Result<String, crate::auth::AuthError> {
	let value = match name {
		"catalogue_search" => {
			let Some(query) = args.get("query").filter(|v| v.is_string() || v.is_null()) else {
				return Ok("{}".into());
			};
			let page = match args.get("page") {
				None => 1,
				Some(v) => {
					let Some(page) = v.as_i64().and_then(|v| i32::try_from(v).ok()) else {
						return Ok("{}".into());
					};
					page
				}
			};
			search_payload(pool, query.as_str().unwrap_or_default(), page).await?
		}
		"lied_details" => details(pool, args["liedId"].as_str().unwrap_or_default()).await?,
		"zeige_oberflaeche" => json!({}),
		_ => return Ok(format!("Error: Requested function \"{name}\" not found.")),
	};
	// Frozen tools return compact JsonSerializer JSON as a STRING. Meaningful
	// nulls here are not SSE fields and must survive into provider grounding.
	Ok(crate::reading::programme_script::default_json(
		value.to_string(),
	))
}
pub(super) fn prefix(text: &str, bound: usize) -> String {
	let mut units = 0;
	text.chars()
		.take_while(|c| {
			units += c.len_utf16();
			units <= bound
		})
		.collect()
}
// .NET slices UTF16 code units; its default JSON serializer replaces a sliced
// high surrogate with U+FFFD. This is a WIRE rule, not PostgreSQL encoding.
pub(super) fn wire_prefix(text: &str, bound: usize) -> String {
	String::from_utf16_lossy(&text.encode_utf16().take(bound).collect::<Vec<_>>())
}
// Independently observed frozen ChatThreadHistoryProvider/Npgsql10.0.3 rejects
// an unpaired surrogate before its turn batch commits. Never silently store a
// shorter scalar-safe answer or apply JSON's replacement policy to SQL.
pub(super) fn persisted_prefix(text: &str, bound: usize) -> Option<String> {
	String::from_utf16(&text.encode_utf16().take(bound).collect::<Vec<_>>()).ok()
}
pub(super) fn lied_payload(lied: &Lied, labels: Vec<&str>) -> Value {
	json!({"id":lied.id,"title":lied.title,"composer":lied.composer,"arranger":lied.arranger,"lyricist":lied.lyricist,"lyricsExcerpt":lied.lyrics.as_deref().filter(|l|!l.is_empty()).map(|l|wire_prefix(l,300)),"trefferIn":labels})
}
async fn search_payload(pool: &sqlx::PgPool, query: &str, page: i32) -> Result<Value, sqlx::Error> {
	let boundary = query
		.trim()
		.encode_utf16()
		.take(200)
		.last()
		.filter(|u| (0xd800..=0xdbff).contains(u));
	let query = prefix(query.trim(), 200);
	let page = page.max(1);
	if page > 5 {
		return Ok(
			json!({"lieder":[],"error":"Höchstens 5 Seiten abrufbar. Bitte die Suche eingrenzen.","limitReached":true}),
		);
	}
	let tokens = search::tokens_of(&query);
	let rows = queries::lieder(pool).await?;
	let noten = search::load_noten_texts(pool, &tokens).await?;
	let mut matches = search::matching(&tokens, &rows, &noten, &query);
	// JSON replacement is wrong for internal matching: frozen ordinal Contains
	// retains the sliced high surrogate, and must not also return scalar-prefix
	// decoys. Use shared search for candidates/order, then constrain only this
	// otherwise unrepresentable final token as UTF16, retaining shared folds.
	let mut earlier = tokens.clone();
	let split = boundary.map(|high| {
		let last = search::fold(Some(query.rsplit(' ').next().unwrap_or_default().trim()));
		if !last.is_empty() {
			earlier.pop();
		}
		let mut units = last.encode_utf16().collect::<Vec<_>>();
		units.push(high);
		units
	});
	if let Some(split) = &split {
		matches.retain(|m| m.fields.iter().any(|f| contains_units(&f.folded, split)));
		for m in &mut matches {
			let title = search::fold(Some(&m.lied.title));
			m.rank = if earlier.iter().all(|t| title.contains(t)) && contains_units(&title, split) {
				1
			} else {
				2
			};
		}
		matches.sort_by(|a, b| {
			a.rank
				.cmp(&b.rank)
				.then_with(|| {
					search::ordinal(
						&search::fold(Some(&a.lied.title)),
						&search::fold(Some(&b.lied.title)),
					)
				})
				.then(a.lied.id.cmp(&b.lied.id))
		});
	}
	let lieder: Vec<_> = search::page(&matches, page, 10)
		.iter()
		.map(|m| {
			lied_payload(
				m.lied,
				if let Some(split) = &split {
					m.fields
						.iter()
						.filter(|f| {
							earlier.iter().any(|t| f.folded.contains(t))
								|| contains_units(&f.folded, split)
						})
						.map(|f| f.label)
						.collect()
				} else {
					search::labels(&tokens, &m.fields)
				},
			)
		})
		.collect();
	let more = (page as usize) * 10 < matches.len();
	Ok(
		json!({"lieder":lieder,"page":page,"pageSize":10,"totalCount":matches.len(),"hasMore":more,"nextPage":if page<5&&more{Some(page+1)}else{None},"limitReached":page==5&&more}),
	)
}
fn contains_units(text: &str, token: &[u16]) -> bool {
	text.encode_utf16()
		.collect::<Vec<_>>()
		.windows(token.len())
		.any(|w| w == token)
}
async fn details(pool: &sqlx::PgPool, raw: &str) -> Result<Value, crate::auth::AuthError> {
	let Some(id) = crate::clr_guid::try_parse(raw) else {
		return Ok(json!({}));
	};
	let Some(lied) = queries::lied(pool, id).await? else {
		return Ok(json!({}));
	};
	let mut files = queries::dateien(pool, Some(id), None).await?;
	files.sort_by(|a, b| a.kind.cmp(&b.kind).then(a.id.cmp(&b.id)));
	let ids: Vec<_> = files.iter().map(|d| d.id).collect();
	let texts: Vec<(uuid::Uuid, i32, Option<String>)> = sqlx::query_as(
		r#"SELECT "DateiId","Status","Text" FROM extraction_jobs WHERE "DateiId"=ANY($1)"#,
	)
	.bind(ids)
	.fetch_all(pool)
	.await?;
	let mut budget = 6000;
	let mut sent = std::collections::HashSet::new();
	let mut dateien = Vec::new();
	for file in files {
		let analysis = if file.kind == "noten" {
			texts
				.iter()
				.find(|(id, status, _)| *id == file.id && *status == 2)
				.map(|(_, _, text)| crate::extraction::score::analyze(text.as_deref()))
		} else {
			None
		};
		let text = analysis
			.as_ref()
			.filter(|a| !a.clean_text.is_empty() && budget > 0 && sent.insert(a.clean_text.clone()))
			.map(|a| {
				let text = wire_prefix(&a.clean_text, 2000.min(budget));
				budget -= text.encode_utf16().count();
				text
			});
		dateien.push(json!({"art":file.kind,"partitur":file.is_partitur,"stimme":file.stimme,"beschreibung":file.description,"notenFakten":analysis.as_ref().filter(|a|!a.facts.is_empty()).map(|a|&a.facts),"notenText":text}));
	}
	let (sung, planned) = crate::events::history::chat_history(pool, id).await?;
	Ok(
		json!({"lieder":[lied_payload(&lied,vec![])],"besetzung":lied.voice_configuration,"tonart":lied.musical_key,"sprache":lied.language,"anlass":lied.occasion,"dateien":dateien,"gesungen":sung,"geplant":planned}),
	)
}
