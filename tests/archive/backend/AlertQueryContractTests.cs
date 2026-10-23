using System.Text.RegularExpressions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-042 (#149): the Azure alert queries in
/// <c>infrastructure/archive/main.bicep</c> key on the exact console-log lines
/// the backend ships. The scheduled-query contract and the shipped log lines
/// must move together — a renamed log template must break the build the same
/// way a renamed deployment price does, before an alert silently stops
/// firing. These tests pin: the alert markers, per-item correlation markers,
/// the bounded workspace settings, and the maintenance-disabled default.
/// </summary>
public sealed class AlertQueryContractTests
{
	private static string Bicep() => File.ReadAllText(Path.Combine(
		ShippedConfiguration.RepositoryRoot(), "infrastructure", "archive", "main.bicep"));

	private static string Source(params string[] path) => File.ReadAllText(
		new[] { "src", "archive" }.Concat(path).Aggregate(ShippedConfiguration.RepositoryRoot(), Path.Combine));

	private static string ServiceDefaultsSource(string file) => Source("service-defaults", file);

	private static string BackendSource(string file) => Source("backend", file);

	[Fact]
	public void AlertsExistAsScheduledQueriesAndStartDisabledWithoutDestinations()
	{
		var bicep = Bicep();

		Assert.Contains("param maintainerAlertEmails array = []", bicep);
		// Every alert is a scheduled query rule scoped to the Log Analytics
		// workspace, and every one is disabled while no destination exists.
		var rules = Regex.Matches(bicep, @"resource \w+ 'Microsoft\.Insights/scheduledQueryRules@[^']+'");
		Assert.True(rules.Count >= 6, $"expected job, item, app, maintenance, ai-threshold and ai-cap alerts; found {rules.Count}");
		Assert.Contains("actionGroupId: alertsEnabled ? maintainerActionGroup.id : ''", bicep);
	}

	[Fact]
	public void NoEmailAddressIsBakedIntoTheTemplate()
	{
		Assert.DoesNotMatch("'[\\w.+-]+@[\\w.-]+'", Bicep());
	}

	[Fact]
	public void TheJobFailureAlertKeysOnTheLineFiniteJobsShip()
	{
		var bicep = Bicep();
		var templates = Regex.Matches(
			ServiceDefaultsSource("Extensions.cs"),
			@"LogError\(""(Archive job \{JobName\} failed \(\{ExceptionType\}\))""");
		Assert.Equal(1, templates.Count);

		// The query must match the shipped line by its fixed prefix and suffix
		// only, so job names and exception types stay free.
		Assert.Contains("has \"Archive job \"", bicep);
		Assert.Contains("has \" failed (\"", bicep);
	}

	[Fact]
	public void TheItemFailureAlertKeysOnTheTerminalLadderLinesThatCarryTheDateiId()
	{
		var bicep = Bicep();
		foreach (var worker in new[] { "Extraction/ExtractionWorker.cs", "Reading/ReadingWorker.cs" })
		{
			var source = BackendSource(worker);
			Assert.Contains("exhausted its attempts", source);
			// Terminal-only: the transient per-attempt warning line must never
			// be an alert marker; the reading worker parks budget runs in
			// ReadingStatus.WaitingForBudget, a normal state, not a failure.
			Assert.Contains("({ExceptionType})", source);
		}
		Assert.Contains("WaitingForBudget", BackendSource("Reading/ReadingWorker.cs"));

		Assert.Contains("has \"exhausted its attempts\"", bicep);
		Assert.Contains("extract(@\"Datei ([0-9a-f-]{36})\"", bicep);
		// Transient retries and budget parks are normal states: never markers.
		Assert.DoesNotContain("has \"failed (\"", bicep);
	}

	[Fact]
	public void TheAppFailureAlertKeysOnTheRequestFailureLine()
	{
		var source = BackendSource("Program.cs");
		Assert.Contains("\"Archive request failed ({ExceptionType}) {StackTrace}\"", source);
		Assert.Contains("has \"Archive request failed (\"", Bicep());
	}

	[Fact]
	public void TheMaintenanceWindowAlertKeysOnTheJobsDailyHappyPathLine()
	{
		var bicep = Bicep();
		var program = BackendSource("Program.cs");

		// A daily sweep run that is paused by maintenance is the deterministic
		// "window is open" line; it only appears while maintenance is on.
		Assert.Contains("paused: maintenance mode is enabled", program);
		Assert.Contains("has \"paused: maintenance mode is enabled\"", bicep);
		// The line comes from the finite jobs, never from member traffic.
		foreach (var falsePositive in new[] { "Wartungsarbeiten", "wartung" })
			Assert.DoesNotContain($"has \"{falsePositive}\"", bicep);
	}

	[Fact]
	public void TheAiCostAlertsKeyOnTheLedgerNoticeAndCapLines()
	{
		var bicep = Bicep();
		var notice = Source("backend", "Ai", "AiCostNotice.cs");
		var ledger = Source("backend", "Ai", "AiBudgetLedger.cs");

		// Pre-cap threshold: the review logs one deterministic German warning
		// line per newly reached level; the alert is keyed on its fixed prefix,
		// never on an amount or feature name.
		Assert.Contains("KI-Budget: Schwellwert {NoticeEur} EUR", notice);
		Assert.Contains("has \"KI-Budget: Schwellwert\"", bicep);
		// The cap: refused calls (AiBudgetLedger) and the daily review share
		// one marker, so one rule covers a refused call and a review that
		// states the cap even when no call happens after it.
		Assert.Contains("KI-Budget: Monatsbudget erreicht", ledger);
		Assert.Contains("KI-Budget: Monatsbudget erreicht ", notice);
		Assert.Contains("has \"Monatsbudget erreicht\"", bicep);
		// The threshold rule must not match cap lines and vice versa.
		foreach (var query in Regex.Matches(bicep, @"query: '''(?<q>.*?)'''", RegexOptions.Singleline)
			.SelectMany(m => new[] { m.Groups["q"].Value }))
		{
			if (query.Contains("Monatsbudget erreicht"))
				Assert.DoesNotContain("Schwellwert", query);
			if (query.Contains("Schwellwert"))
				Assert.DoesNotContain("Monatsbudget erreicht", query);
		}
	}

	[Fact]
	public void TheAiNoticeThresholdsAreExplicitConfigurationWithNoHiddenDefault()
	{
		var bicep = Bicep();
		// The Bicep value is pass-through text: empty = off, and no default
		// threshold is invented in the template either.
		var param = Regex.Match(bicep, @"param aiNoticeThresholdsEur string = '([^']*)'");
		Assert.True(param.Success, "aiNoticeThresholdsEur must be a pass-through string parameter");
		Assert.Equal(string.Empty, param.Groups[1].Value);
		// The backend ships no default: an empty list is the disabled state.
		var options = BackendSource("Ai/AiOptions.cs");
		Assert.Contains("NoticeThresholdsEur", options);
		Assert.Matches(@"NoticeThresholdsEur \{ get; set; \} = \[\];", options);
		// The dispatch sweep (and the app's view) carry the configured list;
		// the --read-queue worker itself does not need it.
		Assert.Equal(2, Regex.Matches(bicep, @"name: 'Archive__Ai__NoticeThresholdsEur'").Count);
	}

	[Fact]
	public void TheWorkspaceKeepsItsBoundedRetentionAndDailyCap()
	{
		var bicep = Bicep();
		Assert.Matches(@"retentionInDays: \d+", bicep);
		Assert.Matches(@"dailyQuotaGb: \w+", bicep);
	}
}
