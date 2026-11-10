// Process-isolated ENV tests: no unsafe/global environment changes in parallel tests.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use archive_server::ai::*;
use azure_core::credentials::{AccessToken, TokenCredential, TokenRequestOptions};
use serde_json::{Value, json};
use std::sync::{
	Arc,
	atomic::{AtomicUsize, Ordering},
};

fn cases() -> Vec<Value> {
	include_str!("../../../tests/fixtures/228-ai-config.jsonl")
		.lines()
		.map(|l| serde_json::from_str(l).unwrap())
		.collect()
}
fn price(options: &Options, model: &str) -> Value {
	options.find_price(model).map(|p|json!({"input":p.input.to_string(),"output":p.output.to_string(),"costOneEachMicro":cost_micro_eur(p,1,1).unwrap()})).unwrap_or(Value::Null)
}
fn snapshot(options: Result<Options, Error>) -> Value {
	match options {
		Ok(o) => {
			json!({"error":false,"cap":o.monthly_cap_eur.to_string(),"input":o.max_input_tokens,"output":o.max_output_tokens,"luna":price(&o,"GpT-6-LuNa"),"mixed":price(&o,"mixed-test"),"unknown":price(&o,"unknown-deployment")})
		}
		Err(_) => json!({"error":true}),
	}
}
fn assert_binding(case: &Value) {
	let actual = snapshot(Options::from_env(false));
	if case["name"] == "invalid-price" {
		// Frozen dictionary binding drops an invalid model; Rust keeps its
		// existing stricter configuration error. Both fail before any call;
		// invalid overrides must NEVER restore the shipped positive price.
		assert!(case["expected"]["luna"].is_null());
		assert_eq!(actual, json!({"error":true}));
	} else {
		assert_eq!(actual, case["expected"], "{}", case["name"]);
	}
}
fn child_case() -> Option<Value> {
	std::env::var("ARCHIVE_228_CONFIG_CASE")
		.ok()
		.map(|v| cases()[v.parse::<usize>().unwrap()].clone())
}
fn run_children(name: &str, ignored: bool) {
	for (index, case) in cases().iter().enumerate() {
		let mut child = std::process::Command::new(std::env::current_exe().unwrap());
		child.args(["--exact", name, "--nocapture"]);
		if ignored {
			child.arg("--ignored");
		}
		for (key, _) in std::env::vars() {
			if key
				.replace("__", ":")
				.to_ascii_lowercase()
				.starts_with("archive:ai:")
			{
				child.env_remove(key);
			}
		}
		for (key, value) in case["env"].as_object().unwrap() {
			child.env(key, value.as_str().unwrap());
		}
		child.env("ARCHIVE_228_CONFIG_CASE", index.to_string());
		let output = child.output().unwrap();
		assert!(
			output.status.success(),
			"case {}\n{}\n{}",
			case["name"],
			String::from_utf8_lossy(&output.stdout),
			String::from_utf8_lossy(&output.stderr)
		);
	}
}
#[test]
fn environment_binding_matches_actual_frozen_configuration_builder() {
	if let Some(case) = child_case() {
		assert_binding(&case);
	} else {
		run_children(
			"environment_binding_matches_actual_frozen_configuration_builder",
			false,
		);
	}
}

#[derive(Debug)]
struct Credential;
#[async_trait::async_trait]
impl TokenCredential for Credential {
	async fn get_token(
		&self,
		scopes: &[&str],
		_: Option<TokenRequestOptions<'_>>,
	) -> azure_core::Result<AccessToken> {
		assert_eq!(scopes, ["https://cognitiveservices.azure.com/.default"]);
		Ok(AccessToken::new(
			"offline-config-228",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
#[tokio::test]
#[ignore = "explicit PostgreSQL: process-isolated configured cap/bounds/prices before provider/rows"]
async fn configured_guards_and_prices_reach_real_gateway_and_sql() {
	let Some(case) = child_case() else {
		run_children(
			"configured_guards_and_prices_reach_real_gateway_and_sql",
			true,
		);
		return;
	};
	let app = support::App::new_scoped("ai228_config").await;
	let options = Options::from_env(false);
	assert_binding(&case);
	let Ok(options) = options else {
		let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_usage_entries")
			.fetch_one(&app.pool)
			.await
			.unwrap();
		assert_eq!(count, 0);
		app.close().await;
		return;
	};
	let expected = &case["expected"];
	let model = case["model"].as_str().unwrap().to_owned();
	let requested_model = model.clone();
	let selected = if model.eq_ignore_ascii_case("gpt-6-luna") {
		&expected["luna"]
	} else if model.eq_ignore_ascii_case("mixed-test") {
		&expected["mixed"]
	} else {
		&expected["unknown"]
	};
	let will_refuse = selected.is_null()
		|| options.max_input_tokens < 10
		|| options.monthly_cap_eur.to_string() == "0"
		|| case["name"] == "mixed-lower-cap";
	let output_bound = options.max_output_tokens.clamp(1, 50_000);
	let count = Arc::new(AtomicUsize::new(0));
	let captured = count.clone();
	let route = format!("/openai/deployments/{model}/chat/completions");
	let router=axum::Router::new().route(&route,axum::routing::post(move |axum::Json(body):axum::Json<Value>|{let captured=captured.clone();let requested_model=requested_model.clone();async move {
		assert_eq!(body["model"],requested_model);assert_eq!(body["max_completion_tokens"],output_bound);captured.fetch_add(1,Ordering::SeqCst);
		axum::Json(json!({"choices":[{"index":0,"message":{"role":"assistant","content":"Antwort"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}))
	}}));
	let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
	let endpoint = format!("http://{}/", listener.local_addr().unwrap());
	let server = tokio::spawn(async move {
		axum::serve(listener, router).await.unwrap();
	});
	let (_, rx) = tokio::sync::watch::channel(options);
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let gateway = Gateway::azure_with_credential(
		ledger.clone(),
		AzureConfig {
			endpoint,
			deployment: model.clone(),
		},
		Arc::new(Credential),
	)
	.unwrap();
	let mut request = Request::new(vec![Message::text("user", "Frage")]);
	request.operation = Some(Operation::start("chat", None));
	request.options.max_output_tokens = Some(50_000);
	let result = gateway.start(request, false).unwrap().response().await;
	assert_eq!(result.is_err(), will_refuse, "{}", case["name"]);
	assert!(gateway.drain().await);
	let rows = ledger
		.month_entries(&ledger.status().await.unwrap().month)
		.await
		.unwrap();
	assert_eq!(count.load(Ordering::SeqCst), usize::from(!will_refuse));
	if will_refuse {
		assert!(rows.is_empty());
		let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM ai_usage_entries")
			.fetch_one(&app.pool)
			.await
			.unwrap();
		assert_eq!(count, 0);
	} else {
		assert_eq!(rows.len(), 1);
		assert_eq!(rows[0].model, model);
		assert_eq!(
			rows[0].cost_micro_eur,
			selected["costOneEachMicro"].as_i64().unwrap()
		);
	}
	server.abort();
	let _ = server.await;
	app.close().await;
}
