using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>#166: Noten Dateien of a Lied through the shared resumable upload sessions.</summary>
public sealed class DateiUploadTests
{
	[Fact]
	public async Task EditorAddsPartiturAndTwoStimmenAndMemberReadsThem()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");

		var partitur = await host.UploadNotenAsync(editor, liedId, null, isPartitur: true, fileName: "partitur.pdf");
		var tenor = await host.UploadNotenAsync(editor, liedId, "Tenor 1", fileName: "tenor1.pdf");
		await host.UploadNotenAsync(editor, liedId, "Bass 2", fileName: "bass2.pdf");

		Assert.Equal("noten", partitur.GetProperty("kind").GetString());
		Assert.True(partitur.GetProperty("isPartitur").GetBoolean());
		Assert.Equal("Tenor 1", tenor.GetProperty("stimme").GetString());

		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		var names = lied.GetProperty("dateien").EnumerateArray().Select(d => d.GetProperty("fileName").GetString()!).ToArray();
		Assert.Equal(["partitur.pdf", "bass2.pdf", "tenor1.pdf"], names);

		var access = await host.GetJsonAsync($"/api/dateien/{tenor.GetProperty("id").GetString()}/access", member);
		Assert.Equal("application/pdf", access.GetProperty("contentType").GetString());
		Assert.False(string.IsNullOrEmpty(access.GetProperty("viewUrl").GetString()));
		Assert.NotEqual(access.GetProperty("viewUrl").GetString(), access.GetProperty("downloadUrl").GetString());
	}

	[Fact]
	public async Task PromotedBlobsUseTheDateiPrefixAndPendingBlobIsGone()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId,
			new { kind = "noten", stimme = "Tenor 1" });
		var pendingBlob = host.Factory.Storage.Find(uploadUrl)!.BlobName;
		host.Factory.Storage.Store(pendingBlob, LiedTestHost.ValidPdf(64));
		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);

		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var datei = await db.Dateien.SingleAsync();
		Assert.StartsWith($"dateien/{datei.Id}/", datei.BlobName);
		Assert.True(host.Factory.Storage.Has(datei.BlobName));
		Assert.False(host.Factory.Storage.Has(pendingBlob));
		Assert.Equal(liedId, datei.LiedId);
		Assert.Null(datei.DeletedAt);
	}

	[Fact]
	public async Task FinalizeIsIdempotent()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId, new { kind = "noten", isPartitur = true });
		host.Factory.Storage.Store(host.Factory.Storage.Find(uploadUrl)!.BlobName, LiedTestHost.ValidPdf(64));

		using var first = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		using var second = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);
		var a = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("datei").GetProperty("id").GetString();
		var b = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("datei").GetProperty("id").GetString();
		Assert.Equal(a, b);
		using var scope = host.Services.CreateScope();
		Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Dateien.CountAsync());
	}

	[Fact]
	public async Task InterruptedUploadResumesThroughRenewAndKeepsTheBlob()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId,
			new { kind = "noten", stimme = "Bass 1", fileName = "bass1.pdf", sizeBytes = 2048 });
		var blobName = host.Factory.Storage.Find(uploadUrl)!.BlobName;
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync(s => s.Id == sessionId);
			session.UploadTicketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
			await db.SaveChangesAsync();
		}

		using var renewed = await host.PostAsync($"/api/upload-sessions/{sessionId}/renew", new { }, editor);
		Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
		var json = await renewed.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(blobName, json.GetProperty("blobName").GetString());
		Assert.NotEqual(uploadUrl, json.GetProperty("uploadUrl").GetString());

		host.Factory.Storage.Store(blobName, LiedTestHost.ValidPdf(2048));
		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize",
			new { sizeBytes = 2048, fileName = "bass1.pdf" }, editor);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
	}

	[Fact]
	public async Task CancelledSessionDeletesTheBlobAndCannotFinalize()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId, new { kind = "noten", isPartitur = true });
		var blobName = host.Factory.Storage.Find(uploadUrl)!.BlobName;
		host.Factory.Storage.Store(blobName, LiedTestHost.ValidPdf(64));

		using var cancel = await host.DeleteAsync($"/api/upload-sessions/{sessionId}", editor);
		Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
		Assert.False(host.Factory.Storage.Has(blobName));
		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.Conflict, finalize.StatusCode);
	}

	[Fact]
	public async Task NonPdfIsRejectedAndTheSessionAbandoned()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var (sessionId, uploadUrl, _) = await host.OpenUploadAsync(editor, liedId, new { kind = "noten", isPartitur = true });
		host.Factory.Storage.Store(host.Factory.Storage.Find(uploadUrl)!.BlobName, "keine pdf datei"u8.ToArray());

		using var finalize = await host.PostAsync($"/api/upload-sessions/{sessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, finalize.StatusCode);
		var problem = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(DateiKindRules.InvalidPdfMessage, problem.GetProperty("title").GetString());
		using var scope = host.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Empty(await db.Dateien.ToListAsync());
		Assert.Equal(PendingUploadState.Abandoned, (await db.UploadSessions.SingleAsync()).State);
	}

	[Fact]
	public async Task SessionRequestsAreValidated()
	{
		await using var host = new LiedTestHost(new Dictionary<string, string?> { ["Archive:Assets:MaxUploadBytes"] = "2048" });
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var path = $"/api/lieder/{liedId}/dateien/upload-session";

		using var memberResponse = await host.PostAsync(path, new { kind = "noten", isPartitur = true }, member);
		Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
		using var noStimme = await host.PostAsync(path, new { kind = "noten" }, editor);
		Assert.Equal(HttpStatusCode.BadRequest, noStimme.StatusCode);
		using var unknownKind = await host.PostAsync(path, new { kind = "video" }, editor);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownKind.StatusCode);
		using var replace = await host.PostAsync(path,
			new { kind = "noten", isPartitur = true, replacesDateiId = Guid.NewGuid() }, editor);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, replace.StatusCode);
		using var tooBig = await host.PostAsync(path, new { kind = "noten", isPartitur = true, sizeBytes = 4096 }, editor);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
		using var missing = await host.PostAsync($"/api/lieder/{Guid.NewGuid()}/dateien/upload-session",
			new { kind = "noten", isPartitur = true }, editor);
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task SeveralUploadsToOneLiedMayBeOpenAtOnce()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var first = await host.OpenUploadAsync(editor, liedId, new { kind = "noten", stimme = "Tenor 1" });
		var second = await host.OpenUploadAsync(editor, liedId, new { kind = "noten", stimme = "Tenor 2" });
		host.Factory.Storage.Store(host.Factory.Storage.Find(first.UploadUrl)!.BlobName, LiedTestHost.ValidPdf(64));
		host.Factory.Storage.Store(host.Factory.Storage.Find(second.UploadUrl)!.BlobName, LiedTestHost.ValidPdf(64));
		using var a = await host.PostAsync($"/api/upload-sessions/{first.SessionId}/finalize", new { }, editor);
		using var b = await host.PostAsync($"/api/upload-sessions/{second.SessionId}/finalize", new { }, editor);
		Assert.Equal(HttpStatusCode.OK, a.StatusCode);
		Assert.Equal(HttpStatusCode.OK, b.StatusCode);
	}

	[Fact]
	public async Task CollectionBudgetOfALiedIsEnforced()
	{
		await using var host = new LiedTestHost(new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "2048",
			["Archive:Assets:MaxCollectionBytes"] = "3000",
		});
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		await host.UploadNotenAsync(editor, liedId, "Tenor 1", size: 2000);

		using var over = await host.PostAsync($"/api/lieder/{liedId}/dateien/upload-session",
			new { kind = "noten", stimme = "Tenor 2", sizeBytes = 1500 }, editor);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
		var otherLied = await host.CreateLiedIdAsync(editor, "Anderes Lied");
		var fits = await host.OpenUploadAsync(editor, otherLied, new { kind = "noten", stimme = "Tenor 2", sizeBytes = 1500 });
		Assert.NotEqual(Guid.Empty, fits.SessionId);
	}

	[Fact]
	public async Task AccessHidesTrashedDateienAndRequiresSignIn()
	{
		await using var host = new LiedTestHost();
		var editor = await host.EditorAsync();
		var member = await host.MemberAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1");
		var id = datei.GetProperty("id").GetString();

		using var anonymous = await host.GetAsync($"/api/dateien/{id}/access", null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		using (var scope = host.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			(await db.Dateien.SingleAsync()).DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}
		using var hidden = await host.GetAsync($"/api/dateien/{id}/access", member);
		Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
		var lied = (await host.GetJsonAsync($"/api/lieder/{liedId}", member)).GetProperty("lied");
		Assert.Equal(0, lied.GetProperty("dateien").GetArrayLength());
	}

	[Fact]
	public async Task RegisteredHooksRunAroundTheCommittedUpload()
	{
		var hook = new RecordingHook();
		await using var host = new LiedTestHost(hook: hook);
		var editor = await host.EditorAsync();
		var liedId = await host.CreateLiedIdAsync(editor, "Notenlied");
		var datei = await host.UploadNotenAsync(editor, liedId, "Tenor 1");

		Assert.Equal([Guid.Parse(datei.GetProperty("id").GetString()!)], hook.Before);
		Assert.Equal(hook.Before, hook.After);
	}

	private sealed class RecordingHook : IDateiFinalizedHook
	{
		public List<Guid> Before { get; } = [];

		public List<Guid> After { get; } = [];

		public Task BeforeSaveAsync(Datei datei, PendingUploadContext upload, ArchiveDbContext db, CancellationToken token)
		{
			Before.Add(datei.Id);
			return Task.CompletedTask;
		}

		public Task AfterSaveAsync(Datei datei, ArchiveDbContext db, CancellationToken token)
		{
			After.Add(datei.Id);
			return Task.CompletedTask;
		}
	}
}
