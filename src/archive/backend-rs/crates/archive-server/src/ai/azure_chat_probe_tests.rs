//! Maintainer-approved OFFLINE equivalent of the sole foreign-ModelId probe.
//! Private module: no exported raw provider constructor and no hosted credential.
use super::*;
use azure_core::credentials::{AccessToken, TokenRequestOptions};
use std::sync::Mutex;
#[derive(Debug)]
struct Credential;
#[async_trait::async_trait]
impl TokenCredential for Credential {
	async fn get_token(
		&self,
		scopes: &[&str],
		_: Option<TokenRequestOptions<'_>>,
	) -> azure_core::Result<AccessToken> {
		assert_eq!(scopes, [SCOPE]);
		Ok(AccessToken::new(
			"synthetic232-offline-token",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
#[tokio::test]
async fn approved_foreign_model_nonstream_probe_uses_only_loopback_and_matches_frozen_wire() {
	let seen = Arc::new(Mutex::new(Vec::new()));
	let router=axum::Router::new().route("/openai/deployments/configured/chat/completions",axum::routing::post({let seen=seen.clone();move |uri:axum::http::Uri,axum::Json(body):axum::Json<Value>| {let seen=seen.clone();async move{seen.lock().unwrap().push((uri.to_string(),body));axum::Json(json!({"id":"offline","model":"offline-response-model","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}))}}}));
	let listener = tokio::net::TcpListener::bind(("127.0.0.1", 0))
		.await
		.unwrap();
	let endpoint = format!("http://{}/", listener.local_addr().unwrap());
	let server = tokio::spawn(async move {
		axum::serve(listener, router).await.unwrap();
	});
	let credential = tokio::sync::OnceCell::new();
	assert!(
		credential
			.set(Arc::new(Credential) as Arc<dyn TokenCredential>)
			.is_ok()
	);
	let azure = Azure {
		config: AzureConfig {
			endpoint,
			deployment: "configured".into(),
		},
		client: reqwest::Client::builder()
			.retry(reqwest::retry::never())
			.redirect(reqwest::redirect::Policy::none())
			.build()
			.unwrap(),
		credential: Arc::new(credential),
	};
	let request = ProviderRequest {
		messages: vec![super::super::Message::text("user", "Antworte nur mit OK.")],
		options: super::super::CallOptions {
			model_id: Some("modell-das-es-nicht-gibt".into()),
			max_output_tokens: Some(16),
			..Default::default()
		},
		streaming: false,
	};
	let updates: Vec<_> = azure.stream(request).collect().await;
	server.abort();
	let _ = server.await;
	assert!(updates.iter().all(Result::is_ok));
	let observed = seen.lock().unwrap();
	assert_eq!(observed.len(), 1);
	assert_eq!(
		observed[0].0,
		"/openai/deployments/configured/chat/completions?api-version=2024-10-21"
	);
	assert_eq!(observed[0].1["model"], "modell-das-es-nicht-gibt");
	assert_eq!(observed[0].1["max_completion_tokens"], 16);
	assert!(observed[0].1.get("stream").is_none());
	assert_eq!(
		updates[0]
			.as_ref()
			.unwrap()
			.wire_usage
			.as_ref()
			.unwrap()
			.model
			.as_deref(),
		Some("offline-response-model")
	);
}
