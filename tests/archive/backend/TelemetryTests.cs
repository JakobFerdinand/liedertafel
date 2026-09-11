using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Archive.Backend.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void DevelopmentRejectsConsoleOnlyTelemetry()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "";
        Assert.Throws<InvalidOperationException>(() => builder.AddServiceDefaults());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiniteJobsFlushAuthenticatedOtlpLogsTracesAndMetricsEvenOnFailure(bool fail)
    {
        var received = new ConcurrentDictionary<string, string>();
        var collectorBuilder = WebApplication.CreateBuilder();
        collectorBuilder.Logging.ClearProviders();
        await using var collector = collectorBuilder.Build();
        collector.Urls.Add("http://127.0.0.1:0");
        collector.MapPost("/v1/{signal}", async (HttpContext context, string signal) =>
        {
            Assert.Equal("local-test", context.Request.Headers["x-otlp-test"]);
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            received[signal] = received.GetValueOrDefault(signal, "") + Encoding.UTF8.GetString(body.ToArray());
            context.Response.ContentType = "application/x-protobuf";
        });
        await collector.StartAsync();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Urls.Single(),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-test=local-test",
            ["OTEL_SERVICE_NAME"] = "archive-test-worker"
        });
        builder.AddServiceDefaults();
        using var host = builder.Build();
        await host.StartAsync();
        using var meter = new Meter(Extensions.MeterName);
        var counter = meter.CreateCounter<long>("archive.test.work");
        var result = await host.RunArchiveJobAsync("verification-job", _ =>
        {
            System.Diagnostics.Activity.Current?.SetTag("url.full", "https://storage.test/file?sig=secret-do-not-export");
            counter.Add(1);
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Test").LogInformation("Worker test started");
            if (fail) throw new InvalidOperationException("secret-do-not-export");
            return Task.CompletedTask;
        });

        Assert.Equal(fail ? 1 : 0, result);
        foreach (var signal in new[] { "logs", "traces", "metrics" })
        {
            Assert.True(received.ContainsKey(signal), $"No OTLP {signal} received");
            Assert.Contains("archive-test-worker", received[signal]);
            Assert.DoesNotContain("secret-do-not-export", received[signal]);
        }
        Assert.Contains("verification-job", received["traces"]);
        Assert.Contains("archive.test.work", received["metrics"]);
        Assert.Contains("Worker test started", received["logs"]);
    }
}
