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

var initialize = builder.AddProject<Projects.Archive_Backend>("archive-storage-init", launchProfileName: null)
    .WithArgs("--initialize-local-storage")
    .WithArchiveDependencies(dependencies);

builder.AddProject<Projects.Archive_Backend>("archive-migrate", launchProfileName: null)
    .WithArgs("--migrate")
    .WithArchiveTelemetry()
    .WithReference(database, "archive-migrations")
    .WaitFor(database)
    .WithExplicitStart();

var api = builder.AddProject<Projects.Archive_Backend>("archive-api", launchProfileName: "http")
    .WithArchiveDependencies(dependencies)
    .WithEnvironment("Development__KeysPath", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../.local/keys")))
    .WaitForCompletion(initialize)
    .WithHttpHealthCheck("/alive");

// This finite diagnostic demonstrates the registration/flush contract for later jobs.
builder.AddProject<Projects.Archive_Backend>("archive-worker-smoke", launchProfileName: null)
    .WithArgs("--worker-smoke")
    .WithArchiveDependencies(dependencies)
    .WaitForCompletion(initialize)
    .WithExplicitStart();

builder.AddJavaScriptApp("archive-frontend", "../frontend")
    .WithPnpm()
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("ARCHIVE_API_URL", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
