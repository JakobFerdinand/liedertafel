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

	/// <summary>
	/// Sends the explicit Azure integration-test message (ARC-010). Only the
	/// <c>--send-test-mail</c> operator command calls this; no sign-in,
	/// invitation, or address-change flow uses it.
	/// </summary>
	Task SendTestMessageAsync(string email, CancellationToken cancellationToken);
}

public sealed class SmtpSignInCodeSender(IConfiguration configuration, ILogger<SmtpSignInCodeSender> logger) : IArchiveMailSender
{
	private string From => configuration["Auth:MailFrom"] ?? ArchiveMailTemplates.DefaultFrom;

	public Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.send", ArchiveMailTemplates.SignInCode(code, lifetime), email, cancellationToken,
			"Sign-in code sent (lifetime {LifetimeMinutes} minutes)", (int)lifetime.TotalMinutes);

	public Task SendInvitationAsync(string email, string? displayName, string role, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.invite.send", ArchiveMailTemplates.Invitation(displayName, role), email, cancellationToken,
			"Invitation mail accepted by sender (role {Role})", role);

	public Task SendEmailChangeCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.email_change.send", ArchiveMailTemplates.EmailChangeCode(code, lifetime), email, cancellationToken,
			"Email change code sent (lifetime {LifetimeMinutes} minutes)", (int)lifetime.TotalMinutes);

	public Task SendTestMessageAsync(string email, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.test.send", ArchiveMailTemplates.AzureSendTest(), email, cancellationToken,
			"Test mail accepted by sender");

	private async Task SendAsync(
		string activityName, ArchiveMailMessage template, string email,
		CancellationToken cancellationToken, string logMessage, object? logArgument = null)
	{
		using var mail = new MimeMessage();
		mail.From.Add(new MailboxAddress("Liedertafel Archiv", From));
		mail.To.Add(MailboxAddress.Parse(email.Trim()));
		mail.Subject = template.Subject;
		mail.Body = new TextPart("plain") { Text = template.PlainTextBody };
		using var smtp = new SmtpClient { Timeout = 10000 };
		using var mailActivity = Extensions.Activities.StartActivity(activityName, ActivityKind.Client);
		await smtp.ConnectAsync(
			configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required."),
			configuration.GetValue<int>("Mail:Port"),
			SecureSocketOptions.None,
			cancellationToken);
		await smtp.SendAsync(mail, cancellationToken);
		await smtp.DisconnectAsync(true, cancellationToken);
		// Never log the recipient address local part or the code.
		if (logArgument is not null)
			logger.LogInformation(logMessage, logArgument);
		else
			logger.LogInformation(logMessage);
	}
}
