using Archive.AppHost;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var persist = builder.Configuration.GetValue("Archive:PersistLocalData", true);
var postgres = builder.AddPostgres("archive-postgres").WithImageTag("17.6");
if (persist) postgres.WithDataVolume();
var database = postgres.AddDatabase("archive-db", "archive");
var storage = builder.AddAzureStorage("archive-storage")
    .RunAsEmulator(emulator =>
    {
        emulator.WithImageTag("3.35.0");
        if (persist) emulator.WithDataVolume();
    });
var blobs = storage.AddBlobs("archive-blobs");
var queues = storage.AddQueues("archive-queues");
var mail = builder.AddContainer("archive-mail", "docker.io/axllent/mailpit", "v1.27.8")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp")
    .WithHttpHealthCheck("/readyz");
var dependencies = new ArchiveDependencies(database, blobs, queues, mail);

// ARC-010: ordinary startup stays on Mailpit capture (Smtp default). Setting
// Mail:Provider=Azure via user secrets or environment (never committed), plus
// Mail:AzureConnectionString as the local authorized credential, routes the
// API and the explicit archive-mail-test run below through the real sender.
var mailSource = new ArchiveMailSource(
    builder.Configuration.GetValue("Mail:Provider", "Smtp") ?? "Smtp",
    builder.Configuration["Mail:AzureEndpoint"],
    builder.Configuration["Mail:AzureConnectionString"]);

var initialize = builder.AddProject<Projects.Archive_Backend>("archive-storage-init", launchProfileName: null)
    .WithArgs("--initialize-local-storage")
    .WithArchiveDependencies(dependencies, mailSource);

builder.AddProject<Projects.Archive_Backend>("archive-migrate", launchProfileName: null)
    .WithArgs("--migrate")
    .WithArchiveTelemetry()
    .WithReference(database, "archive-migrations")
    .WaitFor(database)
    .WithExplicitStart();

var api = builder.AddProject<Projects.Archive_Backend>("archive-api", launchProfileName: "http")
    .WithArchiveDependencies(dependencies, mailSource)
    .WithEnvironment("Development__KeysPath", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../.local/keys")))
    .WaitForCompletion(initialize)
    .WithHttpHealthCheck("/alive");

// This finite diagnostic demonstrates the registration/flush contract for later jobs.
builder.AddProject<Projects.Archive_Backend>("archive-worker-smoke", launchProfileName: null)
    .WithArgs("--worker-smoke")
    .WithArchiveDependencies(dependencies, mailSource)
    .WaitForCompletion(initialize)
    .WithExplicitStart();

// Explicit ARC-010 integration run with Aspire telemetry: sends the marked
// German test message via the real Azure sender. Requires Mail:Provider=Azure
// and Archive:MailTestRecipient in AppHost configuration; the backend refuses
// otherwise, so this resource can never emit real mail on default startup.
builder.AddProject<Projects.Archive_Backend>("archive-mail-test", launchProfileName: null)
    .WithArgs("--send-test-mail", builder.Configuration.GetValue("Archive:MailTestRecipient", string.Empty) ?? string.Empty)
    .WithArchiveDependencies(dependencies, mailSource)
    .WaitForCompletion(initialize)
    .WithExplicitStart();

builder.AddJavaScriptApp("archive-frontend", "../frontend")
    .WithPnpm()
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("ARCHIVE_API_URL", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
