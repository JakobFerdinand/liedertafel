using Archive.Backend.Extraction;
using Archive.Backend.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
	[InlineData("--read-queue", false)]
	[InlineData("--dispatch-reading", false)]
	[InlineData("--cleanup-dropped-blobs", false)]
	[InlineData("--worker-smoke", true)]
	[InlineData("--cleanup-uploads", true)]
	[InlineData("--initialize-local-storage", true)]
	[InlineData("--seed-dev-auth", true)]
	[InlineData("--send-test-mail", true)]
	[InlineData("--definitely-unknown", true)]
	[InlineData(null, true)]
	public void GateAllowsOnlyProductionLegalCommandsOutsideDevelopment(string? command, bool developmentRequired) =>
		Assert.Equal(developmentRequired, FiniteJobs.IsDevelopmentRequired(command));

	// #189: the shared runner shape. These tests pin the contract every
	// finite command relies on: a gated command pauses under maintenance —
	// the body never runs, the pause line ships under the command's log
	// category (the Azure maintenance-window alert keys on it) and the run
	// exits 0 through the normal completion path — while an ungated command
	// runs through maintenance, every body receives its own DI scope, and a
	// queue command's ensure no-ops without a queue backend.
	private const string PapierkorbPause =
		"Papierkorb cleanup paused: maintenance mode is enabled";

	[Fact]
	public async Task GatedCommandPausesUnderMaintenanceAndStillExitsZero()
	{
		var logs = new PauseLog();
		using var host = JobHost(maintenance: true, logs);
		var bodyRuns = false;

		var exit = await FiniteJobs.RunAsync(host, "--cleanup-papierkorb",
			("Archive.Papierkorb", PapierkorbPause), (_, _) => { bodyRuns = true; return Task.CompletedTask; });

		Assert.False(bodyRuns);
		Assert.Equal(0, exit);
		Assert.Contains(logs.Lines, line =>
			line.Category == "Archive.Papierkorb" && line.Message == PapierkorbPause);
		// A paused run is a completed job, not a failure: the release
		// workflow's smoke check reads this line.
		Assert.Contains(logs.Lines, line => line.Message.Contains("cleanup-papierkorb completed"));
	}

	[Fact]
	public async Task GatedCommandRunsItsBodyInItsOwnScopeWithoutMaintenance()
	{
		using var host = JobHost(maintenance: false);
		ScopeProbe? bodyProbe = null;

		var exit = await FiniteJobs.RunAsync(host, "--cleanup-papierkorb",
			("Archive.Papierkorb", PapierkorbPause), (provider, _) => { bodyProbe = provider.GetRequiredService<ScopeProbe>(); return Task.CompletedTask; });

		Assert.Equal(0, exit);
		// The body resolves from a per-run scope, never from the root: scoped
		// services (DbContext, workers) must be fresh per command run.
		var rootProbe = host.Services.GetRequiredService<ScopeProbe>();
		Assert.NotSame(rootProbe, bodyProbe);
	}

	[Fact]
	public async Task UngatedCommandRunsThroughMaintenance()
	{
		var logs = new PauseLog();
		using var host = JobHost(maintenance: true, logs);
		var bodyRuns = false;

		var exit = await FiniteJobs.RunAsync(host, "--cleanup-uploads",
			null, (_, _) => { bodyRuns = true; return Task.CompletedTask; });

		// Commands without a pause line — migrate/admin, the local services
		// and the one-off cleanups — run through the window as before.
		Assert.True(bodyRuns);
		Assert.Equal(0, exit);
		Assert.DoesNotContain(logs.Lines, line => line.Message.Contains("paused: maintenance mode is enabled"));
	}

	[Theory]
	[InlineData("--extract-queue")]
	[InlineData("--dispatch-extraction")]
	[InlineData("--read-queue")]
	[InlineData("--dispatch-reading")]
	[InlineData("--transcode-queue")]
	[InlineData("--dispatch-videos")]
	public async Task QueueCommandsEnsureTheirQueueThenRunWithoutABackend(string command)
	{
		using var host = JobHost(maintenance: false);
		var bodyRuns = false;

		var exit = await FiniteJobs.RunAsync(host, command,
			null, (_, _) => { bodyRuns = true; return Task.CompletedTask; });

		// Without any queue configuration the ensure degrades to a no-op
		// (rows wait un-enqueued for the sweep) and the body still runs.
		Assert.True(bodyRuns);
		Assert.Equal(0, exit);
	}

	/// <summary>Bare host for the runner tests: in-memory maintenance flag,
	/// a capturing logger provider and a scoped probe, no OTLP and no
	/// database — the runner must not need more.</summary>
	private static IHost JobHost(bool maintenance, PauseLog? logs = null)
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		if (logs is not null) builder.Logging.AddProvider(logs);
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			[MaintenanceConfiguration.ModeKey] = maintenance ? "true" : "false",
		});
		builder.Services.AddScoped<ScopeProbe>();
		builder.Services.AddOptions<ExtractionOptions>().BindConfiguration(ExtractionOptions.SectionName);
		return builder.Build();
	}

	private sealed class ScopeProbe;

	/// <summary>Records every shipped line with its category, so the tests
	/// can pin the pause line and the completion line the way the console
	/// (and the Azure alert queries) see them.</summary>
	private sealed class PauseLog : ILoggerProvider
	{
		public List<(string Category, string Message)> Lines { get; } = new();

		public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

		public void Dispose() { }

		private sealed class Recorder(PauseLog owner, string category) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
				Func<TState, Exception?, string> formatter) =>
				owner.Lines.Add((category, formatter(state, exception)));
		}
	}
}
