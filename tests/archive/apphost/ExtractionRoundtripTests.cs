using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit.Abstractions;

namespace Archive.AppHost.Tests;

/// <summary>
/// ARC-034 slice S3: the real-stack extraction roundtrip. A text-bearing PDF
/// is uploaded through the ARC-015 protocol against real Azurite storage,
/// finalize queues the extraction row, and the explicitly started finite
/// <c>archive-extract</c> worker drains the real Azurite extraction queue to
/// the completed state. An immediate rerun exits finitely on the empty queue
/// without reworking the row, a scanned PDF ends in the explicit noText
/// state, and a duplicated queue message (development diagnostic) is handled
/// idempotently. Every run ends Finished with exit code 0 — no continuously
/// running worker stays behind.
/// </summary>
public sealed class ExtractionRoundtripTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ExtractionRoundtripThroughRealAzuriteQueueAndFiniteWorker()
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
        await RunFiniteAsync(app, "archive-migrate", token);

        using var frontend = app.CreateHttpClient("archive-frontend", "http");
        using var mail = app.CreateHttpClient("archive-mail", "http");
        // A jar-free client carries exactly the manually attached session so
        // tickets never compete (see the walking skeleton's ARC-006 comment).
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

        // Editor builds the catalogue row: song → arrangement → version.
        using var songResponse = await PostJsonAsync(api, "/api/songs",
            new { title = "ARC-034 Auswertungslied" }, editorSession, token);
        Assert.Equal(HttpStatusCode.Created, songResponse.StatusCode);
        var songBody = await songResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var songId = Guid.Parse(songBody.GetProperty("song").GetProperty("id").GetString()!);
        using var detailResponse = await GetAsync(api, $"/api/songs/{songId}", editorSession, token);
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var versionId = Guid.Parse(detail.GetProperty("song").GetProperty("arrangements")[0]
            .GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);

        // First asset: a text-bearing PDF built with the real PdfPig writer,
        // uploaded through the real Azurite transfer protocol.
        var textAsset = await CreateAssetAsync(api, editorSession, versionId, token);
        var (_, textRevisionId) = await TransferAndFinalizeAsync(
            api, storage, editorSession, textAsset, TextPdf("Hallo Liedertafel ARC-034"), token);

        // Finalize created the extraction row in the same save and the queue
        // send was accepted: the editor sees the queued state.
        var queuedItem = await GetStatusItemAsync(api, editorSession, textRevisionId, token);
        Assert.Equal("queued", queuedItem.GetProperty("status").GetString());
        Assert.Equal(0, queuedItem.GetProperty("attemptCount").GetInt32());

        // The finite worker dispatches nothing new and drains the real
        // extraction queue, then exits — no continuously running worker.
        await RunFiniteAsync(app, "archive-extract", token);

        var completedItem = await GetStatusItemAsync(api, editorSession, textRevisionId, token);
        Assert.Equal("completed", completedItem.GetProperty("status").GetString());
        Assert.Contains("Hallo Liedertafel", completedItem.GetProperty("text").GetString());
        Assert.Equal(1, completedItem.GetProperty("attemptCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, completedItem.GetProperty("completedAt").ValueKind);
        var completedText = completedItem.GetProperty("text").GetString();

        // Finite rerun on the empty queue: immediate finite exit, and the
        // completed row is never rewritten by the duplicate round trip.
        await RunFiniteAsync(app, "archive-extract", token);
        var rerunItem = await GetStatusItemAsync(api, editorSession, textRevisionId, token);
        Assert.Equal("completed", rerunItem.GetProperty("status").GetString());
        Assert.Equal(completedText, rerunItem.GetProperty("text").GetString()!);
        Assert.Equal(1, rerunItem.GetProperty("attemptCount").GetInt32());

        // Second asset: a one-page PDF without any text (the scanned case)
        // ends in the explicit noText terminal state after its own run.
        var scannedAsset = await CreateAssetAsync(api, editorSession, versionId, token);
        var (_, scannedRevisionId) = await TransferAndFinalizeAsync(
            api, storage, editorSession, scannedAsset, ScannedPdf(), token);
        await RunFiniteAsync(app, "archive-extract", token);
        var scannedItem = await GetStatusItemAsync(api, editorSession, scannedRevisionId, token);
        Assert.Equal("noText", scannedItem.GetProperty("status").GetString());
        Assert.Equal(1, scannedItem.GetProperty("attemptCount").GetInt32());

        // ARC-034 verification: a duplicated queue message (development
        // diagnostic re-send against the real queue) changes nothing.
        var (dupCookie, dupToken) = await GetCsrfAsync(api, editorSession, token);
        using var duplicate = new HttpRequestMessage(HttpMethod.Post,
            $"/api/dev/extraction-duplicate?revisionId={textRevisionId}");
        duplicate.Headers.Add("Cookie", $"{dupCookie}; {editorSession}");
        duplicate.Headers.Add("X-CSRF-TOKEN", dupToken);
        duplicate.Content = JsonContent.Create(new { });
        using var duplicateResponse = await api.SendAsync(duplicate, token);
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        var duplicateBody = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.True(duplicateBody.GetProperty("sent").GetBoolean());

        await RunFiniteAsync(app, "archive-extract", token);
        var duplicateHandled = await GetStatusItemAsync(api, editorSession, textRevisionId, token);
        Assert.Equal("completed", duplicateHandled.GetProperty("status").GetString());
        Assert.Equal(completedText, duplicateHandled.GetProperty("text").GetString()!);
        Assert.Equal(1, duplicateHandled.GetProperty("attemptCount").GetInt32());
    }

    /// <summary>
    /// Starts one explicitly-started finite resource and asserts a clean exit.
    /// The start happens before the waits, and the first wait requires a
    /// non-terminal state so a replayed Finished snapshot of the previous
    /// finite run can never satisfy this run's completion wait.
    /// </summary>
    private static async Task RunFiniteAsync(DistributedApplication app, string resource, CancellationToken token)
    {
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        await commands.ExecuteCommandAsync(resource, "start", token);
        await app.ResourceNotifications.WaitForResourceAsync(resource, update =>
        {
            var state = update.Snapshot.State?.Text;
            return state != KnownResourceStates.Finished && state != KnownResourceStates.FailedToStart;
        }, token);
        int? exitCode = null;
        await app.ResourceNotifications.WaitForResourceAsync(resource, update =>
        {
            if (update.Snapshot.State?.Text != KnownResourceStates.Finished) return false;
            exitCode = update.Snapshot.ExitCode;
            return true;
        }, token);
        Assert.Equal(0, exitCode);
    }

    private static async Task<JsonElement> GetStatusItemAsync(
        HttpClient api, string sessionCookie, Guid revisionId, CancellationToken token)
    {
        using var response = await GetAsync(api, $"/api/revisions/extraction?ids={revisionId}", sessionCookie, token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return Assert.Single(body.GetProperty("results").EnumerateArray()).Clone();
    }

    private static byte[] TextPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }

    private static byte[] ScannedPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        return builder.Build();
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
            $"/api/musical-versions/{versionId}/assets",
            new { assetType = "score" }, sessionCookie, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private static async Task<(Guid SessionId, string UploadUrl)> CreateUploadSessionAsync(
        HttpClient client, string sessionCookie, Guid assetId, CancellationToken token)
    {
        using var response = await PostJsonAsync(client,
            $"/api/assets/{assetId}/upload-session", new { }, sessionCookie, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return (
            Guid.Parse(body.GetProperty("uploadSessionId").GetString()!),
            body.GetProperty("uploadUrl").GetString()!);
    }

    private static async Task<(Guid UploadSessionId, Guid RevisionId)> TransferAndFinalizeAsync(
        HttpClient api, HttpClient storage, string sessionCookie, Guid assetId,
        byte[] content, CancellationToken token)
    {
        var (uploadSessionId, uploadUrl) = await CreateUploadSessionAsync(api, sessionCookie, assetId, token);
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
        if (finalize.StatusCode != HttpStatusCode.OK)
        {
            var body = await finalize.Content.ReadAsStringAsync(token);
            throw new Xunit.Sdk.XunitException(
                $"Finalize expected OK but was {finalize.StatusCode}.\nuploadUrl: {uploadUrl}\nbody: {body}");
        }
        var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>(token);
        return (uploadSessionId, Guid.Parse(revision.GetProperty("revisionId").GetString()!));
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
