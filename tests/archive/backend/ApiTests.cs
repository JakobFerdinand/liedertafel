using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Archive.Backend.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task BuildAndLivenessWorkWithoutAnyDataServices()
    {
        await using var factory = new ArchiveFactory();
        using var client = factory.CreateClient();
        var build = await client.GetFromJsonAsync<BuildResponse>("/api/build");
        Assert.Equal("Liedertafel Archiv", build!.Application);
        Assert.False(build.Development);
        Assert.NotEmpty(build.Version);
        Assert.Equal("Healthy", await client.GetStringAsync("/alive"));
    }

    [Theory]
    [InlineData("GET", "/api/missing")]
    [InlineData("GET", "/api/missing.js")]
    [InlineData("POST", "/api/missing")]
    [InlineData("GET", "/api/dev/database")]
    [InlineData("POST", "/api/dev/exercise")]
    [InlineData("GET", "/api")]
    [InlineData("GET", "/API/missing")]
    public async Task ApiErrorsNeverReturnTheFrontend(string method, string path)
    {
        await using var factory = new ArchiveFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("frontend-fixture", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/system/status/")]
    [InlineData("/system/status")]
    public async Task ExportedDeepLinksResolveWithoutNode(string path)
    {
        await using var factory = new ArchiveFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync(path);
        Assert.Contains("frontend-fixture", html);
    }

    [Fact]
    public async Task MissingAssetsAndUnknownPagesKeepTheir404()
    {
        await using var factory = new ArchiveFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_next/missing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/unknown/page/")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentMutationRejectsMissingOrForgedCsrf(bool forged)
    {
        await using var factory = new ArchiveFactory("Development");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/exercise");
        if (forged)
        {
            await client.GetAsync("/api/antiforgery");
            request.Headers.Add("X-CSRF-TOKEN", "forged");
        }
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CsrfCookieIsHttpOnlySameSiteAndSecureInProduction()
    {
        await using var factory = new ArchiveFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/api/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    private sealed record BuildResponse(string Application, string Version, bool Development);
}

internal sealed class ArchiveFactory : WebApplicationFactory<Program>
{
    private readonly string environment;
    private readonly string root = Path.Combine(Path.GetTempPath(), $"archive-tests-{Guid.NewGuid():N}");

    public ArchiveFactory(string environment = "Production")
    {
        this.environment = environment;
        Directory.CreateDirectory(Path.Combine(root, "system/status"));
        Directory.CreateDirectory(Path.Combine(root, "api"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<html lang=de><h1>frontend-fixture</h1></html>");
        File.WriteAllText(Path.Combine(root, "system/status/index.html"), "<html lang=de><h1>frontend-fixture status</h1></html>");
        File.WriteAllText(Path.Combine(root, "api/missing.js"), "frontend-fixture must never reach an API request");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment(environment).UseWebRoot(root)
        .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1")
        .UseSetting("OTEL_EXPORTER_OTLP_TIMEOUT", "10")
        .UseSetting("Development:KeysPath", Path.Combine(root, "keys"))
        .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:archive-db"] = null }));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
