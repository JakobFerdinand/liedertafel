using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Assets;

/// <summary>
/// ARC-015 private score assets: one upload protocol, one visibility decision.
/// Slice boundary and extension points:
/// - New asset types (audio, MIDI, event documents) register in the create
///   endpoint's type check (ARC-016/ARC-023); "score" is the only type here.
/// - The upload session contract (create ticket, browser PUT, finalize) is
///   shared with ARC-017; block upload extends it, it does not replace it.
/// - ARC-025 extends this pattern with event-owned assets via an owner
///   registry (<see cref="ArchiveAsset.EventId"/>) instead of the
///   musical-version link: document/photo types, creator-only session
///   initiation, and event-scoped visibility and budgets.
/// - Revision changes: swapping <see cref="ArchiveAsset.CurrentRevisionId"/> is
///   the atom ARC-031 performs; extraction/search consumers subscribe later
///   (ARC-032/ARC-033).
/// - Retained reference: revisions must not be removed while referenced
///   (ARC-037); the database enforces this via the current-revision FK.
/// Signed ticket URLs exist only in JSON responses and are never logged.
/// </summary>
public static class AssetEndpoints
{
	public const string UnknownAssetTypeMessage = "Unbekannter Materialtyp.";

	public const string MusicalVersionNotFoundMessage = "Musikalische Fassung nicht gefunden.";

	public const string AssetNotFoundMessage = "Material nicht gefunden.";

	public const string UploadNotFoundMessage = "Upload nicht gefunden.";

	public const string UploadOwnerMessage = "Nur die anlegendende Person kann den Upload abschließen.";

	public const string AssetOwnerMessage = "Nur die anlegendende Person kann das Material bearbeiten.";

	public const string AssetTypeLockedMessage = "Der Materialtyp kann nach dem ersten Hochladen nicht geändert werden.";

	public const string VoiceLabelTooLongMessage = "Das Stimmenlabel ist zu lang.";

	public const string DescriptionTooLongMessage = "Die Beschreibung ist zu lang.";

	public const string UploadAbandonedMessage = "Upload wurde abgebrochen.";

	public const string UploadExpiredMessage = "Der Uploadzeitraum ist abgelaufen.";

	public const string UploadAlreadyFinalizedMessage = "Der Upload wurde bereits abgeschlossen.";

	public const string UploadCancelledMessage = "Der Upload wurde abgebrochen.";

	public const string UploadMismatchMessage = "Die Datei passt nicht zur bestehenden Uploadsitzung.";

	public const string FileNameTooLongMessage = "Der Dateiname ist zu lang.";

	public const string UploadMissingMessage = "Die Datei wurde noch nicht übertragen.";

	public const string UploadTooLargeMessage = "Die Datei ist zu groß.";

	public const string InvalidPdfMessage = "Die Datei ist kein gültiges PDF.";

	public const string InvalidAudioMessage = "Die Datei ist keine gültige Audiodatei.";

	public const string InvalidMidiMessage = "Die Datei ist keine gültige MIDI-Datei.";

	public const string InvalidDocumentMessage = "Die Datei ist kein gültiges Dokument.";

	public const string InvalidPhotoMessage = "Die Datei ist kein gültiges Bild.";

	public const string StorageFailureMessage = "Speicherdienst nicht erreichbar.";

	/// <summary>ARC-017: storage default for block-list commits without an explicit blob content type.</summary>
	public const string StorageDefaultContentType = "application/octet-stream";

	public const string NoCurrentRevisionMessage = "Für diese Fassung liegen noch keine aktuellen Noten vor.";

	/// <summary>ARC-025: neutral pending message for event-owned assets.</summary>
	public const string NoCurrentRevisionEventMessage = "Für dieses Material wurde noch keine Datei hochgeladen.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string ScoreAssetType = "score";

	public const string AudioAssetType = "audio";

	public const string MidiAssetType = "midi";

	public const string DocumentAssetType = "document";

	public const string PhotoAssetType = "photo";

	public const string PdfContentType = "application/pdf";

	public const string Mp3ContentType = "audio/mpeg";

	public const string MidiContentType = "audio/midi";

	public const string XMidiContentType = "audio/x-midi";

	public const string JpegContentType = "image/jpeg";

	public const string PngContentType = "image/png";

	public const string WebpContentType = "image/webp";

	/// <summary>ARC-016/ARC-025 content-type whitelist per asset type.</summary>
	private static readonly IReadOnlyDictionary<string, string[]> AssetTypeContentTypes =
		new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[ScoreAssetType] = [PdfContentType],
			[AudioAssetType] = [Mp3ContentType, "audio/mp4", "audio/x-m4a", "audio/wav", "audio/ogg"],
			[MidiAssetType] = [MidiContentType, XMidiContentType],
			[DocumentAssetType] = [PdfContentType],
			[PhotoAssetType] = [JpegContentType, PngContentType, WebpContentType],
		};

	/// <summary>
	/// ARC-025 per-owner type whitelists: musical-version assets keep the
	/// ARC-016 set, event-owned assets only accept documents and photographs.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, string[]> VersionAssetTypes =
		new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[ScoreAssetType] = AssetTypeContentTypes[ScoreAssetType],
			[AudioAssetType] = AssetTypeContentTypes[AudioAssetType],
			[MidiAssetType] = AssetTypeContentTypes[MidiAssetType],
		};

	private static readonly IReadOnlyDictionary<string, string[]> EventAssetTypes =
		new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[DocumentAssetType] = AssetTypeContentTypes[DocumentAssetType],
			[PhotoAssetType] = AssetTypeContentTypes[PhotoAssetType],
		};

	/// <summary>Validates a type against the owner's whitelist and reports the German problem otherwise.</summary>
	private static IResult? ValidateAssetTypeForOwner(string assetType, bool eventOwned)
	{
		var whitelist = eventOwned ? EventAssetTypes : VersionAssetTypes;
		if (!whitelist.ContainsKey(assetType))
			return Results.Problem(statusCode: 422, title: UnknownAssetTypeMessage);
		return null;
	}

	private static string[] ContentTypeWhitelist(string assetType) =>
		AssetTypeContentTypes.TryGetValue(assetType, out var contentTypes)
			? contentTypes
			: [PdfContentType];

	/// <summary>German finalize rejection per asset type.</summary>
	private static string InvalidTypeMessage(string assetType) => assetType switch
	{
		AudioAssetType => InvalidAudioMessage,
		MidiAssetType => InvalidMidiMessage,
		DocumentAssetType => InvalidDocumentMessage,
		PhotoAssetType => InvalidPhotoMessage,
		_ => InvalidPdfMessage,
	};

	/// <summary>PNG signature: 89 50 4E 47 0D 0A 1A 0A.</summary>
	private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	/// <summary>
	/// Type-conditioned magic-byte gate: score/document demand the "%PDF-"
	/// prefix (ARC-015); photographs must present the honest signature of
	/// their effective image type (JPEG FF D8 FF, PNG signature, WEBP "RIFF"
	/// + bytes 8..11 "WEBP"); audio/MIDI skip the gate (ARC-016).
	/// </summary>
	private static bool HasValidMagicBytes(string assetType, byte[]? header, string? contentType) => assetType switch
	{
		ScoreAssetType or DocumentAssetType => header is not null && header.AsSpan().StartsWith("%PDF-"u8),
		PhotoAssetType => HasValidImageHeader(header, contentType),
		_ => true,
	};

	private static bool HasValidImageHeader(byte[]? header, string? contentType)
	{
		if (header is null)
			return false;
		var span = header.AsSpan();
		if (string.Equals(contentType, JpegContentType, StringComparison.OrdinalIgnoreCase))
			return span.Length >= 3 && span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF;
		if (string.Equals(contentType, PngContentType, StringComparison.OrdinalIgnoreCase))
			return span.Length >= PngMagic.Length && span.StartsWith(PngMagic);
		if (string.Equals(contentType, WebpContentType, StringComparison.OrdinalIgnoreCase))
			return span.Length >= 12
				&& span.StartsWith("RIFF"u8)
				&& span.Slice(8, 4).SequenceEqual("WEBP"u8);
		return false;
	}

	/// <summary>
	/// Derives the effective image content type from the header magic bytes:
	/// JPEG FF D8 FF, PNG signature, WEBP "RIFF" + bytes 8..11 "WEBP". Returns
	/// null when no whitelisted image signature matches.
	/// </summary>
	private static string? DetectImageContentType(byte[]? header)
	{
		if (header is null)
			return null;
		var span = header.AsSpan();
		if (span.Length >= 3 && span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF)
			return JpegContentType;
		if (span.Length >= PngMagic.Length && span.StartsWith(PngMagic))
			return PngContentType;
		if (span.Length >= 12
			&& span.StartsWith("RIFF"u8)
			&& span.Slice(8, 4).SequenceEqual("WEBP"u8))
			return WebpContentType;
		return null;
	}

	public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/musical-versions/{id}/assets", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreateAssetRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var assetType = (body?.AssetType ?? ScoreAssetType).Trim().ToLowerInvariant();
			var typeError = ValidateAssetTypeForOwner(assetType, eventOwned: false);
			if (typeError is not null)
				return typeError;
			var voiceLabel = CleanOptional(body?.VoiceLabel, 200, VoiceLabelTooLongMessage, out var voiceError);
			if (voiceError is not null)
				return voiceError;
			var description = CleanOptional(body?.Description, 500, DescriptionTooLongMessage, out var descriptionError);
			if (descriptionError is not null)
				return descriptionError;
			var version = await db.MusicalVersions.FirstOrDefaultAsync(v => v.Id == id, token);
			if (version is null)
				return Results.Problem(statusCode: 404, title: MusicalVersionNotFoundMessage);
			var asset = new ArchiveAsset
			{
				MusicalVersionId = version.Id,
				AssetType = assetType,
				VoiceLabel = voiceLabel,
				Description = description,
				CreatedAt = time.GetUtcNow(),
				CreatedByAccountId = decision!.AccountId,
			};
			db.Assets.Add(asset);
			await db.SaveChangesAsync(token);
			return Results.Created($"/api/assets/{asset.Id}", new
			{
				id = asset.Id,
				musicalVersionId = asset.MusicalVersionId,
				eventId = asset.EventId,
				assetType = asset.AssetType,
				voiceLabel = asset.VoiceLabel,
				description = asset.Description,
				createdAt = asset.CreatedAt,
				currentRevision = (object?)null,
			});
		}).DisableAntiforgery();

		/// <summary>
		/// ARC-025: editors attach a historical document or photograph to an
		/// event; the three-step upload protocol (session, browser PUT,
		/// finalize) is shared with the ARC-015 version assets. The event row
		/// itself is never touched: attaching material creates no programme
		/// or performance records (ARC-026 does that explicitly).
		/// </summary>
		app.MapPost("/api/events/{id}/assets", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreateEventAssetRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var assetType = (body?.AssetType ?? DocumentAssetType).Trim().ToLowerInvariant();
			var typeError = ValidateAssetTypeForOwner(assetType, eventOwned: true);
			if (typeError is not null)
				return typeError;
			var description = CleanOptional(body?.Description, 500, DescriptionTooLongMessage, out var descriptionError);
			if (descriptionError is not null)
				return descriptionError;
			var choirEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null)
				return Results.Problem(statusCode: 404, title: Events.EventEndpoints.NotFoundMessage);
			var asset = new ArchiveAsset
			{
				EventId = choirEvent.Id,
				AssetType = assetType,
				VoiceLabel = null,
				Description = description,
				CreatedAt = time.GetUtcNow(),
				CreatedByAccountId = decision!.AccountId,
			};
			db.Assets.Add(asset);
			await db.SaveChangesAsync(token);
			return Results.Created($"/api/assets/{asset.Id}", new
			{
				id = asset.Id,
				musicalVersionId = asset.MusicalVersionId,
				eventId = asset.EventId,
				assetType = asset.AssetType,
				voiceLabel = asset.VoiceLabel,
				description = asset.Description,
				createdAt = asset.CreatedAt,
				currentRevision = (object?)null,
			});
		}).DisableAntiforgery();

		app.MapPatch("/api/assets/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchAssetRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var asset = await db.Assets
				.Include(a => a.CurrentRevision)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (asset is null)
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			if (asset.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: AssetOwnerMessage);
			string? assetType = null;
			if (body?.AssetType is not null)
			{
				assetType = body.AssetType.Trim().ToLowerInvariant();
				var typeError = ValidateAssetTypeForOwner(assetType, eventOwned: asset.EventId is not null);
				if (typeError is not null)
					return typeError;
				if (asset.CurrentRevisionId is not null && assetType != asset.AssetType)
					return Results.Problem(statusCode: 409, title: AssetTypeLockedMessage);
			}
			var voiceLabel = PatchOptional(body?.VoiceLabel, 200, VoiceLabelTooLongMessage, out var voiceError);
			if (voiceError is not null)
				return voiceError;
			var description = PatchOptional(body?.Description, 500, DescriptionTooLongMessage, out var descriptionError);
			if (descriptionError is not null)
				return descriptionError;
			if (assetType is not null)
				asset.AssetType = assetType;
			if (body?.VoiceLabel is not null)
				asset.VoiceLabel = voiceLabel;
			if (body?.Description is not null)
				asset.Description = description;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new
			{
				id = asset.Id,
				musicalVersionId = asset.MusicalVersionId,
				eventId = asset.EventId,
				assetType = asset.AssetType,
				voiceLabel = asset.VoiceLabel,
				description = asset.Description,
				createdAt = asset.CreatedAt,
				currentRevision = asset.CurrentRevision is null
					? null
					: (object)RevisionPayload(asset.CurrentRevision),
			});
		}).DisableAntiforgery();

		app.MapPost("/api/assets/{id}/upload-session", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage, Guid id, CancellationToken token,
			CreateUploadSessionRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, token);
			if (asset is null)
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			// ARC-025: pending objects must not be reassigned to someone else;
			// only the asset's creator may initiate upload sessions (event and
			// version assets alike).
			if (asset.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: AssetOwnerMessage);
			// ARC-017: the declared identity is stored for the finalization
			// mismatch check; a stale session cannot commit a different file.
			string? declaredFileName = null;
			if (body?.FileName is not null)
			{
				declaredFileName = body.FileName.Trim();
				if (declaredFileName.Length == 0)
					declaredFileName = null;
				else if (declaredFileName.Length > 300)
					return Results.Problem(statusCode: 400, title: FileNameTooLongMessage);
			}
			var declaredSize = body?.SizeBytes;
			var storageOptions = options.Value;
			if (declaredSize > storageOptions.MaxUploadBytes)
				return Results.Problem(statusCode: 413, title: UploadTooLargeMessage);
			// ARC-017: a new session supersedes the editor's earlier pending
			// sessions on the same asset; failed transfers would otherwise
			// reserve budget until the grace-window cleanup catches them.
			var staleSessions = await db.UploadSessions
				.Where(s => s.AssetId == asset.Id
					&& s.CreatedByAccountId == decision!.AccountId
					&& s.State == PendingUploadState.Pending)
				.ToListAsync(token);
			foreach (var stale in staleSessions)
			{
				stale.State = PendingUploadState.Cancelled;
			}
			if (staleSessions.Count > 0)
				await db.SaveChangesAsync(token);
			foreach (var stale in staleSessions)
			{
				await DeletePendingBestEffortAsync(storage, stale.BlobName, token);
			}
			// Per-owner collection budget: the declared size must fit
			// alongside other pending sessions and finalized revisions.
			// Undeclared sessions reserve their per-file cap; declared ones
			// only their declared size. ARC-025: the budget scopes per owner —
			// version-owned assets share their musical version's budget,
			// event-owned assets the event's; both conditions together
			// exclude the other owner class in SQL and InMemory alike.
			var pendingBudget = await db.UploadSessions
				.Where(s => s.State == PendingUploadState.Pending
					&& s.Asset.MusicalVersionId == asset.MusicalVersionId
					&& s.Asset.EventId == asset.EventId)
				.SumAsync(s => (long?)(s.DeclaredSizeBytes ?? s.MaxSizeBytes), token) ?? 0;
			var finalizedBytes = await db.FileRevisions
				.Where(r => r.Asset.MusicalVersionId == asset.MusicalVersionId
					&& r.Asset.EventId == asset.EventId)
				.SumAsync(r => (long?)r.SizeBytes, token) ?? 0;
			if ((declaredSize ?? 0) + pendingBudget + finalizedBytes > storageOptions.MaxCollectionBytes)
				return Results.Problem(statusCode: 413, title: UploadTooLargeMessage);
			var now = time.GetUtcNow();
			var pending = new PendingUpload
			{
				AssetId = asset.Id,
				BlobName = $"pending/{Guid.CreateVersion7()}",
				ContentType = ContentTypeWhitelist(asset.AssetType)[0],
				MaxSizeBytes = storageOptions.MaxUploadBytes,
				DeclaredFileName = declaredFileName,
				DeclaredSizeBytes = declaredSize,
				UploadTicketExpiresAt = now + storageOptions.UploadSessionLifetime,
				State = PendingUploadState.Pending,
				CreatedByAccountId = decision!.AccountId,
				CreatedAt = now,
			};
			db.UploadSessions.Add(pending);
			await db.SaveChangesAsync(token);
			// ARC-049: storage misconfigurations and outages surface as a
			// German 502 instead of an unhandled 500 (the hosted account is
			// unreachable, for example, while Entra credentials are missing).
			string uploadUrl;
			try
			{
				uploadUrl = await storage.CreateUploadTicketAsync(
					pending.BlobName, storageOptions.UploadSessionLifetime, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: StorageFailureMessage);
			}
			return Results.Created($"/api/assets/{asset.Id}/access", new
			{
				uploadSessionId = pending.Id,
				blobName = (string?)null,
				uploadUrl,
				expiresAt = pending.UploadTicketExpiresAt,
				maxBytes = pending.MaxSizeBytes,
				blockBytes = storageOptions.UploadBlockBytes,
			});
		}).DisableAntiforgery();

		/// <summary>
		/// ARC-017: renewed upload sessions keep their pending blob name so a
		/// browser can resume committed blocks; the fresh ticket replaces an
		/// expired one even when the previous lifetime already elapsed.
		/// </summary>
		app.MapPost("/api/upload-sessions/{id}/renew", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, token);
			if (session is null)
				return Results.Problem(statusCode: 404, title: UploadNotFoundMessage);
			if (session.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: UploadOwnerMessage);
			if (session.State == PendingUploadState.Finalized)
				return Results.Problem(statusCode: 409, title: UploadAlreadyFinalizedMessage);
			if (session.State != PendingUploadState.Pending)
				return Results.Problem(statusCode: 409, title: UploadAbandonedMessage);
			var storageOptions = options.Value;
			session.UploadTicketExpiresAt = time.GetUtcNow() + storageOptions.UploadSessionLifetime;
			await db.SaveChangesAsync(token);
			string uploadUrl;
			try
			{
				uploadUrl = await storage.CreateUploadTicketAsync(
					session.BlobName, storageOptions.UploadSessionLifetime, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: StorageFailureMessage);
			}
			return Results.Ok(new
			{
				uploadSessionId = session.Id,
				blobName = session.BlobName,
				uploadUrl,
				expiresAt = session.UploadTicketExpiresAt,
				maxBytes = session.MaxSizeBytes,
				blockBytes = storageOptions.UploadBlockBytes,
			});
		}).DisableAntiforgery();

		/// <summary>
		/// ARC-017: editors abandon a stale transfer explicitly; cancellation
		/// is terminal, releases the pending blob immediately and keeps the
		/// session row so late renew/finalize calls report the cancelled state.
		/// </summary>
		app.MapDelete("/api/upload-sessions/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IAssetStorageAdapter storage, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, token);
			if (session is null)
				return Results.Problem(statusCode: 404, title: UploadNotFoundMessage);
			if (session.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: UploadOwnerMessage);
			if (session.State is PendingUploadState.Finalized or PendingUploadState.Abandoned)
				return Results.Problem(statusCode: 409, title: UploadAbandonedMessage);
			if (session.State == PendingUploadState.Cancelled)
				return Results.Problem(statusCode: 409, title: UploadCancelledMessage);
			session.State = PendingUploadState.Cancelled;
			await db.SaveChangesAsync(token);
			await DeletePendingBestEffortAsync(storage, session.BlobName, token);
			return Results.NoContent();
		}).DisableAntiforgery();

		app.MapPost("/api/upload-sessions/{id}/finalize", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage, Guid id, CancellationToken token,
			FinalizeUploadRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var session = await db.UploadSessions
				.Include(s => s.Asset).ThenInclude(a => a.Revisions)
				.Include(s => s.FinalizedRevision)
				.FirstOrDefaultAsync(s => s.Id == id, token);
			if (session is null)
				return Results.Problem(statusCode: 404, title: UploadNotFoundMessage);
			if (session.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: UploadOwnerMessage);
			if (session.State == PendingUploadState.Finalized)
			{
				var finalized = session.FinalizedRevision;
				if (finalized is null)
					return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
				return Results.Ok(RevisionPayload(finalized));
			}
			if (session.State == PendingUploadState.Cancelled)
				return Results.Problem(statusCode: 409, title: UploadCancelledMessage);
			if (session.State != PendingUploadState.Pending)
				return Results.Problem(statusCode: 409, title: UploadAbandonedMessage);
			if (session.UploadTicketExpiresAt < time.GetUtcNow())
			{
				session.State = PendingUploadState.Abandoned;
				await db.SaveChangesAsync(token);
				return Results.Problem(statusCode: 409, title: UploadExpiredMessage);
			}
			// ARC-017: verify the declared identity before touching storage;
			// a mismatch keeps the session pending so the editor can retry
			// after re-selecting the correct file.
			var declaredSize = body?.SizeBytes;
			var declaredFileName = body?.FileName?.Trim();
			if (declaredSize is long requestedSize
				&& session.DeclaredSizeBytes is long storedSize
				&& requestedSize != storedSize)
				return Results.Problem(statusCode: 409, title: UploadMismatchMessage);
			if (!string.IsNullOrEmpty(declaredFileName)
				&& session.DeclaredFileName is string storedName
				&& !string.Equals(declaredFileName, storedName, StringComparison.Ordinal))
				return Results.Problem(statusCode: 409, title: UploadMismatchMessage);
			AssetObjectInfo? probe;
			byte[]? header;
			try
			{
				probe = await storage.ProbeAsync(session.BlobName, token);
				// Twelve leading bytes cover every honest format gate: the
				// PDF prefix, the PNG signature and the WEBP "RIFF…WEBP"
				// window at bytes 8..11. Shorter objects return what exists.
				header = await storage.ReadHeaderAsync(session.BlobName, 12, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: StorageFailureMessage);
			}
			if (probe is null)
				return Results.Problem(statusCode: 409, title: UploadMissingMessage);
			if (probe.SizeBytes > session.MaxSizeBytes)
			{
				await DeletePendingBestEffortAsync(storage, session.BlobName, token);
				session.State = PendingUploadState.Abandoned;
				await db.SaveChangesAsync(token);
				return Results.Problem(statusCode: 413, title: UploadTooLargeMessage);
			}
			// ARC-017: at finalization the actual size must fit alongside the
			// owner collection's finalized revisions; the same terminal shape
			// as the per-file oversize branch applies. ARC-025: the budget
			// scopes per owner (musical version or event) like initiation.
			var finalizedBytes = await db.FileRevisions
				.Where(r => r.Asset.MusicalVersionId == session.Asset.MusicalVersionId
					&& r.Asset.EventId == session.Asset.EventId)
				.SumAsync(r => (long?)r.SizeBytes, token) ?? 0;
			if (probe.SizeBytes + finalizedBytes > options.Value.MaxCollectionBytes)
			{
				await DeletePendingBestEffortAsync(storage, session.BlobName, token);
				session.State = PendingUploadState.Abandoned;
				await db.SaveChangesAsync(token);
				return Results.Problem(statusCode: 413, title: UploadTooLargeMessage);
			}
			var whitelistedContentTypes = ContentTypeWhitelist(session.Asset.AssetType);
			var invalidTypeMessage = InvalidTypeMessage(session.Asset.AssetType);
			// ARC-017: a block-list commit carries no blob content type (the
			// storage reports its default application/octet-stream), so a
			// missing/default stored type falls back to the session's declared
			// whitelisted type — the same fallback the revision payload uses.
			// ARC-025: photos cannot rely on that fallback (the session always
			// declares the first whitelisted type, image/jpeg), so for a
			// missing/default stored type the effective type is derived from
			// the header magic bytes and rejected when no image signature
			// matches.
			var storedContentType = probe.ContentType is { Length: > 0 } storedType
				&& !string.Equals(storedType, StorageDefaultContentType, StringComparison.OrdinalIgnoreCase)
					? storedType
					: null;
			var isPhoto = session.Asset.AssetType == PhotoAssetType;
			string effectiveContentType;
			if (storedContentType is null && isPhoto)
			{
				effectiveContentType = DetectImageContentType(header) ?? string.Empty;
			}
			else
			{
				effectiveContentType = storedContentType ?? session.ContentType;
			}
			// Score/document keep the ARC-015 behavior: the effective type is
			// checked against the whitelist and the %PDF- magic-bytes gate;
			// photos must present an effective whitelisted image type with
			// honest JPEG/PNG/WEBP magic bytes; audio/MIDI must present an
			// effective whitelisted type and skip magic-byte validation.
			var contentTypeValid = session.Asset.AssetType is ScoreAssetType or DocumentAssetType
				? string.Equals(effectiveContentType, PdfContentType, StringComparison.OrdinalIgnoreCase)
				: whitelistedContentTypes.Contains(effectiveContentType, StringComparer.OrdinalIgnoreCase);
			var magicBytesValid = HasValidMagicBytes(session.Asset.AssetType, header, effectiveContentType);
			if (!contentTypeValid || !magicBytesValid)
			{
				await DeletePendingBestEffortAsync(storage, session.BlobName, token);
				session.State = PendingUploadState.Abandoned;
				await db.SaveChangesAsync(token);
				return Results.Problem(statusCode: 422, title: invalidTypeMessage);
			}
			var asset = session.Asset;
			var revision = new FileRevision
			{
				AssetId = asset.Id,
				RevisionNumber = asset.Revisions.Count == 0 ? 1 : asset.Revisions.Max(r => r.RevisionNumber) + 1,
				BlobName = $"revisions/{asset.Id}/{Guid.CreateVersion7()}",
				ContentType = effectiveContentType,
				SizeBytes = probe.SizeBytes,
				CreatedByAccountId = decision.AccountId,
				CreatedAt = time.GetUtcNow(),
			};
			try
			{
				await storage.PromoteAsync(session.BlobName, revision.BlobName, token);
			}
			catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
			{
				return Results.Problem(statusCode: 502, title: StorageFailureMessage);
			}
			await DeletePendingBestEffortAsync(storage, session.BlobName, token);
			db.FileRevisions.Add(revision);
			asset.CurrentRevisionId = revision.Id;
			asset.RowVersion++;
			session.State = PendingUploadState.Finalized;
			session.FinalizedRevisionId = revision.Id;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(RevisionPayload(revision));
		}).DisableAntiforgery();

		app.MapGet("/api/assets/{id}/access", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IOptions<AssetStorageOptions> options,
			IAssetStorageAdapter storage, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			var asset = await db.Assets.AsNoTracking()
				.Include(a => a.CurrentRevision)
				.Include(a => a.MusicalVersion).ThenInclude(v => v!.Arrangement).ThenInclude(a => a.Song)
				.Include(a => a.Event)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (asset is null)
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			if (asset.Event is not null && asset.EventId is not null)
			{
				// ARC-025 event-owned assets follow the shared event
				// visibility decision: members only see published events, so
				// a draft event's documents answer the same indistinguishable
				// 404 as the event detail itself.
				if (!isEditor && !EventVisibility.IsMemberVisible(asset.Event))
					return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			}
			else
			{
				if (asset.MusicalVersion?.Arrangement?.Song is null)
					return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
				if (!isEditor && !CatalogueVisibility.IsMemberVisible(asset.MusicalVersion.Arrangement.Song))
					return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			}
			var revision = asset.CurrentRevision;
			if (revision is null)
				return Results.Problem(statusCode: 404, title: asset.Event is not null
					? NoCurrentRevisionEventMessage
					: NoCurrentRevisionMessage);
			var lifetime = options.Value.ReadTicketLifetime;
			string viewUrl;
			string downloadUrl;
			try
			{
				viewUrl = await storage.CreateReadTicketAsync(revision.BlobName, lifetime, asDownload: false, token);
				downloadUrl = await storage.CreateReadTicketAsync(revision.BlobName, lifetime, asDownload: true, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: StorageFailureMessage);
			}
			return Results.Ok(new
			{
				assetId = asset.Id,
				revisionId = revision.Id,
				revisionNumber = revision.RevisionNumber,
				contentType = revision.ContentType,
				sizeBytes = revision.SizeBytes,
				createdAt = revision.CreatedAt,
				viewUrl,
				downloadUrl,
				expiresAt = time.GetUtcNow() + lifetime,
			});
		});
	}

	private static object RevisionPayload(FileRevision revision) => new
	{
		assetId = revision.AssetId,
		revisionId = revision.Id,
		revisionNumber = revision.RevisionNumber,
		contentType = revision.ContentType,
		sizeBytes = revision.SizeBytes,
		createdAt = revision.CreatedAt,
	};

	private static async Task DeletePendingBestEffortAsync(
		IAssetStorageAdapter storage, string blobName, CancellationToken token)
	{
		try
		{
			await storage.DeleteAsync(blobName, token);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static string? CleanOptional(string? raw, int maxLength, string tooLongTitle, out IResult? error)
	{
		error = null;
		var trimmed = raw?.Trim();
		if (trimmed is not null && trimmed.Length > maxLength)
		{
			error = Results.Problem(statusCode: 400, title: tooLongTitle);
			return null;
		}
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>Mirrors <see cref="CleanOptional"/> for PATCH semantics: absent means unchanged, empty clears.</summary>
	private static string? PatchOptional(string? raw, int maxLength, string tooLongTitle, out IResult? error)
		=> CleanOptional(raw, maxLength, tooLongTitle, out error);

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		return (decision, null);
	}

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!IsEditor(decision))
			return (null, Results.Problem(statusCode: 403, title: "Keine Berechtigung für das Liedverzeichnis."));
		return (decision, null);
	}
}

public sealed record CreateAssetRequest(string? AssetType, string? VoiceLabel, string? Description);

/// <summary>ARC-025: event-owned assets carry no voice label, only an optional description.</summary>
public sealed record CreateEventAssetRequest(string? AssetType, string? Description);

public sealed record PatchAssetRequest(string? AssetType, string? VoiceLabel, string? Description);

/// <summary>ARC-017: optional declared file identity at upload-session initiation.</summary>
public sealed record CreateUploadSessionRequest(long? SizeBytes, string? FileName);

/// <summary>ARC-017: optional observed file identity at finalization, compared against the declaration.</summary>
public sealed record FinalizeUploadRequest(long? SizeBytes, string? FileName);
