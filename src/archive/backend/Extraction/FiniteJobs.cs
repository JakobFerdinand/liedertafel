using Archive.Backend.Maintenance;
using Archive.Backend.Reading;
using Archive.Backend.Videos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// Development gate for the finite host commands (ARC-034). The extraction
/// worker and the dispatch sweep are production jobs — the Container Apps Job
/// executes the same backend image with <c>--extract-queue</c>, and the
/// maintainer runs <c>--dispatch-extraction</c> there too (ARC-034-1 adds the
/// AI reading pair <c>--read-queue</c> / <c>--dispatch-reading</c>) — so they join the
/// migrate/admin commands outside the Development-only local services.
/// The class also owns the one runner shape every finite command shares
/// (#189), so a new command declares only its name, pause line and body.
/// </summary>
internal static class FiniteJobs
{
	/// <summary>True when the command may only run under Development.</summary>
	public static bool IsDevelopmentRequired(string? command) => command switch
	{
		"--migrate" or "--bootstrap-admin" or "--repair-admin" or "--cleanup-dropped-blobs"
			or "--extract-queue" or "--dispatch-extraction"
			or "--read-queue" or "--dispatch-reading"
			or "--transcode-queue" or "--dispatch-videos" => false,
		_ => true,
	};

	/// <summary>
	/// The one runner shape for every finite command (#189). Program.cs
	/// declares only the command's raw name, its maintenance pause line and
	/// its body; this method owns the shared wiring — one DI scope per run,
	/// the maintenance gate with its pause log line, the queue ensure, the
	/// job activity with its W3C trace context, the telemetry flush and the
	/// process exit code. Call it after <c>StartAsync</c>; like
	/// <c>RunArchiveJobAsync</c>, it stops the host, including on a handled
	/// failure.
	/// </summary>
	public static async Task<int> RunAsync(IHost host, string command,
		(string Category, string Message)? maintenance,
		Func<IServiceProvider, CancellationToken, Task> worker)
	{
		return await host.RunArchiveJobAsync(command[2..], async token =>
		{
			using var scope = host.Services.CreateScope();
			var provider = scope.ServiceProvider;
			if (maintenance is { } pause)
			{
				// A paused run touches nothing and exits 0 through the normal
				// completion path; the queue scale rule re-triggers it later.
				var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(pause.Category);
				if (MaintenanceConfiguration.IsEnabled(provider.GetRequiredService<IConfiguration>()))
				{
					logger.LogInformation(pause.Message);
					return;
				}
			}
			await EnsureQueueAsync(command, provider, token);
			await worker(provider, token);
		}, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
	}

	// ARC-034 S5: a queue command ensures its queue before any work — the
	// first queue-triggered run on a fresh production account self-provisions
	// it (the job identities hold Storage Queue Data Contributor) instead of
	// failing dispatch and drain. False from a missing backend keeps the
	// existing degradation: rows wait un-enqueued for the sweep.
	private static async Task EnsureQueueAsync(string command, IServiceProvider provider, CancellationToken token)
	{
		switch (command)
		{
			case "--extract-queue" or "--dispatch-extraction":
				await ExtractionQueueClient.EnsureQueueAsync(provider.GetRequiredService<IConfiguration>(),
					provider.GetRequiredService<IOptions<ExtractionOptions>>().Value, token);
				break;
			case "--read-queue" or "--dispatch-reading":
				await ReadingQueue.EnsureQueueAsync(provider.GetRequiredService<IConfiguration>(), token);
				break;
			case "--transcode-queue" or "--dispatch-videos":
				await VideoQueue.EnsureQueueAsync(provider.GetRequiredService<IConfiguration>(), token);
				break;
		}
	}
}
