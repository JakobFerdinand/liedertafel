using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// Seam for handing one revision's extraction work to the queue backend
/// (ARC-034). Implementers report whether a message was accepted so the
/// caller can stamp the row's <see cref="ExtractionJob.LastEnqueuedAt"/>;
/// infrastructure failures may throw — the caller owns the retry policy.
/// </summary>
public interface IExtractionQueue
{
	/// <summary>Sends the extraction envelope for the revision; true when a message was accepted.</summary>
	Task<bool> SendAsync(Guid revisionId, CancellationToken token);
}

/// <summary>
/// Queue message body for the extraction handoff (ARC-034). It carries trace
/// context only — never secrets, provider identifiers or baggage — so the
/// finite worker can correlate its <c>archive.queue.process</c> span with the
/// sender, matching the <c>RunArchiveJobAsync</c> contract.
/// </summary>
public sealed record ExtractionEnvelope(string Type, Guid RevisionId, string? TraceParent, string? TraceState)
{
	/// <summary>Only extraction messages travel on this queue.</summary>
	public const string ExtractionType = "extraction";

	/// <summary>
	/// Explicit camelCase wire format shared by sender and consumer so the
	/// envelope shape stays a frozen contract across slices.
	/// </summary>
	public static JsonSerializerOptions SerializerOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};
}

/// <summary>
/// Chooses the queue backend from configuration (ARC-034): the
/// <c>archive-queues</c> connection string (Azurite/local) first, the Entra
/// token service URI (<c>Archive:Extraction:QueueServiceUri</c>, mirroring
/// the ARC-049 blob path) second, and — when neither is configured — a no-op
/// queue. Missing configuration is never an error; rows then wait un-enqueued
/// for a later dispatch sweep.
/// </summary>
public static class ExtractionQueue
{
	public static IExtractionQueue Create(IConfiguration configuration)
	{
		var options = configuration.GetSection(ExtractionOptions.SectionName).Get<ExtractionOptions>()
			?? new ExtractionOptions();
		var connectionString = configuration.GetConnectionString("archive-queues");
		if (!string.IsNullOrWhiteSpace(connectionString))
		{
			return new AzureExtractionQueue(new QueueServiceClient(connectionString, BoundedRetryOptions()),
				Options.Create(options));
		}
		if (!string.IsNullOrWhiteSpace(options.QueueServiceUri))
		{
			return new AzureExtractionQueue(
				new QueueServiceClient(new Uri(options.QueueServiceUri), new DefaultAzureCredential(),
					BoundedRetryOptions()),
				Options.Create(options));
		}
		return new NullExtractionQueue();
	}

	/// <summary>Same bounded retry posture as the local blob/queue services.</summary>
	private static QueueClientOptions BoundedRetryOptions() => new()
	{
		Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) },
	};
}

/// <summary>
/// Azure Storage queue sender (ARC-034). The client is cached; the envelope
/// is never logged. Send failures surface as exceptions — the caller decides
/// the retry policy.
/// </summary>
public sealed class AzureExtractionQueue(QueueServiceClient client, IOptions<ExtractionOptions> options) : IExtractionQueue
{
	private QueueClient? queueClient;

	public async Task<bool> SendAsync(Guid revisionId, CancellationToken token)
	{
		var queue = queueClient ??= client.GetQueueClient(options.Value.QueueName);
		var envelope = new ExtractionEnvelope(ExtractionEnvelope.ExtractionType, revisionId,
			Activity.Current?.Id, Activity.Current?.TraceStateString);
		await queue.SendMessageAsync(
			JsonSerializer.Serialize(envelope, ExtractionEnvelope.SerializerOptions), token);
		return true;
	}
}

/// <summary>
/// Graceful degradation when no queue backend is configured (ARC-034):
/// reports "not accepted" so rows stay un-enqueued and are rediscovered by the
/// dispatch sweeper. Never throws.
/// </summary>
public sealed class NullExtractionQueue : IExtractionQueue
{
	public Task<bool> SendAsync(Guid revisionId, CancellationToken token) => Task.FromResult(false);
}
