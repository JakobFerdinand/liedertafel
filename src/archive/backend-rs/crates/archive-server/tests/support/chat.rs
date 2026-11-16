//! Provider-only delivery, real product router/auth/Gateway/current PostgreSQL.
#![allow(dead_code)] // Independently compiled translated chat siblings.
use super::App;
use archive_server::{
	Origin, Settings,
	ai::*,
	auth::{Auth, AuthConfig},
	build_router,
};
use serde_json::{Value, json};
use std::{path::PathBuf, sync::Arc};
pub struct Harness {
	pub origin: Arc<Origin>,
	pub script: Script,
	pub options: tokio::sync::watch::Sender<Options>,
}
pub fn install(app: &mut App, script: Script) -> Harness {
	install_with(app, script, |_| {})
}
pub fn install_with(app: &mut App, script: Script, configure: impl FnOnce(&mut Origin)) -> Harness {
	let auth = Auth::new(
		app.pool.clone(),
		AuthConfig::development(),
		app.mail.clone(),
		app.time.clone(),
	)
	.unwrap();
	let (options, rx) = tokio::sync::watch::channel(Options::shipped(true));
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let mut origin = Origin::new(Settings::new(true, PathBuf::new(), 0)).with_auth(auth);
	origin.chat.enabled = true;
	origin.ai = Some(Services {
		gateway: Gateway::scripted(ledger.clone(), script.clone()),
		ledger,
	});
	configure(&mut origin);
	let origin = Arc::new(origin);
	app.router = build_router(origin.clone());
	Harness {
		origin,
		script,
		options,
	}
}
pub fn body(thread: &str, question: &str) -> Value {
	json!({"threadId":thread,"runId":"run_1","messages":[{"id":"m1","role":"user","content":question}]})
}
pub async fn run(member: &mut super::Client, thread: &str, question: &str) -> Vec<Value> {
	let (status, headers, bytes) = member
		.wire("POST", "/api/chat", &body(thread, question).to_string())
		.await;
	assert_eq!(status, 200, "{}", String::from_utf8_lossy(&bytes));
	assert_eq!(headers["cache-control"], "no-store");
	assert!(
		headers["content-type"]
			.to_str()
			.unwrap()
			.starts_with("text/event-stream")
	);
	String::from_utf8(bytes.to_vec())
		.unwrap()
		.lines()
		.filter_map(|l| l.strip_prefix("data: "))
		.map(|v| serde_json::from_str(v).unwrap())
		.collect()
}
pub fn text(events: &[Value]) -> String {
	events
		.iter()
		.filter(|v| v["type"] == "TEXT_MESSAGE_CONTENT")
		.filter_map(|v| v["delta"].as_str())
		.collect()
}
pub fn tool(name: &str, args: Value) -> ScriptStep {
	ScriptStep::Update(Update {
		tools: vec![ToolDelta {
			index: 0,
			id: Some("call_1".into()),
			name: Some(name.into()),
			arguments: args.to_string(),
		}],
		..Default::default()
	})
}
pub async fn invoke(app: &mut App, name: &str, args: Value) -> Value {
	// Each independent adapter fixture signs in through the real code boundary.
	// Move its injected clock past the previous email allocation window instead
	// of disabling/deleting the product's abuse counters during table cases.
	*app.time.0.lock().unwrap() += chrono::Duration::hours(2);
	let harness = install(app, Script::new(vec![vec![tool(name, args)], vec![]]));
	let mut member = app.member().await;
	let events = run(&mut member, "", "Synthetische Werkzeugfrage").await;
	assert_eq!(events.last().unwrap()["type"], "RUN_FINISHED");
	let requests = harness.script.requests();
	assert_eq!(requests.len(), 2);
	let Content::FunctionResult { result, .. } = &requests[1].messages.last().unwrap().contents[0]
	else {
		unreachable!()
	};
	let value = serde_json::from_str(result.raw()).unwrap();
	assert!(harness.origin.drain_readings().await);
	value
}
