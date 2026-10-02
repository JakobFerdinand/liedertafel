using Archive.Backend.Extraction;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034 slice S3: the finite-host Development gate. The extraction
/// commands run in production (queue-triggered job / maintainer sweep) and
/// join migrate/admin outside the gate; every other local service command
/// stays Development-only, including unknown or missing commands.
/// </summary>
public sealed class FiniteJobsTests
{
	[Theory]
	[InlineData("--migrate", false)]
	[InlineData("--bootstrap-admin", false)]
	[InlineData("--repair-admin", false)]
	[InlineData("--extract-queue", false)]
	[InlineData("--dispatch-extraction", false)]
	[InlineData("--worker-smoke", true)]
	[InlineData("--cleanup-uploads", true)]
	[InlineData("--initialize-local-storage", true)]
	[InlineData("--seed-dev-auth", true)]
	[InlineData("--send-test-mail", true)]
	[InlineData("--definitely-unknown", true)]
	[InlineData(null, true)]
	public void GateAllowsOnlyProductionLegalCommandsOutsideDevelopment(string? command, bool developmentRequired) =>
		Assert.Equal(developmentRequired, FiniteJobs.IsDevelopmentRequired(command));
}
