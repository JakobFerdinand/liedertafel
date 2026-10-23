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
		Assert.True(rules.Count >= 4, $"expected job, item, app and maintenance alerts; found {rules.Count}");
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
	public void TheWorkspaceKeepsItsBoundedRetentionAndDailyCap()
	{
		var bicep = Bicep();
		Assert.Matches(@"retentionInDays: \d+", bicep);
		Assert.Matches(@"dailyQuotaGb: \w+", bicep);
	}
}
