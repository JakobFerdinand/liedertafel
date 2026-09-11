using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Archive.AppHost.Tests;

public sealed class WalkingSkeletonTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CleanStartConnectsDependenciesAndRunsExplicitMigrationsAndWorker()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Archive_AppHost>(
            ["--Archive:PersistLocalData=false"], (options, _) => options.DisableDashboard = false, token);
        await using var app = await builder.BuildAsync(token);
        await app.StartAsync(token);
        try
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync("archive-api", token);
        }
        catch
        {
            var logs = app.Services.GetRequiredService<ResourceLoggerService>();
            foreach (var name in new[] { "archive-api", "archive-storage-init" })
                await foreach (var batch in logs.GetAllAsync(name))
                    foreach (var line in batch) output.WriteLine($"{name}: {line.Content}");
            throw;
        }
        await app.ResourceNotifications.WaitForResourceHealthyAsync("archive-frontend", token);
        await app.ResourceNotifications.WaitForResourceAsync("archive-migrate", KnownResourceStates.NotStarted, token);
        await app.ResourceNotifications.WaitForResourceAsync("archive-worker-smoke", KnownResourceStates.NotStarted, token);

        using var frontend = app.CreateHttpClient("archive-frontend", "http");
        Assert.Contains("Systemstatus", await frontend.GetStringAsync("/system/status/", token));
        var build = await frontend.GetFromJsonAsync<JsonElement>("/api/build", token);
        Assert.Equal("Liedertafel Archiv", build.GetProperty("application").GetString());
        var database = await frontend.GetFromJsonAsync<JsonElement>("/api/dev/database", token);
        Assert.True(database.GetProperty("connected").GetBoolean());
        Assert.Single(database.GetProperty("pendingMigrations").EnumerateArray());

        // Merely starting the API did not apply a schema. Execute it explicitly.
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        await commands.ExecuteCommandAsync("archive-migrate", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-migrate", token);
        database = await frontend.GetFromJsonAsync<JsonElement>("/api/dev/database", token);
        Assert.Empty(database.GetProperty("pendingMigrations").EnumerateArray());

        // Cookie/token flow crosses the real Next.js development proxy.
        var csrf = await frontend.GetAsync("/api/antiforgery", token);
        var csrfBody = await csrf.Content.ReadFromJsonAsync<JsonElement>(token);
        using var exercise = new HttpRequestMessage(HttpMethod.Post, "/api/dev/exercise");
        exercise.Headers.Add("Cookie", csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        exercise.Headers.Add("X-CSRF-TOKEN", csrfBody.GetProperty("token").GetString());
        var exerciseResponse = await frontend.SendAsync(exercise, token);
        Assert.Equal(HttpStatusCode.OK, exerciseResponse.StatusCode);

        await commands.ExecuteCommandAsync("archive-worker-smoke", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-worker-smoke", token);
        using var mail = app.CreateHttpClient("archive-mail", "http");
        var messages = await mail.GetFromJsonAsync<JsonElement>("/api/v1/messages", token);
        Assert.Equal(2, messages.GetProperty("total").GetInt32());
    }

    private static async Task AssertSuccessfulCompletion(ResourceNotificationService notifications, string name, CancellationToken token)
    {
        int? exitCode = null;
        await notifications.WaitForResourceAsync(name, update =>
        {
            if (update.Snapshot.State?.Text != KnownResourceStates.Finished) return false;
            exitCode = update.Snapshot.ExitCode;
            return true;
        }, token);
        Assert.Equal(0, exitCode);
    }
}
