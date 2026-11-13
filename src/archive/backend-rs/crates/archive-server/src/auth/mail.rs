use lettre::{AsyncSmtpTransport, AsyncTransport, Message, Tokio1Executor};
use std::{future::Future, pin::Pin};
pub mod acs;

pub fn from_env(development: bool) -> Result<std::sync::Arc<dyn Mail>, super::AuthError> {
	let azure = std::env::var("Mail__Provider").is_ok_and(|s| s.eq_ignore_ascii_case("Azure"));
	if !development
		&& (!azure
			|| std::env::var("Mail__AzureConnectionString").is_ok_and(|s| !s.trim().is_empty()))
	{
		return Err(super::AuthError::Configuration);
	}
	if azure {
		return acs::from_env(development);
	}
	Ok(std::sync::Arc::new(SmtpMail {
		host: std::env::var("Mail__Host").unwrap_or_else(|_| "localhost".into()),
		port: std::env::var("Mail__Port")
			.ok()
			.and_then(|s| s.parse().ok())
			.unwrap_or(1025),
		from: std::env::var("Auth__MailFrom")
			.unwrap_or_else(|_| "archiv@liedertafel-mining.at".into()),
	}))
}

/// Shared delivery seam for #222; recipients/codes never enter Debug or telemetry.
pub struct MailMessage {
	pub to: String,
	pub subject: String,
	pub text: String,
}
#[derive(Debug, thiserror::Error)]
pub enum MailError {
	#[error("Azure mail handoff failed")]
	Azure {
		status: u16,
		error_code: Option<String>,
		operation_id: Option<uuid::Uuid>,
	},
	#[error("mail delivery failed")]
	Delivery,
	#[error("mail delivery timed out")]
	Timeout,
}
pub trait Mail: Send + Sync {
	fn send<'a>(
		&'a self,
		message: MailMessage,
	) -> Pin<Box<dyn Future<Output = Result<(), MailError>> + Send + 'a>>;
}

pub struct SmtpMail {
	pub host: String,
	pub port: u16,
	pub from: String,
}
impl Mail for SmtpMail {
	fn send<'a>(
		&'a self,
		message: MailMessage,
	) -> Pin<Box<dyn Future<Output = Result<(), MailError>> + Send + 'a>> {
		Box::pin(async move {
			let message = Message::builder()
				.from(self.from.parse().map_err(|_| MailError::Delivery)?)
				.to(message.to.parse().map_err(|_| MailError::Delivery)?)
				.subject(message.subject)
				.body(message.text)
				.map_err(|_| MailError::Delivery)?;
			let transport = AsyncSmtpTransport::<Tokio1Executor>::builder_dangerous(&self.host)
				.port(self.port)
				.timeout(Some(std::time::Duration::from_secs(10)))
				.build();
			transport
				.send(message)
				.await
				.map_err(|_| MailError::Delivery)?;
			tracing::info!("Mail accepted by SMTP");
			Ok(())
		})
	}
}

pub fn code_message(email: String, code: &str, minutes: i64) -> MailMessage {
	MailMessage {
		to: email.trim().into(),
		subject: "Ihr Anmeldecode für das Liedertafel-Archiv".into(),
		text: format!(
			"Guten Tag!\n\nIhr Anmeldecode lautet: {code}\n\nDer Code ist {minutes} Minuten gültig und kann einmal verwendet werden.\nFalls Sie keinen Code angefordert haben, ignorieren Sie diese Nachricht.\n\nIhre Liedertafel Mining 1906"
		),
	}
}

pub fn test_message(email: String) -> MailMessage {
	MailMessage{to:email.trim().into(),subject:"Archiv: Azure-Versandtest".into(),text:"Guten Tag!\n\nDies ist eine Testnachricht des Liedertafel-Archivs zur Prüfung des E-Mail-Versands.\n\nBitte antworten Sie nicht auf diese Nachricht.\n\nIhre Liedertafel Mining 1906".into()}
}

pub fn invitation_message(email: String, name: Option<&str>, role: &str) -> MailMessage {
	let greeting = name
		.map(str::trim)
		.filter(|s| !s.is_empty())
		.map_or_else(|| "Guten Tag!".into(), |name| format!("Guten Tag, {name}!"));
	let role = match role {
		"Editor" => "als Editor",
		"Administrator" => "als Administrator",
		_ => "als Mitglied",
	};
	MailMessage {
		to: email.trim().into(),
		subject: "Einladung zum Liedertafel-Archiv".into(),
		text: format!(
			"{greeting}\n\nSie wurden zum Liedertafel-Archiv eingeladen {role}.\n\nMelden Sie sich mit dieser E-Mail-Adresse unter /anmelden/ an. Sie erhalten dann einen Code per E-Mail.\n\nFalls Sie keine Einladung erwarten, ignorieren Sie diese Nachricht.\n\nIhre Liedertafel Mining 1906"
		),
	}
}

pub fn email_change_message(email: String, code: &str, minutes: i64) -> MailMessage {
	MailMessage {
		to: email.trim().into(),
		subject: "Neue E-Mail-Adresse für das Liedertafel-Archiv bestätigen".into(),
		text: format!(
			"Guten Tag!\n\nFür Ihr Archivkonto wurde eine neue E-Mail-Adresse hinterlegt.\n\nIhr Bestätigungscode lautet: {code}\n\nDer Code ist {minutes} Minuten gültig und kann einmal verwendet werden. Geben Sie ihn in der Mitgliederverwaltung ein, um die Änderung abzuschließen.\nFalls Sie keine Änderung erwarten, ignorieren Sie diese Nachricht: die Adresse wird ohne Code nicht übernommen.\n\nIhre Liedertafel Mining 1906"
		),
	}
}
