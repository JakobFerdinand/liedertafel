//! Minimum ACS handoff seam needed for production code sign-in, extended by #222.
// Scripted transport assertions are test-only, never reachable from the origin.
#![allow(clippy::unwrap_used)]
use archive_server::auth::{
	Mail, MailError, MailMessage,
	mail::acs::{AccessToken, AcsRequest, AcsResponse, AcsTransport, AzureMail},
};
use serde_json::json;
use std::{
	collections::VecDeque,
	future::Future,
	pin::Pin,
	sync::{Arc, Mutex},
	time::Duration,
};
struct Token;
impl AccessToken for Token {
	fn get(&self) -> Pin<Box<dyn Future<Output = Result<String, MailError>> + Send + '_>> {
		Box::pin(async { Ok("synthetic-bearer".into()) })
	}
}
struct Transport {
	responses: Mutex<VecDeque<AcsResponse>>,
	requests: Mutex<Vec<AcsRequest>>,
}
impl AcsTransport for Transport {
	fn send(
		&self,
		request: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>> {
		self.requests.lock().unwrap().push(request);
		Box::pin(async { Ok(self.responses.lock().unwrap().pop_front().unwrap()) })
	}
}
fn message() -> MailMessage {
	MailMessage {
		to: "synthetic@example.test".into(),
		subject: "Ihr Anmeldecode für das Liedertafel-Archiv".into(),
		text: "Ihr Anmeldecode lautet: 123456".into(),
	}
}
#[tokio::test]
async fn acs_waits_for_handoff_and_binds_polling_to_same_https_origin() {
	let transport=Arc::new(Transport{responses:Mutex::new(VecDeque::from([
		AcsResponse{status:202,operation_location:Some("https://synthetic.communication.azure.com/emails/operations/221?api-version=2023-03-31".into()),retry_after:Duration::ZERO,body:json!({"status":"Running"})},
		AcsResponse{status:200,operation_location:None,retry_after:Duration::ZERO,body:json!({"status":"Succeeded"})},
	])),requests:Mutex::new(vec![])});
	let sender = AzureMail::new(
		"https://synthetic.communication.azure.com".parse().unwrap(),
		"archiv@liedertafel-mining.at".into(),
		Arc::new(Token),
		transport.clone(),
		Duration::from_secs(1),
	)
	.unwrap();
	sender.send(message()).await.unwrap();
	let requests = transport.requests.lock().unwrap();
	assert_eq!(requests.len(), 2);
	assert_eq!(requests[0].method, "POST");
	assert_eq!(requests[1].method, "GET");
	assert_eq!(
		requests[0].body["recipients"]["to"][0]["address"],
		"synthetic@example.test"
	);
	assert_eq!(
		requests[0].body["content"]["plainText"],
		"Ihr Anmeldecode lautet: 123456"
	);
}
#[tokio::test]
async fn acs_rejects_nonterminal_errors_external_poll_url_and_bounded_timeout() {
	for (status, location, body, timeout) in [
		(
			401,
			None,
			json!({"error":{"message":"secret recipient payload"}}),
			false,
		),
		(
			202,
			Some("https://evil.example/steal"),
			json!({"status":"Running"}),
			false,
		),
		(
			202,
			Some("https://synthetic.communication.azure.com/emails/operations/221"),
			json!({"status":"Running"}),
			true,
		),
		(
			202,
			None,
			json!({"status":"Failed","error":{"message":"private"}}),
			false,
		),
	] {
		let transport = Arc::new(Transport {
			responses: Mutex::new(VecDeque::from([AcsResponse {
				status,
				operation_location: location.map(str::to_owned),
				retry_after: Duration::from_secs(60),
				body,
			}])),
			requests: Mutex::new(vec![]),
		});
		let sender = AzureMail::new(
			"https://synthetic.communication.azure.com".parse().unwrap(),
			"archiv@liedertafel-mining.at".into(),
			Arc::new(Token),
			transport,
			Duration::from_millis(20),
		)
		.unwrap();
		let error = sender.send(message()).await.unwrap_err();
		assert_eq!(matches!(error, MailError::Timeout), timeout);
		assert!(!error.to_string().contains("private"));
	}
}
