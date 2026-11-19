use archive_server::auth::mail::{
	Mail, SmtpMail, code_message, email_change_message, invitation_message,
};
use serde_json::Value;
use std::time::Duration;

#[tokio::test]
#[ignore = "requires a fresh local Mailpit on dedicated SMTP and HTTP ports"]
async fn mailpit_decodes_german_auth_mail() {
	let port = |key| std::env::var(key).unwrap().parse::<u16>().unwrap();
	let mail = SmtpMail {
		host: "127.0.0.1".into(),
		port: port("ARCHIVE_TEST_MAILPIT_SMTP_PORT"),
		from: "archive@example.test".into(),
	};
	let api = format!(
		"http://127.0.0.1:{}/api/v1",
		port("ARCHIVE_TEST_MAILPIT_HTTP_PORT")
	);
	let client = reqwest::Client::builder()
		.no_proxy()
		.redirect(reqwest::redirect::Policy::none())
		.timeout(Duration::from_secs(3))
		.build()
		.unwrap();
	let get = |path: String| async {
		client
			.get(path)
			.send()
			.await
			.unwrap()
			.error_for_status()
			.unwrap()
			.json::<Value>()
			.await
			.unwrap()
	};
	let messages = || get(format!("{api}/messages"));
	assert_eq!(messages().await["total"], 0, "use an empty Mailpit");

	let recipient = "member@example.test";
	for (message, subject, expected) in [
		(
			email_change_message(recipient.into(), "012345", 10),
			"Neue E-Mail-Adresse für das Liedertafel-Archiv bestätigen",
			&[
				"Ihr Bestätigungscode lautet: 012345",
				"Für Ihr Archivkonto wurde eine neue E-Mail-Adresse hinterlegt.",
			],
		),
		(
			code_message(recipient.into(), "987654", 10),
			"Ihr Anmeldecode für das Liedertafel-Archiv",
			&[
				"Ihr Anmeldecode lautet: 987654",
				"Der Code ist 10 Minuten gültig und kann einmal verwendet werden.",
			],
		),
		(
			invitation_message(recipient.into(), Some("Änne"), "Editor"),
			"Einladung zum Liedertafel-Archiv",
			&[
				"Guten Tag, Änne!",
				"Melden Sie sich mit dieser E-Mail-Adresse unter /anmelden/ an.",
			],
		),
	] {
		mail.send(message).await.unwrap();
		let decoded = tokio::time::timeout(Duration::from_secs(5), async {
			loop {
				if let Some(row) = messages().await["messages"]
					.as_array()
					.unwrap()
					.iter()
					.find(|row| row["Subject"] == subject)
				{
					return get(format!("{api}/message/{}", row["ID"].as_str().unwrap())).await;
				}
				tokio::time::sleep(Duration::from_millis(50)).await;
			}
		})
		.await
		.expect("Mailpit did not receive the message with the expected subject");
		let text = decoded["Text"].as_str().expect("decoded plain-text body");
		for phrase in expected {
			assert!(
				text.contains(phrase),
				"Mailpit did not decode {phrase:?} in {subject:?}: {text:?}"
			);
		}
	}
}
