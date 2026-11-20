using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var workspace = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../backend-rs"));
var binary = Path.GetFullPath(builder.Configuration["Archive:ServerBinary"] ?? "target/debug/archive-server", workspace);
var persist = builder.Configuration.GetValue("Archive:PersistLocalData", true);
var postgres = builder.AddPostgres("archive-postgres").WithImageTag("17.6");
if (persist) postgres.WithDataVolume();
var database = postgres.AddDatabase("archive-db", "archive");
var storage = builder.AddAzureStorage("archive-storage").RunAsEmulator(emulator =>
{
    emulator.WithImageTag("3.35.0");
    if (persist) emulator.WithDataVolume();
    // Direct signed links and Azurite's own server-side copy use the same port.
    emulator.WithBlobPort(10000);
    emulator.WithArgs("--skipApiVersionCheck");
});
var blobs = storage.AddBlobs("archive-blobs");
var queues = storage.AddQueues("archive-queues");
var mail = builder.AddContainer("archive-mail", "docker.io/axllent/mailpit", "v1.27.8")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp")
    .WithHttpHealthCheck("/readyz");
var azureMail = builder.Configuration["Mail:Provider"]?.Equals("Azure", StringComparison.OrdinalIgnoreCase) == true;

IResourceBuilder<ExecutableResource> Server(string name, string? command = null)
{
    var resource = builder.AddExecutable(name, binary, workspace)
        .WithEnvironment("ARCHIVE_ENVIRONMENT", "Development")
        .WithEnvironment("ARCHIVE_STATIC_ROOT", Path.Combine(workspace, "../frontend/out"))
        .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
        .WithOtlpExporter(OtlpProtocol.HttpProtobuf);
    if (command is not null) resource.WithArgs(command);
    if (builder.Configuration["Archive:Pdfium:LibraryPath"] is { Length: > 0 } pdfium)
        resource.WithEnvironment("Archive__Pdfium__LibraryPath", pdfium);
    return resource;
}

IResourceBuilder<ExecutableResource> WithMail(IResourceBuilder<ExecutableResource> resource)
{
    if (azureMail)
    {
        resource.WithEnvironment("Mail__Provider", "Azure")
            .WithEnvironment("Mail__AzureEndpoint", builder.Configuration["Mail:AzureEndpoint"]);
        if (builder.Configuration["Mail:AzureConnectionString"] is { Length: > 0 } connection)
            resource.WithEnvironment("Mail__AzureConnectionString", connection);
        return resource;
    }
    return resource.WithEnvironment("Mail__Provider", "Smtp")
        .WithEnvironment("Mail__Host", mail.GetEndpoint("smtp").Property(EndpointProperty.Host))
        .WithEnvironment("Mail__Port", mail.GetEndpoint("smtp").Property(EndpointProperty.Port));
}

IResourceBuilder<ExecutableResource> WithDependencies(IResourceBuilder<ExecutableResource> resource) =>
    WithMail(resource).WithReference(database).WithReference(blobs).WithReference(queues)
        .WithEnvironment("Archive__Chat__Provider", "Scripted")
        .WaitFor(database).WaitFor(blobs).WaitFor(queues).WaitFor(mail);

var initialize = WithDependencies(Server("archive-storage-init", "--initialize-local-storage"));
var api = WithDependencies(Server("archive-api")).WithHttpEndpoint(env: "PORT")
    .WaitForCompletion(initialize).WithHttpHealthCheck("/alive");

builder.AddJavaScriptApp("archive-frontend", "../frontend")
    .WithPnpm()
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("NEXT_TELEMETRY_DISABLED", "1")
    .WithEnvironment("ARCHIVE_API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

Server("archive-migrate", "--migrate")
    .WithReference(database, "archive-migrations").WaitFor(database).WithExplicitStart();

foreach (var (name, command) in new[]
{
    ("archive-worker-smoke", "--worker-smoke"),
    ("archive-extract", "--extract-queue"),
    ("archive-read", "--read-queue"),
    ("archive-transcode", "--transcode-queue"),
    ("archive-papierkorb-cleanup", "--cleanup-papierkorb"),
})
    WithDependencies(Server(name, command)).WaitForCompletion(initialize).WithExplicitStart();

// Hosted mail must be selected explicitly and never runs during startup.
if (azureMail && builder.Configuration["Archive:MailTestRecipient"] is { Length: > 0 } recipient)
    WithMail(Server("archive-mail-test", "--send-test-mail")).WithArgs(recipient).WithExplicitStart();

builder.Build().Run();
