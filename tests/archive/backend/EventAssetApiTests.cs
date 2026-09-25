using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-025 event documents/photos: editors attach historical programme scans
/// and photographs to an event through the shared three-step upload protocol,
/// finalize validation checks content type and honest magic bytes,
/// publication gates member visibility (draft events answer the same
/// indistinguishable 404 as the event itself), pending objects cannot be
/// reassigned to another editor, attaching material never mutates the event
/// row, and the collection budget scopes per event.
/// </summary>
public sealed class EventAssetApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";
	private const string SecondEditor = "zweitredaktion@liedertafel.test";

	[Fact]
	public async Task EditorAttachesDocumentAndPhotoThroughUploadProtocol()
	{
		await using var factory = new AuthApiFactory();
		var editorId = await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new
		{
			kind = "concert",
			title = "Frühjahrskonzert",
			dateYear = 1950,
			dateMonth = 5,
			dateApproximate = true,
		});

		using var documentCreate = await PostJsonAsync(client, $"/api/events/{eventId}/assets",
			new { assetType = "document", description = "  Programmheft 1950  " }, editorSession);
		Assert.Equal(HttpStatusCode.Created, documentCreate.StatusCode);
		var documentBody = await documentCreate.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(eventId, Guid.Parse(documentBody.GetProperty("eventId").GetString()!));
		Assert.True(documentBody.GetProperty("musicalVersionId").ValueKind is JsonValueKind.Null);
		Assert.Equal(AssetEndpoints.DocumentAssetType, documentBody.GetProperty("assetType").GetString());
		Assert.Equal("Programmheft 1950", documentBody.GetProperty("description").GetString());
		Assert.True(documentBody.GetProperty("voiceLabel").ValueKind is JsonValueKind.Null);
		Assert.True(documentBody.GetProperty("currentRevision").ValueKind is JsonValueKind.Null);
		var documentId = Guid.Parse(documentBody.GetProperty("id").GetString()!);

		using var photoCreate = await PostJsonAsync(client, $"/api/events/{eventId}/assets",
			new { assetType = "photo" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, photoCreate.StatusCode);
		var photoBody = await photoCreate.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.PhotoAssetType, photoBody.GetProperty("assetType").GetString());
		var photoId = Guid.Parse(photoBody.GetProperty("id").GetString()!);

		// The document rides the unchanged three-step upload protocol.
		var (documentSessionId, documentUrl, _) = await CreateUploadSessionAsync(client, editorSession, documentId);
		factory.Storage.Store(factory.Storage.Find(documentUrl)!.BlobName, ValidPdf(1024));
		using (var documentFinalize = await FinalizeAsync(client, editorSession, documentSessionId))
		{
			Assert.Equal(HttpStatusCode.OK, documentFinalize.StatusCode);
			var revision = await documentFinalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(1, revision.GetProperty("revisionNumber").GetInt32());
			Assert.Equal(AssetEndpoints.PdfContentType, revision.GetProperty("contentType").GetString());
			Assert.Equal(1024, revision.GetProperty("sizeBytes").GetInt64());
		}

		// The photo session declares the first whitelisted image type; the
		// PNG transfer finalizes with its own effective type.
		var (photoSessionId, photoUrl, _) = await CreateUploadSessionAsync(client, editorSession, photoId);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(AssetEndpoints.JpegContentType,
				(await db.UploadSessions.SingleAsync(s => s.Id == photoSessionId)).ContentType);
		}
		factory.Storage.Store(factory.Storage.Find(photoUrl)!.BlobName, ValidPng(512), AssetEndpoints.PngContentType);
		using (var photoFinalize = await FinalizeAsync(client, editorSession, photoSessionId))
		{
			Assert.Equal(HttpStatusCode.OK, photoFinalize.StatusCode);
			var revision = await photoFinalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.PngContentType, revision.GetProperty("contentType").GetString());
			Assert.Equal(512, revision.GetProperty("sizeBytes").GetInt64());
		}

		string documentRevisionBlob;
		string photoRevisionBlob;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var assets = await db.Assets.ToListAsync();
			Assert.Equal(2, assets.Count);
			Assert.All(assets, a =>
			{
				Assert.Equal(eventId, a.EventId);
				Assert.Null(a.MusicalVersionId);
				Assert.Null(a.VoiceLabel);
				Assert.Equal(editorId, a.CreatedByAccountId);
			});
			Assert.Equal(2, await db.FileRevisions.CountAsync());
			Assert.All(await db.UploadSessions.ToListAsync(),
				s => Assert.Equal(PendingUploadState.Finalized, s.State));
			documentRevisionBlob = (await db.FileRevisions
				.SingleAsync(r => r.AssetId == documentId)).BlobName;
			photoRevisionBlob = (await db.FileRevisions
				.SingleAsync(r => r.AssetId == photoId)).BlobName;
		}
		// The pending objects are deleted, the promoted revisions remain.
		Assert.False(factory.Storage.Has(factory.Storage.Find(documentUrl)!.BlobName));
		Assert.False(factory.Storage.Has(factory.Storage.Find(photoUrl)!.BlobName));
		Assert.True(factory.Storage.Has(documentRevisionBlob));
		Assert.True(factory.Storage.Has(photoRevisionBlob));
		Assert.Equal(2, factory.Storage.ObjectCount);
	}

	[Fact]
	public async Task FinalizeValidatesDocumentAndPhotoContent()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "service", title = "Gottesdienst mit Programm" });
		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var photoId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");

		// Document: correct content type, wrong magic bytes.
		var (documentSessionId, documentUrl, _) = await CreateUploadSessionAsync(client, editorSession, documentId);
		factory.Storage.Store(factory.Storage.Find(documentUrl)!.BlobName,
			"Kein Dokument, nur Text."u8.ToArray(), AssetEndpoints.PdfContentType);
		using (var response = await FinalizeAsync(client, editorSession, documentSessionId))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidDocumentMessage, problem.GetProperty("title").GetString());
		}

		// Document: valid PDF bytes but a content type outside the whitelist.
		var (secondDocumentSessionId, secondDocumentUrl, _) =
			await CreateUploadSessionAsync(client, editorSession, documentId);
		factory.Storage.Store(factory.Storage.Find(secondDocumentUrl)!.BlobName,
			ValidPdf(512), "text/plain");
		using (var response = await FinalizeAsync(client, editorSession, secondDocumentSessionId))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidDocumentMessage, problem.GetProperty("title").GetString());
		}

		// Photo: JPEG bytes presented as image/png fail the honest header gate.
		var (photoSessionId, photoUrl, _) = await CreateUploadSessionAsync(client, editorSession, photoId);
		factory.Storage.Store(factory.Storage.Find(photoUrl)!.BlobName,
			ValidJpeg(512), AssetEndpoints.PngContentType);
		using (var response = await FinalizeAsync(client, editorSession, photoSessionId))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidPhotoMessage, problem.GetProperty("title").GetString());
		}

		// Photo: valid PNG bytes but a content type outside the image whitelist.
		var (secondPhotoSessionId, secondPhotoUrl, _) =
			await CreateUploadSessionAsync(client, editorSession, photoId);
		factory.Storage.Store(factory.Storage.Find(secondPhotoUrl)!.BlobName,
			ValidPng(512), AssetEndpoints.PdfContentType);
		using (var response = await FinalizeAsync(client, editorSession, secondPhotoSessionId))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidPhotoMessage, problem.GetProperty("title").GetString());
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var document = await db.Assets.SingleAsync(a => a.Id == documentId);
			var photo = await db.Assets.SingleAsync(a => a.Id == photoId);
			Assert.Null(document.CurrentRevisionId);
			Assert.Null(photo.CurrentRevisionId);
			Assert.Equal(0, await db.FileRevisions.CountAsync());
			Assert.Equal(0, factory.Storage.ObjectCount);
			Assert.All(await db.UploadSessions.ToListAsync(),
				s => Assert.Equal(PendingUploadState.Abandoned, s.State));
		}
	}

	[Fact]
	public async Task PhotoMagicBytesAcceptJpegPngAndWebp()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "festival", title = "Sängerfest" });
		var jpegId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		var pngId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		var webpId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");

		async Task FinalizePhoto(Guid assetId, byte[] content, string contentType, string expectedType)
		{
			var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
			factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, content, contentType);
			using var finalize = await FinalizeAsync(client, editorSession, sessionId);
			Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
			var revision = await finalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(expectedType, revision.GetProperty("contentType").GetString());
		}

		await FinalizePhoto(jpegId, ValidJpeg(512), AssetEndpoints.JpegContentType, AssetEndpoints.JpegContentType);
		await FinalizePhoto(pngId, ValidPng(640), AssetEndpoints.PngContentType, AssetEndpoints.PngContentType);
		await FinalizePhoto(webpId, ValidWebp(768), AssetEndpoints.WebpContentType, AssetEndpoints.WebpContentType);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(3, await db.FileRevisions.CountAsync());
		}
	}

	[Fact]
	public async Task PhotoFinalizeDerivesTypeFromMagicBytesWithoutStoredContentType()
	{
		// ARC-017 block-list commits carry no blob content type; the session
		// always declares the first whitelisted type (image/jpeg), so the
		// finalize fallback must derive the effective type from the header
		// magic bytes instead of failing every non-JPEG photograph.
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "concert", title = "Fotofallback" });
		var pngId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		var webpId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		var garbageId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		var mismatchId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");

		// PNG without a stored content type finalizes as image/png.
		var (pngSessionId, pngUrl, _) = await CreateUploadSessionAsync(client, editorSession, pngId);
		factory.Storage.Store(factory.Storage.Find(pngUrl)!.BlobName, ValidPng(512));
		using (var pngFinalize = await FinalizeAsync(client, editorSession, pngSessionId))
		{
			Assert.Equal(HttpStatusCode.OK, pngFinalize.StatusCode);
			var revision = await pngFinalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.PngContentType, revision.GetProperty("contentType").GetString());
			Assert.Equal(512, revision.GetProperty("sizeBytes").GetInt64());
		}

		// WEBP without a stored content type finalizes as image/webp.
		var (webpSessionId, webpUrl, _) = await CreateUploadSessionAsync(client, editorSession, webpId);
		factory.Storage.Store(factory.Storage.Find(webpUrl)!.BlobName, ValidWebp(640));
		using (var webpFinalize = await FinalizeAsync(client, editorSession, webpSessionId))
		{
			Assert.Equal(HttpStatusCode.OK, webpFinalize.StatusCode);
			var revision = await webpFinalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.WebpContentType, revision.GetProperty("contentType").GetString());
		}

		// A garbage header matches no image signature and is rejected with
		// the German photo message; the blob is deleted and the session
		// abandoned.
		var (garbageSessionId, garbageUrl, _) = await CreateUploadSessionAsync(client, editorSession, garbageId);
		factory.Storage.Store(factory.Storage.Find(garbageUrl)!.BlobName, "Kein Bild, nur Text."u8.ToArray());
		using (var garbageFinalize = await FinalizeAsync(client, editorSession, garbageSessionId))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, garbageFinalize.StatusCode);
			var problem = await garbageFinalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidPhotoMessage, problem.GetProperty("title").GetString());
		}
		Assert.False(factory.Storage.Has(factory.Storage.Find(garbageUrl)!.BlobName));

		// An explicitly stored type keeps the correlated rule: PNG bytes
		// presented as image/webp fail the honest header gate.
		var (mismatchSessionId, mismatchUrl, _) = await CreateUploadSessionAsync(client, editorSession, mismatchId);
		factory.Storage.Store(factory.Storage.Find(mismatchUrl)!.BlobName,
			ValidPng(512), AssetEndpoints.WebpContentType);
		using (var mismatchFinalize = await FinalizeAsync(client, editorSession, mismatchSessionId))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatchFinalize.StatusCode);
			var problem = await mismatchFinalize.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.InvalidPhotoMessage, problem.GetProperty("title").GetString());
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(2, await db.FileRevisions.CountAsync());
			var pngRevision = await db.FileRevisions.SingleAsync(r => r.AssetId == pngId);
			var webpRevision = await db.FileRevisions.SingleAsync(r => r.AssetId == webpId);
			Assert.Equal(AssetEndpoints.PngContentType, pngRevision.ContentType);
			Assert.Equal(AssetEndpoints.WebpContentType, webpRevision.ContentType);
			Assert.Null((await db.Assets.SingleAsync(a => a.Id == garbageId)).CurrentRevisionId);
			Assert.Null((await db.Assets.SingleAsync(a => a.Id == mismatchId)).CurrentRevisionId);
			Assert.Equal(PendingUploadState.Abandoned,
				(await db.UploadSessions.SingleAsync(s => s.Id == garbageSessionId)).State);
			Assert.Equal(PendingUploadState.Abandoned,
				(await db.UploadSessions.SingleAsync(s => s.Id == mismatchSessionId)).State);
			// The garbage and mismatched pending objects are gone, the two
			// promoted photo revisions remain.
			Assert.Equal(2, factory.Storage.ObjectCount);
		}
	}

	[Fact]
	public async Task EventAssetFinalizeIsIdempotent()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "concert", title = "Idempotenzkonzert" });
		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, documentId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(768));

		using var first = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		var firstRevision = await first.Content.ReadFromJsonAsync<JsonElement>();
		var revisionId = Guid.Parse(firstRevision.GetProperty("revisionId").GetString()!);

		using var retry = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		var retryRevision = await retry.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(revisionId, Guid.Parse(retryRevision.GetProperty("revisionId").GetString()!));

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.FileRevisions.CountAsync());
			Assert.Equal(revisionId, (await db.Assets.SingleAsync()).CurrentRevisionId);
		}
	}

	[Fact]
	public async Task PublishedEventDetailListsFinalizedDocumentsToMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new
		{
			kind = "concert",
			title = "Veröffentlichtes Programm",
			dateYear = 1951,
			dateMonth = 3,
			dateDay = 9,
			sourceNote = "Programmheft im Vereinsarchiv.",
		});
		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document",
			description: "Programmheft 1951");
		var photoId = await CreateEventAssetAsync(client, editorSession, eventId, "photo",
			description: "Bühnenfoto");
		var pendingId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var (_, _, documentRevision) = await UploadAndFinalizeAsync(
			factory, client, editorSession, documentId, ValidPdf(1024), AssetEndpoints.PdfContentType);
		var (_, _, photoRevision) = await UploadAndFinalizeAsync(
			factory, client, editorSession, photoId, ValidPng(512), AssetEndpoints.PngContentType);

		// The editor sees all three items, pending ones without tickets, in
		// the createdAt/id order the endpoint promises.
		var editorDetail = await GetEventDetailAsync(client, editorSession, eventId);
		var editorDocuments = editorDetail.GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal(3, editorDocuments.Count);
		var editorIds = editorDocuments.Select(d => Guid.Parse(d.GetProperty("id").GetString()!)).ToList();
		Assert.Equal(await ExpectedDocumentOrderAsync(factory, eventId), editorIds);
		Assert.Equal(AssetEndpoints.DocumentAssetType, editorDocuments[0].GetProperty("assetType").GetString());
		Assert.Equal("Programmheft 1951", editorDocuments[0].GetProperty("description").GetString());
		var editorDocumentRevision = editorDocuments[0].GetProperty("currentRevision");
		Assert.Equal(Guid.Parse(documentRevision.GetProperty("revisionId").GetString()!),
			Guid.Parse(editorDocumentRevision.GetProperty("revisionId").GetString()!));
		Assert.Equal(1, editorDocumentRevision.GetProperty("revisionNumber").GetInt32());
		Assert.Equal(AssetEndpoints.PdfContentType, editorDocumentRevision.GetProperty("contentType").GetString());
		Assert.Equal(1024, editorDocumentRevision.GetProperty("sizeBytes").GetInt64());
		Assert.Equal(Guid.Parse(photoRevision.GetProperty("revisionId").GetString()!),
			Guid.Parse(editorDocuments[1].GetProperty("currentRevision").GetProperty("revisionId").GetString()!));
		Assert.True(editorDocuments[2].GetProperty("currentRevision").ValueKind is JsonValueKind.Null);

		await PublishEventAsync(client, editorSession, eventId);

		// Members see only finalized material, sorted by createdAt then id.
		var memberDetail = await GetEventDetailAsync(client, memberSession, eventId);
		var memberDocuments = memberDetail.GetProperty("documents").EnumerateArray().ToList();
		Assert.Equal(2, memberDocuments.Count);
		var memberIds = memberDocuments.Select(d => Guid.Parse(d.GetProperty("id").GetString()!)).ToList();
		Assert.Equal((await ExpectedDocumentOrderAsync(factory, eventId))
			.Where(id => id != pendingId).ToList(), memberIds);
		Assert.All(memberDocuments, d =>
		{
			Assert.False(d.GetProperty("currentRevision").ValueKind is JsonValueKind.Null);
			Assert.NotNull(d.GetProperty("currentRevision").GetProperty("revisionId").GetString());
			Assert.NotNull(d.GetProperty("createdAt").GetString());
		});
		Assert.Equal(AssetEndpoints.PhotoAssetType, memberDocuments[1].GetProperty("assetType").GetString());
		Assert.Equal("Programmheft 1951", memberDocuments[0].GetProperty("description").GetString());
	}

	[Fact]
	public async Task DraftEventDocumentsStayInvisibleToMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Entwurfskonzert" });
		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var pendingId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		await UploadAndFinalizeAsync(factory, client, editorSession, documentId, ValidPdf(1024), AssetEndpoints.PdfContentType);

		// The member receives the indistinguishable event 404 and cannot read
		// the document either, even knowing the asset id.
		using (var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}"))
		{
			memberDetail.Headers.Add("Cookie", memberSession);
			using var response = await client.SendAsync(memberDetail);
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(EventEndpoints.NotFoundMessage, problem.GetProperty("title").GetString());
		}
		using (var memberAccess = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{documentId}/access"))
		{
			memberAccess.Headers.Add("Cookie", memberSession);
			using var response = await client.SendAsync(memberAccess);
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.AssetNotFoundMessage, problem.GetProperty("title").GetString());
		}

		// Editors inspect draft material: finalized documents issue tickets,
		// pending ones answer the neutral event message.
		using (var editorAccess = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{documentId}/access"))
		{
			editorAccess.Headers.Add("Cookie", editorSession);
			using var response = await client.SendAsync(editorAccess);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}
		using (var editorPending = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{pendingId}/access"))
		{
			editorPending.Headers.Add("Cookie", editorSession);
			using var response = await client.SendAsync(editorPending);
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.NoCurrentRevisionEventMessage, problem.GetProperty("title").GetString());
		}
	}

	[Fact]
	public async Task MemberAccessReturnsViewAndDownloadTicketsForPublishedEvent()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "concert", title = "Lesezauber" });
		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var pendingId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		var (_, _, revisionBody) = await UploadAndFinalizeAsync(
			factory, client, editorSession, documentId, ValidPdf(1024), AssetEndpoints.PdfContentType);
		var revisionId = Guid.Parse(revisionBody.GetProperty("revisionId").GetString()!);
		await PublishEventAsync(client, editorSession, eventId);
		using var access = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{documentId}/access");
		access.Headers.Add("Cookie", memberSession);
		using var response = await client.SendAsync(access);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(documentId, Guid.Parse(body.GetProperty("assetId").GetString()!));
		Assert.Equal(revisionId, Guid.Parse(body.GetProperty("revisionId").GetString()!));
		Assert.Equal(1, body.GetProperty("revisionNumber").GetInt32());
		Assert.Equal(AssetEndpoints.PdfContentType, body.GetProperty("contentType").GetString());
		Assert.Equal(1024, body.GetProperty("sizeBytes").GetInt64());
		var viewUrl = body.GetProperty("viewUrl").GetString()!;
		var downloadUrl = body.GetProperty("downloadUrl").GetString()!;
		Assert.NotEqual(viewUrl, downloadUrl);
		var expiresAt = DateTimeOffset.Parse(body.GetProperty("expiresAt").GetString()!);
		Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
		var view = factory.Storage.Find(viewUrl)!;
		var download = factory.Storage.Find(downloadUrl)!;
		Assert.False(view.Download);
		Assert.True(download.Download);
		Assert.Equal(AssetEndpoints.PdfContentType, view.ContentType);
		Assert.Equal(AssetEndpoints.PdfContentType, download.ContentType);
		Assert.Equal(TimeSpan.FromMinutes(15), view.Lifetime);

		// A member who knows the id of a pending asset still gets the neutral
		// pending answer, not a ticket.
		using var pendingAccess = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{pendingId}/access");
		pendingAccess.Headers.Add("Cookie", memberSession);
		using var pendingResponse = await client.SendAsync(pendingAccess);
		Assert.Equal(HttpStatusCode.NotFound, pendingResponse.StatusCode);
		var pendingProblem = await pendingResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.NoCurrentRevisionEventMessage, pendingProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task OtherEditorsCannotTouchAnotherEditorsEventAsset()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, SecondEditor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, SecondEditor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "concert", title = "Fremdmaterial" });
		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document");

		// The pending object cannot be reassigned: the second editor cannot
		// initiate an upload session on someone else's asset.
		using (var sessionResponse = await PostJsonAsync(client,
			$"/api/assets/{documentId}/upload-session", new { }, secondSession))
		{
			Assert.Equal(HttpStatusCode.Forbidden, sessionResponse.StatusCode);
			var problem = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.AssetOwnerMessage, problem.GetProperty("title").GetString());
		}

		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, documentId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, ValidPdf(512));

		// Finalizing the first editor's pending session stays refused.
		using (var finalizeResponse = await FinalizeAsync(client, secondSession, sessionId))
		{
			Assert.Equal(HttpStatusCode.Forbidden, finalizeResponse.StatusCode);
			var problem = await finalizeResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadOwnerMessage, problem.GetProperty("title").GetString());
		}

		// Metadata edits stay creator-only as well.
		using (var patchResponse = await PatchJsonAsync(client,
			$"/api/assets/{documentId}", new { description = "Übernommen" }, secondSession))
		{
			Assert.Equal(HttpStatusCode.Forbidden, patchResponse.StatusCode);
			var problem = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.AssetOwnerMessage, problem.GetProperty("title").GetString());
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var session = await db.UploadSessions.SingleAsync();
			Assert.Equal(PendingUploadState.Pending, session.State);
			Assert.Null(session.FinalizedRevisionId);
			Assert.Equal(0, await db.FileRevisions.CountAsync());
			Assert.Null((await db.Assets.SingleAsync()).Description);
		}

		// The creator can still finalize their own session.
		using var ownFinalize = await FinalizeAsync(client, editorSession, sessionId);
		Assert.Equal(HttpStatusCode.OK, ownFinalize.StatusCode);
	}

	[Fact]
	public async Task AttachingAssetsLeavesTheEventRowUnchanged()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new
		{
			kind = "wedding",
			title = "Hochzeitsauftritt",
			venue = "Stadtpfarrkirche",
			dateYear = 1949,
			dateMonth = 7,
			dateDay = 2,
			startTime = "11:00",
			notes = "Stehchoral im Freien.",
			sourceNote = "Aushang im Pfarramt.",
		});
		var before = await GetEventDetailAsync(client, editorSession, eventId);

		var documentId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var photoId = await CreateEventAssetAsync(client, editorSession, eventId, "photo");
		await UploadAndFinalizeAsync(factory, client, editorSession, documentId, ValidPdf(1024), AssetEndpoints.PdfContentType);
		await UploadAndFinalizeAsync(factory, client, editorSession, photoId, ValidPng(512), AssetEndpoints.PngContentType);

		// Every detail field besides documents stays identical.
		var after = await GetEventDetailAsync(client, editorSession, eventId);
		foreach (var property in before.EnumerateObject())
		{
			if (property.Name == "documents")
			{
				Assert.Equal(2, after.GetProperty("documents").GetArrayLength());
				continue;
			}
			Assert.True(JsonElement.DeepEquals(property.Value, after.GetProperty(property.Name)),
				$"Event detail field {property.Name} changed when attaching assets.");
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
			Assert.Equal(0u, persisted.RowVersion);
			Assert.Equal(DateTimeOffset.Parse(before.GetProperty("createdAt").GetString()!), persisted.CreatedAt);
			Assert.Equal(DateTimeOffset.Parse(before.GetProperty("updatedAt").GetString()!), persisted.UpdatedAt);
			Assert.Null(persisted.PublishedAt);
			Assert.Equal(1, await db.Events.CountAsync());
			// ARC-025 acceptance: attaching a scanned programme creates no
			// confirmed performance records. ARC-026 introduces the ordered
			// programme aggregate beside the event; what must not exist at
			// all is anything that tracks performed or confirmed songs.
			Assert.DoesNotContain(db.Model.GetEntityTypes(), t =>
				t.ClrType.Name.Contains("Performance", StringComparison.OrdinalIgnoreCase)
				|| t.ClrType.Name.Contains("Performed", StringComparison.OrdinalIgnoreCase)
				|| t.ClrType.Name.Contains("Confirmed", StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public async Task EventAssetBudgetIsScopedPerEvent()
	{
		// The event budget spans pending declarations and finalized revisions
		// of the event's own assets; version assets budget separately.
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Assets:MaxUploadBytes"] = "8192",
			["Archive:Assets:MaxCollectionBytes"] = "4096",
		});
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "concert", title = "Budgetkonzert" });
		var documentAId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var documentBId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var (_, versionId) = await CreateSongWithVersionAsync(client, editorSession);
		var scoreId = await CreateVersionAssetAsync(client, editorSession, versionId, "score");

		await UploadAndFinalizeAsync(factory, client, editorSession, documentAId, ValidPdf(1024), AssetEndpoints.PdfContentType);

		// 1024 finalized + 3073 declared exceed the 4096-byte event budget.
		using (var exceeding = await PostJsonAsync(client,
			$"/api/assets/{documentBId}/upload-session", new { sizeBytes = 3073 }, editorSession))
		{
			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, exceeding.StatusCode);
			var problem = await exceeding.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UploadTooLargeMessage, problem.GetProperty("title").GetString());
		}

		// Exactly at the limit (1024 + 3072 = 4096) the session fits.
		var (_, _, fitted) = await CreateUploadSessionAsync(client, editorSession, documentBId,
			new { sizeBytes = 3072, fileName = "programm-2.pdf" });

		// The version-owned asset keeps its own budget: the full event budget
		// does not starve it, and its pending reservation stays independent.
		using (var versionSession = await PostJsonAsync(client,
			$"/api/assets/{scoreId}/upload-session", new { sizeBytes = 1024 }, editorSession))
		{
			Assert.Equal(HttpStatusCode.Created, versionSession.StatusCode);
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(0, await db.UploadSessions.CountAsync(
				s => s.AssetId == documentBId && s.State == PendingUploadState.Pending
					&& s.Id != Guid.Parse(fitted.GetProperty("uploadSessionId").GetString()!)));
		}
	}

	[Fact]
	public async Task OwnerWhitelistsRejectForeignTypes()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "other", title = "Typgrenzen" });
		var (_, versionId) = await CreateSongWithVersionAsync(client, editorSession);

		foreach (var versionType in new[] { AssetEndpoints.ScoreAssetType, AssetEndpoints.AudioAssetType, AssetEndpoints.MidiAssetType })
		{
			using var response = await PostJsonAsync(client,
				$"/api/events/{eventId}/assets", new { assetType = versionType }, editorSession);
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UnknownAssetTypeMessage, problem.GetProperty("title").GetString());
		}

		foreach (var eventType in new[] { AssetEndpoints.DocumentAssetType, AssetEndpoints.PhotoAssetType })
		{
			using var response = await PostJsonAsync(client,
				$"/api/musical-versions/{versionId}/assets", new { assetType = eventType }, editorSession);
			Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UnknownAssetTypeMessage, problem.GetProperty("title").GetString());
		}

		// PATCH keeps the owner whitelists as well.
		var eventAssetId = await CreateEventAssetAsync(client, editorSession, eventId, "document");
		var versionAssetId = await CreateVersionAssetAsync(client, editorSession, versionId, "score");
		using (var eventPatch = await PatchJsonAsync(client,
			$"/api/assets/{eventAssetId}", new { assetType = "score" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, eventPatch.StatusCode);
			var problem = await eventPatch.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UnknownAssetTypeMessage, problem.GetProperty("title").GetString());
		}
		using (var versionPatch = await PatchJsonAsync(client,
			$"/api/assets/{versionAssetId}", new { assetType = "document" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.UnprocessableEntity, versionPatch.StatusCode);
			var problem = await versionPatch.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(AssetEndpoints.UnknownAssetTypeMessage, problem.GetProperty("title").GetString());
		}

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(2, await db.Assets.CountAsync());
		}
	}

	[Fact]
	public async Task OverlongEventDescriptionIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var eventId = await CreateEventAsync(client, editorSession, new { kind = "other", title = "Lange Liste" });

		using var response = await PostJsonAsync(client,
			$"/api/events/{eventId}/assets",
			new { assetType = "document", description = new string('x', 501) }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(AssetEndpoints.DescriptionTooLongMessage, problem.GetProperty("title").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(0, await db.Assets.CountAsync());
		}

		// Up to the documented 500 characters are accepted.
		using var accepted = await PostJsonAsync(client,
			$"/api/events/{eventId}/assets",
			new { assetType = "document", description = new string('x', 500) }, editorSession);
		Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
	}

	private static async Task<Guid> CreateEventAsync(HttpClient client, string editorSession, object body)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/events", body, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(parsed.GetProperty("event").GetProperty("id").GetString()!);
	}

	private static async Task PublishEventAsync(HttpClient client, string editorSession, Guid eventId)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/events/{eventId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<JsonElement> GetEventDetailAsync(HttpClient client, string session, Guid eventId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("event");
	}

	/// <summary>createdAt-ascending then id order, the document sort rule.</summary>
	private static async Task<List<Guid>> ExpectedDocumentOrderAsync(AuthApiFactory factory, Guid eventId)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		return await db.Assets.AsNoTracking()
			.Where(a => a.EventId == eventId)
			.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
			.Select(a => a.Id)
			.ToListAsync();
	}

	private static async Task<Guid> CreateEventAssetAsync(
		HttpClient client, string editorSession, Guid eventId, string assetType, string? description = null)
	{
		using var response = await PostJsonAsync(client, $"/api/events/{eventId}/assets",
			new { assetType, description }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("id").GetString()!);
	}

	private static async Task<(Guid SongId, Guid VersionId)> CreateSongWithVersionAsync(
		HttpClient client, string editorSession, string title = "Notenlied")
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var songId = Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>())
			.GetProperty("song").GetProperty("id").GetString()!);

		using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		detailRequest.Headers.Add("Cookie", editorSession);
		using var detailResponse = await client.SendAsync(detailRequest);
		detailResponse.EnsureSuccessStatusCode();
		var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
		var versionId = Guid.Parse(detail.GetProperty("song").GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		return (songId, versionId);
	}

	private static async Task<Guid> CreateVersionAssetAsync(
		HttpClient client, string editorSession, Guid versionId, string assetType)
	{
		using var response = await PostJsonAsync(client,
			$"/api/musical-versions/{versionId}/assets", new { assetType }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("id").GetString()!);
	}

	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> CreateUploadSessionAsync(
		HttpClient client, string session, Guid assetId, object? body = null)
	{
		using var response = await PostJsonAsync(client,
			$"/api/assets/{assetId}/upload-session", body ?? new { }, session);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (
			Guid.Parse(json.GetProperty("uploadSessionId").GetString()!),
			json.GetProperty("uploadUrl").GetString()!,
			json);
	}

	/// <summary>Creates the session, stores the content and finalizes.</summary>
	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Revision)> UploadAndFinalizeAsync(
		AuthApiFactory factory, HttpClient client, string session, Guid assetId,
		byte[] content, string contentType, object? declaration = null)
	{
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, session, assetId, declaration);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName, content, contentType);
		using var finalize = await FinalizeAsync(client, session, sessionId);
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

	private static byte[] ValidJpeg(long size)
	{
		var bytes = new byte[Math.Max(size, 3)];
		new byte[] { 0xFF, 0xD8, 0xFF }.CopyTo(bytes, 0);
		return bytes;
	}

	private static byte[] ValidPng(long size)
	{
		var bytes = new byte[Math.Max(size, 12)];
		new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
		return bytes;
	}

	private static byte[] ValidWebp(long size)
	{
		var bytes = new byte[Math.Max(size, 12)];
		"RIFF"u8.CopyTo(bytes);
		"WEBP"u8.CopyTo(bytes.AsSpan(8));
		return bytes;
	}

	private static async Task<HttpResponseMessage> PostJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPost(path, body, $"{cookie}; {session}", token));
	}

	private static async Task<HttpResponseMessage> PatchJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPatch(path, body, $"{cookie}; {session}", token));
	}

	private static HttpRequestMessage AuthedPatch(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Patch, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
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
}
