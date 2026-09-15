using System.Diagnostics;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Archive.Backend.Auth;

/// <summary>
/// Mail-sender contract for authentication codes and member invitations. Local
/// development uses SMTP capture (Mailpit via <c>Mail:Host</c>/<c>Mail:Port</c>);
/// ARC-010 provides the Azure Communication Services implementation behind this
/// same interface.
/// </summary>
public interface IArchiveMailSender
{
	Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken);

	/// <summary>
	/// Sends the German invitation mail. Success means the sender accepted the
	/// message for delivery; it never promises arrival in the inbox.
	/// </summary>
	Task SendInvitationAsync(string email, string? displayName, string role, CancellationToken cancellationToken);

	/// <summary>
	/// Sends the German address-change confirmation code to the <em>new</em>
	/// address (ARC-008). The code alone creates no session; only the admin
	/// confirm endpoint can apply it to the bound account.
	/// </summary>
	Task SendEmailChangeCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken);
}

public sealed class SmtpSignInCodeSender(IConfiguration configuration, ILogger<SmtpSignInCodeSender> logger) : IArchiveMailSender
{
	public async Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		var from = configuration["Auth:MailFrom"] ?? "archiv@liedertafel-mining.at";
		using var mail = new MimeMessage();
		mail.From.Add(new MailboxAddress("Liedertafel Archiv", from));
		mail.To.Add(MailboxAddress.Parse(email.Trim()));
		mail.Subject = "Ihr Anmeldecode für das Liedertafel-Archiv";
		var minutes = (int)lifetime.TotalMinutes;
		mail.Body = new TextPart("plain")
		{
			Text = $"Guten Tag!\n\nIhr Anmeldecode lautet: {code}\n\n"
				+ $"Der Code ist {minutes} Minuten gültig und kann einmal verwendet werden.\n"
				+ $"Falls Sie keinen Code angefordert haben, ignorieren Sie diese Nachricht.\n\n"
				+ $"Ihre Liedertafel Mining 1906",
		};
		using var smtp = new SmtpClient { Timeout = 10000 };
		using var mailActivity = Extensions.Activities.StartActivity("archive.mail.send", ActivityKind.Client);
		await smtp.ConnectAsync(
			configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required."),
			configuration.GetValue<int>("Mail:Port"),
			SecureSocketOptions.None,
			cancellationToken);
		await smtp.SendAsync(mail, cancellationToken);
		await smtp.DisconnectAsync(true, cancellationToken);
		// Never log the recipient address local part or the code.
		logger.LogInformation("Sign-in code sent (lifetime {LifetimeMinutes} minutes)", minutes);
	}

	public async Task SendInvitationAsync(string email, string? displayName, string role, CancellationToken cancellationToken)
	{
		var from = configuration["Auth:MailFrom"] ?? "archiv@liedertafel-mining.at";
		using var mail = new MimeMessage();
		mail.From.Add(new MailboxAddress("Liedertafel Archiv", from));
		mail.To.Add(MailboxAddress.Parse(email.Trim()));
		mail.Subject = "Einladung zum Liedertafel-Archiv";
		var anrede = string.IsNullOrWhiteSpace(displayName) ? "Guten Tag!" : $"Guten Tag, {displayName.Trim()}!";
		var rollenbezeichnung = role switch
		{
			ArchiveRoles.Editor => "als Editor",
			ArchiveRoles.Administrator => "als Administrator",
			_ => "als Mitglied",
		};
		mail.Body = new TextPart("plain")
		{
			Text = $"{anrede}\n\nSie wurden zum Liedertafel-Archiv eingeladen {rollenbezeichnung}.\n\n"
				+ $"Melden Sie sich mit dieser E-Mail-Adresse unter /anmelden/ an. "
				+ $"Sie erhalten dann einen Code per E-Mail.\n\n"
				+ $"Falls Sie keine Einladung erwarten, ignorieren Sie diese Nachricht.\n\n"
				+ $"Ihre Liedertafel Mining 1906",
		};
		using var smtp = new SmtpClient { Timeout = 10000 };
		using var mailActivity = Extensions.Activities.StartActivity("archive.mail.invite.send", ActivityKind.Client);
		await smtp.ConnectAsync(
			configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required."),
			configuration.GetValue<int>("Mail:Port"),
			SecureSocketOptions.None,
			cancellationToken);
		await smtp.SendAsync(mail, cancellationToken);
		await smtp.DisconnectAsync(true, cancellationToken);
		// Never log the recipient address local part.
		logger.LogInformation("Invitation mail accepted by sender (role {Role})", role);
	}

	public async Task SendEmailChangeCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		var from = configuration["Auth:MailFrom"] ?? "archiv@liedertafel-mining.at";
		using var mail = new MimeMessage();
		mail.From.Add(new MailboxAddress("Liedertafel Archiv", from));
		mail.To.Add(MailboxAddress.Parse(email.Trim()));
		mail.Subject = "Neue E-Mail-Adresse für das Liedertafel-Archiv bestätigen";
		var minutes = (int)lifetime.TotalMinutes;
		mail.Body = new TextPart("plain")
		{
			Text = $"Guten Tag!\n\nFür Ihr Archivkonto wurde eine neue E-Mail-Adresse hinterlegt.\n\n"
				+ $"Ihr Bestätigungscode lautet: {code}\n\n"
				+ $"Der Code ist {minutes} Minuten gültig und kann einmal verwendet werden. "
				+ $"Geben Sie ihn in der Mitgliederverwaltung ein, um die Änderung abzuschließen.\n"
				+ $"Falls Sie keine Änderung erwarten, ignorieren Sie diese Nachricht: "
				+ $"die Adresse wird ohne Code nicht übernommen.\n\n"
				+ $"Ihre Liedertafel Mining 1906",
		};
		using var smtp = new SmtpClient { Timeout = 10000 };
		using var mailActivity = Extensions.Activities.StartActivity("archive.mail.email_change.send", ActivityKind.Client);
		await smtp.ConnectAsync(
			configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required."),
			configuration.GetValue<int>("Mail:Port"),
			SecureSocketOptions.None,
			cancellationToken);
		await smtp.SendAsync(mail, cancellationToken);
		await smtp.DisconnectAsync(true, cancellationToken);
		// Never log the recipient address local part or the code.
		logger.LogInformation("Email change code sent (lifetime {LifetimeMinutes} minutes)", minutes);
	}
}
