using System.Diagnostics;
using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

/// <summary>
/// Failure of an Azure Communication Services send. Carries only the service
/// status, error code and operation id: never the recipient address, subject,
/// body, or code contents. Services treat it like any transport failure
/// (invitations stay retryable; code requests surface a German error and keep
/// safe resend semantics).
/// </summary>
public sealed class AzureMailSendException(int? status, string? errorCode, string? operationId, string message)
	: Exception(message)
{
	public int? Status { get; } = status;

	public string? ErrorCode { get; } = errorCode;

	public string? OperationId { get; } = operationId;
}

/// <summary>
/// Thin seam over Azure Communication Services Email. The production
/// implementation wraps <see cref="EmailClient"/>; tests substitute a fake to
/// simulate sender rejection, throttling, and delayed delivery without
/// network access.
/// </summary>
public interface IAzureEmailTransport
{
	/// <summary>
	/// Sends one plain-text mail and waits for the service to accept it for
	/// delivery. Success means handoff, never inbox arrival.
	/// </summary>
	/// <returns>The service operation id (GUID, safe to log).</returns>
	/// <exception cref="AzureMailSendException">Rejection, throttling, failed handoff, or timeout.</exception>
	Task<string> SendAsync(string from, string to, string subject, string plainTextBody, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IAzureEmailTransport"/> on top of
/// <see cref="EmailClient"/>. Bounded by the caller's cancellation token:
/// the initial send fails fast on sender rejection (4xx) and throttling
/// (429, retried only by the SDK's bounded policy), then delivery polling is
/// capped by <see cref="MailOptions.SendTimeout"/>. No application-level
/// retry loop: resend policy belongs to the calling services.
/// </summary>
public sealed class EmailClientTransport(EmailClient client, TimeSpan pollInterval) : IAzureEmailTransport
{
	public static EmailClientTransport Create(MailOptions mail) => new(CreateClient(mail), TimeSpan.FromSeconds(2));

	public static EmailClient CreateClient(MailOptions mail)
	{
		if (!string.IsNullOrWhiteSpace(mail.AzureConnectionString))
			return new EmailClient(mail.AzureConnectionString);
		if (Uri.TryCreate(mail.AzureEndpoint, UriKind.Absolute, out var endpoint))
			return new EmailClient(endpoint, new DefaultAzureCredential(), new EmailClientOptions
			{
				// Content logging stays off: troubleshooting uses only the
				// operation id, status and error code.
				Diagnostics = { IsLoggingContentEnabled = false },
			});
		throw new InvalidOperationException(
			"Mail:AzureEndpoint or Mail:AzureConnectionString is required for the Azure mail provider.");
	}

	public async Task<string> SendAsync(
		string from, string to, string subject, string plainTextBody, CancellationToken cancellationToken)
	{
		var message = new EmailMessage(
			senderAddress: from,
			recipientAddress: to,
			content: new EmailContent(subject) { PlainText = plainTextBody });
		EmailSendOperation operation;
		try
		{
			operation = await client.SendAsync(WaitUntil.Started, message, cancellationToken);
		}
		catch (RequestFailedException exception)
		{
			throw new AzureMailSendException(
				exception.Status, exception.ErrorCode, null,
				$"Azure mail send rejected (status {exception.Status}, code {exception.ErrorCode}).");
		}
		while (!operation.HasCompleted)
		{
			await Task.Delay(pollInterval, cancellationToken);
			try
			{
				await operation.UpdateStatusAsync(cancellationToken);
			}
			catch (RequestFailedException exception)
			{
				throw new AzureMailSendException(
					exception.Status, exception.ErrorCode, operation.Id,
					$"Azure mail status check failed (status {exception.Status}, code {exception.ErrorCode}).");
			}
		}
		if (!operation.HasValue)
			throw new AzureMailSendException(
				null, null, operation.Id, "Azure mail send completed without a result.");
		// Note: EmailSendResult exposes only Id and Status publicly; failure
		// details beyond the terminal status live in Monitor/Event Grid, so
		// the status plus operation id is the complete safe diagnostic here.
		if (operation.Value.Status != EmailSendStatus.Succeeded)
			throw new AzureMailSendException(
				null, operation.Value.Status.ToString(), operation.Id,
				$"Azure mail not accepted (status {operation.Value.Status}).");
		return operation.Id;
	}
}

/// <summary>
/// <see cref="IArchiveMailSender"/> over Azure Communication Services Email
/// (ARC-010). Renders the same shared German templates as the SMTP sender;
/// the sender address must be <c>archiv@liedertafel-mining.at</c> on the
/// verified, linked domain. Logs only the operation id and failure codes:
/// never recipient addresses, codes, subjects, or bodies.
/// </summary>
public sealed class AzureCommunicationMailSender(
	IConfiguration configuration,
	IOptions<MailOptions> mailOptions,
	IAzureEmailTransport transport,
	ILogger<AzureCommunicationMailSender> logger) : IArchiveMailSender
{
	private readonly TimeSpan timeout = mailOptions.Value.SendTimeout > TimeSpan.Zero
		? mailOptions.Value.SendTimeout
		: TimeSpan.FromSeconds(45);

	private string From => configuration["Auth:MailFrom"] ?? ArchiveMailTemplates.DefaultFrom;

	public Task SendSignInCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.send", ArchiveMailTemplates.SignInCode(code, lifetime), email, cancellationToken);

	public Task SendInvitationAsync(string email, string? displayName, string role, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.invite.send", ArchiveMailTemplates.Invitation(displayName, role), email, cancellationToken);

	public Task SendEmailChangeCodeAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.email_change.send", ArchiveMailTemplates.EmailChangeCode(code, lifetime), email, cancellationToken);

	public Task SendTestMessageAsync(string email, CancellationToken cancellationToken) =>
		SendAsync("archive.mail.test.send", ArchiveMailTemplates.AzureSendTest(), email, cancellationToken);

	private async Task SendAsync(
		string activityName, ArchiveMailMessage template, string email, CancellationToken cancellationToken)
	{
		using var mailActivity = Extensions.Activities.StartActivity(activityName, ActivityKind.Client);
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(timeout);
		try
		{
			var operationId = await transport.SendAsync(
				From, email.Trim(), template.Subject, template.PlainTextBody, timeoutSource.Token);
			// The operation id is a service GUID: safe alongside telemetry.
			logger.LogInformation("Azure mail accepted for delivery (operation {OperationId})", operationId);
		}
		catch (AzureMailSendException exception)
		{
			logger.LogError(
				"Azure mail failed (status {Status}, code {ErrorCode}, operation {OperationId})",
				exception.Status, exception.ErrorCode, exception.OperationId);
			throw;
		}
		catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			logger.LogError("Azure mail timed out ({ExceptionType})", exception.GetType().Name);
			throw new AzureMailSendException(null, "SendTimeout", null, "Azure mail send timed out.");
		}
		catch (Exception exception)
		{
			logger.LogError("Azure mail failed ({ExceptionType})", exception.GetType().Name);
			throw;
		}
	}
}

/// <summary>
/// Startup safety for mail configuration (ARC-010): local SMTP capture must
/// never serve production, and sender secrets must never reach production.
/// </summary>
public static class MailGuards
{
	public static void ValidateProductionMail(IConfiguration configuration, IHostEnvironment environment)
	{
		if (!environment.IsProduction())
			return;
		var mail = configuration.GetSection(MailOptions.SectionName).Get<MailOptions>() ?? new MailOptions();
		if (!mail.IsAzure)
			throw new InvalidOperationException(
				"Production requires Mail:Provider=Azure; local SMTP capture must never serve production mail.");
		if (!string.IsNullOrWhiteSpace(mail.AzureConnectionString))
			throw new InvalidOperationException(
				"Production forbids Mail:AzureConnectionString; the runtime managed identity sends keyless via DefaultAzureCredential.");
	}
}
