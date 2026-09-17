namespace Archive.Backend.Auth;

/// <summary>
/// Mail transport selection (ARC-010). <c>Smtp</c> is the default and keeps
/// ordinary AppHost startup on local Mailpit capture. <c>Azure</c> selects
/// Azure Communication Services Email with the verified choir-domain sender;
/// it is the only provider allowed in production and the only path for the
/// explicit <c>--send-test-mail</c> integration run.
/// </summary>
public sealed class MailOptions
{
	public const string SectionName = "Mail";

	/// <summary><c>Smtp</c> (default, local capture) or <c>Azure</c> (real sending).</summary>
	public string Provider { get; set; } = "Smtp";

	/// <summary>
	/// Communication Services endpoint for keyless sends, e.g.
	/// <c>https://acs-liedertafel-archive.communication.azure.com</c>.
	/// Production sends use this with the runtime managed identity.
	/// </summary>
	public string? AzureEndpoint { get; set; }

	/// <summary>
	/// Local development credential for the explicit integration run only.
	/// Forbidden outside Development; production sends keyless instead.
	/// </summary>
	public string? AzureConnectionString { get; set; }

	/// <summary>Bounded total budget per send, including delivery polling.</summary>
	public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(45);

	public bool IsAzure => string.Equals(Provider?.Trim(), "Azure", StringComparison.OrdinalIgnoreCase);
}
