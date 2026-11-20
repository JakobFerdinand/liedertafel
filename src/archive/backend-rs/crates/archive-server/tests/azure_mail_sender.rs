//! Remaining AzureMailSenderTests translations; fakes replace delivery only.
// Fixture synchronization failures are assertion failures, never request paths.
#![allow(clippy::unwrap_used)]
use archive_server::auth::{
	Mail, MailError, MailMessage,
	mail::{self, acs::*},
};
use serde_json::json;
use std::{
	future::Future,
	pin::Pin,
	sync::{Arc, Mutex},
	time::Duration,
};
struct Token;
impl AccessToken for Token {
	fn get(&self) -> Pin<Box<dyn Future<Output = Result<String, MailError>> + Send + '_>> {
		Box::pin(async { Ok("synthetic".into()) })
	}
}
struct Capture(Mutex<Vec<AcsRequest>>);
impl AcsTransport for Capture {
	fn send(
		&self,
		request: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>> {
		self.0.lock().unwrap().push(request);
		Box::pin(async {
			Ok(AcsResponse {
				status: 202,
				operation_location: None,
				retry_after: Duration::ZERO,
				body: json!({"id":"00000000-0000-0000-0000-000000000222","status":"Succeeded"}),
			})
		})
	}
}

struct PollCapture(Mutex<Vec<AcsRequest>>);
impl AcsTransport for PollCapture {
	fn send(
		&self,
		request: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>> {
		let first = request.method == "POST";
		self.0.lock().unwrap().push(request);
		Box::pin(async move {
			Ok(AcsResponse{status:if first{202}else{200},operation_location:first.then(||"https://synthetic.communication.azure.com/emails/operations/00000000-0000-0000-0000-000000000222?api-version=2023-03-31".into()),retry_after:Duration::ZERO,body:json!({"status":if first{"Running"}else{"Succeeded"}})})
		})
	}
}
#[tokio::test]
async fn shared_german_templates_and_configured_from_reach_azure_transport() {
	let transport = Arc::new(Capture(Mutex::new(vec![])));
	let sender = AzureMail::new(
		"https://synthetic.communication.azure.com".parse().unwrap(),
		"chor@liedertafel-mining.at".into(),
		Arc::new(Token),
		transport.clone(),
		Duration::from_secs(1),
	)
	.unwrap();
	sender
		.send(mail::invitation_message(
			"  neu@liedertafel.test  ".into(),
			Some("  Jürgen Ünter  "),
			"Editor",
		))
		.await
		.unwrap();
	sender
		.send(mail::email_change_message(
			"neu@liedertafel.test".into(),
			"654321",
			10,
		))
		.await
		.unwrap();
	let requests = transport.0.lock().unwrap();
	assert_eq!(
		requests[0].body["senderAddress"],
		"chor@liedertafel-mining.at"
	);
	assert_eq!(
		requests[0].body["content"]["subject"],
		"Einladung zum Liedertafel-Archiv"
	);
	assert_eq!(
		requests[0].body["content"]["plainText"],
		"Guten Tag, Jürgen Ünter!\n\nSie wurden zum Liedertafel-Archiv eingeladen als Editor.\n\nMelden Sie sich mit dieser E-Mail-Adresse unter /anmelden/ an. Sie erhalten dann einen Code per E-Mail.\n\nFalls Sie keine Einladung erwarten, ignorieren Sie diese Nachricht.\n\nIhre Liedertafel Mining 1906"
	);
	assert_eq!(
		requests[1].body["content"]["subject"],
		"Neue E-Mail-Adresse für das Liedertafel-Archiv bestätigen"
	);
	assert_eq!(
		requests[1].body["content"]["plainText"],
		"Guten Tag!\n\nFür Ihr Archivkonto wurde eine neue E-Mail-Adresse hinterlegt.\n\nIhr Bestätigungscode lautet: 654321\n\nDer Code ist 10 Minuten gültig und kann einmal verwendet werden. Geben Sie ihn in der Mitgliederverwaltung ein, um die Änderung abzuschließen.\nFalls Sie keine Änderung erwarten, ignorieren Sie diese Nachricht: die Adresse wird ohne Code nicht übernommen.\n\nIhre Liedertafel Mining 1906"
	);
	for (role, designation) in [
		("Member", "Mitglied"),
		("Administrator", "Administrator"),
		("Unbekannt", "Mitglied"),
	] {
		assert!(
			mail::invitation_message("x@y.test".into(), None, role)
				.text
				.starts_with(&format!(
					"Guten Tag!\n\nSie wurden zum Liedertafel-Archiv eingeladen als {designation}."
				))
		);
	}
}

#[tokio::test]
async fn development_connection_string_signs_send_and_poll_at_delivery_boundary() {
	let transport = Arc::new(PollCapture(Mutex::new(vec![])));
	let sender = AzureMail::with_connection_string(
		"endpoint=https://synthetic.communication.azure.com/;accesskey=c3ludGhldGljLWtleQ==",
		"archiv@liedertafel-mining.at".into(),
		transport.clone(),
		Duration::from_secs(1),
	)
	.unwrap();
	sender
		.send(MailMessage {
			to: "member@example.test".into(),
			subject: "test".into(),
			text: "body".into(),
		})
		.await
		.unwrap();
	let requests = transport.0.lock().unwrap();
	assert_eq!(requests.len(), 2);
	assert_eq!(requests[1].method, "GET");
	assert!(
		requests[0].headers["authorization"]
			.to_str()
			.unwrap()
			.starts_with("HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=")
	);
	assert!(!requests[0].headers["x-ms-content-sha256"].is_empty());
	assert!(requests[0].headers["authorization"].is_sensitive());
	assert!(requests[0].headers["x-ms-content-sha256"].is_sensitive());
	// Independently reconstruct with ring, not the application's HMAC crate.
	use base64::{Engine, engine::general_purpose::STANDARD};
	use sha2::{Digest, Sha256};
	let request = &requests[0];
	assert_eq!(
		request.url.as_str(),
		"https://synthetic.communication.azure.com/emails:send?api-version=2023-03-31"
	);
	let date = request.headers["x-ms-date"].to_str().unwrap();
	let hash = STANDARD.encode(Sha256::digest(serde_json::to_vec(&request.body).unwrap()));
	assert_eq!(request.headers["x-ms-content-sha256"], hash);
	let canonical = format!(
		"POST\n/emails:send?api-version=2023-03-31\n{date};synthetic.communication.azure.com;{hash}"
	);
	let signature = ring::hmac::sign(
		&ring::hmac::Key::new(ring::hmac::HMAC_SHA256, b"synthetic-key"),
		canonical.as_bytes(),
	);
	assert!(
		request.headers["authorization"]
			.to_str()
			.unwrap()
			.ends_with(&STANDARD.encode(signature.as_ref()))
	);
	assert_eq!(
		requests[1].headers["x-ms-content-sha256"],
		STANDARD.encode(Sha256::digest([]))
	);
	assert_ne!(
		requests[1].headers["authorization"],
		requests[0].headers["authorization"]
	);
}

struct Delayed {
	started: tokio::sync::Notify,
	finished: std::sync::atomic::AtomicBool,
	delay: Duration,
}
impl AcsTransport for Delayed {
	fn send(
		&self,
		_: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>> {
		Box::pin(async {
			self.started.notify_one();
			tokio::time::sleep(self.delay).await;
			self.finished
				.store(true, std::sync::atomic::Ordering::Relaxed);
			Ok(AcsResponse {
				status: 202,
				operation_location: None,
				retry_after: Duration::ZERO,
				body: json!({"status":"Succeeded"}),
			})
		})
	}
}

#[tokio::test]
async fn delayed_handoff_timeout_and_caller_cancellation_are_distinct() {
	for (delay, timeout, should_timeout) in [
		(Duration::from_millis(20), Duration::from_secs(1), false),
		(Duration::from_secs(60), Duration::from_millis(30), true),
	] {
		let transport = Arc::new(Delayed {
			started: tokio::sync::Notify::new(),
			finished: false.into(),
			delay,
		});
		let sender = AzureMail::new(
			"https://synthetic.communication.azure.com".parse().unwrap(),
			"from@example.test".into(),
			Arc::new(Token),
			transport.clone(),
			timeout,
		)
		.unwrap();
		let result = sender
			.send(mail::test_message("to@example.test".into()))
			.await;
		assert_eq!(matches!(result, Err(MailError::Timeout)), should_timeout);
		assert_eq!(
			transport
				.finished
				.load(std::sync::atomic::Ordering::Relaxed),
			!should_timeout
		);
	}
	let transport = Arc::new(Delayed {
		started: tokio::sync::Notify::new(),
		finished: false.into(),
		delay: Duration::from_secs(60),
	});
	let sender = AzureMail::new(
		"https://synthetic.communication.azure.com".parse().unwrap(),
		"from@example.test".into(),
		Arc::new(Token),
		transport.clone(),
		Duration::from_secs(30),
	)
	.unwrap();
	let task = tokio::spawn(async move {
		sender
			.send(mail::test_message("to@example.test".into()))
			.await
	});
	transport.started.notified().await;
	task.abort();
	assert!(task.await.unwrap_err().is_cancelled());
	assert!(
		!transport
			.finished
			.load(std::sync::atomic::Ordering::Relaxed)
	);
}

struct Rejected;
impl AcsTransport for Rejected {
	fn send(
		&self,
		_: AcsRequest,
	) -> Pin<Box<dyn Future<Output = Result<AcsResponse, MailError>> + Send + '_>> {
		Box::pin(async {
			Ok(AcsResponse {
				status: 403,
				operation_location: None,
				retry_after: Duration::ZERO,
				body: json!({"id":"00000000-0000-0000-0000-000000000222","error":{"code":"SenderNotAllowed","message":"secret@example.test 123456 Anmeldecode lautet"}}),
			})
		})
	}
}
#[derive(Clone)]
struct Log(Arc<Mutex<Vec<u8>>>);
impl std::io::Write for Log {
	fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
		self.0.lock().unwrap().extend_from_slice(bytes);
		Ok(bytes.len())
	}
	fn flush(&mut self) -> std::io::Result<()> {
		Ok(())
	}
}

#[tokio::test]
async fn azure_diagnostics_log_safe_operation_status_code_but_never_content() {
	let bytes = Arc::new(Mutex::new(vec![]));
	let writer = Log(bytes.clone());
	let subscriber = tracing_subscriber::fmt()
		.without_time()
		.with_ansi(false)
		.with_writer(move || writer.clone())
		.finish();
	let _guard = tracing::subscriber::set_default(subscriber);
	let transport = Arc::new(Capture(Mutex::new(vec![])));
	let sender = AzureMail::new(
		"https://synthetic.communication.azure.com".parse().unwrap(),
		"from@example.test".into(),
		Arc::new(Token),
		transport,
		Duration::from_secs(1),
	)
	.unwrap();
	sender
		.send(mail::code_message(
			"secret@example.test".into(),
			"123456",
			10,
		))
		.await
		.unwrap();
	let sender = AzureMail::new(
		"https://synthetic.communication.azure.com".parse().unwrap(),
		"from@example.test".into(),
		Arc::new(Token),
		Arc::new(Rejected),
		Duration::from_secs(1),
	)
	.unwrap();
	let error = sender
		.send(mail::code_message(
			"secret@example.test".into(),
			"123456",
			10,
		))
		.await
		.unwrap_err();
	assert!(matches!(error, MailError::Azure { status: 403, .. }));
	let logs = String::from_utf8(bytes.lock().unwrap().clone()).unwrap();
	assert!(logs.contains("00000000-0000-0000-0000-000000000222"));
	assert!(logs.contains("403"));
	assert!(logs.contains("SenderNotAllowed"));
	for secret in ["secret@example.test", "123456", "Anmeldecode lautet"] {
		assert!(!logs.contains(secret));
	}
}

#[tokio::test]
async fn test_mail_command_is_development_azure_only_and_uses_marked_template() {
	let transport = Arc::new(Capture(Mutex::new(vec![])));
	let sender = AzureMail::new(
		"https://synthetic.communication.azure.com".parse().unwrap(),
		"from@example.test".into(),
		Arc::new(Token),
		transport.clone(),
		Duration::from_secs(1),
	)
	.unwrap();
	use archive_server::auth::operator::send_test_mail;
	for (dev, azure, args) in [
		(false, true, vec!["member@example.test".into()]),
		(true, false, vec!["member@example.test".into()]),
		(true, true, vec![]),
		(true, true, vec!["invalid".into()]),
	] {
		assert!(send_test_mail(dev, &args, azure, &sender).await.is_err());
	}
	assert!(transport.0.lock().unwrap().is_empty());
	send_test_mail(true, &["member@example.test".into()], true, &sender)
		.await
		.unwrap();
	let requests = transport.0.lock().unwrap();
	let body = &requests[0].body["content"];
	assert_eq!(body["subject"], "Archiv: Azure-Versandtest");
	assert!(
		body["plainText"]
			.as_str()
			.unwrap()
			.contains("Testnachricht")
	);
	assert!(!body["plainText"].as_str().unwrap().contains("Code"));
	let signin = mail::code_message("member@example.test".into(), "123456", 10);
	assert_eq!(
		signin.text,
		"Guten Tag!\n\nIhr Anmeldecode lautet: 123456\n\nDer Code ist 10 Minuten gültig und kann einmal verwendet werden.\nFalls Sie keinen Code angefordert haben, ignorieren Sie diese Nachricht.\n\nIhre Liedertafel Mining 1906"
	);
}

#[tokio::test]
async fn production_guards_and_development_sender_selection_at_startup_without_sends() {
	for (dev, provider, endpoint, connection, valid) in [
		(true, "", None, None, true),
		(false, "Smtp", None, None, false),
		(
			false,
			"Azure",
			Some("https://synthetic.communication.azure.com"),
			None,
			true,
		),
		(
			false,
			"Azure",
			None,
			Some(
				"endpoint=https://synthetic.communication.azure.com;accesskey=c3ludGhldGljLWtleQ==",
			),
			false,
		),
		(true, "Smtp", None, None, true),
		(
			true,
			"Azure",
			None,
			Some(
				"endpoint=https://synthetic.communication.azure.com;accesskey=c3ludGhldGljLWtleQ==",
			),
			true,
		),
		(
			true,
			"Azure",
			Some("https://synthetic.communication.azure.com"),
			None,
			true,
		),
		(true, "Azure", None, Some("invalid"), false),
	] {
		let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
		let port = listener.local_addr().unwrap().port();
		let mut command = tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"));
		command
			.env(
				"ARCHIVE_ENVIRONMENT",
				if dev { "Development" } else { "Production" },
			)
			.env("PORT", port.to_string())
			.env("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
			.env("Mail__Provider", provider)
			.env("Authentication__PasskeyRelyingPartyId", "localhost")
			.env("Authentication__PasskeyOrigins__0", "https://localhost")
			.env_remove("ConnectionStrings__archive-db")
			.env_remove("Mail__AzureEndpoint")
			.env_remove("Mail__AzureConnectionString")
			.kill_on_drop(true);
		if let Some(endpoint) = endpoint {
			command.env("Mail__AzureEndpoint", endpoint);
		}
		if let Some(connection) = connection {
			command.env("Mail__AzureConnectionString", connection);
		}
		let output = tokio::time::timeout(Duration::from_secs(10), command.output())
			.await
			.unwrap()
			.unwrap();
		let error = String::from_utf8_lossy(&output.stderr);
		assert!(!output.status.success());
		assert_eq!(
			error.contains("could not listen"),
			valid,
			"{dev} {provider}: {error}"
		);
		assert!(!error.contains("c3ludGhldGljLWtleQ=="));
	}
	for (dev, provider, args) in [
		(false, "Azure", vec!["member@example.test"]),
		(true, "Smtp", vec!["member@example.test"]),
		(true, "Azure", vec![]),
		(true, "Azure", vec!["invalid"]),
	] {
		let output = tokio::time::timeout(
			Duration::from_secs(10),
			tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
				.arg("--send-test-mail")
				.args(args)
				.env(
					"ARCHIVE_ENVIRONMENT",
					if dev { "Development" } else { "Production" },
				)
				.env("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
				.env("Mail__Provider", provider)
				.kill_on_drop(true)
				.output(),
		)
		.await
		.unwrap()
		.unwrap();
		assert!(!output.status.success());
		assert!(!String::from_utf8_lossy(&output.stderr).contains("member@example.test"));
	}
}
