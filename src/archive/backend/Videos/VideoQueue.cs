using System.Diagnostics;
using System.Text.Json;
using Archive.Backend.Extraction;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Videos;

/// <summary>
/// Seam for handing one Datei's transcode work to the queue backend (#161),
/// mirroring <see cref="IExtractionQueue"/>: true when a message was
/// accepted, so the caller can stamp the row's enqueue time. Infrastructure
/// failures may throw — the caller owns the retry policy.
/// </summary>
public interface IVideoQueue
{
	/// <summary>Sends the transcode envelope for one Datei; true when a message was accepted.</summary>
	Task<bool> SendAsync(Guid dateiId, CancellationToken token);
}

/// <summary>
/// Chooses the video queue backend from configuration: the
/// <c>archive-queues</c> connection string (Azurite/local) first, the video
/// service URI falling back to the extraction queue's (same assets account)
/// second, otherwise a no-op queue whose rows wait for the dispatch sweep —
/// exactly the reading queue's degradation.
/// </summary>
public static class VideoQueue
{
	public static IVideoQueue Create(IConfiguration configuration)
	{
		var options = Options(configuration);
		var client = Client(configuration, options);
		return client is null ? new NullVideoQueue() : new AzureVideoQueue(client, Microsoft.Extensions.Options.Options.Create(options));
	}

	public static VideoOptions Options(IConfiguration configuration)
		=> configuration.GetSection(VideoOptions.SectionName).Get<VideoOptions>() ?? new VideoOptions();

	public static QueueServiceClient? Client(IConfiguration configuration, VideoOptions options)
		=> ExtractionQueueClient.Create(configuration, options.QueueServiceUri
			?? configuration.GetSection(ExtractionOptions.SectionName).Get<ExtractionOptions>()?.QueueServiceUri);

	/// <summary>
	/// Creates the queue when missing so the first queue-triggered production
	/// run self-provisions it (the job identity holds Storage Queue Data
	/// Contributor). False when no queue backend is configured.
	/// </summary>
	public static async Task<bool> EnsureQueueAsync(IConfiguration configuration, CancellationToken token)
	{
		var options = Options(configuration);
		var client = Client(configuration, options);
		if (client is null)
			return false;
		await client.GetQueueClient(options.QueueName).CreateIfNotExistsAsync(cancellationToken: token);
		return true;
	}
}

/// <summary>
/// Azure Storage queue sender for transcode work. The envelope is the shared
/// one with type <see cref="TranscodeType"/>: trace context only, never
/// secrets or file material.
/// </summary>
public sealed class AzureVideoQueue(QueueServiceClient client, IOptions<VideoOptions> options) : IVideoQueue
{
	public const string TranscodeType = "transcode";

	private QueueClient? queueClient;

	public async Task<bool> SendAsync(Guid dateiId, CancellationToken token)
	{
		var queue = queueClient ??= client.GetQueueClient(options.Value.QueueName);
		var envelope = new ExtractionEnvelope(TranscodeType, dateiId,
			Activity.Current?.Id, Activity.Current?.TraceStateString);
		await queue.SendMessageAsync(
			JsonSerializer.Serialize(envelope, ExtractionEnvelope.SerializerOptions), token);
		return true;
	}
}

/// <summary>No queue backend: reports "not accepted" so rows stay un-enqueued for the sweep.</summary>
public sealed class NullVideoQueue : IVideoQueue
{
	public Task<bool> SendAsync(Guid dateiId, CancellationToken token) => Task.FromResult(false);
}

/// <summary>Receive seam of the video queue; a type of its own so all queues can live in one host.</summary>
public interface IVideoMessageSource : IExtractionMessageSource;

/// <summary>Azure Storage implementation of <see cref="IVideoMessageSource"/> over the shared queue client factory.</summary>
public sealed class AzureVideoMessageSource(QueueServiceClient client, IOptions<VideoOptions> options) : IVideoMessageSource
{
	private readonly AzureExtractionMessageSource inner =
		new(client, options.Value.QueueName, options.Value.ReceiveVisibility);

	public Task<IReadOnlyList<ExtractionMessage>> ReceiveBatchAsync(int maxMessages, CancellationToken token)
		=> inner.ReceiveBatchAsync(maxMessages, token);

	public Task DeleteAsync(string messageId, string popReceipt, CancellationToken token)
		=> inner.DeleteAsync(messageId, popReceipt, token);

	public Task AbandonAsync(string messageId, string popReceipt, TimeSpan visibility, CancellationToken token)
		=> inner.AbandonAsync(messageId, popReceipt, visibility, token);
}
