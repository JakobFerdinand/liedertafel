// Recording-server/fake-credential fixture helpers are assertion-only test code.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use archive_server::ai::{
	AzureConfig, Gateway, Ledger, Message, Operation, Options, PostgresStore, Request,
};
use axum::{Json, Router, routing::post};
use azure_core::credentials::{AccessToken, TokenCredential, TokenRequestOptions};
use serde_json::{Value, json};
use std::sync::Arc;
#[path = "support/ai.rs"]
mod pure;
use archive_server::ai::{Content, Payload, ResponseFormat, Tool, ToolCalls};

async fn offline_gateway(
	body: String,
	content_type: &'static str,
	status: u16,
) -> (
	Gateway,
	pure::Harness,
	Arc<tokio::sync::Mutex<Vec<Value>>>,
	tokio::task::JoinHandle<()>,
) {
	let h = pure::Harness::new();
	h.options.send_modify(|o| {
		o.models.insert(
			"gpt-6-luna".into(),
			archive_server::ai::Price::new("1", "4").unwrap(),
		);
	});
	let capture = Arc::new(tokio::sync::Mutex::new(Vec::new()));
	let sink = capture.clone();
	let router = Router::new().route(
		"/openai/deployments/gpt-6-luna/chat/completions",
		post(
			move |axum::extract::RawQuery(query): axum::extract::RawQuery,
			      Json(request): Json<Value>| {
				let sink = sink.clone();
				let body = body.clone();
				async move {
					assert_eq!(query.as_deref(), Some("api-version=2024-10-21"));
					sink.lock().await.push(request);
					let bytes = body.into_bytes();
					let stream = futures::stream::unfold((bytes, 0), |(bytes, index)| async move {
						if index == bytes.len() {
							None
						} else {
							tokio::task::yield_now().await;
							let end = (index + if bytes.len() > 10000 { 16384 } else { 1 })
								.min(bytes.len());
							Some((
								Ok::<_, std::io::Error>(axum::body::Bytes::copy_from_slice(
									&bytes[index..end],
								)),
								(bytes, end),
							))
						}
					});
					let mut response =
						axum::response::Response::new(axum::body::Body::from_stream(stream));
					*response.status_mut() = axum::http::StatusCode::from_u16(status).unwrap();
					response
						.headers_mut()
						.insert("content-type", content_type.parse().unwrap());
					response
						.headers_mut()
						.insert("retry-after-ms", "1".parse().unwrap());
					response
				}
			},
		),
	);
	let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
	let address = listener.local_addr().unwrap();
	let server = tokio::spawn(async move {
		axum::serve(listener, router).await.unwrap();
	});
	let gateway = Gateway::azure_with_credential(
		h.ledger.clone(),
		AzureConfig {
			endpoint: format!("http://{address}/"),
			deployment: "gpt-6-luna".into(),
		},
		Arc::new(Credential),
	)
	.unwrap();
	(gateway, h, capture, server)
}
#[tokio::test]
async fn actual_frozen_sdk_requests_match_plain_schema_image_and_tool_history() {
	let fixtures = include_str!("../../../tests/fixtures/228-ai-wire.jsonl")
		.lines()
		.map(|l| serde_json::from_str::<Value>(l).unwrap())
		.collect::<Vec<_>>();
	for scenario in ["plain", "schema", "image", "tools"] {
		let body=json!({"choices":[{"index":0,"message":{"role":"assistant","content":"Antwort"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}).to_string();
		let (gateway, h, capture, server) = offline_gateway(body, "application/json", 200).await;
		let mut request = h.request();
		request.messages = vec![Message::text("user", "Frage ü😀")];
		request.options.instructions = Some("Anweisung".into());
		request.options.max_output_tokens = Some(200);
		let expected =
			&fixtures.iter().find(|v| v["scenario"] == scenario).unwrap()["Requests"][0]["body"];
		match scenario {
			"schema" => {
				request.options.response_format = Some(ResponseFormat {
					name: "felder".into(),
					description: Some("Felder lesen".into()),
					schema: serde_json::from_value(
						expected["response_format"]["json_schema"]["schema"].clone(),
					)
					.unwrap(),
					strict: None,
				})
			}
			"image" => {
				request.messages = vec![Message {
					role: "user".into(),
					contents: vec![
						Content::Text("abc".into()),
						Content::ImageUrl("data:image/png;base64,AQID".into()),
					],
				}]
			}
			"tools" => {
				request.options.tools = vec![Tool {
					name: "suche".into(),
					description: "Lied suchen".into(),
					schema: serde_json::from_value(
						expected["tools"][0]["function"]["parameters"].clone(),
					)
					.unwrap(),
				}];
				request.messages.push(Message {
					role: "assistant".into(),
					contents: vec![Content::FunctionCall {
						id: "call_1".into(),
						name: "suche".into(),
						arguments: Payload::from_value(&json!({"titel":"Grüß😀"})).unwrap(),
					}],
				});
				request.messages.push(Message {
					role: "tool".into(),
					contents: vec![Content::FunctionResult {
						id: "call_1".into(),
						result: Payload::from_value(&json!({"titel":"Grüß😀"})).unwrap(),
					}],
				});
			}
			_ => {}
		}
		request.operation.as_mut().unwrap().non_text_input_tokens = Some(5000);
		assert_eq!(
			gateway
				.start(request, false)
				.unwrap()
				.response()
				.await
				.unwrap()[0]
				.text,
			"Antwort"
		);
		assert_eq!(capture.lock().await[0], *expected, "{scenario}");
		assert_eq!(h.row().await.cost_micro_eur, 13);
		assert!(gateway.drain().await);
		server.abort();
		let _ = server.await;
	}
}
#[tokio::test]
async fn fragmented_utf8_sse_tools_and_trailing_usage_survive_finish_reason_until_done() {
	let events = [
		json!({"choices":[{"index":0,"delta":{"content":"Grüß😀","tool_calls":[{"index":1,"id":"b","function":{"name":"zwei","arguments":"{\"b\":"}},{"index":0,"id":"a","function":{"name":"eins","arguments":"{\"titel\":\""}}]},"finish_reason":null}],"usage":null}),
		json!({"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ü😀\"}"}},{"index":1,"function":{"arguments":"2}"}}]},"finish_reason":"tool_calls"}],"usage":null}),
		json!({"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":10}}),
	];
	let body = format!(
		": comment\r\n\r\n{}data: [DONE]\r\n\r\n",
		events
			.iter()
			.map(|e| format!("data: {e}\r\n\r\n"))
			.collect::<String>()
	);
	let (gateway, h, _, server) =
		offline_gateway(body, "text/event-stream; charset=utf-8", 200).await;
	let updates = gateway
		.start(h.request(), true)
		.unwrap()
		.response()
		.await
		.unwrap();
	let mut calls = ToolCalls::default();
	for update in &updates {
		calls.observe(&update.tools).unwrap();
	}
	let tools = calls.finish().unwrap();
	assert_eq!(tools.len(), 2);
	if let Content::FunctionCall { id, arguments, .. } = &tools[0] {
		assert_eq!(id, "a");
		assert_eq!(arguments.raw(), "{\"titel\":\"ü😀\"}");
	} else {
		panic!("missing tool")
	};
	assert_eq!(h.row().await.cost_micro_eur, 140);
	assert_eq!(updates[0].text, "Grüß😀");
	assert!(gateway.drain().await);
	server.abort();
	let _ = server.await;
}
#[tokio::test]
async fn errors_malformed_missing_done_wrong_content_type_and_oversized_frames_are_charged_and_redacted()
 {
	for (body, kind, status) in [
		("{bad".into(), "application/json", 200),
		(
			"data: {\"choices\":[]}\n\n".into(),
			"text/event-stream",
			200,
		),
		(
			"data: {\"error\":{\"message\":\"private-secret\"}}\n\n".into(),
			"text/event-stream",
			200,
		),
		("<secret-html>".into(), "text/html", 200),
		("private-secret".into(), "application/json", 400),
		(
			format!("data: {}\n\n", "x".repeat(1024 * 1024 + 1)),
			"text/event-stream",
			200,
		),
	] {
		let (gateway, h, capture, server) = offline_gateway(body, kind, status).await;
		let error = gateway
			.start(h.request(), kind.starts_with("text/event"))
			.unwrap()
			.response()
			.await
			.unwrap_err();
		assert!(!format!("{error:?}").contains("private-secret"));
		assert_eq!(capture.lock().await.len(), 1);
		assert_eq!(h.row().await.cost_micro_eur, 24010);
		assert!(gateway.drain().await);
		server.abort();
		let _ = server.await;
	}
}
#[tokio::test]
async fn production_transport_retry_statuses_match_frozen_physical_attempts_under_one_reservation()
{
	for (status, expected) in [
		(408, 4),
		(429, 4),
		(500, 4),
		(502, 4),
		(503, 4),
		(504, 4),
		(409, 1),
		(400, 1),
	] {
		let (gateway, h, capture, server) =
			offline_gateway("private-error".into(), "application/json", status).await;
		assert!(
			gateway
				.start(h.request(), false)
				.unwrap()
				.response()
				.await
				.is_err()
		);
		assert_eq!(capture.lock().await.len(), expected, "status {status}");
		assert_eq!(h.row().await.calls, 1);
		assert_eq!(h.row().await.cost_micro_eur, 24010);
		assert!(gateway.drain().await);
		server.abort();
		let _ = server.await;
	}
}

#[tokio::test]
async fn content_filter_finish_preserves_reported_usage_like_actual_frozen_sdk() {
	let body=json!({"choices":[{"index":0,"message":{"role":"assistant","content":"Antwort"},"finish_reason":"content_filter"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}).to_string();
	let (gateway, h, _, server) = offline_gateway(body, "application/json", 200).await;
	let updates = gateway
		.start(h.request(), false)
		.unwrap()
		.response()
		.await
		.unwrap();
	assert_eq!(updates[0].finish_reason.as_deref(), Some("content_filter"));
	assert_eq!(h.row().await.cost_micro_eur, 13);
	assert!(gateway.drain().await);
	server.abort();
	let _ = server.await;
}

#[derive(Debug)]
struct Credential;

#[tokio::test]
async fn malformed_usage_is_not_a_free_reported_zero_call() {
	for usage in [
		json!({"prompt_tokens":"bad"}),
		json!({"prompt_tokens":null}),
		json!({"prompt_tokens":2147483648_i64}),
	] {
		let body=json!({"choices":[{"index":0,"message":{"role":"assistant","content":"Antwort"},"finish_reason":"stop"}],"usage":usage}).to_string();
		let (gateway, h, _, server) = offline_gateway(body, "application/json", 200).await;
		assert!(
			gateway
				.start(h.request(), false)
				.unwrap()
				.response()
				.await
				.is_err()
		);
		assert_eq!(h.row().await.cost_micro_eur, 24010);
		assert!(gateway.drain().await);
		server.abort();
		let _ = server.await;
	}
}
#[async_trait::async_trait]
impl TokenCredential for Credential {
	async fn get_token(
		&self,
		scopes: &[&str],
		_: Option<TokenRequestOptions<'_>>,
	) -> azure_core::Result<AccessToken> {
		assert_eq!(scopes, ["https://cognitiveservices.azure.com/.default"]);
		Ok(AccessToken::new(
			"fake-228",
			time::OffsetDateTime::now_utc() + time::Duration::hours(1),
		))
	}
}
#[tokio::test]
#[ignore = "explicit local HTTP and PostgreSQL: real hand-rolled AOAI under gateway"]
async fn azure_request_is_deployment_bound_and_uses_frozen_wire_fields() {
	let app = support::App::new_scoped("ai228").await;
	let capture = Arc::new(tokio::sync::Mutex::new(Vec::<Value>::new()));
	let sink = capture.clone();
	let router=Router::new().route("/openai/deployments/gpt-6-luna/chat/completions",post(move |headers:axum::http::HeaderMap,Json(body):Json<Value>| { let sink=sink.clone(); async move {
		assert_eq!(headers["authorization"],"Bearer fake-228"); sink.lock().await.push(body);
		Json(json!({"choices":[{"index":0,"message":{"role":"assistant","content":"Antwort"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}))
	} }));
	let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
	let address = listener.local_addr().unwrap();
	let server = tokio::spawn(async move {
		axum::serve(listener, router).await.unwrap();
	});
	let (_, rx) = tokio::sync::watch::channel(Options::shipped(false));
	let ledger = Ledger::new(
		Arc::new(PostgresStore::new(app.pool.clone())),
		rx,
		app.time.clone(),
	);
	let gateway = Gateway::azure_with_credential(
		ledger,
		AzureConfig {
			endpoint: format!("http://{address}/"),
			deployment: "gpt-6-luna".into(),
		},
		Arc::new(Credential),
	)
	.unwrap();
	assert!(capture.lock().await.is_empty(), "construction is lazy");
	let mut request = Request::new(vec![Message::text("user", "Frage")]);
	request.operation = Some(Operation::start("chat", None));
	request.options.max_output_tokens = Some(200);
	let response = gateway
		.start(request, false)
		.unwrap()
		.response()
		.await
		.unwrap();
	assert_eq!(response[0].text, "Antwort");
	let bodies = capture.lock().await;
	assert_eq!(
		bodies[0],
		json!({"model":"gpt-6-luna","messages":[{"role":"user","content":"Frage"}],"max_completion_tokens":200,"reasoning_effort":"none"})
	);
	assert!(gateway.drain().await);
	server.abort();
	let _ = server.await;
	app.close().await;
}
