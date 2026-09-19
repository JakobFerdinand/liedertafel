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
        Assert.Contains(pending, m => m.EndsWith("_CatalogueSongs", StringComparison.Ordinal));
        Assert.Contains(pending, m => m.EndsWith("_ArrangementVoiceConfigurationAndVersionKeys", StringComparison.Ordinal));
        Assert.Contains(pending, m => m.EndsWith("_ArchiveAssetsAndUploadSessions", StringComparison.Ordinal));

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

        // ARC-007 revocation through the real proxy with two sessions.
        // The administrator deactivates the invited singer; the singer's
        // already-open session obeys on its next request (signed-out),
        // history stays linked to the stable account ID, and reactivation
        // requires a fresh sign-in (old tickets never revive).
        var (deactivateCsrfCookie, deactivateToken) = await GetCsrfAsync(api, $"{dupCsrfCookie}; {adminSession}", token);
        using var deactivate = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/deactivate");
        deactivate.Headers.Add("Cookie", $"{deactivateCsrfCookie}; {adminSession}");
        deactivate.Headers.Add("X-CSRF-TOKEN", deactivateToken);
        deactivate.Content = JsonContent.Create(new { accountId = invitedAccountId });
        using var deactivateResponse = await api.SendAsync(deactivate, token);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var deactivateBody = await deactivateResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Contains("deaktiviert", deactivateBody.GetProperty("message").GetString());
        Assert.Equal("deactivated", deactivateBody.GetProperty("status").GetString());

        using var listRevoked = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
        listRevoked.Headers.Add("Cookie", adminSession);
        using var listRevokedResponse = await api.SendAsync(listRevoked, token);
        var listRevokedBody = await listRevokedResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var revokedEntry = listRevokedBody.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "neu@liedertafel.test");
        Assert.Equal("deactivated", revokedEntry.GetProperty("status").GetString());
        Assert.Equal(invitedAccountId, revokedEntry.GetProperty("accountId").GetString());
        Assert.Contains("Member", revokedEntry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!));

        using var neuMeRevoked = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        neuMeRevoked.Headers.Add("Cookie", neuSession);
        using var neuMeRevokedResponse = await api.SendAsync(neuMeRevoked, token);
        var neuMeRevokedBody = await neuMeRevokedResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.False(neuMeRevokedBody.GetProperty("authenticated").GetBoolean());

        using var neuAdminProbe = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
        neuAdminProbe.Headers.Add("Cookie", neuSession);
        using var neuAdminProbeResponse = await api.SendAsync(neuAdminProbe, token);
        Assert.Equal(HttpStatusCode.Unauthorized, neuAdminProbeResponse.StatusCode);

        // Denied re-verification by the inactive account: no disclosure on
        // request (202) and uniform invalid-code on verify.
        var (revokedCsrfCookie, revokedToken) = await GetCsrfAsync(api, deactivateCsrfCookie, token);
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        revokedRequest.Headers.Add("Cookie", revokedCsrfCookie);
        revokedRequest.Headers.Add("X-CSRF-TOKEN", revokedToken);
        revokedRequest.Content = JsonContent.Create(new { email = "neu@liedertafel.test" });
        using var revokedRequestResponse = await api.SendAsync(revokedRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, revokedRequestResponse.StatusCode);
        var (revokedVerifyCsrfCookie, revokedVerifyToken) = await GetCsrfAsync(api, revokedCsrfCookie, token);
        using var revokedVerify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        revokedVerify.Headers.Add("Cookie", revokedVerifyCsrfCookie);
        revokedVerify.Headers.Add("X-CSRF-TOKEN", revokedVerifyToken);
        revokedVerify.Content = JsonContent.Create(new { email = "neu@liedertafel.test", code = "123456" });
        using var revokedVerifyResponse = await api.SendAsync(revokedVerify, token);
        Assert.Equal(HttpStatusCode.BadRequest, revokedVerifyResponse.StatusCode);

        var (reactivateCsrfCookie, reactivateToken) = await GetCsrfAsync(api, $"{revokedVerifyCsrfCookie}; {adminSession}", token);
        using var reactivate = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/reactivate");
        reactivate.Headers.Add("Cookie", $"{reactivateCsrfCookie}; {adminSession}");
        reactivate.Headers.Add("X-CSRF-TOKEN", reactivateToken);
        reactivate.Content = JsonContent.Create(new { accountId = invitedAccountId });
        using var reactivateResponse = await api.SendAsync(reactivate, token);
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);
        var reactivateBody = await reactivateResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Contains("erneute Anmeldung", reactivateBody.GetProperty("message").GetString());
        Assert.Equal("active", reactivateBody.GetProperty("status").GetString());

        // The pre-revocation ticket stays dead after reactivation.
        using var neuMeStale = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        neuMeStale.Headers.Add("Cookie", neuSession);
        using var neuMeStaleResponse = await api.SendAsync(neuMeStale, token);
        var neuMeStaleBody = await neuMeStaleResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.False(neuMeStaleBody.GetProperty("authenticated").GetBoolean());

        var (neu2CsrfCookie, neu2Token) = await GetCsrfAsync(api, reactivateCsrfCookie, token);
        using var neu2Request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        neu2Request.Headers.Add("Cookie", neu2CsrfCookie);
        neu2Request.Headers.Add("X-CSRF-TOKEN", neu2Token);
        neu2Request.Content = JsonContent.Create(new { email = "neu@liedertafel.test" });
        using var neu2RequestResponse = await api.SendAsync(neu2Request, token);
        Assert.Equal(HttpStatusCode.Accepted, neu2RequestResponse.StatusCode);
        // Two code mails exist for neu@ now (acceptance + reactivation);
        // poll until a code different from the consumed one appears.
        var neu2Code = await GetFreshSignInCodeAsync(mail, "neu@liedertafel.test", neuCode, token);
        var (neu2VerifyCsrfCookie, neu2VerifyToken) = await GetCsrfAsync(api, neu2CsrfCookie, token);
        using var neu2Verify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        neu2Verify.Headers.Add("Cookie", neu2VerifyCsrfCookie);
        neu2Verify.Headers.Add("X-CSRF-TOKEN", neu2VerifyToken);
        neu2Verify.Content = JsonContent.Create(new { email = "neu@liedertafel.test", code = neu2Code });
        using var neu2VerifyResponse = await api.SendAsync(neu2Verify, token);
        Assert.Equal(HttpStatusCode.OK, neu2VerifyResponse.StatusCode);
        var neu2Body = await neu2VerifyResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(invitedAccountId, neu2Body.GetProperty("accountId").GetString());
        var neu2Session = Assert.Single(
            neu2VerifyResponse.Headers.GetValues("Set-Cookie"), c => c.StartsWith("archive.auth=")).Split(';')[0];

        // Role changes obey on the next request without a fresh sign-in.
        var (roleCsrfCookie, roleToken) = await GetCsrfAsync(api, $"{neu2VerifyCsrfCookie}; {adminSession}", token);
        using var roleChange = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/role");
        roleChange.Headers.Add("Cookie", $"{roleCsrfCookie}; {adminSession}");
        roleChange.Headers.Add("X-CSRF-TOKEN", roleToken);
        roleChange.Content = JsonContent.Create(new { accountId = invitedAccountId, role = "Editor" });
        using var roleChangeResponse = await api.SendAsync(roleChange, token);
        Assert.Equal(HttpStatusCode.OK, roleChangeResponse.StatusCode);
        using var neu2Me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        neu2Me.Headers.Add("Cookie", neu2Session);
        using var neu2MeResponse = await api.SendAsync(neu2Me, token);
        var neu2MeBody = await neu2MeResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.True(neu2MeBody.GetProperty("authenticated").GetBoolean());
        Assert.Contains("Editor", neu2MeBody.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!));

        // ARC-008 verified email change through the real proxy and Mailpit.
        // The administrator moves the invited singer to a new address; the
        // stable account ID and history follow, obsolete challenges die, the
        // singer's open session dies on its next request, and the change mail
        // alone never attaches the new address elsewhere.
        var (changeCsrfCookie, changeToken) = await GetCsrfAsync(api, $"{roleCsrfCookie}; {adminSession}", token);
        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/email/request");
        changeRequest.Headers.Add("Cookie", $"{changeCsrfCookie}; {adminSession}");
        changeRequest.Headers.Add("X-CSRF-TOKEN", changeToken);
        changeRequest.Content = JsonContent.Create(new { accountId = invitedAccountId, newEmail = "umzug@liedertafel.test" });
        using var changeRequestResponse = await api.SendAsync(changeRequest, token);
        Assert.Equal(HttpStatusCode.OK, changeRequestResponse.StatusCode);
        var changeCode = await GetEmailChangeCodeAsync(mail, "umzug@liedertafel.test", token);

        // No silent attach: the sign-in flow has no account for the new
        // address yet (uniform 202 without mail) and rejects the change code.
        var (preCsrfCookie, preToken) = await GetCsrfAsync(api, changeCsrfCookie, token);
        using var preRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        preRequest.Headers.Add("Cookie", preCsrfCookie);
        preRequest.Headers.Add("X-CSRF-TOKEN", preToken);
        preRequest.Content = JsonContent.Create(new { email = "umzug@liedertafel.test" });
        using var preRequestResponse = await api.SendAsync(preRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, preRequestResponse.StatusCode);
        var (misCsrfCookie, misToken) = await GetCsrfAsync(api, preCsrfCookie, token);
        using var misuse = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        misuse.Headers.Add("Cookie", misCsrfCookie);
        misuse.Headers.Add("X-CSRF-TOKEN", misToken);
        misuse.Content = JsonContent.Create(new { email = "umzug@liedertafel.test", code = changeCode });
        using var misuseResponse = await api.SendAsync(misuse, token);
        Assert.Equal(HttpStatusCode.BadRequest, misuseResponse.StatusCode);

        var (confirmCsrfCookie, confirmToken) = await GetCsrfAsync(api, $"{misCsrfCookie}; {adminSession}", token);
        using var confirm = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/email/confirm");
        confirm.Headers.Add("Cookie", $"{confirmCsrfCookie}; {adminSession}");
        confirm.Headers.Add("X-CSRF-TOKEN", confirmToken);
        confirm.Content = JsonContent.Create(new { accountId = invitedAccountId, newEmail = "umzug@liedertafel.test", code = changeCode });
        using var confirmResponse = await api.SendAsync(confirm, token);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var confirmBody = await confirmResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Contains("Adresse geändert", confirmBody.GetProperty("message").GetString());
        Assert.Equal("umzug@liedertafel.test", confirmBody.GetProperty("email").GetString());

        using var listMoved = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
        listMoved.Headers.Add("Cookie", adminSession);
        using var listMovedResponse = await api.SendAsync(listMoved, token);
        var listMovedBody = await listMovedResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var movedEntry = listMovedBody.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "umzug@liedertafel.test");
        Assert.Equal(invitedAccountId, movedEntry.GetProperty("accountId").GetString());
        Assert.Equal("active", movedEntry.GetProperty("status").GetString());
        Assert.Contains("Editor", movedEntry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!));

        // The singer's open session dies on its next request; the acting
        // admin session is untouched.
        using var neu2MeMoved = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        neu2MeMoved.Headers.Add("Cookie", neu2Session);
        using var neu2MeMovedResponse = await api.SendAsync(neu2MeMoved, token);
        var neu2MeMovedBody = await neu2MeMovedResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.False(neu2MeMovedBody.GetProperty("authenticated").GetBoolean());

        // The old address no longer signs in; the new one returns the same ID.
        var (oldMailCsrfCookie, oldMailToken) = await GetCsrfAsync(api, confirmCsrfCookie, token);
        using var oldMailRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        oldMailRequest.Headers.Add("Cookie", oldMailCsrfCookie);
        oldMailRequest.Headers.Add("X-CSRF-TOKEN", oldMailToken);
        oldMailRequest.Content = JsonContent.Create(new { email = "neu@liedertafel.test" });
        using var oldMailRequestResponse = await api.SendAsync(oldMailRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, oldMailRequestResponse.StatusCode);
        var (oldVerifyCsrfCookie, oldVerifyToken) = await GetCsrfAsync(api, oldMailCsrfCookie, token);
        using var oldVerify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        oldVerify.Headers.Add("Cookie", oldVerifyCsrfCookie);
        oldVerify.Headers.Add("X-CSRF-TOKEN", oldVerifyToken);
        oldVerify.Content = JsonContent.Create(new { email = "neu@liedertafel.test", code = neu2Code });
        using var oldVerifyResponse = await api.SendAsync(oldVerify, token);
        Assert.Equal(HttpStatusCode.BadRequest, oldVerifyResponse.StatusCode);

        var (movedCsrfCookie, movedToken) = await GetCsrfAsync(api, oldVerifyCsrfCookie, token);
        using var movedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        movedRequest.Headers.Add("Cookie", movedCsrfCookie);
        movedRequest.Headers.Add("X-CSRF-TOKEN", movedToken);
        movedRequest.Content = JsonContent.Create(new { email = "umzug@liedertafel.test" });
        using var movedRequestResponse = await api.SendAsync(movedRequest, token);
        Assert.Equal(HttpStatusCode.Accepted, movedRequestResponse.StatusCode);
        var movedCode = await GetSignInCodeAsync(mail, "umzug@liedertafel.test", token);
        var (movedVerifyCsrfCookie, movedVerifyToken) = await GetCsrfAsync(api, movedCsrfCookie, token);
        using var movedVerify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        movedVerify.Headers.Add("Cookie", movedVerifyCsrfCookie);
        movedVerify.Headers.Add("X-CSRF-TOKEN", movedVerifyToken);
        movedVerify.Content = JsonContent.Create(new { email = "umzug@liedertafel.test", code = movedCode });
        using var movedVerifyResponse = await api.SendAsync(movedVerify, token);
        Assert.Equal(HttpStatusCode.OK, movedVerifyResponse.StatusCode);
        var movedVerifyBody = await movedVerifyResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(invitedAccountId, movedVerifyBody.GetProperty("accountId").GetString());

        // Collision with the administrator's address is rejected cleanly.
        var (collisionCsrfCookie, collisionToken) = await GetCsrfAsync(api, $"{movedVerifyCsrfCookie}; {adminSession}", token);
        using var collision = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/email/request");
        collision.Headers.Add("Cookie", $"{collisionCsrfCookie}; {adminSession}");
        collision.Headers.Add("X-CSRF-TOKEN", collisionToken);
        collision.Content = JsonContent.Create(new { accountId = invitedAccountId, newEmail = "verwaltung@liedertafel.test" });
        using var collisionResponse = await api.SendAsync(collision, token);
        Assert.Equal(HttpStatusCode.Conflict, collisionResponse.StatusCode);

        // Last-administrator handling: the only administrator can neither be
        // deactivated nor demoted; repair belongs to the maintainer path.
        var adminEntry = listRevokedBody.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == "verwaltung@liedertafel.test");
        var adminAccountId = adminEntry.GetProperty("accountId").GetString()!;
        var (lastCsrfCookie, lastToken) = await GetCsrfAsync(api, $"{collisionCsrfCookie}; {adminSession}", token);
        using var lastDeactivate = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/deactivate");
        lastDeactivate.Headers.Add("Cookie", $"{lastCsrfCookie}; {adminSession}");
        lastDeactivate.Headers.Add("X-CSRF-TOKEN", lastToken);
        lastDeactivate.Content = JsonContent.Create(new { accountId = adminAccountId });
        using var lastDeactivateResponse = await api.SendAsync(lastDeactivate, token);
        Assert.Equal(HttpStatusCode.Conflict, lastDeactivateResponse.StatusCode);
        using var lastDemote = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/role");
        lastDemote.Headers.Add("Cookie", $"{lastCsrfCookie}; {adminSession}");
        lastDemote.Headers.Add("X-CSRF-TOKEN", lastToken);
        lastDemote.Content = JsonContent.Create(new { accountId = adminAccountId, role = "Member" });
        using var lastDemoteResponse = await api.SendAsync(lastDemote, token);
        Assert.Equal(HttpStatusCode.Conflict, lastDemoteResponse.StatusCode);

        await commands.ExecuteCommandAsync("archive-worker-smoke", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-worker-smoke", token);
        var messages = await mail.GetFromJsonAsync<JsonElement>("/api/v1/messages", token);
        // Diagnostic mail + member/editor/admin code mails + two invitation
        // mails (invite + same-role resend) + invited code mail + reactivated
        // code mail + email-change code mail + moved-address code mail +
        // worker mail.
        Assert.Equal(11, messages.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task PrivateScoreUploadRoundtripThroughRealAzuriteStorage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Archive_AppHost>(
            // Port randomization is off so the pinned Azurite blob port (see
            // AppHost) is honored: the server-side copy fetches the copy
            // source from inside the emulator container, which can only
            // resolve the ticket host when container and host ports match.
            ["--Archive:PersistLocalData=false", "DcpPublisher:RandomizePorts=false"],
            (options, _) => options.DisableDashboard = false, token);
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

        // Merely starting the API did not apply a schema. Execute it explicitly.
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        await commands.ExecuteCommandAsync("archive-migrate", "start", token);
        await AssertSuccessfulCompletion(app.ResourceNotifications, "archive-migrate", token);

        using var frontend = app.CreateHttpClient("archive-frontend", "http");
        using var mail = app.CreateHttpClient("archive-mail", "http");
        // A jar-free client carries exactly the manually attached session so
        // editor/admin/member tickets never compete (see ARC-006 comment in
        // the walking skeleton above).
        using var api = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = frontend.BaseAddress,
        };
        // Server-side transfer client: CORS is irrelevant off-browser, the
        // upload URL points straight at the local Azurite endpoint.
        using var storage = new HttpClient();

        var (seedCsrfCookie, seedToken) = await GetCsrfAsync(api, null, token);
        using var seed = new HttpRequestMessage(HttpMethod.Post, "/api/dev/auth/seed");
        seed.Headers.Add("Cookie", seedCsrfCookie);
        seed.Headers.Add("X-CSRF-TOKEN", seedToken);
        seed.Content = JsonContent.Create(new { });
        using var seedResponse = await api.SendAsync(seed, token);
        Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);

        var editorSession = await SignInAsync(api, mail, "redaktion@liedertafel.test", token);
        var adminSession = await SignInAsync(api, mail, "verwaltung@liedertafel.test", token);
        var memberSession = await SignInAsync(api, mail, "mitglied@liedertafel.test", token);

        // Editor builds the catalogue row: song → arrangement → version.
        using var songResponse = await PostJsonAsync(api, "/api/songs",
            new { title = "ARC-015 Integrationslied" }, editorSession, token);
        Assert.Equal(HttpStatusCode.Created, songResponse.StatusCode);
        var songBody = await songResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var songId = Guid.Parse(songBody.GetProperty("song").GetProperty("id").GetString()!);
        using var detailResponse = await GetAsync(api, $"/api/songs/{songId}", editorSession, token);
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var versionId = Guid.Parse(detail.GetProperty("song").GetProperty("arrangements")[0]
            .GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);

        var assets = new Guid[4];
        for (var i = 0; i < assets.Length; i++)
            assets[i] = await CreateAssetAsync(api, editorSession, versionId, token);

        // Wrong-owner finalize: the administrator passes the editor gate but
        // is not the session creator, so finalization is refused (403) and
        // the session stays pending.
        var (wrongOwnerSession, _, _) = await CreateUploadSessionAsync(api, editorSession, assets[3], token);
        using var wrongOwner = await PostJsonAsync(api,
            $"/api/upload-sessions/{wrongOwnerSession}/finalize", new { }, adminSession, token);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.StatusCode);

        // Finalize with the object never transferred → 409, session stays
        // retryable.
        var (missingSession, _, _) = await CreateUploadSessionAsync(api, editorSession, assets[0], token);
        using var missing = await PostJsonAsync(api,
            $"/api/upload-sessions/{missingSession}/finalize", new { }, editorSession, token);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);

        // Access before any finalized revision → 404.
        using var premature = await GetAsync(api, $"/api/assets/{assets[0]}/access", editorSession, token);
        Assert.Equal(HttpStatusCode.NotFound, premature.StatusCode);

        // Real direct-to-blob transfer: PUT a tiny valid PDF to the returned
        // upload URL, then finalize.
        var pdf = ValidPdf(768);
        var (uploadSessionId, revisionId) = await TransferAndFinalizeAsync(
            api, storage, editorSession, assets[1], pdf, token);

        // Invalid PDF: text bytes reach storage but finalization rejects
        // them with 422.
        await TransferAndFinalizeAsync(api, storage, editorSession, assets[2],
            "Kein PDF, nur Text."u8.ToArray(), token, expectFailure: HttpStatusCode.UnprocessableEntity);

        // Finalizing the accepted session again is idempotent: the identical
        // revision comes back and no extra revision was created.
        using var retry = await PostJsonAsync(api,
            $"/api/upload-sessions/{uploadSessionId}/finalize", new { }, editorSession, token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(revisionId, Guid.Parse(retryBody.GetProperty("revisionId").GetString()!));

        // Anonymous read access is unauthorized.
        using var anonymous = new HttpClient { BaseAddress = frontend.BaseAddress };
        using var anonymousResponse = await anonymous.GetAsync(
            $"/api/assets/{assets[1]}/access", token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        // A signed-in member gets 404 on a draft song even though the asset
        // has a current revision.
        using var memberAccess = await GetAsync(api, $"/api/assets/{assets[1]}/access", memberSession, token);
        Assert.Equal(HttpStatusCode.NotFound, memberAccess.StatusCode);

        // The editor reads scoped tickets against the real Azurite endpoint
        // and both round-trip the exact uploaded bytes.
        using var access = await GetAsync(api, $"/api/assets/{assets[1]}/access", editorSession, token);
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        var accessBody = await access.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(revisionId, Guid.Parse(accessBody.GetProperty("revisionId").GetString()!));
        Assert.Equal(1, accessBody.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("application/pdf", accessBody.GetProperty("contentType").GetString());
        Assert.Equal(pdf.Length, accessBody.GetProperty("sizeBytes").GetInt64());
        var viewUrl = accessBody.GetProperty("viewUrl").GetString()!;
        var downloadUrl = accessBody.GetProperty("downloadUrl").GetString()!;
        Assert.True(Uri.TryCreate(viewUrl, UriKind.Absolute, out var view)
            && view.Scheme is "http" or "https", $"viewUrl is not absolute: {viewUrl}");
        Assert.True(Uri.TryCreate(downloadUrl, UriKind.Absolute, out var download)
            && download.Scheme is "http" or "https", $"downloadUrl is not absolute: {downloadUrl}");
        Assert.NotEqual(viewUrl, downloadUrl);

        using var viewResponse = await storage.GetAsync(viewUrl, token);
        Assert.Equal(HttpStatusCode.OK, viewResponse.StatusCode);
        Assert.Equal(pdf, await viewResponse.Content.ReadAsByteArrayAsync(token));
        using var downloadResponse = await storage.GetAsync(downloadUrl, token);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(pdf, await downloadResponse.Content.ReadAsByteArrayAsync(token));
        Assert.Contains("attachment", downloadResponse.Content.Headers.ContentDisposition?.DispositionType ?? "");
    }

    private static byte[] ValidPdf(int size)
    {
        var bytes = new byte[size];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    private static async Task<string> SignInAsync(
        HttpClient api, HttpClient mail, string email, CancellationToken token)
    {
        var (requestCookie, requestToken) = await GetCsrfAsync(api, null, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/request");
        request.Headers.Add("Cookie", requestCookie);
        request.Headers.Add("X-CSRF-TOKEN", requestToken);
        request.Content = JsonContent.Create(new { email });
        using var requestResponse = await api.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);
        var code = await GetSignInCodeAsync(mail, email, token);
        var (verifyCookie, verifyToken) = await GetCsrfAsync(api, requestCookie, token);
        using var verify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code/verify");
        verify.Headers.Add("Cookie", verifyCookie);
        verify.Headers.Add("X-CSRF-TOKEN", verifyToken);
        verify.Content = JsonContent.Create(new { email, code });
        using var verifyResponse = await api.SendAsync(verify, token);
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        return Assert.Single(
            verifyResponse.Headers.GetValues("Set-Cookie"), c => c.StartsWith("archive.auth=")).Split(';')[0];
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string path, object body, string sessionCookie, CancellationToken token)
    {
        var (cookie, csrfToken) = await GetCsrfAsync(client, sessionCookie, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{cookie}; {sessionCookie}");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string sessionCookie, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        return await client.SendAsync(request, token);
    }

    private static async Task<Guid> CreateAssetAsync(
        HttpClient client, string sessionCookie, Guid versionId, CancellationToken token)
    {
        using var response = await PostJsonAsync(client,
            $"/api/musical-versions/{versionId}/assets", new { assetType = "score" }, sessionCookie, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private static async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> CreateUploadSessionAsync(
        HttpClient client, string sessionCookie, Guid assetId, CancellationToken token)
    {
        using var response = await PostJsonAsync(client,
            $"/api/assets/{assetId}/upload-session", new { }, sessionCookie, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return (
            Guid.Parse(body.GetProperty("uploadSessionId").GetString()!),
            body.GetProperty("uploadUrl").GetString()!,
            body);
    }

    private async Task<(Guid UploadSessionId, Guid RevisionId)> TransferAndFinalizeAsync(
        HttpClient api, HttpClient storage, string sessionCookie, Guid assetId,
        byte[] content, CancellationToken token, HttpStatusCode? expectFailure = null)
    {
        var (uploadSessionId, uploadUrl, _) = await CreateUploadSessionAsync(api, sessionCookie, assetId, token);
        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        put.Content.Headers.ContentType = new("application/pdf");
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var putResponse = await storage.SendAsync(put, token);
        Assert.Equal(HttpStatusCode.Created, putResponse.StatusCode);
        using var finalize = await PostJsonAsync(api,
            $"/api/upload-sessions/{uploadSessionId}/finalize", new { }, sessionCookie, token);
        if (finalize.StatusCode != (expectFailure ?? HttpStatusCode.OK))
        {
            var body = await finalize.Content.ReadAsStringAsync(token);
            throw new Xunit.Sdk.XunitException(
                $"Finalize expected {expectFailure ?? HttpStatusCode.OK} but was {finalize.StatusCode}."
                + $"\nuploadUrl: {uploadUrl}\nbody: {body}");
        }
        if (expectFailure is not null)
            return (uploadSessionId, Guid.Empty);
        var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>(token);
        return (uploadSessionId, Guid.Parse(revision.GetProperty("revisionId").GetString()!));
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

    private static async Task<string> GetFreshSignInCodeAsync(
        HttpClient mail, string email, string consumedCode, CancellationToken token)
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
                if (match.Success && !string.Equals(match.Groups[1].Value, consumedCode, StringComparison.Ordinal))
                    return match.Groups[1].Value;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new Xunit.Sdk.XunitException($"No fresh sign-in code mail found for {email}.");
    }

    private static async Task<string> GetEmailChangeCodeAsync(HttpClient mail, string email, CancellationToken token)
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
                var match = Regex.Match(text ?? string.Empty, @"Bestätigungscode lautet:\s*(\d{6})");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new Xunit.Sdk.XunitException($"No email-change code mail found for {email}.");
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
