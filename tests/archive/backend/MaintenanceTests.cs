using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Archive.Backend.Tests;

// ARC-012: the shared maintenance state for controlled database releases.
// While Archive:MaintenanceMode is set, every /api/* request except the
// release contract endpoints answers 503 with the German maintenance
// message; probes, /api/build and /api/maintenance stay available so the
// release workflow and future jobs can observe the window.
public sealed class MaintenanceTests
{
    [Fact]
    public async Task MaintenanceEndpointReportsAvailableByDefault()
    {
        await using var factory = new MaintenanceFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/maintenance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var state = await response.Content.ReadFromJsonAsync<MaintenanceResponse>();
        Assert.False(state!.Maintenance);
        Assert.NotEmpty(state.Message);
    }

    [Fact]
    public async Task MaintenanceEndpointReportsTheActiveWindow()
    {
        await using var factory = new MaintenanceFactory("true");
        using var client = factory.CreateClient();
        var state = await client.GetFromJsonAsync<MaintenanceResponse>("/api/maintenance");
        Assert.True(state!.Maintenance);
        Assert.Contains("Wartung", state.Message);
    }

    [Theory]
    [InlineData("/alive")]
    [InlineData("/api/build")]
    [InlineData("/api/maintenance")]
    public async Task ProbesAndReleaseContractStayAvailable(string path)
    {
        await using var factory = new MaintenanceFactory("true");
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/antiforgery")]
    [InlineData("POST", "/api/auth/code/request")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/api/nonexistent-arc012-probe")]
    public async Task ConflictingApiWorkPausesWithGermanProblem(string method, string path)
    {
        await using var factory = new MaintenanceFactory("true");
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Wartung", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiServesNormallyOutsideTheWindow()
    {
        await using var factory = new MaintenanceFactory();
        using var client = factory.CreateClient();
        // Hosted requests arrive as plain HTTP behind the TLS-terminating
        // front proxy; production antiforgery needs the forwarded proto.
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/antiforgery")).StatusCode);
    }

    private sealed record MaintenanceResponse(bool Maintenance, string Message);
}

internal sealed class MaintenanceFactory : WebApplicationFactory<Program>
{
    private readonly string? maintenance;
    private readonly string root = Path.Combine(Path.GetTempPath(), $"archive-maintenance-{Guid.NewGuid():N}");

    public MaintenanceFactory(string? maintenance = null)
    {
        this.maintenance = maintenance;
        Directory.CreateDirectory(Path.Combine(root, "system/status"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<html lang=de><h1>frontend-fixture</h1></html>");
        File.WriteAllText(Path.Combine(root, "system/status/index.html"), "<html lang=de><h1>frontend-fixture status</h1></html>");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseEnvironment("Production").UseWebRoot(root)
            .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1")
            .UseSetting("OTEL_EXPORTER_OTLP_TIMEOUT", "10")
            .UseSetting("Development:KeysPath", Path.Combine(root, "keys"))
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:archive-db"] = null,
                    ["Archive:MaintenanceMode"] = maintenance,
                    // Antiforgery needs a key ring; the ephemeral escape
                    // keeps these tests I/O-free like the other factories.
                    ["Authentication:AllowEphemeralKeysForTests"] = "true",
                }));
        foreach (var (key, value) in AuthApiFactory.ProductionMailSettings)
            builder.UseSetting(key, value);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
