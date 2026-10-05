using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Extraction;
using Archive.Backend.Recordings;
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
/// - Revision changes: <see cref="RevisionChanges.MakeCurrentAsync"/> is the
///   only place that swaps <see cref="ArchiveAsset.CurrentRevisionId"/>
///   (ARC-033), for a finalized upload and for an editor's restore alike.
///   ARC-034 subscribes there: a PDF revision's extraction row is written in
///   the same save (outbox) and handed to the queue afterwards; text search
///   (ARC-035) reads through the pointer.
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

	public const string EmptyRecordingMessage = "Die Datei ist leer und kann nicht als Aufnahme gespeichert werden.";

	public const string InvalidPlaybackMessage =
		"Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme.";

	public const string RecordingAssetManagedMessage = "Dateien einer Aufnahme werden über die Aufnahme verwaltet.";

	public const string StorageFailureMessage = "Speicherdienst nicht erreichbar.";

	/// <summary>ARC-017: storage default for block-list commits without an explicit blob content type.</summary>
	public const string StorageDefaultContentType = "application/octet-stream";

	public const string NoCurrentRevisionMessage = "Für diese Fassung liegen noch keine aktuellen Noten vor.";

	/// <summary>ARC-025: neutral pending message for event-owned assets.</summary>
	public const string NoCurrentRevisionEventMessage = "Für dieses Material wurde noch keine Datei hochgeladen.";

	public const string RevisionNotFoundMessage = "Dateistand nicht gefunden.";

	public const string RevisionRequiredMessage = "Bitte einen Dateistand angeben.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string ScoreAssetType = "score";

	public const string AudioAssetType = "audio";

	public const string MidiAssetType = "midi";

	public const string DocumentAssetType = "document";

	public const string PhotoAssetType = "photo";

	/// <summary>ARC-030: the preserved original of an event recording, any format.</summary>
	public const string RecordingOriginalAssetType = "recording-original";

	/// <summary>ARC-030: an externally converted playback copy, validated as playable.</summary>
	public const string RecordingPlaybackAssetType = "recording-playback";

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
			// ARC-030: only the session's declared default; the real type
			// of a recording file is recognised from its bytes at finalize.
			[RecordingOriginalAssetType] = [StorageDefaultContentType],
			[RecordingPlaybackAssetType] = [StorageDefaultContentType],
		};

	/// <summary>
	/// ARC-030: recording files are event-owned assets, but they are created
	/// and described only through their recording (never through the generic
	/// create/patch endpoints, whose owner whitelists omit them), and members
	/// reach them only through the recording's own access endpoint.
	/// </summary>
	public static bool IsRecordingAssetType(string assetType) =>
		assetType is RecordingOriginalAssetType or RecordingPlaybackAssetType;

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
		RecordingOriginalAssetType => EmptyRecordingMessage,
		RecordingPlaybackAssetType => InvalidPlaybackMessage,
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
			if (IsRecordingAssetType(asset.AssetType))
				return Results.Problem(statusCode: 409, title: RecordingAssetManagedMessage);
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
			// ARC-025: pending objects must not be reassigned to someone else.
			// ARC-033 widens this only for corrections; the session (and its
			// pending object) still belongs to its creator either way.
			if (!MayChangeCurrentFile(asset, decision!.AccountId))
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
			IOptions<AssetStorageOptions> options, IAssetStorageAdapter storage,
			ILoggerFactory loggerFactory, IExtractionQueue extractionQueue,
			Guid id, CancellationToken token,
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
				// ARC-030 recording containers need a longer look.
				header = await storage.ReadHeaderAsync(session.BlobName,
					IsRecordingAssetType(session.Asset.AssetType) ? RecordingFormats.HeaderBytes : 12, token);
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
			string effectiveContentType;
			bool contentTypeValid;
			bool magicBytesValid;
			if (IsRecordingAssetType(session.Asset.AssetType))
			{
				// ARC-030: the type of a recording file is what its bytes
				// say, for the kind its recording declares. An original is
				// preserved whatever it is (only an empty file is refused);
				// a playback copy must be a container browsers play, or it
				// never becomes current and the earlier copy stays in place.
				var kind = await db.Recordings
					.Where(r => r.OriginalAssetId == session.AssetId || r.PlaybackAssetId == session.AssetId)
					.Select(r => r.Kind)
					.FirstOrDefaultAsync(token);
				var format = RecordingFormats.Detect(header, kind ?? RecordingKinds.Video);
				effectiveContentType = format.ContentType;
				contentTypeValid = probe.SizeBytes > 0;
				magicBytesValid = session.Asset.AssetType == RecordingOriginalAssetType || format.Playable;
			}
			else
			{
				var isPhoto = session.Asset.AssetType == PhotoAssetType;
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
				contentTypeValid = session.Asset.AssetType is ScoreAssetType or DocumentAssetType
					? string.Equals(effectiveContentType, PdfContentType, StringComparison.OrdinalIgnoreCase)
					: whitelistedContentTypes.Contains(effectiveContentType, StringComparer.OrdinalIgnoreCase);
				magicBytesValid = HasValidMagicBytes(session.Asset.AssetType, header, effectiveContentType);
			}
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
				OriginalFileName = session.DeclaredFileName,
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
			db.FileRevisions.Add(revision);
			session.State = PendingUploadState.Finalized;
			session.FinalizedRevisionId = revision.Id;
			// ARC-033 revision-change contract: pointer swap, history entry
			// and (ARC-034 outbox) the PDF revision's extraction row ride the
			// same SaveChanges as the revision, so a committed upload can
			// never lose its extraction request. The queue send after the
			// save is best-effort: a send failure leaves the Queued row
			// un-enqueued for the dispatch sweep, and it never fails
			// finalize. The idempotent re-entry branch above never reaches
			// this, so no revision ever gains a second row.
			var enqueueExtraction = await RevisionChanges.MakeCurrentAsync(
				db, asset, revision, RevisionChangeKind.Upload, decision.AccountId, time.GetUtcNow(), token);
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateException exception) when (RevisionChanges.IsLostRace(exception))
			{
				// ARC-033: a competing replacement won — either the asset's
				// token moved or the revision number is taken. Nothing of the
				// winner is touched: this attempt's own, uniquely named copy is
				// dropped and the session stays pending with its staged object,
				// so retrying finalize adds the next revision.
				await DeletePendingBestEffortAsync(storage, revision.BlobName, token);
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			// Keep the staged upload replayable until the revision is committed.
			await DeletePendingBestEffortAsync(storage, session.BlobName, token);
			if (enqueueExtraction)
			{
				await ExtractionService.TryEnqueueAsync(
					db, extractionQueue, loggerFactory.CreateLogger("Archive.Extraction"), revision.Id, time, token);
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
			// ARC-030: a recording's files obey the recording's publication
			// and download switch, which only its own access endpoint
			// applies; members get the indistinguishable 404 here.
			if (!isEditor && IsRecordingAssetType(asset.AssetType))
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
			return await AccessTicketsAsync(storage, revision, options.Value.ReadTicketLifetime, time, token);
		});

		/// <summary>
		/// ARC-033 editor-only history: every retained revision of the asset
		/// with its attribution, plus the append-only log of pointer changes.
		/// Members answer 403 like every other editor surface; the history
		/// never appears in member payloads.
		/// </summary>
		app.MapGet("/api/assets/{id}/revisions", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var asset = await db.Assets.AsNoTracking()
				.Include(a => a.Revisions)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (asset is null)
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			return Results.Ok(await HistoryPayloadAsync(db, asset, token));
		});

		/// <summary>
		/// ARC-033: scoped tickets for one retained revision, current or not.
		/// Editor-only — members read through <c>/access</c>, which only ever
		/// resolves the current revision. A revision of another asset answers
		/// the same 404 as an unknown one.
		/// </summary>
		app.MapGet("/api/assets/{id}/revisions/{revisionId}/access", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IOptions<AssetStorageOptions> options,
			IAssetStorageAdapter storage, Guid id, Guid revisionId, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var revision = await db.FileRevisions.AsNoTracking()
				.FirstOrDefaultAsync(r => r.Id == revisionId && r.AssetId == id, token);
			if (revision is null)
				return Results.Problem(statusCode: 404, title: RevisionNotFoundMessage);
			return await AccessTicketsAsync(storage, revision, options.Value.ReadTicketLifetime, time, token);
		});

		/// <summary>
		/// ARC-033: an editor makes a retained revision current again. No
		/// revision is created or changed; the swap goes through the shared
		/// revision-change contract (<see cref="RevisionChanges"/>), so
		/// members, old links, extraction and search follow it like a
		/// replacement. <c>expectedCurrentRevisionId</c> guards against acting
		/// on a stale history; repeating the request for the revision that is
		/// already current is an idempotent success without a new entry.
		/// </summary>
		app.MapPost("/api/assets/{id}/current-revision", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			ILoggerFactory loggerFactory, IExtractionQueue extractionQueue,
			Guid id, CancellationToken token, SetCurrentRevisionRequest? body) =>
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
				.Include(a => a.Revisions)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (asset is null)
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			// Same ownership rule as starting a correction: event-owned
			// assets stay with the editor who created them.
			if (!MayChangeCurrentFile(asset, decision!.AccountId))
				return Results.Problem(statusCode: 403, title: AssetOwnerMessage);
			if (body?.RevisionId is not { } requestedRevisionId)
				return Results.Problem(statusCode: 400, title: RevisionRequiredMessage);
			var revision = asset.Revisions.FirstOrDefault(r => r.Id == requestedRevisionId);
			if (revision is null)
				return Results.Problem(statusCode: 404, title: RevisionNotFoundMessage);
			if (asset.CurrentRevisionId == revision.Id)
				return Results.Ok(await HistoryPayloadAsync(db, asset, token));
			if (body?.ExpectedCurrentRevisionId is { } expected && expected != asset.CurrentRevisionId)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			var enqueueExtraction = await RevisionChanges.MakeCurrentAsync(
				db, asset, revision, RevisionChangeKind.Restore, decision.AccountId, time.GetUtcNow(), token);
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateException exception) when (RevisionChanges.IsLostRace(exception))
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			if (enqueueExtraction)
			{
				await ExtractionService.TryEnqueueAsync(
					db, extractionQueue, loggerFactory.CreateLogger("Archive.Extraction"), revision.Id, time, token);
			}
			return Results.Ok(await HistoryPayloadAsync(db, asset, token));
		}).DisableAntiforgery();
	}

	/// <summary>
	/// Who may change which file an asset currently serves — by starting an
	/// upload session or by making a retained revision current (ARC-033).
	/// The creating editor always may. Every other editor may only correct a
	/// musical-version asset that already has a file; an asset without a
	/// file, and every event-owned asset (ARC-025), stay with their creator.
	/// </summary>
	private static bool MayChangeCurrentFile(ArchiveAsset asset, Guid accountId) =>
		asset.CreatedByAccountId == accountId
		|| (asset.MusicalVersionId is not null && asset.CurrentRevisionId is not null);

	/// <summary>Scoped view and download tickets for exactly one revision.</summary>
	private static async Task<IResult> AccessTicketsAsync(
		IAssetStorageAdapter storage, FileRevision revision, TimeSpan lifetime, TimeProvider time,
		CancellationToken token)
	{
		string viewUrl;
		string downloadUrl;
		try
		{
			viewUrl = await storage.CreateReadTicketAsync(
				revision.BlobName, lifetime, asDownload: false, contentType: revision.ContentType, token);
			downloadUrl = await storage.CreateReadTicketAsync(
				revision.BlobName, lifetime, asDownload: true, contentType: revision.ContentType, token);
		}
		catch (InvalidOperationException)
		{
			return Results.Problem(statusCode: 502, title: StorageFailureMessage);
		}
		return Results.Ok(new
		{
			assetId = revision.AssetId,
			revisionId = revision.Id,
			revisionNumber = revision.RevisionNumber,
			contentType = revision.ContentType,
			sizeBytes = revision.SizeBytes,
			createdAt = revision.CreatedAt,
			viewUrl,
			downloadUrl,
			expiresAt = time.GetUtcNow() + lifetime,
		});
	}

	/// <summary>
	/// ARC-033 history wire shape: revisions newest first, pointer changes
	/// newest first, attribution resolved to display names (null when the
	/// account no longer exists). Requires <see cref="ArchiveAsset.Revisions"/>.
	/// </summary>
	private static async Task<object> HistoryPayloadAsync(
		ArchiveDbContext db, ArchiveAsset asset, CancellationToken token)
	{
		var changes = await db.RevisionChanges.AsNoTracking()
			.Where(c => c.AssetId == asset.Id)
			.ToListAsync(token);
		var accountIds = asset.Revisions.Select(r => r.CreatedByAccountId)
			.Concat(changes.Select(c => c.ChangedByAccountId))
			.Distinct()
			.ToList();
		var names = await db.Users.AsNoTracking()
			.Where(u => accountIds.Contains(u.Id))
			.Select(u => new { u.Id, Name = u.DisplayName ?? u.Email })
			.ToDictionaryAsync(u => u.Id, u => u.Name, token);
		var numbers = asset.Revisions.ToDictionary(r => r.Id, r => r.RevisionNumber);
		return new
		{
			assetId = asset.Id,
			assetType = asset.AssetType,
			currentRevisionId = asset.CurrentRevisionId,
			revisions = asset.Revisions
				.OrderByDescending(r => r.RevisionNumber)
				.Select(r => new
				{
					revisionId = r.Id,
					revisionNumber = r.RevisionNumber,
					contentType = r.ContentType,
					sizeBytes = r.SizeBytes,
					fileName = r.OriginalFileName,
					createdAt = r.CreatedAt,
					createdBy = names.GetValueOrDefault(r.CreatedByAccountId),
					isCurrent = r.Id == asset.CurrentRevisionId,
				})
				.ToList(),
			// Guid v7 ids break ties between entries of the same instant.
			changes = changes
				.OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id)
				.Select(c => new
				{
					kind = c.Kind == RevisionChangeKind.Restore ? "restore" : "upload",
					revisionId = c.RevisionId,
					revisionNumber = numbers.TryGetValue(c.RevisionId, out var number) ? number : (int?)null,
					previousRevisionNumber = c.PreviousRevisionId is { } previous
						&& numbers.TryGetValue(previous, out var previousNumber) ? previousNumber : (int?)null,
					changedAt = c.ChangedAt,
					changedBy = names.GetValueOrDefault(c.ChangedByAccountId),
				})
				.ToList(),
		};
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

/// <summary>
/// ARC-033: the retained revision to make current, and optionally the revision
/// the editor saw as current (stale-history guard).
/// </summary>
public sealed record SetCurrentRevisionRequest(Guid? RevisionId, Guid? ExpectedCurrentRevisionId);
