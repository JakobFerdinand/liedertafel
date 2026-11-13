//! ProgrammReadingEvaluation / ProgrammReadingEvaluationLive. Generated images
//! and real HTTP/reader/matcher/gateway/SQL; live config is NOT spending consent.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Frozen evaluation assertions.
mod support;
use archive_server::{ai, reading};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{collections::BTreeMap, sync::Arc};
#[derive(Clone, Deserialize)]
struct ExpectedEntry {
	text: String,
	lied: Option<String>,
}
#[derive(Clone, Deserialize)]
struct Expected {
	title: Option<String>,
	year: Option<i32>,
	month: Option<i32>,
	day: Option<i32>,
	venue: Option<String>,
	#[serde(default)]
	approximate: bool,
	programm: Vec<ExpectedEntry>,
}
#[derive(Deserialize)]
struct Document {
	name: String,
	transcription: Option<String>,
	file: Option<String>,
	expected: Expected,
	#[serde(rename = "extraLieder", default)]
	extra: Vec<String>,
}
#[derive(Deserialize)]
struct Corpus {
	lieder: Vec<String>,
	documents: Vec<Document>,
}
#[derive(Debug, Serialize)]
struct EntryResult {
	text: String,
	outcome: String,
}
#[derive(Debug, Serialize)]
struct ResultRow {
	name: String,
	passed: bool,
	cost_micro_eur: i64,
	model_calls: i64,
	wrong_header: Vec<String>,
	entries: Vec<EntryResult>,
}
fn judge(body: &Value, expected: &Expected) -> (Vec<String>, Vec<EntryResult>) {
	let fields = body["felder"]
		.as_array()
		.unwrap()
		.iter()
		.map(|f| (f["feld"].as_str().unwrap(), f["wert"].as_str().unwrap()))
		.collect::<BTreeMap<_, _>>();
	let mut wrong = vec![];
	for (field, want) in [
		("title", expected.title.clone()),
		("jahr", expected.year.map(|n| n.to_string())),
		("monat", expected.month.map(|n| n.to_string())),
		("tag", expected.day.map(|n| n.to_string())),
		("venue", expected.venue.clone()),
	] {
		if fields.get(field).copied() != want.as_deref() {
			wrong.push(field.into());
		}
	}
	if expected.approximate != fields.contains_key("unsicher") {
		wrong.push("unsicher".into());
	}
	let read = body["programm"].as_array().unwrap();
	let mut entries = vec![];
	for index in 0..expected.programm.len().max(read.len()) {
		let want = expected.programm.get(index);
		let found = read.get(index);
		let outcome = match (found, want) {
			(None, Some(_)) => "read-missing",
			(Some(_), None) => "extra",
			(Some(found), Some(want)) => {
				let picked = found["lied"]["titel"].as_str();
				match (picked, want.lied.as_deref()) {
					(Some(p), Some(w)) if p == w => {
						if found["lied"]["sicher"] == true {
							"matched-correct"
						} else {
							"matched-unsure-correct"
						}
					}
					(Some(_), _) => "matched-wrong",
					(None, None) => "unmatched-correct",
					(None, Some(w)) => {
						if found["kandidaten"]
							.as_array()
							.unwrap()
							.iter()
							.any(|c| c["titel"] == w)
						{
							"candidates-correct"
						} else {
							"unmatched-missed"
						}
					}
				}
			}
			_ => unreachable!("max length"),
		};
		entries.push(EntryResult {
			text: found
				.and_then(|v| v["text"].as_str())
				.map(str::to_owned)
				.unwrap_or_else(|| want.unwrap().text.clone()),
			outcome: outcome.into(),
		});
	}
	(wrong, entries)
}
async fn evaluate(
	document: &Document,
	catalogue: &[String],
	file: Vec<u8>,
	options: ai::Options,
	live: Option<ai::AzureConfig>,
) -> ResultRow {
	let mut app = support::App::new_scoped("programm_eval231").await;
	for title in catalogue.iter().chain(&document.extra) {
		support::catalogue::lied(&app, title, json!({})).await;
	}
	let gateway = if let Some(config) = live {
		let (_, rx) = tokio::sync::watch::channel(options);
		let ledger = ai::Ledger::new(
			Arc::new(ai::PostgresStore::new(app.pool.clone())),
			rx,
			app.time.clone(),
		);
		let gateway = ai::Gateway::azure(ledger.clone(), config).unwrap();
		let mut origin =
			archive_server::Origin::new(archive_server::Settings::new(true, Default::default(), 0));
		origin.reading_enabled = true;
		origin.reading = support::programme::enabled();
		origin.ai = Some(ai::Services {
			gateway: gateway.clone(),
			ledger,
		});
		app.router = archive_server::build_router(Arc::new(
			origin.with_auth(
				archive_server::auth::Auth::new(
					app.pool.clone(),
					archive_server::auth::AuthConfig::development(),
					app.mail.clone(),
					app.time.clone(),
				)
				.unwrap(),
			),
		));
		gateway
	} else {
		support::programme::configure(
			&mut app,
			reading::scripted_scan_reader_with_programme_transcription(
				document.transcription.clone(),
			),
			support::programme::enabled(),
			options,
		)
	};
	let client = app.admin().await;
	let (status, body) = support::programme::post(&client, &file).await;
	assert_eq!(status, 200, "{body}");
	let (wrong_header, entries) = judge(&body, &document.expected);
	assert!(gateway.drain().await);
	let (cost_micro_eur,model_calls,reserved):(i64,i64,i64)=sqlx::query_as(r#"SELECT COALESCE(sum("CostMicroEur"),0)::bigint,COALESCE(sum("Calls"),0)::bigint,COALESCE(sum("ReservedMicroEur"),0)::bigint FROM ai_usage_entries"#).fetch_one(&app.pool).await.unwrap();
	assert_eq!(reserved, 0);
	let passed = wrong_header.is_empty()
		&& entries
			.iter()
			.all(|e| !["matched-wrong", "extra", "read-missing"].contains(&e.outcome.as_str()));
	app.close().await;
	ResultRow {
		name: document.name.clone(),
		passed,
		cost_micro_eur,
		model_calls,
		wrong_header,
		entries,
	}
}
fn report(rows: &[ResultRow], label: &str) {
	eprintln!(
		"PROGRAMM-EVAL {label}: {}/{} documents; {} micro-EUR",
		rows.iter().filter(|r| r.passed).count(),
		rows.len(),
		rows.iter().map(|r| r.cost_micro_eur).sum::<i64>()
	);
	for row in rows {
		eprintln!("PROGRAMM-EVAL {row:?}");
	}
	if let Ok(path) = std::env::var("ARCHIVE_PROGRAMM_EVAL_REPORT")
		&& !path.trim().is_empty()
	{
		std::fs::write(
			path,
			serde_json::to_vec_pretty(&json!({"label":label,"results":rows})).unwrap(),
		)
		.unwrap();
	}
}
#[tokio::test]
#[ignore = "ProgrammReadingEvaluation: real PostgreSQL, generated photos, no live provider"]
async fn seven_scripted_documents_report_headers_all_entry_outcomes_and_settled_cost() {
	let corpus: Corpus = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-evaluation.json"
	))
	.unwrap();
	let mut rows = vec![];
	for document in &corpus.documents {
		rows.push(
			evaluate(
				document,
				&corpus.lieder,
				support::programme::photo(),
				ai::Options::shipped(true),
				None,
			)
			.await,
		);
	}
	report(&rows, "scripted");
	assert!(rows.iter().all(|r| r.passed));
	let oracle: Vec<Value> = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-oracle.json"
	))
	.unwrap();
	for row in &rows {
		let source = oracle
			.iter()
			.find(|r| r["kind"] == "scripted-cost" && r["name"] == row.name)
			.unwrap();
		assert_eq!(
			row.cost_micro_eur,
			source["cost"].as_i64().unwrap(),
			"{}: actual frozen scripted cost",
			row.name
		);
	}
	assert!(
		rows.iter()
			.filter(|r| r.name != "leer")
			.all(|r| r.cost_micro_eur > 0 && r.model_calls >= 2)
	);
	assert!(
		rows.iter()
			.flat_map(|r| &r.entries)
			.all(|e| e.outcome != "matched-wrong")
	);
	assert_eq!(rows.last().unwrap().model_calls, 1);
}
#[tokio::test]
#[ignore = "ProgrammReadingEvaluation: real HTTP/PG negative controls, no live provider"]
async fn wrong_preselection_and_invented_extra_fail_the_document() {
	let corpus: Corpus = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-evaluation.json"
	))
	.unwrap();
	for (name, text, wanted, outcome) in [
		(
			"falsch",
			"Konzert\n1. Der Mond ist aufgegangn",
			json!([{"text":"Der Mond ist aufgegangn","lied":"Im Wald und auf der Heide"}]),
			"matched-wrong",
		),
		(
			"erfunden",
			"Konzert\n1. Ave Maria\n2. Waldfahrt",
			json!([{"text":"Ave Maria","lied":"Ave Maria"}]),
			"extra",
		),
	] {
		let document: Document = serde_json::from_value(
			json!({"name":name,"transcription":text,"expected":{"programm":wanted}}),
		)
		.unwrap();
		let row = evaluate(
			&document,
			&corpus.lieder,
			support::programme::photo(),
			ai::Options::shipped(true),
			None,
		)
		.await;
		assert!(!row.passed);
		assert!(row.entries.iter().any(|e| e.outcome == outcome));
		report(&[row], "negative");
	}
}
#[tokio::test]
#[ignore = "ProgrammReadingEvaluation: real HTTP/PG isolated taxonomy/cost controls"]
async fn negative_outcomes_are_independently_sensitive_and_unmatched_missed_keeps_the_frozen_allowance()
 {
	let corpus: Corpus = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-evaluation.json"
	))
	.unwrap();
	for (name, text, wanted, outcome, passed) in [
		(
			"wrong-only",
			"Konzert\n1. Der Mond ist aufgegangn",
			json!([{"text":"Der Mond ist aufgegangn","lied":"Im Wald und auf der Heide"}]),
			"matched-wrong",
			false,
		),
		(
			"extra-only",
			"Konzert\n1. Ave Maria\n2. Waldfahrt",
			json!([{"text":"Ave Maria","lied":"Ave Maria"}]),
			"extra",
			false,
		),
		(
			"missing-only",
			"Konzert",
			json!([{"text":"Ave Maria","lied":"Ave Maria"}]),
			"read-missing",
			false,
		),
		(
			"weak-allowed",
			"Konzert\n1. Stille Nacht",
			json!([{"text":"Stille Nacht","lied":"Stille Nacht, heilige Nacht"}]),
			"unmatched-missed",
			true,
		),
	] {
		let document: Document = serde_json::from_value(
			json!({"name":name,"transcription":text,"expected":{"title":"Konzert","programm":wanted}}),
		)
		.unwrap();
		let row = evaluate(
			&document,
			&corpus.lieder,
			support::programme::photo(),
			ai::Options::shipped(true),
			None,
		)
		.await;
		assert!(row.wrong_header.is_empty());
		assert_eq!(row.passed, passed);
		assert!(row.entries.iter().any(|e| e.outcome == outcome));
		assert!(row.cost_micro_eur > 0 && row.model_calls == 2);
		report(&[row], "isolated-taxonomy");
	}
}
fn evaluation_price(input: &str, output: &str) -> Result<ai::Price, ai::Error> {
	let price = ai::Price::from_configuration(input, output)?;
	if price.input <= rust_decimal::Decimal::ZERO || price.output <= rust_decimal::Decimal::ZERO {
		return Err(ai::Error::Configuration);
	}
	Ok(price)
}
#[test]
fn live_configuration_guard_and_prices_fail_closed_before_io() {
	for missing in ai::LiveEvaluation::Programm.required() {
		assert!(
			!ai::LiveEvaluation::Programm
				.configured(|key| Some(if key == *missing { " \t" } else { "configured" }.into()))
		);
	}
	for bad in [
		"1_0",
		"NaN",
		"Infinity",
		"0",
		"-1",
		"1,000",
		"0.00000000000000000000000000005",
		"",
	] {
		assert!(evaluation_price(bad, "1").is_err());
		assert!(evaluation_price("1", bad).is_err());
	}
	assert_eq!(
		evaluation_price("0.50000000000000000000000000005", "0.5")
			.unwrap()
			.input,
		rust_decimal::Decimal::new(5, 1)
	);
}
#[tokio::test]
async fn programm_reading_evaluation_live() {
	if !ai::LiveEvaluation::Programm.configured_from_env() {
		eprintln!(
			"PROGRAMM-EVAL live run skipped: set ARCHIVE_PROGRAMM_EVAL_DIR, ARCHIVE_CHAT_ENDPOINT and ARCHIVE_CHAT_DEPLOYMENT_NAME (az login)."
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
		options.models.insert(
			deployment.clone(),
			evaluation_price(&input, &output).expect("unusable configured prices"),
		);
	}
	let price = options
		.models
		.iter()
		.find(|(key, _)| key.eq_ignore_ascii_case(&deployment))
		.map(|(_, p)| p)
		.expect("live deployment must be priced before corpus/provider access");
	assert!(
		price.input > rust_decimal::Decimal::ZERO && price.output > rust_decimal::Decimal::ZERO
	);
	let directory = std::path::PathBuf::from(std::env::var("ARCHIVE_PROGRAMM_EVAL_DIR").unwrap());
	let mut manifest: Value = serde_json::from_slice(
		&std::fs::read(directory.join("expected.json")).expect("configured manifest unavailable"),
	)
	.expect("configured manifest malformed");
	for doc in manifest["documents"]
		.as_array_mut()
		.expect("documents required")
	{
		doc["name"] = doc["file"].clone();
	}
	let corpus: Corpus = serde_json::from_value(manifest).expect("configured corpus malformed");
	assert!(
		!corpus.documents.is_empty(),
		"configured corpus must not silently skip"
	);
	let mut rows = vec![];
	for document in &corpus.documents {
		let bytes = std::fs::read(directory.join(document.file.as_ref().expect("file required")))
			.expect("configured scan unavailable");
		rows.push(
			evaluate(
				document,
				&corpus.lieder,
				bytes,
				options.clone(),
				Some(ai::AzureConfig {
					endpoint: endpoint.clone(),
					deployment: deployment.clone(),
				}),
			)
			.await,
		);
	}
	report(&rows, &format!("live {deployment}"));
	assert!(
		rows.iter()
			.flat_map(|r| &r.entries)
			.all(|e| e.outcome != "matched-wrong")
	);
}
