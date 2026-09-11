using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    public const string ActivitySourceName = "Liedertafel.Archive";
    public const string MeterName = "Liedertafel.Archive";
    public static readonly ActivitySource Activities = new(ActivitySourceName);

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Development requires OTLP configuration. Start the archive through AppHost.");

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName))
            .WithMetrics(metrics => metrics.AddMeter(MeterName)
                .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation())
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsDevelopment()) tracing.SetSampler(new AlwaysOnSampler());
                tracing.AddSource(ActivitySourceName, "Npgsql", "Azure.*")
                    .AddProcessor(new UrlRedactionProcessor())
                    .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/alive") &&
                        !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation();
            });
        // UseOtlpExporter exports all three signals and consumes injected protocol/headers.
        if (!string.IsNullOrWhiteSpace(endpoint)) telemetry.UseOtlpExporter();

        builder.Services.AddServiceDiscovery();
        // Retries must be chosen per operation; never silently retry archive writes.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Neither endpoint resolves a database or storage client, in any environment.
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
        if (app.Environment.IsDevelopment()) app.MapHealthChecks("/health");
        return app;
    }

    // Future finite workers call this after StartAsync, including on handled failure.
    // A queue envelope carries traceparent/tracestate, never secrets or baggage.
    public static async Task<int> RunArchiveJobAsync(this IHost host, string jobName,
        Func<CancellationToken, Task> execute, CancellationToken cancellationToken = default)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Jobs");
        var exitCode = 0;
        using (var activity = Activities.StartActivity(jobName, ActivityKind.Internal))
        {
            try
            {
                await execute(cancellationToken);
                logger.LogInformation("Archive job {JobName} completed", jobName);
            }
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                // Provider exception messages can contain connection details or signed URLs.
                logger.LogError("Archive job {JobName} failed ({ExceptionType})", jobName, exception.GetType().Name);
                exitCode = 1;
            }
        }
        var traces = host.Services.GetService<TracerProvider>()?.ForceFlush(5000) ?? true;
        var metrics = host.Services.GetService<MeterProvider>()?.ForceFlush(5000) ?? true;
        if (!traces || !metrics)
        {
            logger.LogError("Archive job telemetry flush timed out");
            exitCode = 1;
        }
        if (host.Services.GetService<LoggerProvider>()?.ForceFlush(5000) == false) exitCode = 1;
        await host.StopAsync(CancellationToken.None);
        return exitCode;
    }
}

// Azure dependency spans also carry URL tags. Strip queries regardless of SDK
// instrumentation defaults so queue receipts and future SAS never reach OTLP.
internal sealed class UrlRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        foreach (var key in new[] { "url.full", "http.url", "url.query" })
        {
            if (activity.GetTagItem(key) is not string value) continue;
            var query = value.IndexOf('?');
            if (key == "url.query") activity.SetTag(key, "[redacted]");
            else if (query >= 0) activity.SetTag(key, value[..query] + "?[redacted]");
        }
    }
}
