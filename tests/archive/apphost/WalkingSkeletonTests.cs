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

        // ARC-006 invitations through the real Next.js proxy and Mailpit.
        // The administrator signs in, invites a new singer, and the recipient
        // accepts through the existing email-code flow with a stable account ID.
        // A jar-free client carries exactly the manually attached session:
        // the shared client's cookie jar still holds earlier member/editor
        // tickets, and even a fresh HttpClient would accumulate competing
        // archive.auth cookies from the verify responses below.
        using var api = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = frontend.BaseAddress,
        };
        var (adminCsrfCookie, adminToken) = await GetCsrfAsync(api, null, token);
        using var adminRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        adminRequest.Headers.Add("Cookie", adminCsrfCookie);
        adminRequest.Headers.Add("X-CSRF-TOKEN", adminToken);
        adminRequest.Content = JsonContent.Create(new { email = "verwaltung@liedertafel.test" });
        using var adminRequestResponse = await api.SendAsync(adminRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, adminRequestResponse.StatusCode);
        var adminCode = await GetSignInCodeAsync(mail, "verwaltung@liedertafel.test", token);
        var (adminVerifyCsrfCookie, adminVerifyToken) = await GetCsrfAsync(api, adminCsrfCookie, token);
        using var adminVerify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        adminVerify.Headers.Add("Cookie", adminVerifyCsrfCookie);
        adminVerify.Headers.Add("X-CSRF-TOKEN", adminVerifyToken);
        adminVerify.Content = JsonContent.Create(new { email = "verwaltung@liedertafel.test", code = adminCode });
        using var adminVerifyResponse = await api.SendAsync(adminVerify, token);
        Assert.Equal(HttpStatusCode.OK, adminVerifyResponse.StatusCode);
        var adminSession = Assert.Single(
            adminVerifyResponse.Headers.GetValues("Set-Cookie"), c => c.StartsWith("archive.auth=")).Split(';')[0];

        var (inviteCsrfCookie, inviteToken) = await GetCsrfAsync(api, $"{adminVerifyCsrfCookie}; {adminSession}", token);
        using var invite = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
        invite.Headers.Add("Cookie", $"{inviteCsrfCookie}; {adminSession}");
        invite.Headers.Add("X-CSRF-TOKEN", inviteToken);
        invite.Content = JsonContent.Create(new { email = "neu@liedertafel.test", displayName = "Neue Stimme", role = "Member" });
        using var inviteResponse = await api.SendAsync(invite, token);
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
        var inviteBody = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        // Mail acceptance is reported, delivery is explicitly not confirmed.
        Assert.Contains("zum Versand angenommen", inviteBody.GetProperty("message").GetString());
        Assert.Contains("Zustellung wird nicht bestätigt", inviteBody.GetProperty("message").GetString());
        var invitedAccountId = inviteBody.GetProperty("accountId").GetString()!;
        await WaitForInvitationMailAsync(mail, "neu@liedertafel.test", token);

        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
        list.Headers.Add("Cookie", adminSession);
        using var listResponse = await api.SendAsync(list, token);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var invitedEntry = listBody.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "neu@liedertafel.test");
        Assert.Equal("invited", invitedEntry.GetProperty("status").GetString());
        Assert.Contains("Member", invitedEntry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!));

        // Repeated submissions neither duplicate nor change the role silently.
        var (dupCsrfCookie, dupToken) = await GetCsrfAsync(api, $"{inviteCsrfCookie}; {adminSession}", token);
        using var dupSame = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
        dupSame.Headers.Add("Cookie", $"{dupCsrfCookie}; {adminSession}");
        dupSame.Headers.Add("X-CSRF-TOKEN", dupToken);
        dupSame.Content = JsonContent.Create(new { email = "neu@liedertafel.test", displayName = "Neue Stimme", role = "Member" });
        using var dupSameResponse = await api.SendAsync(dupSame, token);
        Assert.Equal(HttpStatusCode.OK, dupSameResponse.StatusCode);
        using var dupRole = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
        dupRole.Headers.Add("Cookie", $"{dupCsrfCookie}; {adminSession}");
        dupRole.Headers.Add("X-CSRF-TOKEN", dupToken);
        dupRole.Content = JsonContent.Create(new { email = "neu@liedertafel.test", role = "Editor" });
        using var dupRoleResponse = await api.SendAsync(dupRole, token);
        Assert.Equal(HttpStatusCode.Conflict, dupRoleResponse.StatusCode);
        using var dupActive = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
        dupActive.Headers.Add("Cookie", $"{dupCsrfCookie}; {adminSession}");
        dupActive.Headers.Add("X-CSRF-TOKEN", dupToken);
        dupActive.Content = JsonContent.Create(new { email = "mitglied@liedertafel.test", role = "Editor" });
        using var dupActiveResponse = await api.SendAsync(dupActive, token);
        Assert.Equal(HttpStatusCode.Conflict, dupActiveResponse.StatusCode);

        // The invited singer accepts with the existing code flow; the stable
        // account ID from the invitation matches the verified session.
        var (neuCsrfCookie, neuToken) = await GetCsrfAsync(api, dupCsrfCookie, token);
        using var neuRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        neuRequest.Headers.Add("Cookie", neuCsrfCookie);
        neuRequest.Headers.Add("X-CSRF-TOKEN", neuToken);
        neuRequest.Content = JsonContent.Create(new { email = "neu@liedertafel.test" });
        using var neuRequestResponse = await api.SendAsync(neuRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, neuRequestResponse.StatusCode);
        var neuCode = await GetSignInCodeAsync(mail, "neu@liedertafel.test", token);
        var (neuVerifyCsrfCookie, neuVerifyToken) = await GetCsrfAsync(api, neuCsrfCookie, token);
        using var neuVerify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        neuVerify.Headers.Add("Cookie", neuVerifyCsrfCookie);
        neuVerify.Headers.Add("X-CSRF-TOKEN", neuVerifyToken);
        neuVerify.Content = JsonContent.Create(new { email = "neu@liedertafel.test", code = neuCode });
        using var neuVerifyResponse = await api.SendAsync(neuVerify, token);
        Assert.Equal(HttpStatusCode.OK, neuVerifyResponse.StatusCode);
        var neuVerifyBody = await neuVerifyResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(invitedAccountId, neuVerifyBody.GetProperty("accountId").GetString());
        var neuSession = Assert.Single(
            neuVerifyResponse.Headers.GetValues("Set-Cookie"), c => c.StartsWith("archive.auth=")).Split(';')[0];

        // Ordinary members cannot invoke invitation administration.
        var (memberCsrfCookie, memberToken) = await GetCsrfAsync(api, $"{neuVerifyCsrfCookie}; {neuSession}", token);
        using var memberInvite = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
        memberInvite.Headers.Add("Cookie", $"{memberCsrfCookie}; {neuSession}");
        memberInvite.Headers.Add("X-CSRF-TOKEN", memberToken);
        memberInvite.Content = JsonContent.Create(new { email = "weiter@liedertafel.test", role = "Member" });
        using var memberInviteResponse = await api.SendAsync(memberInvite, token);
        Assert.Equal(HttpStatusCode.Forbidden, memberInviteResponse.StatusCode);

        // After acceptance the member list shows the active state; resending
        // an accepted invitation is rejected without side effects.
        using var listAfter = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
        listAfter.Headers.Add("Cookie", adminSession);
        using var listAfterResponse = await api.SendAsync(listAfter, token);
        Assert.Equal(HttpStatusCode.OK, listAfterResponse.StatusCode);
        var listAfterBody = await listAfterResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("active", listAfterBody.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "neu@liedertafel.test")
            .GetProperty("status").GetString());
        using var resendAccepted = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations/resend");
        resendAccepted.Headers.Add("Cookie", $"{dupCsrfCookie}; {adminSession}");
        resendAccepted.Headers.Add("X-CSRF-TOKEN", dupToken);
        resendAccepted.Content = JsonContent.Create(new { email = "neu@liedertafel.test" });
        using var resendAcceptedResponse = await api.SendAsync(resendAccepted, token);
        Assert.Equal(HttpStatusCode.Conflict, resendAcceptedResponse.StatusCode);

        await commands.ExecuteCommandAsync("archive-worker-smoke", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-worker-smoke", token);
        var messages = await mail.GetFromJsonAsync<JsonElement>("/api/v1/messages", token);
        // Diagnostic mail + member/editor/admin code mails + two invitation
        // mails (invite + same-role resend) + invited code mail + worker mail.
        Assert.Equal(8, messages.GetProperty("total").GetInt32());
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

    private static async Task WaitForInvitationMailAsync(HttpClient mail, string email, CancellationToken token)
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
                var raw = detail.GetRawText();
                if (raw.Contains(email, StringComparison.OrdinalIgnoreCase)
                    && raw.Contains("Einladung zum Liedertafel-Archiv", StringComparison.Ordinal))
                    return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new Xunit.Sdk.XunitException($"No invitation mail found for {email}.");
    }
}
