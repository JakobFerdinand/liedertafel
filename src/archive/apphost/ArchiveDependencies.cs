using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Archive.AppHost;

public sealed record ArchiveDependencies(
    IResourceBuilder<PostgresDatabaseResource> Database,
    IResourceBuilder<AzureBlobStorageResource> Blobs,
    IResourceBuilder<AzureQueueStorageResource> Queues,
    IResourceBuilder<ContainerResource> Mail);

/// <summary>
/// ARC-010 mail source selection. The default (<c>Smtp</c>) keeps ordinary
/// startup on local Mailpit capture. <c>Azure</c> routes the backend through
/// the real Communication Services sender; the connection string is a local
/// development credential from AppHost configuration (user secrets or
/// environment, never committed), while production sends keyless.
/// </summary>
public sealed record ArchiveMailSource(string Provider, string? AzureEndpoint, string? AzureConnectionString);

public static class ArchiveResourceExtensions
{
    public static IResourceBuilder<ProjectResource> WithArchiveTelemetry(
        this IResourceBuilder<ProjectResource> project) => project
        .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
        .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
        .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "false")
        .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "false");

    // Every local C# worker uses this reference/readiness contract, Service Defaults,
    // and WithExplicitStart for finite operator-triggered work.
    public static IResourceBuilder<ProjectResource> WithArchiveDependencies(
        this IResourceBuilder<ProjectResource> project, ArchiveDependencies dependencies, ArchiveMailSource? mail = null)
    {
        var configured = project
            .WithArchiveTelemetry()
            .WithReference(dependencies.Database)
            .WithReference(dependencies.Blobs)
            .WithReference(dependencies.Queues)
            .WithEnvironment("Mail__Host", dependencies.Mail.GetEndpoint("smtp").Property(EndpointProperty.Host))
            .WithEnvironment("Mail__Port", dependencies.Mail.GetEndpoint("smtp").Property(EndpointProperty.Port));
        // Explicit opt-in only: without Mail:Provider=Azure nothing changes and
        // Mailpit capture stays the single active sender.
        if (mail is not null && mail.Provider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
        {
            configured = configured.WithEnvironment("Mail__Provider", "Azure");
            if (!string.IsNullOrWhiteSpace(mail.AzureEndpoint))
                configured = configured.WithEnvironment("Mail__AzureEndpoint", mail.AzureEndpoint);
            if (!string.IsNullOrWhiteSpace(mail.AzureConnectionString))
                configured = configured.WithEnvironment("Mail__AzureConnectionString", mail.AzureConnectionString);
        }
        return configured
            .WaitFor(dependencies.Database)
            .WaitFor(dependencies.Blobs)
            .WaitFor(dependencies.Queues)
            .WaitFor(dependencies.Mail);
    }
}
