using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        // API start must not apply schema: auth migrations are still pending.
        var pending = database.GetProperty("pendingMigrations").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty).ToList();
        Assert.Contains(pending, m => m.EndsWith("_AuthSignIn", StringComparison.Ordinal));
        Assert.Contains(pending, m => m.EndsWith("_AuthSignInConcurrency", StringComparison.Ordinal));

        // Merely starting the API did not apply a schema. Execute it explicitly.
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        await commands.ExecuteCommandAsync("archive-migrate", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-migrate", token);
        database = await frontend.GetFromJsonAsync<JsonElement>("/api/dev/database", token);
        Assert.Empty(database.GetProperty("pendingMigrations").EnumerateArray());

        // Cookie/token flow crosses the real Next.js development proxy.
        var csrf = await frontend.GetAsync("/api/antiforgery", token);
        var csrfBody = await csrf.Content.ReadFromJsonAsync<JsonElement>(token);
        var csrfCookie = csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var exercise = new HttpRequestMessage(HttpMethod.Post, "/api/dev/exercise");
        exercise.Headers.Add("Cookie", csrfCookie);
        exercise.Headers.Add("X-CSRF-TOKEN", csrfBody.GetProperty("token").GetString());
        var exerciseResponse = await frontend.SendAsync(exercise, token);
        Assert.Equal(HttpStatusCode.OK, exerciseResponse.StatusCode);

        // ARC-005 email-code sign-in through the real Next.js proxy.
        var (seedCsrfCookie, seedToken) = await GetCsrfAsync(frontend, csrfCookie, token);
        using var seed = new HttpRequestMessage(HttpMethod.Post, "/api/dev/auth/seed");
        seed.Headers.Add("Cookie", seedCsrfCookie);
        seed.Headers.Add("X-CSRF-TOKEN", seedToken);
        seed.Content = JsonContent.Create(new { });
        using var seedResponse = await frontend.SendAsync(seed, token);
        Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);
        var seedBody = await seedResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var roles = seedBody.GetProperty("accounts").EnumerateArray()
            .Select(a => a.GetProperty("role").GetString()!).OrderBy(r => r).ToArray();
        Assert.Equal(["Administrator", "Editor", "Member"], roles);

        // No-disclosure check first: one request per address, well below the
        // 5 requests/email/hour limit.
        var (unknownCsrfCookie, unknownToken) = await GetCsrfAsync(frontend, seedCsrfCookie, token);
        using var unknownRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        unknownRequest.Headers.Add("Cookie", unknownCsrfCookie);
        unknownRequest.Headers.Add("X-CSRF-TOKEN", unknownToken);
        unknownRequest.Content = JsonContent.Create(new { email = "fremd@liedertafel.test" });
        using var unknownResponse = await frontend.SendAsync(unknownRequest, token);
        var unknownBody = await unknownResponse.Content.ReadAsStringAsync(token);

        var (knownCsrfCookie, knownToken) = await GetCsrfAsync(frontend, unknownCsrfCookie, token);
        using var knownRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        knownRequest.Headers.Add("Cookie", knownCsrfCookie);
        knownRequest.Headers.Add("X-CSRF-TOKEN", knownToken);
        knownRequest.Content = JsonContent.Create(new { email = "mitglied@liedertafel.test" });
        using var knownResponse = await frontend.SendAsync(knownRequest, token);
        var knownBody = await knownResponse.Content.ReadAsStringAsync(token);

        Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);
        Assert.Equal(knownBody, unknownBody);

        using var mail = app.CreateHttpClient("archive-mail", "http");
        var code = await GetSignInCodeAsync(mail, "mitglied@liedertafel.test", token);

        var (verifyCsrfCookie, verifyToken) = await GetCsrfAsync(frontend, knownCsrfCookie, token);
        using var verify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        verify.Headers.Add("Cookie", verifyCsrfCookie);
        verify.Headers.Add("X-CSRF-TOKEN", verifyToken);
        verify.Content = JsonContent.Create(new { email = "mitglied@liedertafel.test", code });
        using var verifyResponse = await frontend.SendAsync(verify, token);
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var sessionCookie = Assert.Single(
            verifyResponse.Headers.GetValues("Set-Cookie"), c => c.StartsWith("archive.auth=")).Split(';')[0];

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Add("Cookie", sessionCookie);
        using var meResponse = await frontend.SendAsync(me, token);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.True(meBody.GetProperty("authenticated").GetBoolean());
        Assert.Contains("Member", meBody.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!));
        Assert.Contains("no-store", meResponse.Headers.CacheControl!.ToString());

        // Browsers fetch the token with the session cookie attached, which
        // binds the antiforgery token to the authenticated user.
        var (_, logoutToken) = await GetCsrfAsync(frontend, $"{verifyCsrfCookie}; {sessionCookie}", token);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", $"{verifyCsrfCookie}; {sessionCookie}");
        logout.Headers.Add("X-CSRF-TOKEN", logoutToken);
        logout.Content = JsonContent.Create(new { });
        using var logoutResponse = await frontend.SendAsync(logout, token);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        // The shared test-client cookie jar keeps sending the session ticket
        // after logout (cookie auth is stateless), so the anonymous check
        // needs a jar-free client to prove the cookie is truly gone.
        using var anonymous = new HttpClient { BaseAddress = frontend.BaseAddress };
        var meAfter = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/me", token);
        Assert.False(meAfter.GetProperty("authenticated").GetBoolean());

        // Concurrent race on real PostgreSQL: exactly one winner, the rest
        // get the uniform invalid-code response (no 500s). Uses the editor
        // account so no resend cooldown applies.
        var (editorCsrfCookie, editorToken) = await GetCsrfAsync(frontend, verifyCsrfCookie, token);
        using var editorRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        editorRequest.Headers.Add("Cookie", editorCsrfCookie);
        editorRequest.Headers.Add("X-CSRF-TOKEN", editorToken);
        editorRequest.Content = JsonContent.Create(new { email = "redaktion@liedertafel.test" });
        using var editorResponse = await frontend.SendAsync(editorRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, editorResponse.StatusCode);
        var editorCode = await GetSignInCodeAsync(mail, "redaktion@liedertafel.test", token);
        var race = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            var (raceCsrfCookie, raceToken) = await GetCsrfAsync(frontend, editorCsrfCookie, token);
            using var raceVerify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
            raceVerify.Headers.Add("Cookie", raceCsrfCookie);
            raceVerify.Headers.Add("X-CSRF-TOKEN", raceToken);
            raceVerify.Content = JsonContent.Create(new { email = "redaktion@liedertafel.test", code = editorCode });
            using var raceResponse = await frontend.SendAsync(raceVerify, token);
            return raceResponse.StatusCode;
        }));
        Assert.Single(race, s => s == HttpStatusCode.OK);
        Assert.Equal(4, race.Count(s => s == HttpStatusCode.BadRequest));

        await commands.ExecuteCommandAsync("archive-worker-smoke", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-worker-smoke", token);
        var messages = await mail.GetFromJsonAsync<JsonElement>("/api/v1/messages", token);
        // Diagnostic mail + two sign-in code mails + worker-smoke mail.
        Assert.Equal(4, messages.GetProperty("total").GetInt32());
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

    private static async Task<(string Cookie, string Token)> GetCsrfAsync(HttpClient frontend, string? cookie, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        using var response = await frontend.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        var requestToken = body.GetProperty("token").GetString()!;
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            return (Assert.Single(values).Split(';')[0], requestToken);
        // The shared test-client cookie jar already presents a valid
        // antiforgery cookie, so the backend reuses it without re-issuing
        // Set-Cookie. Only a cookieless first call must set one.
        Assert.True(cookie is not null, "GET /api/antiforgery returned no Set-Cookie for a cookieless request.");
        return (cookie, requestToken);
    }

    private static async Task<string> GetSignInCodeAsync(HttpClient mail, string email, CancellationToken token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var list = await mail.GetFromJsonAsync<JsonElement>("/api/v1/messages?limit=50", token);
            var ids = list.GetProperty("messages").EnumerateArray()
                .Select(m => m.TryGetProperty("ID", out var id) ? id.GetString() : null)
                .Where(id => id is not null).Cast<string>().ToArray();
            foreach (var id in ids)
            {
                var detail = await mail.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}", token);
                if (!detail.GetRawText().Contains(email, StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = detail.TryGetProperty("Text", out var textProp) ? textProp.GetString() : null;
                var match = Regex.Match(text ?? string.Empty, @"Ihr Anmeldecode lautet:\s*(\d{6})");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new Xunit.Sdk.XunitException($"No sign-in code mail found for {email}.");
    }
}
