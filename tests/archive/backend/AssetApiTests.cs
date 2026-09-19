using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-015 private score assets: editor upload flow (create asset, upload
/// session, transfer, idempotent finalize), read tickets for members and the
/// negative matrix (roles, validation, abandoned sessions, visibility).
/// </summary>
public sealed class AssetApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";
	private const string SecondEditor = "zweitredaktion@liedertafel.test";

	[Fact]
	public async Task EditorUploadFlowCreatesRevisionAndIsIdempotent()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);

		using var createResponse = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType = "score" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		var assetBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(versionId, Guid.Parse(assetBody.GetProperty("musicalVersionId").GetString()!));
		Assert.Equal(AssetEndpoints.ScoreAssetType, assetBody.GetProperty("assetType").GetString());
		Assert.True(assetBody.GetProperty("voiceLabel").ValueKind is JsonValueKind.Null);
		Assert.True(assetBody.GetProperty("currentRevision").ValueKind is JsonValueKind.Null);
		var assetId = Guid.Parse(assetBody.GetProperty("id").GetString()!);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.SingleAsync();
			Assert.Equal(editorId, asset.CreatedByAccountId);
			Assert.Equal(AssetEndpoints.ScoreAssetType, asset.AssetType);
			Assert.Null(asset.VoiceLabel);
			Assert.Null(asset.CurrentRevisionId);
		}

		var (sessionId, uploadUrl, sessionBody) = await CreateUploadSessionAsync(client, editorSession, assetId);
		Assert.Equal(20 * 1024 * 1024, sessionBody.GetProperty("maxBytes").GetInt64());
		var expiresAt = DateTimeOffset.Parse(sessionBody.GetProperty("expiresAt").GetString()!);
		Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));

		var content = ValidPdf(1024);
		var pendingBlobName = factory.Storage.Find(uploadUrl)!.BlobName;
		factory.Storage.Store(pendingBlobName, content);

		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		var revisionId = Guid.Parse(revision.GetProperty("revisionId").GetString()!);
		Assert.Equal(1, revision.GetProperty("revisionNumber").GetInt32());
		Assert.Equal(AssetEndpoints.PdfContentType, revision.GetProperty("contentType").GetString());
		Assert.Equal(1024, revision.GetProperty("sizeBytes").GetInt64());

		string revisionBlobName;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var revisionRow = await db.FileRevisions.Include(r => r.Asset).SingleAsync();
			revisionBlobName = revisionRow.BlobName;
			Assert.StartsWith("revisions/", revisionBlobName);
			Assert.Equal(assetId, revisionRow.AssetId);
			Assert.Equal(editorId, revisionRow.CreatedByAccountId);
			Assert.Equal(revisionId, revisionRow.Asset.CurrentRevisionId);
			var session = await db.UploadSessions.SingleAsync();
			Assert.Equal(PendingUploadState.Finalized, session.State);
			Assert.Equal(revisionId, session.FinalizedRevisionId);
		}
		Assert.False(factory.Storage.Has(pendingBlobName));
		Assert.True(factory.Storage.Has(revisionBlobName));

		// Finalizing again is idempotent: same revision, no duplicate row.
		using var retry = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(revisionId, Guid.Parse(retryBody.GetProperty("revisionId").GetString()!));
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.FileRevisions.CountAsync());
		}
	}

	[Fact]
	public async Task SongDetailEmbedsCurrentRevisionForEditorsAndPublishedMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, revisionBody) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revisionBody.GetProperty("revisionId").GetString()!);

		// Drafts stay editor-visible with the populated revision.
		var draftDetail = await GetSongDetailAsync(client, editorSession, songId);
		var draftAssets = draftDetail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("assets").EnumerateArray().ToList();
		Assert.Single(draftAssets);
		Assert.Equal(assetId, Guid.Parse(draftAssets[0].GetProperty("id").GetString()!));
		Assert.Equal(revisionId, Guid.Parse(
			draftAssets[0].GetProperty("currentRevision").GetProperty("revisionId").GetString()!));

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var publishResponse = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

		var memberDetail = await GetSongDetailAsync(client, memberSession, songId);
		var memberAssets = memberDetail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("assets").EnumerateArray().ToList();
		Assert.Single(memberAssets);
		Assert.Equal(AssetEndpoints.ScoreAssetType, memberAssets[0].GetProperty("assetType").GetString());
		Assert.Equal(revisionId, Guid.Parse(
			memberAssets[0].GetProperty("currentRevision").GetProperty("revisionId").GetString()!));
		Assert.Equal(1, memberAssets[0].GetProperty("currentRevision").GetProperty("revisionNumber").GetInt32());
	}

	[Fact]
	public async Task SongDetailShowsNullCurrentRevisionBeforeFirstFinalize()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);

		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var assets = detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("assets").EnumerateArray().ToList();
		Assert.Single(assets);
		Assert.Equal(assetId, Guid.Parse(assets[0].GetProperty("id").GetString()!));
		Assert.True(assets[0].GetProperty("currentRevision").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task MutatingAssetEndpointsRejectMembersAndAnonymousCalls()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (sessionId, _, _) = await CreateUploadSessionAsync(client, editorSession, assetId);

		foreach (var (path, body) in new (string, object)[]
		{
			($"/api/musical-versions/{versionId}/assets", new { assetType = "score" }),
			($"/api/assets/{assetId}/upload-session", new { }),
			($"/api/upload-sessions/{sessionId}/finalize", new { }),
		})
		{
			using var memberResponse = await PostJsonAsync(client, path, body, memberSession);
			Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
			var memberProblem = await memberResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.ForbiddenMessage, memberProblem.GetProperty("title").GetString());

			var (anonCookie, anonToken) = await GetCsrfAsync(client);
			using var anonRequest = AuthedPost(path, body, anonCookie, anonToken);
			using var anonResponse = await client.SendAsync(anonRequest);
			Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);
			var anonProblem = await anonResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("Anmeldung erforderlich.", anonProblem.GetProperty("title").GetString());
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.Assets.CountAsync());
			Assert.Equal(1, await db.UploadSessions.CountAsync());
			Assert.Equal(0, await db.FileRevisions.CountAsync());
		}
	}

	[Fact]
	public async Task UnknownAssetTypeIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);

		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType = "video" }, editorSession);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UnknownAssetTypeMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(0, await db.Assets.CountAsync());

		using var videoResponse = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType = "  VIDEO  " }, editorSession);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, videoResponse.StatusCode);
		var videoProblem = await videoResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UnknownAssetTypeMessage, videoProblem.GetProperty("title").GetString());
		Assert.Equal(0, await db.Assets.CountAsync());
	}

	[Fact]
	public async Task AudioAssetUploadSessionFinalizesWithWhitelistedContentType()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);

		using var createResponse = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType = " Audio " }, editorSession);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		var assetBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("audio", assetBody.GetProperty("assetType").GetString());
		var assetId = Guid.Parse(assetBody.GetProperty("id").GetString()!);

		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync();
			Assert.Equal(AssetEndpoints.Mp3ContentType, session.ContentType);
		}
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			new byte[512], AssetEndpoints.Mp3ContentType);

		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.Mp3ContentType, revision.GetProperty("contentType").GetString());
		Assert.Equal(512, revision.GetProperty("sizeBytes").GetInt64());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var revisionRow = await db.FileRevisions.SingleAsync();
			Assert.Equal(AssetEndpoints.Mp3ContentType, revisionRow.ContentType);
			var asset = await db.Assets.SingleAsync();
			Assert.Equal(revisionRow.Id, asset.CurrentRevisionId);
			var session = await db.UploadSessions.SingleAsync();
			Assert.Equal(PendingUploadState.Finalized, session.State);
		}
		Assert.False(factory.Storage.Has(factory.Storage.Find(uploadUrl)!.BlobName));
	}

	[Fact]
	public async Task MidiAssetFinalizeAcceptsMidiContentTypes()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "midi");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(AssetEndpoints.MidiContentType, (await db.UploadSessions.SingleAsync()).ContentType);
		}
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			new byte[256], AssetEndpoints.XMidiContentType);

		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.XMidiContentType, revision.GetProperty("contentType").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(AssetEndpoints.XMidiContentType, (await db.FileRevisions.SingleAsync()).ContentType);
		}
	}

	[Fact]
	public async Task AudioFinalizeRejectsContentOutsideWhitelist()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "audio");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			ValidPdf(512), AssetEndpoints.PdfContentType);

		using var response = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.InvalidAudioMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(PendingUploadState.Abandoned, (await db.UploadSessions.SingleAsync()).State);
		Assert.Equal(0, await db.FileRevisions.CountAsync());
		Assert.Null((await db.Assets.SingleAsync()).CurrentRevisionId);
		Assert.Equal(0, factory.Storage.ObjectCount);
	}

	[Fact]
	public async Task ScoreFinalizeStillRequiresPdfMagicBytes()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateAssetAsync(client, editorSession, versionId, "score");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		// Correct content type, wrong magic bytes.
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			"MIDI-Datei, kein PDF."u8.ToArray(), AssetEndpoints.PdfContentType);

		using var response = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.InvalidPdfMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(PendingUploadState.Abandoned, (await db.UploadSessions.SingleAsync()).State);
		Assert.Equal(0, await db.FileRevisions.CountAsync());
		Assert.Null((await db.Assets.SingleAsync()).CurrentRevisionId);
		Assert.Equal(0, factory.Storage.ObjectCount);
	}

	[Fact]
	public async Task OverlongVoiceLabelIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);

		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets",
			new { assetType = "score", voiceLabel = new string('x', 201) }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Das Stimmenlabel ist zu lang.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task AssetForUnknownVersionIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{Guid.CreateVersion7()}/assets", new { assetType = "score" }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.MusicalVersionNotFoundMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task FinalizeByOtherEditorIsRejectedAndLeavesSessionUntouched()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(512));

		for (var attempt = 0; attempt < 2; attempt++)
		{
			using var response = await FinalizeAsync(client, secondSession, sessionId);
			Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadOwnerMessage, problem.GetProperty("title").GetString());
		}

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var session = await db.UploadSessions.SingleAsync();
		Assert.Equal(PendingUploadState.Pending, session.State);
		Assert.Null(session.FinalizedRevisionId);
		Assert.Equal(0, await db.FileRevisions.CountAsync());
	}

	[Fact]
	public async Task FinalizeWithoutTransferIsRejectedAndStaysRetryable()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);

		using var response = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadMissingMessage, problem.GetProperty("title").GetString());

		// The session stays pending so a late transfer can still finalize.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(PendingUploadState.Pending, (await db.UploadSessions.SingleAsync()).State);
		}

		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(512));
		using var late = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, late.StatusCode);
	}

	[Fact]
	public async Task InvalidPdfIsRejectedAndAbandonsSession()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, "Kein PDF, nur Text."u8.ToArray());

		using var response = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.InvalidPdfMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(PendingUploadState.Abandoned, (await db.UploadSessions.SingleAsync()).State);
		Assert.Equal(0, await db.FileRevisions.CountAsync());
		Assert.Null((await db.Assets.SingleAsync()).CurrentRevisionId);
		Assert.Equal(0, factory.Storage.ObjectCount);
	}

	[Fact]
	public async Task OversizedFileIsRejectedAndAbandonsSession()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (sessionId, uploadUrl, sessionBody) = await CreateUploadSessionAsync(client, editorSession, assetId);
		var maxBytes = sessionBody.GetProperty("maxBytes").GetInt64();
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, new byte[maxBytes + 1]);

		using var response = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadTooLargeMessage, problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Equal(PendingUploadState.Abandoned, (await db.UploadSessions.SingleAsync()).State);
		Assert.Equal(0, await db.FileRevisions.CountAsync());
		Assert.Equal(0, factory.Storage.ObjectCount);
	}

	[Fact]
	public async Task ExpiredSessionIsRejectedAndAbandoned()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync();
			session.UploadTicketExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
			await db.SaveChangesAsync();
		}

		using var response = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.UploadExpiredMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(PendingUploadState.Abandoned, (await db.UploadSessions.SingleAsync()).State);
			Assert.Equal(0, await db.FileRevisions.CountAsync());
		}
	}

	[Fact]
	public async Task MemberReadAccessReturnsViewAndDownloadTickets()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (songId, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		var (_, _, revisionBody) = await UploadAndFinalizeAsync(factory, client, editorSession, assetId);
		var revisionId = Guid.Parse(revisionBody.GetProperty("revisionId").GetString()!);
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var publishResponse = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		var readTicketsBefore = factory.Storage.Tickets.Count;

		using var access = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{assetId}/access");
		access.Headers.Add("Cookie", memberSession);
		using var response = await client.SendAsync(access);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(assetId, Guid.Parse(body.GetProperty("assetId").GetString()!));
		Assert.Equal(revisionId, Guid.Parse(body.GetProperty("revisionId").GetString()!));
		Assert.Equal(1, body.GetProperty("revisionNumber").GetInt32());
		Assert.Equal(AssetEndpoints.PdfContentType, body.GetProperty("contentType").GetString());
		Assert.Equal(1024, body.GetProperty("sizeBytes").GetInt64());
		var viewUrl = body.GetProperty("viewUrl").GetString()!;
		var downloadUrl = body.GetProperty("downloadUrl").GetString()!;
		Assert.NotEqual(viewUrl, downloadUrl);
		var expiresAt = DateTimeOffset.Parse(body.GetProperty("expiresAt").GetString()!);
		Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

		// The adapter only ever saw read tickets for the revision blob.
		var readTickets = factory.Storage.Tickets.Skip(readTicketsBefore).ToList();
		Assert.Equal(2, readTickets.Count);
		string revisionBlobName;
		using (var scope = factory.Services.CreateScope())
		{
			revisionBlobName = (await scope.ServiceProvider
				.GetRequiredService<ArchiveDbContext>()
				.FileRevisions.SingleAsync()).BlobName;
		}
		var view = factory.Storage.Find(viewUrl)!;
		var download = factory.Storage.Find(downloadUrl)!;
		Assert.Equal(revisionBlobName, view.BlobName);
		Assert.Equal(revisionBlobName, download.BlobName);
		Assert.False(view.Download);
		Assert.True(download.Download);
		Assert.Equal(TimeSpan.FromMinutes(15), view.Lifetime);
	}

	[Fact]
	public async Task MemberOnDraftSongGetsNotFoundEvenWithCurrentRevision()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		await UploadAndFinalizeAsync(factory, client, editorSession, assetId);

		using var access = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{assetId}/access");
		access.Headers.Add("Cookie", memberSession);
		using var response = await client.SendAsync(access);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.AssetNotFoundMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task AccessWithoutCurrentRevisionIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);
		await CreateUploadSessionAsync(client, editorSession, assetId);

		using var access = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{assetId}/access");
		access.Headers.Add("Cookie", editorSession);
		using var response = await client.SendAsync(access);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.NoCurrentRevisionMessage, problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task InactiveMemberSessionIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var memberSession = await SignInAsync(factory, Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);

		// Revoking confirmation turns the signed-in member inactive; the
		// access decision re-reads Identity state on every call.
		using (var scope = factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = await users.FindByEmailAsync(Member);
			user!.EmailConfirmed = false;
			Assert.True((await users.UpdateAsync(user)).Succeeded);
		}

		using var access = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{assetId}/access");
		access.Headers.Add("Cookie", memberSession);
		using var response = await client.SendAsync(access);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Anmeldung erforderlich.", problem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task AnonymousReadIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (_, versionId) = await CreateSongWithVersionAsync(factory, client, editorSession);
		var assetId = await CreateScoreAssetAsync(client, editorSession, versionId);

		using var response = await client.GetAsync($"/api/assets/{assetId}/access");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Anmeldung erforderlich.", problem.GetProperty("title").GetString());
	}

	private static async Task<(Guid SongId, Guid VersionId)> CreateSongWithVersionAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, string title = "Notenlied")
	{
		var songId = await CreateSongAsync(factory, client, editorSession, title);
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		return (songId, versionId);
	}

	/// <summary>Creates a score asset (the ARC-015 default type).</summary>
	private static async Task<Guid> CreateScoreAssetAsync(
		HttpClient client, string editorSession, Guid versionId)
		=> await CreateAssetAsync(client, editorSession, versionId, "score");

	private static async Task<Guid> CreateAssetAsync(
		HttpClient client, string editorSession, Guid versionId, string assetType)
	{
		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("id").GetString()!);
	}

	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> CreateUploadSessionAsync(
		HttpClient client, string editorSession, Guid assetId)
	{
		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", new { }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (
			Guid.Parse(body.GetProperty("uploadSessionId").GetString()!),
			body.GetProperty("uploadUrl").GetString()!,
			body);
	}

	/// <summary>Creates the session, stores a valid PDF and finalizes.</summary>
	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Revision)> UploadAndFinalizeAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, Guid assetId, long size = 1024)
	{
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(size));
		using var finalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
		return (sessionId, uploadUrl, revision);
	}

	private static async Task<HttpResponseMessage> FinalizeAsync(
		HttpClient client, string session, Guid sessionId)
		=> await PostJsonAsync(client, $"/api/upload-sessions/{sessionId}/finalize", new { }, session);

	private static byte[] ValidPdf(long size)
	{
		var bytes = new byte[size];
		"%PDF-1.7\n"u8.CopyTo(bytes);
		return bytes;
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
	}

	private static async Task<Guid> CreateSongAsync(
		AuthApiFactory factory, HttpClient client, string editorSession, string title)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);
	}

	private static async Task<Guid> SeedAsync(AuthApiFactory factory, string email, string role)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roles.RoleExistsAsync(role))
			Assert.True((await roles.CreateAsync(new ArchiveRole(role))).Succeeded);
		var user = await users.FindByEmailAsync(email);
		if (user is null)
		{
			user = new ArchiveUser { UserName = email, Email = email, DisplayName = "Test", EmailConfirmed = true };
			Assert.True((await users.CreateAsync(user)).Succeeded);
		}
		if (!await users.IsInRoleAsync(user, role))
			Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
		return user.Id;
	}

	private static async Task<string> SignInAsync(AuthApiFactory factory, string email)
	{
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		var code = factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify", new { email, code }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
		return Assert.Single(verifyResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
	}

	private static async Task<(string Cookie, string Token)> GetCsrfAsync(HttpClient client, string? sessionCookie = null)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		if (sessionCookie is not null)
			tokenRequest.Headers.Add("Cookie", sessionCookie);
		using var response = await client.SendAsync(tokenRequest);
		response.EnsureSuccessStatusCode();
		var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (cookie, body.GetProperty("token").GetString()!);
	}

	private static HttpRequestMessage AuthedPost(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}

	private static async Task<JsonElement> GetSongDetailAsync(HttpClient client, string session, Guid songId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("song");
	}
}

/// <summary>
/// In-memory replacement for the Blob storage adapter (ARC-015): objects live
/// in a dictionary, issued ticket URLs are opaque strings recorded with their
/// blob names so tests can emulate the browser transfer (PUT to uploadUrl)
/// without parsing tickets. Ticket URLs are returned verbatim by the API.
/// </summary>
internal sealed class FakeAssetStorage : IAssetStorageAdapter
{
	public sealed record IssuedTicket(string Url, string BlobName, TimeSpan Lifetime, bool Download);

	private readonly ConcurrentDictionary<string, (byte[] Bytes, string? ContentType)> objects = new(StringComparer.Ordinal);
	private readonly List<IssuedTicket> tickets = [];
	private readonly object gate = new();
	private int sequence;

	public IReadOnlyList<IssuedTicket> Tickets
	{
		get
		{
			lock (gate) return [.. tickets];
		}
	}

	public int ObjectCount => objects.Count;

	public IssuedTicket? Find(string url) =>
		Tickets.FirstOrDefault(t => string.Equals(t.Url, url, StringComparison.Ordinal));

	/// <summary>
	/// Stores an object without a content type; ProbeAsync then reports null,
	/// which skips the endpoint's content-type check (pre-existing behavior).
	/// </summary>
	public void Store(string blobName, byte[] content) => objects[blobName] = (content, ContentType: null);

	/// <summary>Stores an object remembering the transfer content type.</summary>
	public void Store(string blobName, byte[] content, string contentType) =>
		objects[blobName] = (content, contentType);

	public bool Has(string blobName) => objects.ContainsKey(blobName);

	public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		var url = NextUrl(blobName, "upload");
		lock (gate) tickets.Add(new IssuedTicket(url, blobName, lifetime, Download: false));
		return Task.FromResult(url);
	}

	public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, CancellationToken cancellationToken)
	{
		var url = NextUrl(blobName, asDownload ? "download" : "view");
		lock (gate) tickets.Add(new IssuedTicket(url, blobName, lifetime, asDownload));
		return Task.FromResult(url);
	}

	public Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken) =>
		Task.FromResult(objects.TryGetValue(blobName, out var stored)
			? new AssetObjectInfo(stored.Bytes.LongLength, stored.ContentType)
			: null);

	public Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken) =>
		Task.FromResult<byte[]?>(objects.TryGetValue(blobName, out var stored)
			? stored.Bytes[..Math.Min(length, stored.Bytes.Length)]
			: null);

	public Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken)
	{
		if (!objects.TryGetValue(sourceBlobName, out var stored))
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
		// Mirror the Azure copy semantics: the staged object survives until
		// the endpoint deletes it after a successful promotion.
		objects[targetBlobName] = stored;
		return Task.CompletedTask;
	}

	public Task DeleteAsync(string blobName, CancellationToken cancellationToken)
	{
		objects.TryRemove(blobName, out _);
		return Task.CompletedTask;
	}

	private string NextUrl(string blobName, string kind)
	{
		var number = Interlocked.Increment(ref sequence);
		return $"sas://{blobName}?fake={kind}-{number}";
	}
}
