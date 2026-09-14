using System.Diagnostics;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Archive.Backend.Auth;

/// <summary>
/// Mail-sender contract for authentication codes. Local development uses SMTP
/// capture (Mailpit via <c>Mail:Host</c>/<c>Mail:Port</c>); ARC-010 provides the
/// Azure Communication Services implementation behind this same interface.
/// </summary>
public interface IArchiveMailSender
{
	Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken);
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
}
