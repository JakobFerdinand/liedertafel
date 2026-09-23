using System.Diagnostics.Metrics;
using System.Reflection;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Development;
using Archive.Backend.Maintenance;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
// The chat slice's configuration type shares its name with Microsoft's
// Microsoft.Extensions.AI.ChatOptions; the alias keeps the usage sites short.
using ChatOptions = Archive.Backend.Chat.ChatOptions;

var command = args.FirstOrDefault();
if (command is "--migrate" or "--initialize-local-storage" or "--worker-smoke" or "--bootstrap-admin" or "--repair-admin" or "--seed-dev-auth" or "--send-test-mail" or "--cleanup-uploads")
{
    var jobs = Host.CreateApplicationBuilder(args.Skip(1).ToArray());
    jobs.AddServiceDefaults();
    if (command != "--migrate" && !jobs.Environment.IsDevelopment() && command != "--bootstrap-admin" && command != "--repair-admin")
        throw new InvalidOperationException("Local service commands require Development.");
    jobs.Services.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(
        OperatorConfiguration.Connection(jobs.Configuration)));
    jobs.Services.AddArchiveIdentity(jobs.Configuration);
    jobs.Services.AddSingleton<LocalServices>();
    jobs.Services.AddSingleton(TimeProvider.System);
    jobs.Services.AddOptions<AssetStorageOptions>().BindConfiguration("Archive:Assets");
    jobs.Services.AddSingleton<BlobAssetStorageAdapter>();
    jobs.Services.AddSingleton<IAssetStorageAdapter>(sp => sp.GetRequiredService<BlobAssetStorageAdapter>());
    jobs.Services.AddScoped<UploadSessionCleaner>();
    using var host = jobs.Build();
    await host.StartAsync();
    Environment.ExitCode = await host.RunArchiveJobAsync(command[2..], async token =>
    {
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;
        if (command == "--migrate")
            await provider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync(token);
        else if (command == "--initialize-local-storage")
            await provider.GetRequiredService<LocalServices>().InitializeAsync(token);
        else if (command == "--worker-smoke")
            await provider.GetRequiredService<LocalServices>().ExerciseAsync(token);
        else if (command == "--cleanup-uploads")
        {
            // ARC-017 bounded cleanup: finite job abandons ticket-expired
            // upload sessions and releases their pending blobs best-effort.
            var cleaned = await provider.GetRequiredService<UploadSessionCleaner>()
                .CleanAsync(token);
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Jobs")
                .LogInformation("Cleanup marked {AbandonedCount} upload sessions abandoned", cleaned);
        }
        else if (command == "--seed-dev-auth")
            await OperatorConfiguration.SeedDevAuthAsync(provider, token);
        else if (command == "--bootstrap-admin")
            await OperatorConfiguration.BootstrapAdminAsync(provider, host.Services.GetRequiredService<IConfiguration>(), args.Skip(1).ToArray(), token);
        else if (command == "--send-test-mail")
            await SendTestMailAsync(provider, args.Skip(1).ToArray(), token);
        else
            await OperatorConfiguration.RepairAdminAsync(provider, host.Services.GetRequiredService<IConfiguration>(), args.Skip(1).ToArray(), token);
    }, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(
    DatabaseConfiguration.Connection(builder.Configuration, "archive-db")));
builder.Services.AddSingleton<LocalServices>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<AssetStorageOptions>().BindConfiguration("Archive:Assets");
builder.Services.AddSingleton<BlobAssetStorageAdapter>();
builder.Services.AddSingleton<IAssetStorageAdapter>(sp => sp.GetRequiredService<BlobAssetStorageAdapter>());
builder.Services.AddScoped<UploadSessionCleaner>();
builder.Services.AddHttpContextAccessor();
// ARC-022: bounded archive chat configuration (availability gate and caps).
builder.Services.AddOptions<ChatOptions>()
	.BindConfiguration(ChatOptions.SectionName);
// Provider seam: Development without provider configuration runs the
// deterministic ScriptedChatClient; a real Azure OpenAI implementation
// (managed identity, pinned model) is added with ARC-022's provider step.
// The Enabled gate keeps ordinary runs inert, so the scripted stand-in is
// registered unconditionally for every environment.
builder.Services.AddSingleton<IChatClient, ScriptedChatClient>();
builder.Services.AddScoped<ArchiveChatService>();
builder.Services.AddArchiveAuth(builder.Configuration, builder.Environment);
// ARC-011: Container Apps terminates TLS at the front proxy and forwards
// plain HTTP to the container. Without forwarded-header processing Kestrel
// sees every hosted request as insecure, so antiforgery (SecurePolicy.Always
// outside Development) throws before any endpoint runs and no hosted sign-in
// can complete. The container is reachable only through the managed ingress,
// so the front proxy is trusted explicitly. Must run before routing/auth.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "archive.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.ConfigureDataProtection(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()
	&& !app.Configuration.GetValue<bool>(DataProtectionConfiguration.AllowEphemeralKeysForTestsKey))
{
	// URIs only: they name the account/vault/key, never a secret.
	app.Logger.LogInformation("Data Protection keys persist in {KeysBlobUri} wrapped by {KeysKeyVaultKeyUri}",
		app.Configuration[DataProtectionConfiguration.BlobUriKey],
		app.Configuration[DataProtectionConfiguration.KeyVaultKeyUriKey]);
}
// Do not return provider exception details (including credentials) to browsers.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    SuppressDiagnosticsCallback = _ => true,
    ExceptionHandler = context =>
    {
        var failure = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        // Type and frames only: exception messages may carry PII (for
        // example database constraint details), so they never reach logs.
        app.Logger.LogError("Archive request failed ({ExceptionType}) {StackTrace}",
            failure?.Error.GetType().Name, failure?.Error.StackTrace);
        return Results.Problem(statusCode: 500, title: "Die Anfrage konnte nicht verarbeitet werden.").ExecuteAsync(context);
    }
});
app.UseStatusCodePages(async context =>
{
    if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        await Results.Problem(statusCode: context.HttpContext.Response.StatusCode).ExecuteAsync(context.HttpContext);
});
// Resolve exported route directories before endpoint routing, but never serve
// a file from the reserved API namespace.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), frontend =>
{
    frontend.UseDefaultFiles();
    frontend.UseStaticFiles();
});
app.UseRouting();
app.UseArchiveMaintenanceMode();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
using var meter = new Meter(Extensions.MeterName);
var buildRequests = meter.CreateCounter<long>("archive.build.requests");
app.MapGet("/api/build", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    buildRequests.Add(1);
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    app.Logger.LogInformation("Archive build information requested ({Version})", version);
    return Results.Ok(new { application = "Liedertafel Archiv", version, development = app.Environment.IsDevelopment() });
});
app.MapGet("/api/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
});
// ARC-012: maintenance contract endpoint. The pausing middleware runs
// above (before auth), so conflicting work answers 503 while the flag is
// set. Probes and release endpoints (/alive, /health, /api/build,
// /api/maintenance) stay available; the frontend keeps serving so members
// see the German maintenance banner.
app.MapMaintenanceEndpoints();
app.MapAuthEndpoints();
app.MapPasskeyEndpoints();
app.MapMemberAdminEndpoints();
app.MapCatalogueEndpoints();
app.MapChatEndpoints();
app.MapAssetEndpoints();
app.MapDevelopmentDiagnostics();

// A specific fallback reserves the entire API namespace, including missing files.
app.MapFallback("/api/{**path}", () =>
    Results.Problem(statusCode: 404, title: "API-Endpunkt nicht gefunden."));
// Next exports real route directories, including /system/status/. Unknown routes
// keep their 404 instead of hydrating a mismatched App Router index document.
app.MapFallback(async context =>
{
    context.Response.StatusCode = 404;
    var page = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "404.html");
    if (File.Exists(page) && HttpMethods.IsGet(context.Request.Method))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(page);
    }
});
await app.RunAsync();

/// <summary>
/// Explicit ARC-010 integration run: sends the marked German test message
/// through the configured Azure sender. Development-only (see the gate
/// above) and refuses to run unless <c>Mail:Provider=Azure</c> is selected,
/// so ordinary local runs can never emit real mail by accident.
/// </summary>
static async Task SendTestMailAsync(IServiceProvider provider, string[] mailArgs, CancellationToken token)
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    var mail = configuration.GetSection(MailOptions.SectionName).Get<MailOptions>() ?? new MailOptions();
    if (!mail.IsAzure)
        throw new InvalidOperationException("Der Azure-Versandtest erfordert Mail:Provider=Azure.");
    if (mailArgs.Length != 1 || !AuthSecurity.TryNormalizeEmail(mailArgs[0], out _))
        throw new InvalidOperationException("Verwendung: --send-test-mail <empfaenger-adresse>.");
    var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("ArchiveMailTest");
    await provider.GetRequiredService<IArchiveMailSender>().SendTestMessageAsync(mailArgs[0].Trim(), token);
    logger.LogInformation("Azure test mail accepted for delivery");
}

public partial class Program;
