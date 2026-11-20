using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

// An additive, disposable local image gate. No Azure configuration, user
// secrets, persisted volumes, hosted mail/model or registry publishing.
var builder = DistributedApplication.CreateBuilder(args);
var tag = builder.Configuration["Archive:TestImageTag"];
if (tag is null || !System.Text.RegularExpressions.Regex.IsMatch(tag, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z"))
    throw new InvalidOperationException("Set Archive__TestImageTag to a built localhost/liedertafel-archive tag.");

var appHost = builder.AppHostDirectory;
var admin = builder.AddParameter("pg-admin", "archive_admin");
var pg = builder.AddPostgres("archive-postgres", userName: admin)
    .WithImageTag("17.6")
    .WithEnvironment("POSTGRES_DB", "archive")
    // Aspire copies the SQL before startup; a rootless remote engine need
    // not be able to mount this WSL worktree.
    .WithContainerFiles("/docker-entrypoint-initdb.d", [
        new ContainerFile
        {
            Name = "10-bootstrap.sql",
            Contents = File.ReadAllText(Path.Combine(appHost, "../../../infrastructure/neon/bootstrap-roles.sql")),
            Mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        },
        new ContainerFile
        {
            Name = "20-fixture-passwords.sql",
            Contents = File.ReadAllText(Path.Combine(appHost, "fixture-role-passwords.sql")),
            Mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        },
    ]);
var database = pg.AddDatabase("archive-db", "archive");
var runtime = ReferenceExpression.Create(
    $"Host={pg.GetEndpoint("tcp").Property(EndpointProperty.Host)};Port={pg.GetEndpoint("tcp").Property(EndpointProperty.Port)};Database=archive;Username=archive_runtime;Password=rust235-runtime");
var migrator = ReferenceExpression.Create(
    $"Host={pg.GetEndpoint("tcp").Property(EndpointProperty.Host)};Port={pg.GetEndpoint("tcp").Property(EndpointProperty.Port)};Database=archive;Username=archive_migrator;Password=rust235-migrator");

var storage = builder.AddContainer("archive-storage", "mcr.microsoft.com/azure-storage/azurite", "3.35.0")
    .WithContainerNetworkAlias("archive-blobs-235")
    .WithArgs("azurite", "--location", "/data", "--disableProductStyleUrl", "--skipApiVersionCheck",
        "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0")
    .WithHttpEndpoint(targetPort: 10000, name: "blob")
    .WithHttpEndpoint(targetPort: 10001, name: "queue")
    .WithHttpHealthCheck("/devstoreaccount1?comp=list", 403, "blob");
const string Azurite = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
    "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;";
var mail = builder.AddContainer("archive-mail", "docker.io/axllent/mailpit", "v1.27.8")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp")
    .WithHttpHealthCheck("/readyz");

IResourceBuilder<ContainerResource> Image(string name) =>
    builder.AddContainer(name, "localhost/liedertafel-archive", tag)
        .WithImagePullPolicy(ImagePullPolicy.Never)
        // A reproducible local envelope for the small synthetic workloads;
        // not a production capacity claim or an infrastructure sizing change.
        .WithContainerRuntimeArgs("--cpus=3", "--memory=1g")
        .WithEnvironment("ARCHIVE_ENVIRONMENT", "Development")
        .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000")
        .WithOtlpExporter(OtlpProtocol.HttpProtobuf);

IResourceBuilder<ContainerResource> Application(string name) =>
    Image(name)
        .WithEnvironment("ConnectionStrings__archive-db", runtime)
        // Same URL is usable by the API, worker and Azurite's internal fetch.
        // The test's host client connects to the mapped socket without
        // rewriting the signed URI or Host header.
        .WithEnvironment("ConnectionStrings__archive-blobs",
            $"{Azurite}BlobEndpoint=http://archive-blobs-235:10000/devstoreaccount1;")
        .WithEnvironment("ConnectionStrings__archive-queues",
            $"{Azurite}QueueEndpoint=http://archive-blobs-235:10001/devstoreaccount1;")
        .WithEnvironment("Mail__Provider", "Smtp")
        .WithEnvironment("Mail__Host", mail.GetEndpoint("smtp").Property(EndpointProperty.Host))
        .WithEnvironment("Mail__Port", mail.GetEndpoint("smtp").Property(EndpointProperty.Port))
        .WithEnvironment("Archive__Chat__Provider", "Scripted")
        .WaitFor(database)
        .WaitFor(storage)
        .WaitFor(mail);

var init = Application("archive-storage-init").WithArgs("--initialize-local-storage");
var api = Application("archive-api")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WaitForCompletion(init)
    .WithHttpHealthCheck("/alive");

builder.AddJavaScriptApp("archive-frontend", "../frontend")
    .WithPnpm()
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("NEXT_TELEMETRY_DISABLED", "1")
    .WithEnvironment("ARCHIVE_API_URL", api.GetEndpoint("http"))
    .WaitFor(api)
    .WithHttpHealthCheck("/system/status/");

Image("archive-migrate")
    .WithArgs("--migrate")
    .WithEnvironment("ConnectionStrings__archive-migrations", migrator)
    .WaitFor(database)
    .WithExplicitStart();

// The test loads the independently frozen schema into a separate disposable
// database before this command starts. The origin never sees this connection.
var adoption = pg.AddDatabase("archive-adoption-db", "archive_adoption");
Image("archive-adopt-check")
    .WithArgs("--migrate")
    .WithReference(adoption, "archive-migrations")
    .WaitFor(adoption)
    .WithExplicitStart();

foreach (var (name, argument) in new[]
{
    ("archive-worker-smoke", "--worker-smoke"),
    ("archive-extract", "--extract-queue"),
    ("archive-read", "--read-queue"),
    ("archive-transcode", "--transcode-queue"),
})
{
    Application(name).WithArgs(argument).WaitForCompletion(init).WithExplicitStart();
}

builder.Build().Run();
