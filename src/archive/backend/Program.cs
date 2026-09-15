using System.Diagnostics.Metrics;
using System.Reflection;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Development;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var command = args.FirstOrDefault();
if (command is "--migrate" or "--initialize-local-storage" or "--worker-smoke" or "--bootstrap-admin" or "--seed-dev-auth")
{
    var jobs = Host.CreateApplicationBuilder(args.Skip(1).ToArray());
    jobs.AddServiceDefaults();
    if (command != "--migrate" && !jobs.Environment.IsDevelopment() && command != "--bootstrap-admin")
        throw new InvalidOperationException("Local service commands require Development.");
    jobs.Services.AddDbContext<ArchiveDbContext>(options => options.UseNpgsql(
        OperatorConfiguration.Connection(jobs.Configuration)));
    jobs.Services.AddArchiveIdentity(jobs.Configuration);
    jobs.Services.AddSingleton<LocalServices>();
    jobs.Services.AddSingleton(TimeProvider.System);
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
        else if (command == "--seed-dev-auth")
            await OperatorConfiguration.SeedDevAuthAsync(provider, token);
        else
            await OperatorConfiguration.BootstrapAdminAsync(provider, host.Services.GetRequiredService<IConfiguration>(), args.Skip(1).ToArray(), token);
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddArchiveAuth(builder.Configuration, builder.Environment);
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "archive.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
var protection = builder.Services.AddDataProtection().SetApplicationName(
    builder.Environment.IsDevelopment() ? "Liedertafel.Archive.Development" : "Liedertafel.Archive");
if (builder.Environment.IsDevelopment())
{
    var path = builder.Configuration["Development:KeysPath"]
        ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../.local/keys"));
    protection.PersistKeysToFileSystem(Directory.CreateDirectory(path));
}
else if (!string.IsNullOrWhiteSpace(builder.Configuration["Authentication:KeysPath"]))
{
    // ARC-011 owns Blob/Key Vault persistence. Until then an explicitly configured
    // filesystem path (mounted volume) keeps sessions restart-safe without
    // inventing a production Blob path in this slice.
    protection.PersistKeysToFileSystem(
        Directory.CreateDirectory(builder.Configuration["Authentication:KeysPath"]!));
}

var app = builder.Build();
// Do not return provider exception details (including credentials) to browsers.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    SuppressDiagnosticsCallback = _ => true,
    ExceptionHandler = context =>
    {
        var failure = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        app.Logger.LogError("Archive request failed ({ExceptionType})", failure?.Error.GetType().Name);
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
app.MapAuthEndpoints();
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

public partial class Program;
