//! Actual offline provider socket BELOW Gateway + real ledger and chat HTTP.
mod support;
use archive_server::ai::{AzureConfig, Gateway, Script};
use azure_core::credentials::{AccessToken, TokenCredential, TokenRequestOptions};
use serde_json::json;
use std::sync::Arc;
use support::{App, chat::*};

#[derive(Debug)]
struct OfflineCredential;
#[async_trait::async_trait]
impl TokenCredential for OfflineCredential {
	async fn get_token(
		&self,
		_: &[&str],
		_: Option<TokenRequestOptions<'_>>,
	) -> azure_core::Result<AccessToken> {
		Ok(AccessToken::new(
			"offline-only-232",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
#[tokio::test]
#[ignore = "dedicated local PostgreSQL; loopback fake credential only"]
async fn frozen_sdk_usage_metadata_survives_real_wire_without_changing_billing_or_filling_absent_groups()
 {
	let mut app = App::new_scoped("chat232_provider").await;
	for (usage, expected) in [
		(
			json!({"prompt_tokens":5,"completion_tokens":2,"total_tokens":7,"prompt_tokens_details":{"cached_tokens":3},"completion_tokens_details":{"reasoning_tokens":1}}),
			json!({"model":"response-model","inputTokens":5,"outputTokens":2,"totalTokens":7,"cachedInputTokens":3,"reasoningTokens":1}),
		),
		(
			json!({}),
			json!({"model":"response-model","inputTokens":0,"outputTokens":0,"totalTokens":0}),
		),
		(
			json!({"prompt_tokens_details":{},"completion_tokens_details":{}}),
			json!({"model":"response-model","inputTokens":0,"outputTokens":0,"totalTokens":0,"cachedInputTokens":0,"reasoningTokens":0}),
		),
		(
			json!({"prompt_tokens_details":null,"completion_tokens_details":null}),
			json!({"model":"response-model","inputTokens":0,"outputTokens":0,"totalTokens":0}),
		),
		(
			json!({"completion_tokens_details":{"reasoning_tokens":null}}),
			json!({"model":"response-model","inputTokens":0,"outputTokens":0,"totalTokens":0,"reasoningTokens":0}),
		),
	] {
		let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
		let endpoint = format!("http://{}/", listener.local_addr().unwrap());
		let body = format!(
			"data: {}\n\ndata: [DONE]\n\n",
			json!({"id":"c1","object":"chat.completion.chunk","created":1,"model":"response-model","choices":[],"usage":usage})
		);
		let router = axum::Router::new().route(
			"/openai/deployments/gpt-6-luna/chat/completions",
			axum::routing::post(move || {
				let body = body.clone();
				async move {
					(
						[(axum::http::header::CONTENT_TYPE, "text/event-stream")],
						body,
					)
				}
			}),
		);
		let server = tokio::spawn(async move {
			axum::serve(listener, router).await.unwrap();
		});
		let harness = install_with(&mut app, Script::new(vec![]), |o| {
			let services = o.ai.as_mut().unwrap();
			services.gateway = Gateway::azure_with_credential(
				services.ledger.clone(),
				AzureConfig {
					endpoint,
					deployment: "gpt-6-luna".into(),
				},
				Arc::new(OfflineCredential),
			)
			.unwrap();
		});
		let mut member = app.member().await;
		let events = run(&mut member, "", "Synthetic metadata probe").await;
		assert_eq!(events.last().unwrap()["usage"], json!([expected]));
		assert!(harness.origin.drain_readings().await);
		server.abort();
		let _ = server.await;
	}
	app.close().await;
}
