//! ScanReadingEvaluation / ScanReadingEvaluationLive. Runtime skip gate is
//! BEFORE fixture access, credentials or provider construction. Configuration
//! is not permission for an agent to execute the configured paid branch.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)] // Evaluation assertions.
mod support;
use archive_server::{ai, auth::Clock, reading};
use serde::Serialize;
use std::{collections::BTreeMap, sync::Arc};
#[derive(Serialize)]
struct FieldResult {
	field: String,
	outcome: String,
	value: Option<String>,
}
#[derive(Serialize)]
struct DocumentResult {
	name: String,
	passed: bool,
	cost_micro_eur: i64,
	model_calls: i64,
	fields: Vec<FieldResult>,
}
async fn evaluate(
	name: &str,
	pdf: Vec<u8>,
	expected: &BTreeMap<String, String>,
	options: ai::Options,
	live: Option<ai::AzureConfig>,
) -> DocumentResult {
	let app = support::App::new_scoped("scan_eval229").await;
	let (_, rx) = tokio::sync::watch::channel(options);
	let ledger = ai::Ledger::new(
		Arc::new(ai::PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let gateway = if let Some(config) = live {
		ai::Gateway::azure(ledger, config).unwrap()
	} else {
		ai::Gateway::scripted(ledger, reading::scripted_scan_reader())
	};
	let worker = reading::Worker::new(
		app.pool.clone(),
		Arc::new(support::reading::Bytes(pdf)),
		app.time.clone(),
		reading::ScanReadingModel::new(gateway.clone(), reading::Options::default()),
	);
	let owner = support::catalogue::lied(&app, "Waldfahrt", serde_json::json!({})).await;
	let id = support::catalogue::datei(&app, owner, false, "noten", "application/pdf").await;
	support::catalogue::text(&app, id, "", 3, 0).await;
	reading::insert(&app.pool, id, uuid::Uuid::nil(), 20, app.time.now())
		.await
		.unwrap();
	worker
		.handle(
			&archive_server::queue::Message {
				id: "m".into(),
				receipt: "p".into(),
				body: archive_server::queue::envelope("reading", id),
				dequeue_count: 1,
			},
			app.time.now() + chrono::Duration::minutes(8),
			&Default::default(),
		)
		.await
		.unwrap();
	assert!(gateway.drain().await);
	let song = archive_server::catalogue::queries::lied(&app.pool, owner)
		.await
		.unwrap()
		.unwrap();
	let mut fields = vec![];
	let normalize = |v: &str| {
		v.split_whitespace()
			.collect::<Vec<_>>()
			.join(" ")
			.to_lowercase()
	};
	for (field, value) in [
		("composer", song.composer),
		("lyricist", song.lyricist),
		("arranger", song.arranger),
		("musical_key", song.musical_key),
		("lyrics", song.lyrics),
	] {
		let wanted = expected.get(field);
		let outcome = match (&value, wanted) {
			(Some(_), None) => "applied-unchecked",
			(Some(v), Some(e)) => {
				let left = normalize(v);
				let right = normalize(e);
				if (field == "lyrics" && left.starts_with(&right))
					|| (field != "lyrics" && left == right)
				{
					"applied-correct"
				} else {
					"applied-wrong"
				}
			}
			(None, Some(_)) => "missing",
			_ => "empty",
		};
		if wanted.is_some() || value.is_some() {
			fields.push(FieldResult {
				field: field.into(),
				outcome: outcome.into(),
				value,
			});
		}
	}
	let (cost_micro_eur,model_calls):(i64,i64)=sqlx::query_as(r#"SELECT COALESCE(sum("CostMicroEur"),0)::bigint,COALESCE(sum("Calls"),0)::bigint FROM ai_usage_entries"#).fetch_one(&app.pool).await.unwrap();
	let passed = fields.iter().all(|f| {
		f.outcome != "applied-wrong"
			&& (!expected.contains_key(&f.field) || f.outcome == "applied-correct")
	});
	app.close().await;
	DocumentResult {
		name: name.into(),
		passed,
		cost_micro_eur,
		model_calls,
		fields,
	}
}
fn expected() -> BTreeMap<String, String> {
	[
		("composer", "Franz Schubert"),
		("lyricist", "Anna Beispiel"),
		("musical_key", "G-Dur"),
		("lyrics", "Im Wald da singen die Vögel"),
	]
	.into_iter()
	.map(|(k, v)| (k.into(), v.into()))
	.collect()
}
fn report(results: &[DocumentResult], label: &str) {
	let passed = results.iter().filter(|r| r.passed).count();
	let cost = results.iter().map(|r| r.cost_micro_eur).sum::<i64>();
	eprintln!(
		"SCAN-EVAL {label}: {passed}/{} documents, {cost} micro-EUR",
		results.len()
	);
	for r in results {
		eprintln!(
			"SCAN-EVAL {}: passed={}, {} calls, {} micro-EUR; {}",
			r.name,
			r.passed,
			r.model_calls,
			r.cost_micro_eur,
			r.fields
				.iter()
				.map(|f| format!("{}={}", f.field, f.outcome))
				.collect::<Vec<_>>()
				.join(", ")
		);
	}
}
#[tokio::test]
#[ignore = "ScanReadingEvaluation: explicit real PostgreSQL/native PDFium; no live provider"]
async fn scripted_set_reports_correctness_taxonomy_and_positive_real_ledger_cost() {
	let mut results = vec![];
	for (name, pages) in [("klar-1", 1), ("klar-2", 3), ("lang-1", 5)] {
		results.push(
			evaluate(
				name,
				support::reading::scan(pages),
				&expected(),
				ai::Options::shipped(true),
				None,
			)
			.await,
		);
	}
	report(&results, "scripted");
	assert!(
		results
			.iter()
			.all(|r| r.passed && r.cost_micro_eur > 0 && r.model_calls >= 2)
	);
	assert!(
		results
			.iter()
			.flat_map(|r| &r.fields)
			.all(|f| f.outcome != "applied-wrong")
	);
	let mut wrong = expected();
	wrong.insert("composer".into(), "Ludwig van Beethoven".into());
	wrong.insert("arranger".into(), "Fehlender Bearbeiter".into());
	let result = evaluate(
		"negative-control",
		support::reading::scan(1),
		&wrong,
		ai::Options::shipped(true),
		None,
	)
	.await;
	report(std::slice::from_ref(&result), "negative-control");
	assert!(!result.passed);
	assert_eq!(
		result
			.fields
			.iter()
			.find(|f| f.field == "composer")
			.unwrap()
			.outcome,
		"applied-wrong"
	);
	assert_eq!(
		result
			.fields
			.iter()
			.find(|f| f.field == "arranger")
			.unwrap()
			.outcome,
		"missing"
	);
	let result = evaluate(
		"unchecked-control",
		support::reading::scan(1),
		&BTreeMap::new(),
		ai::Options::shipped(true),
		None,
	)
	.await;
	assert!(result.passed);
	assert!(
		result
			.fields
			.iter()
			.all(|f| f.outcome == "applied-unchecked")
	);
}
#[tokio::test]
async fn scan_reading_evaluation_live() {
	if !ai::LiveEvaluation::Scan.configured_from_env() {
		eprintln!(
			"SCAN-EVAL live run skipped: set ARCHIVE_SCAN_EVAL_DIR, ARCHIVE_CHAT_ENDPOINT and ARCHIVE_CHAT_DEPLOYMENT_NAME (az login)."
		);
		return;
	}
	let deployment = std::env::var("ARCHIVE_CHAT_DEPLOYMENT_NAME")
		.unwrap()
		.trim()
		.to_owned();
	let endpoint = std::env::var("ARCHIVE_CHAT_ENDPOINT").unwrap();
	let mut options = ai::Options::shipped(false);
	if let (Ok(input), Ok(output)) = (
		std::env::var("ARCHIVE_CHAT_INPUT_PRICE_EUR"),
		std::env::var("ARCHIVE_CHAT_OUTPUT_PRICE_EUR"),
	) {
		// Explicit decimal configuration, never borrow the scripted price for a
		// foreign deployment. A malformed/unusable configured price fails closed.
		options.models.insert(
			deployment.clone(),
			ai::Price::new(input.trim(), output.trim()).expect("unusable evaluation prices"),
		);
	}
	let price = options
		.models
		.iter()
		.find(|(key, _)| key.eq_ignore_ascii_case(&deployment))
		.map(|(_, p)| p)
		.expect("live deployment must be priced before fixture access");
	assert!(
		price.input > rust_decimal::Decimal::ZERO && price.output > rust_decimal::Decimal::ZERO
	);
	let directory = std::path::PathBuf::from(std::env::var("ARCHIVE_SCAN_EVAL_DIR").unwrap());
	let manifest: serde_json::Value = serde_json::from_slice(
		&std::fs::read(directory.join("expected.json")).expect("configured manifest unavailable"),
	)
	.expect("configured manifest malformed");
	let entries = manifest["documents"]
		.as_array()
		.expect("configured documents malformed");
	assert!(
		!entries.is_empty(),
		"configured corpus must not be silently skipped"
	);
	let mut results = vec![];
	for entry in entries {
		let file = entry["file"].as_str().expect("file required");
		let expected =
			serde_json::from_value(entry["expected"].clone()).expect("expected fields required");
		let pdf = std::fs::read(directory.join(file)).expect("configured PDF unavailable");
		results.push(
			evaluate(
				file,
				pdf,
				&expected,
				options.clone(),
				Some(ai::AzureConfig {
					endpoint: endpoint.clone(),
					deployment: deployment.clone(),
				}),
			)
			.await,
		);
	}
	report(&results, "live");
	if let Ok(path) = std::env::var("ARCHIVE_SCAN_EVAL_REPORT")
		&& !path.trim().is_empty()
	{
		std::fs::write(
			path,
			serde_json::to_vec_pretty(
				&serde_json::json!({"label":format!("live {deployment}"),"results":results}),
			)
			.unwrap(),
		)
		.unwrap();
	}
	assert!(
		results
			.iter()
			.flat_map(|r| &r.fields)
			.all(|f| f.outcome != "applied-wrong")
	);
}
