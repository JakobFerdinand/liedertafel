using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// Development-only diagnostic (ARC-034): re-sends one extraction envelope
/// for a revision to the configured queue, built exactly like the sender —
/// the same shared client factory, the same configured queue name and the
/// current activity's trace context — so a duplicate delivery and its
/// idempotent handling can be observed against the real queue backend.
/// Never mapped outside Development.
/// </summary>
internal static class ExtractionQueueDiagnostics
{
	/// <summary>
	/// Sends one duplicate envelope for the revision; false when no queue
	/// backend is configured (the endpoint answers with an honest problem).
	/// </summary>
	public static async Task<bool> SendDuplicateAsync(IServiceProvider services, Guid revisionId, CancellationToken token)
	{
		var configuration = services.GetRequiredService<IConfiguration>();
		var options = services.GetRequiredService<IOptions<ExtractionOptions>>().Value;
		var client = ExtractionQueueClient.Create(configuration, options);
		if (client is null)
			return false;
		var queue = client.GetQueueClient(options.QueueName);
		var envelope = new ExtractionEnvelope(ExtractionEnvelope.ExtractionType, revisionId,
			Activity.Current?.Id, Activity.Current?.TraceStateString);
		await queue.SendMessageAsync(
			JsonSerializer.Serialize(envelope, ExtractionEnvelope.SerializerOptions), token);
		return true;
	}
}
