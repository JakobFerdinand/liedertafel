using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// One received queue message (ARC-034). The pop receipt pairs with the
/// message id for delete/abandon and the dequeue count drives the bounded
/// visibility backoff; the body carries the extraction envelope.
/// </summary>
public sealed record ExtractionMessage(string MessageId, string PopReceipt, string Body, long DequeueCount);

/// <summary>
/// Receive seam for the finite extraction worker (ARC-034): the pump asks for
/// batches until the queue answers empty, which ends the finite run — no idle
/// polling (production is re-triggered by the queue scale rule). Delete
/// removes a fully handled message; Abandon makes it redisplay after the
/// given visibility window.
/// </summary>
public interface IExtractionMessageSource
{
	/// <summary>Receives up to <paramref name="maxMessages"/>; empty list = queue empty (finite exit).</summary>
	Task<IReadOnlyList<ExtractionMessage>> ReceiveBatchAsync(int maxMessages, CancellationToken token);

	Task DeleteAsync(string messageId, string popReceipt, CancellationToken token);

	/// <summary>Releases the message so it redisplay after <paramref name="visibility"/>.</summary>
	Task AbandonAsync(string messageId, string popReceipt, TimeSpan visibility, CancellationToken token);
}

/// <summary>
/// Shared queue client factory (ARC-034): sender and message source build the
/// same <see cref="QueueServiceClient"/> from configuration — the
/// <c>archive-queues</c> connection string (Azurite/local) first, the Entra
/// token service URI (<c>Archive:Extraction:QueueServiceUri</c>) second. Null
/// when neither is configured (no queue backend; rows wait for a later sweep).
/// </summary>
internal static class ExtractionQueueClient
{
	public static QueueServiceClient? Create(IConfiguration configuration, ExtractionOptions options)
	{
		var connectionString = configuration.GetConnectionString("archive-queues");
		if (!string.IsNullOrWhiteSpace(connectionString))
			return new QueueServiceClient(connectionString, BoundedRetryOptions());
		if (!string.IsNullOrWhiteSpace(options.QueueServiceUri))
			return new QueueServiceClient(new Uri(options.QueueServiceUri), new DefaultAzureCredential(),
				BoundedRetryOptions());
		return null;
	}

	/// <summary>Same bounded retry posture as the local blob/queue services.</summary>
	private static QueueClientOptions BoundedRetryOptions() => new()
	{
		Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) },
	};

	/// <summary>
	/// ARC-034 S5: creates the configured queue when missing so the first
	/// queue-triggered production run self-provisions it on a fresh account
	/// (the extraction job identity holds Storage Queue Data Contributor),
	/// instead of failing the run. False when no queue backend is configured
	/// — the local degradation keeps its existing behavior: rows wait
	/// un-enqueued for the sweep and the pump fails loudly on the missing
	/// message source.
	/// </summary>
	public static async Task<bool> EnsureQueueAsync(
		IConfiguration configuration, ExtractionOptions options, CancellationToken token)
	{
		var client = Create(configuration, options);
		if (client is null)
			return false;
		await client.GetQueueClient(options.QueueName)
			.CreateIfNotExistsAsync(cancellationToken: token);
		return true;
	}
}

/// <summary>
/// Azure Storage implementation of the receive seam (ARC-034), built over the
/// shared client factory so connection-string (Azurite) and Entra token modes
/// behave exactly like the sender. Message bodies are never logged.
/// </summary>
public sealed class AzureExtractionMessageSource(QueueServiceClient client, IOptions<ExtractionOptions> options)
	: IExtractionMessageSource
{
	private QueueClient? queueClient;

	private QueueClient Queue => queueClient ??= client.GetQueueClient(options.Value.QueueName);

	public async Task<IReadOnlyList<ExtractionMessage>> ReceiveBatchAsync(int maxMessages, CancellationToken token)
	{
		var response = await Queue.ReceiveMessagesAsync(
			Math.Min(maxMessages, ExtractionOptions.MaxReceiveBatch), options.Value.ReceiveVisibility, token);
		return response.Value
			.Select(message => new ExtractionMessage(
				message.MessageId, message.PopReceipt, message.MessageText, message.DequeueCount))
			.ToList();
	}

	public Task DeleteAsync(string messageId, string popReceipt, CancellationToken token) =>
		Queue.DeleteMessageAsync(messageId, popReceipt, token);

	public Task AbandonAsync(string messageId, string popReceipt, TimeSpan visibility, CancellationToken token) =>
		// Null text: only the visibility changes, the body stays unchanged.
		Queue.UpdateMessageAsync(messageId, popReceipt, messageText: null, visibility, token);
}
