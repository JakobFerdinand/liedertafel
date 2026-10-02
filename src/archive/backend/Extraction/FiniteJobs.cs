namespace Archive.Backend.Extraction;

/// <summary>
/// Development gate for the finite host commands (ARC-034). The extraction
/// worker and the dispatch sweep are production jobs — the Container Apps Job
/// executes the same backend image with <c>--extract-queue</c>, and the
/// maintainer runs <c>--dispatch-extraction</c> there too — so they join the
/// migrate/admin commands outside the Development-only local services.
/// </summary>
internal static class FiniteJobs
{
	/// <summary>True when the command may only run under Development.</summary>
	public static bool IsDevelopmentRequired(string? command) => command switch
	{
		"--migrate" or "--bootstrap-admin" or "--repair-admin"
			or "--extract-queue" or "--dispatch-extraction" => false,
		_ => true,
	};
}
