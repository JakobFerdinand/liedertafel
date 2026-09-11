using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Archive.Backend.Development;

public sealed class LocalServices(IConfiguration configuration)
{
    public const string ContainerName = "archive-assets";
    public const string QueueName = "archive-work";

    private BlobServiceClient Blobs => new(configuration.GetConnectionString("archive-blobs"),
        new BlobClientOptions { Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) } });
    private QueueServiceClient Queues => new(configuration.GetConnectionString("archive-queues"),
        new QueueClientOptions { Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) } });

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Blobs.GetBlobContainerClient(ContainerName).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await Queues.GetQueueClient(QueueName).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task ExerciseAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var blob = Blobs.GetBlobContainerClient(ContainerName).GetBlobClient($"diagnostics/{id}.txt");
        // A private scratch queue makes parallel smoke runs independent of real work.
        var queue = Queues.GetQueueClient($"archive-smoke-{id}");
        try
        {
            await blob.UploadAsync(BinaryData.FromString(id), cancellationToken: cancellationToken);
            if ((await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToString() != id)
                throw new InvalidOperationException("Blob round trip failed.");
            await queue.CreateAsync(cancellationToken: cancellationToken);
            using (var producer = Extensions.Activities.StartActivity("archive.queue.send", ActivityKind.Producer))
            {
                await queue.SendMessageAsync(JsonSerializer.Serialize(new SmokeMessage(id,
                    Activity.Current?.Id, Activity.Current?.TraceStateString)), cancellationToken);
            }
            var message = (await queue.ReceiveMessageAsync(cancellationToken: cancellationToken)).Value
                ?? throw new InvalidOperationException("Queue round trip failed.");
            var envelope = JsonSerializer.Deserialize<SmokeMessage>(message.MessageText)!;
            ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out var parent);
            using (Extensions.Activities.StartActivity("archive.queue.process", ActivityKind.Consumer, parent))
            {
                if (envelope.Id != id) throw new InvalidOperationException("Queue payload mismatch.");
                await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
            }
            using var mail = new MimeMessage();
            mail.From.Add(new MailboxAddress("Liedertafel Archiv", "archiv@liedertafel.test"));
            mail.To.Add(new MailboxAddress("Lokale Entwicklung", "entwicklung@liedertafel.test"));
            mail.Subject = "Archiv: lokaler Funktionstest";
            mail.Body = new TextPart("plain") { Text = "Blob und Warteschlange sind erreichbar. Diese Nachricht bleibt im lokalen Mailpostfach." };
            using var smtp = new SmtpClient { Timeout = 10000 };
            using var mailActivity = Extensions.Activities.StartActivity("archive.mail.send", ActivityKind.Client);
            await smtp.ConnectAsync(configuration["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required."),
                configuration.GetValue<int>("Mail:Port"), SecureSocketOptions.None, cancellationToken);
            await smtp.SendAsync(mail, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await blob.DeleteIfExistsAsync(cancellationToken: cleanup.Token); }
            finally { await queue.DeleteIfExistsAsync(cleanup.Token); }
        }
    }

    private sealed record SmokeMessage(string Id, string? TraceParent, string? TraceState);
}
