mod support;
use archive_server::ai::{
	Gateway, Ledger, Message, Operation, Options, PostgresStore, Price, Request, Script,
	ScriptStep, Update, Usage,
};
use std::sync::Arc;

#[tokio::test]
#[ignore = "explicit PostgreSQL: scripted delivery below real gateway and ledger"]
async fn scripted_roundtrip_sums_calls_but_takes_component_max_inside_each_call() {
	let app = support::App::new_scoped("ai228").await;
	let mut options = Options::default();
	options
		.models
		.insert("scripted".into(), Price::new("0.83", "4.95").unwrap());
	let (_, rx) = tokio::sync::watch::channel(options);
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let script = Script::new(vec![
		vec![
			ScriptStep::Update(Update::text("Antwort")),
			ScriptStep::Update(Update::usage(Usage::reported(100, 10))),
			ScriptStep::Update(Update::usage(Usage::reported(50, 5))),
		],
		vec![ScriptStep::Update(Update::usage(Usage::reported(200, 20)))],
	]);
	let gateway = Gateway::scripted(ledger.clone(), script.clone());
	let mut request = Request::new(vec![Message::text("user", "Frage")]);
	request.operation = Some(Operation::start("chat", None));
	for _ in 0..2 {
		gateway
			.start(request.clone(), true)
			.unwrap()
			.response()
			.await
			.unwrap();
	}
	assert!(gateway.drain().await);
	assert_eq!(script.requests().len(), 2);
	let rows = ledger
		.month_entries(&ledger.status().await.unwrap().month)
		.await
		.unwrap();
	assert_eq!(rows.len(), 1);
	assert_eq!(
		(
			rows[0].calls,
			rows[0].input_tokens,
			rows[0].output_tokens,
			rows[0].cost_micro_eur,
			rows[0].reserved_micro_eur
		),
		(2, 300, 30, 398, 0)
	);
	app.close().await;
}
