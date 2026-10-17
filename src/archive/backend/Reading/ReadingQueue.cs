using System.Diagnostics;
using System.Text.Json;
using Archive.Backend.Extraction;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>
/// Seam for handing one Datei's reading work to the queue (ARC-034-1),
/// mirroring <see cref="IExtractionQueue"/>: true when a message was
/// accepted, so the caller can stamp the row's enqueue time.
/// </summary>
public interface IReadingQueue
{
	Task<bool> SendAsync(Guid dateiId, CancellationToken token);
}

/// <summary>
/// Chooses the reading queue backend from configuration: the
/// <c>archive-queues</c> connection string (Azurite/local) first, the Entra
/// service URI (<c>Archive:Reading:QueueServiceUri</c>, else the extraction
/// one: same storage account) second, otherwise a no-op queue whose rows wait
/// for the dispatch sweep.
/// </summary>
public static class ReadingQueue
{
	public static IReadingQueue Create(IConfiguration configuration)
	{
		var options = Options(configuration);
		var client = Client(configuration, options);
		return client is null ? new NullReadingQueue() : new AzureReadingQueue(client, Microsoft.Extensions.Options.Options.Create(options));
	}

	public static ReadingOptions Options(IConfiguration configuration)
		=> configuration.GetSection(ReadingOptions.SectionName).Get<ReadingOptions>() ?? new ReadingOptions();

	public static QueueServiceClient? Client(IConfiguration configuration, ReadingOptions options)
		=> ExtractionQueueClient.Create(configuration, options.QueueServiceUri
			?? configuration.GetSection(ExtractionOptions.SectionName).Get<ExtractionOptions>()?.QueueServiceUri);

	/// <summary>
	/// Creates the queue when missing so the first production run (the
	/// reading identity holds Storage Queue Data Contributor) self-provisions
	/// it. False when no queue backend is configured.
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
/// Azure Storage queue sender for reading work. The envelope is the
/// extraction envelope with type <see cref="ReadingType"/>: trace context
/// only, never secrets or the text being read.
/// </summary>
public sealed class AzureReadingQueue(QueueServiceClient client, IOptions<ReadingOptions> options) : IReadingQueue
{
	public const string ReadingType = "reading";

	private QueueClient? queueClient;

	public async Task<bool> SendAsync(Guid dateiId, CancellationToken token)
	{
		var queue = queueClient ??= client.GetQueueClient(options.Value.QueueName);
		var envelope = new ExtractionEnvelope(ReadingType, dateiId,
			Activity.Current?.Id, Activity.Current?.TraceStateString);
		await queue.SendMessageAsync(JsonSerializer.Serialize(envelope, ExtractionEnvelope.SerializerOptions), token);
		return true;
	}
}

/// <summary>No queue backend: reports "not accepted" so rows stay un-enqueued for the sweep.</summary>
public sealed class NullReadingQueue : IReadingQueue
{
	public Task<bool> SendAsync(Guid dateiId, CancellationToken token) => Task.FromResult(false);
}

/// <summary>Receive seam of the reading queue; the same shape as the extraction one, but a type of its own so both can live in one host.</summary>
public interface IReadingMessageSource : IExtractionMessageSource;

/// <summary>Azure Storage implementation of <see cref="IReadingMessageSource"/> over the shared queue client factory.</summary>
public sealed class AzureReadingMessageSource(QueueServiceClient client, IOptions<ReadingOptions> options) : IReadingMessageSource
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
