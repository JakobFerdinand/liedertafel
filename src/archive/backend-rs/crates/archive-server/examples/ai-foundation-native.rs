//! Offline-only process gate, not a product command. Own loopback provider,
//! fake credential, disposable local PostgreSQL and checked finite OTLP flush.
use archive_server::{ai::*, auth::SystemClock};
use axum::{
	Json, Router,
	response::{IntoResponse, Response},
	routing::post,
};
use azure_core::credentials::{AccessToken, TokenCredential, TokenRequestOptions};
use futures::{StreamExt, stream};
use serde_json::{Value, json};
use std::sync::{
	Arc,
	atomic::{AtomicUsize, Ordering},
};

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
			"synthetic-ai-token-228",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
	let _ = rustls::crypto::ring::default_provider().install_default();
	let defaults = archive_telemetry::ServiceDefaults::init(true, "archive-ai-foundation-offline")?;
	let url = std::env::var("ARCHIVE_TEST_AI_DATABASE")?;
	let parsed = url::Url::parse(&url)?;
	assert!(matches!(parsed.host_str(), Some("localhost" | "127.0.0.1")));
	assert!(parsed.path().starts_with("/ai228_native_"));
	let pool = sqlx::PgPool::connect(&url).await?;
	let fail_settlement =
		std::env::var("ARCHIVE_TEST_AI_SETTLEMENT_FAILURE").as_deref() == Ok("offline-228");
	let prefix_error =
		std::env::var("ARCHIVE_TEST_AI_PREFIX_ERROR").as_deref() == Ok("offline-228");
	if fail_settlement {
		sqlx::raw_sql("CREATE FUNCTION ai228_refuse_settlement() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-settlement-error-228'; END $$; CREATE TRIGGER ai228_refuse_settlement BEFORE UPDATE ON ai_usage_entries FOR EACH ROW EXECUTE FUNCTION ai228_refuse_settlement();").execute(&pool).await?;
	}
	let mut options = Options::shipped(false);
	options
		.models
		.insert("gpt-6-luna".into(), Price::new("0.83", "4.95")?);
	let (options, rx) = tokio::sync::watch::channel(options);
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(pool.clone())),
		rx,
		Arc::new(SystemClock),
	);
	let requests = Arc::new(AtomicUsize::new(0));
	let account = uuid::Uuid::now_v7();
	let provider_forbidden_account = account.to_string();
	let count = requests.clone();
	let router=Router::new().route("/openai/deployments/gpt-6-luna/chat/completions",post(move |headers:axum::http::HeaderMap,Json(body):Json<Value>| {let count=count.clone();let provider_forbidden_account=provider_forbidden_account.clone();async move {
		assert_eq!(headers["authorization"],"Bearer synthetic-ai-token-228");let wire=body.to_string();assert!(!wire.contains(&provider_forbidden_account));assert!(!wire.contains("archive-chat"));count.fetch_add(1,Ordering::SeqCst);
		if body["stream"]==true {
			if prefix_error {
				let data="data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"private-answer-228\"},\"finish_reason\":null}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\ndata: {bad\n\n";
				return Response::builder().header("content-type","text/event-stream").body(axum::body::Body::from(data)).unwrap_or_else(|_|Response::new(axum::body::Body::empty()));
			}
			let data="data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"private-answer-228\"},\"finish_reason\":null}]}\n\n";
			let stream=stream::once(async move { Ok::<_,std::io::Error>(data) }).chain(stream::pending());
			Response::builder().header("content-type","text/event-stream").body(axum::body::Body::from_stream(stream)).unwrap_or_else(|_|Response::new(axum::body::Body::empty()))
		} else { Json(json!({"choices":[{"index":0,"message":{"content":"private-answer-228","role":"assistant"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":10}})).into_response() }
	} }));
	let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await?;
	let address = listener.local_addr()?;
	let server = tokio::spawn(async move {
		let _ = axum::serve(listener, router).await;
	});
	let gateway = Gateway::azure_with_credential(
		ledger.clone(),
		AzureConfig {
			endpoint: format!("http://{address}/"),
			deployment: "gpt-6-luna".into(),
		},
		Arc::new(Credential),
	)?;
	let mut request = Request::new(vec![Message::text(
		"user",
		"private-prompt-228 redaktion-228@example.test sig=private-signature-228",
	)]);
	request.operation = Some(Operation::start("chat", Some(account)));
	request.agent = Some("archive-chat".into());
	request.options.max_output_tokens = Some(200);
	request.options.tools.push(Tool {
		name: "suche".into(),
		description: "private-tool-payload-228".into(),
		schema: serde_json::from_str("{\"type\":\"object\",\"properties\":{}}")?,
	});
	if prefix_error {
		let mut call = gateway.start(request, true)?;
		assert!(call.next().await?.is_some());
		assert!(matches!(call.next().await, Err(Error::Protocol)));
		assert!(gateway.drain().await);
		let rows = ledger.month_entries(&ledger.status().await?.month).await?;
		assert_eq!(rows[0].calls, 1);
		assert_eq!(
			(
				rows[0].input_tokens,
				rows[0].output_tokens,
				rows[0].cost_micro_eur,
				rows[0].reserved_micro_eur
			),
			(5, 2, 15, 0)
		);
		tracing::info!("Offline AI ordered prefix/error accounting completed");
		server.abort();
		let _ = server.await;
		pool.close().await;
		if !defaults.finish_finite_job() {
			return Err("finite OTLP flush failed".into());
		}
		return Ok(());
	}
	gateway.start(request.clone(), false)?.response().await?;
	if fail_settlement {
		assert!(!gateway.drain().await);
		let rows = ledger.month_entries(&ledger.status().await?.month).await?;
		assert_eq!(rows[0].calls, 1);
		assert!(rows[0].reserved_micro_eur > 0);
		assert_eq!(rows[0].cost_micro_eur, 0);
		server.abort();
		let _ = server.await;
		pool.close().await;
		if !defaults.finish_finite_job() {
			return Err("finite OTLP flush failed".into());
		}
		return Err("offline AI settlement failed conservatively".into());
	}
	let mut abandoned = gateway.start(request.clone(), true)?;
	assert!(abandoned.next().await?.is_some());
	drop(abandoned);
	options.send_modify(|o| o.monthly_cap_eur = 0.into());
	assert!(gateway.start(request, true)?.response().await.is_err());
	assert!(gateway.drain().await);
	assert_eq!(requests.load(Ordering::SeqCst), 2);
	let rows = ledger.month_entries(&ledger.status().await?.month).await?;
	assert_eq!(rows.len(), 1);
	assert_eq!(rows[0].calls, 2);
	assert_eq!(rows[0].reserved_micro_eur, 0);
	assert!(rows[0].cost_micro_eur > 133);
	tracing::info!("Offline AI foundation native provider/cancel/ledger gate completed");
	server.abort();
	let _ = server.await;
	pool.close().await;
	if !defaults.finish_finite_job() {
		return Err("finite OTLP flush failed".into());
	}
	Ok(())
}
