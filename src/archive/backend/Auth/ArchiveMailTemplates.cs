namespace Archive.Backend.Auth;

/// <summary>
/// A rendered German mail (subject plus plain-text body), shared by the local
/// SMTP capture sender and the Azure Communication Services sender so both
/// transports always emit identical copy.
/// </summary>
public sealed record ArchiveMailMessage(string Subject, string PlainTextBody);

/// <summary>
/// Single source of the German authentication mail copy (ARC-005 contract,
/// ARC-010 transport). Pure functions: no configuration, logging, or
/// transport. The plaintext code is interpolated by the caller and must never
/// be logged by senders.
/// </summary>
public static class ArchiveMailTemplates
{
	public const string DefaultFrom = "archiv@liedertafel-mining.at";

	public static ArchiveMailMessage SignInCode(string code, TimeSpan lifetime)
	{
		var minutes = (int)lifetime.TotalMinutes;
		return new ArchiveMailMessage(
			"Ihr Anmeldecode für das Liedertafel-Archiv",
			$"Guten Tag!\n\nIhr Anmeldecode lautet: {code}\n\n"
				+ $"Der Code ist {minutes} Minuten gültig und kann einmal verwendet werden.\n"
				+ $"Falls Sie keinen Code angefordert haben, ignorieren Sie diese Nachricht.\n\n"
				+ $"Ihre Liedertafel Mining 1906");
	}

	public static ArchiveMailMessage Invitation(string? displayName, string role)
	{
		var anrede = string.IsNullOrWhiteSpace(displayName) ? "Guten Tag!" : $"Guten Tag, {displayName.Trim()}!";
		var rollenbezeichnung = role switch
		{
			ArchiveRoles.Editor => "als Editor",
			ArchiveRoles.Administrator => "als Administrator",
			_ => "als Mitglied",
		};
		return new ArchiveMailMessage(
			"Einladung zum Liedertafel-Archiv",
			$"{anrede}\n\nSie wurden zum Liedertafel-Archiv eingeladen {rollenbezeichnung}.\n\n"
				+ $"Melden Sie sich mit dieser E-Mail-Adresse unter /anmelden/ an. "
				+ $"Sie erhalten dann einen Code per E-Mail.\n\n"
				+ $"Falls Sie keine Einladung erwarten, ignorieren Sie diese Nachricht.\n\n"
				+ $"Ihre Liedertafel Mining 1906");
	}

	public static ArchiveMailMessage EmailChangeCode(string code, TimeSpan lifetime)
	{
		var minutes = (int)lifetime.TotalMinutes;
		return new ArchiveMailMessage(
			"Neue E-Mail-Adresse für das Liedertafel-Archiv bestätigen",
			$"Guten Tag!\n\nFür Ihr Archivkonto wurde eine neue E-Mail-Adresse hinterlegt.\n\n"
				+ $"Ihr Bestätigungscode lautet: {code}\n\n"
				+ $"Der Code ist {minutes} Minuten gültig und kann einmal verwendet werden. "
				+ $"Geben Sie ihn in der Mitgliederverwaltung ein, um die Änderung abzuschließen.\n"
				+ $"Falls Sie keine Änderung erwarten, ignorieren Sie diese Nachricht: "
				+ $"die Adresse wird ohne Code nicht übernommen.\n\n"
				+ $"Ihre Liedertafel Mining 1906");
	}

	/// <summary>
	/// Explicit integration-test message (ARC-010): only sent by the
	/// <c>--send-test-mail</c> operator command, never by sign-in, invitation,
	/// or address-change flows.
	/// </summary>
	public static ArchiveMailMessage AzureSendTest() => new(
		"Archiv: Azure-Versandtest",
		$"Guten Tag!\n\nDies ist eine Testnachricht des Liedertafel-Archivs zur Prüfung des E-Mail-Versands.\n\n"
			+ $"Bitte antworten Sie nicht auf diese Nachricht.\n\n"
			+ $"Ihre Liedertafel Mining 1906");
}
