using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Archive.AppHost;

public sealed record ArchiveDependencies(
    IResourceBuilder<PostgresDatabaseResource> Database,
    IResourceBuilder<AzureBlobStorageResource> Blobs,
    IResourceBuilder<AzureQueueStorageResource> Queues,
    IResourceBuilder<ContainerResource> Mail);

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
        this IResourceBuilder<ProjectResource> project, ArchiveDependencies dependencies) => project
        .WithArchiveTelemetry()
        .WithReference(dependencies.Database)
        .WithReference(dependencies.Blobs)
        .WithReference(dependencies.Queues)
        .WithEnvironment("Mail__Host", dependencies.Mail.GetEndpoint("smtp").Property(EndpointProperty.Host))
        .WithEnvironment("Mail__Port", dependencies.Mail.GetEndpoint("smtp").Property(EndpointProperty.Port))
        .WaitFor(dependencies.Database)
        .WaitFor(dependencies.Blobs)
        .WaitFor(dependencies.Queues)
        .WaitFor(dependencies.Mail);
}
