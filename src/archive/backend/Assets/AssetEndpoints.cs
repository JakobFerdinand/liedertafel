using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
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
/// - ARC-023 extends this pattern with event-owned assets via an owner
///   registry instead of the musical-version link.
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

	public const string FileNameTooLongMessage = "Der Dateiname ist zu lang.";

	public const string UploadMissingMessage = "Die Datei wurde noch nicht übertragen.";

	public const string UploadTooLargeMessage = "Die Datei ist zu groß.";

	public const string InvalidPdfMessage = "Die Datei ist kein gültiges PDF.";

	public const string InvalidAudioMessage = "Die Datei ist keine gültige Audiodatei.";

	public const string InvalidMidiMessage = "Die Datei ist keine gültige MIDI-Datei.";

	public const string StorageFailureMessage = "Speicherdienst nicht erreichbar.";

	public const string NoCurrentRevisionMessage = "Für diese Fassung liegen noch keine aktuellen Noten vor.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string ScoreAssetType = "score";

	public const string AudioAssetType = "audio";

	public const string MidiAssetType = "midi";

	public const string PdfContentType = "application/pdf";

	public const string Mp3ContentType = "audio/mpeg";

	public const string MidiContentType = "audio/midi";

	public const string XMidiContentType = "audio/x-midi";

	/// <summary>ARC-016 content-type whitelist per asset type.</summary>
	private static readonly IReadOnlyDictionary<string, string[]> AssetTypeContentTypes =
		new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[ScoreAssetType] = [PdfContentType],
			[AudioAssetType] = [Mp3ContentType, "audio/mp4", "audio/x-m4a", "audio/wav", "audio/ogg"],
			[MidiAssetType] = [MidiContentType, XMidiContentType],
		};

	private static bool IsKnownAssetType(string assetType) =>
		AssetTypeContentTypes.ContainsKey(assetType);

	private static string[] ContentTypeWhitelist(string assetType) =>
		AssetTypeContentTypes.TryGetValue(assetType, out var contentTypes)
			? contentTypes
			: [PdfContentType];

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
		if (!IsKnownAssetType(assetType))
			return Results.Problem(statusCode: 422, title: UnknownAssetTypeMessage);
			var voiceLabel = CleanOptional(body?.VoiceLabel, VoiceLabelTooLongMessage, out var voiceError);
			if (voiceError is not null)
				return voiceError;
			var description = CleanOptional(body?.Description, DescriptionTooLongMessage, out var descriptionError);
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
				if (!IsKnownAssetType(assetType))
					return Results.Problem(statusCode: 422, title: UnknownAssetTypeMessage);
				if (asset.CurrentRevisionId is not null && assetType != asset.AssetType)
					return Results.Problem(statusCode: 409, title: AssetTypeLockedMessage);
			}
			var voiceLabel = PatchOptional(body?.VoiceLabel, VoiceLabelTooLongMessage, out var voiceError);
			if (voiceError is not null)
				return voiceError;
			var description = PatchOptional(body?.Description, DescriptionTooLongMessage, out var descriptionError);
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
			// Per-version collection budget: the declared size must fit
			// alongside other pending sessions and finalized revisions.
			var pendingBudget = await db.UploadSessions
				.Where(s => s.State == PendingUploadState.Pending
					&& s.Asset.MusicalVersionId == asset.MusicalVersionId)
				.SumAsync(s => (long?)s.MaxSizeBytes, token) ?? 0;
			var finalizedBytes = await db.FileRevisions
				.Where(r => r.Asset.MusicalVersionId == asset.MusicalVersionId)
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
			var uploadUrl = await storage.CreateUploadTicketAsync(
				pending.BlobName, storageOptions.UploadSessionLifetime, token);
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
			var uploadUrl = await storage.CreateUploadTicketAsync(
				session.BlobName, storageOptions.UploadSessionLifetime, token);
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
			AssetObjectInfo? probe;
			byte[]? header;
			try
			{
				probe = await storage.ProbeAsync(session.BlobName, token);
				header = await storage.ReadHeaderAsync(session.BlobName, 5, token);
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
			var whitelistedContentTypes = ContentTypeWhitelist(session.Asset.AssetType);
			var invalidTypeMessage = session.Asset.AssetType switch
			{
				AudioAssetType => InvalidAudioMessage,
				MidiAssetType => InvalidMidiMessage,
				_ => InvalidPdfMessage,
			};
			// Score keeps the ARC-015 behavior: a missing stored content type
			// skips the check (the magic bytes gate); audio/MIDI must present
			// a whitelisted content type and skip magic-byte validation.
			var contentTypeValid = session.Asset.AssetType == ScoreAssetType
				? probe.ContentType is not { Length: > 0 }
					|| string.Equals(probe.ContentType, PdfContentType, StringComparison.OrdinalIgnoreCase)
				: probe.ContentType is { Length: > 0 } storedType
					&& whitelistedContentTypes.Contains(storedType, StringComparer.OrdinalIgnoreCase);
			var magicBytesValid = session.Asset.AssetType != ScoreAssetType
				|| (header is not null && header.AsSpan().StartsWith("%PDF-"u8));
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
				ContentType = probe.ContentType is { Length: > 0 } revisionType
					? revisionType
					: session.ContentType,
				SizeBytes = probe.SizeBytes,
				CreatedByAccountId = decision.AccountId,
				CreatedAt = time.GetUtcNow(),
			};
			try
			{
				await storage.PromoteAsync(session.BlobName, revision.BlobName, token);
			}
			catch (InvalidOperationException)
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
				.Include(a => a.MusicalVersion).ThenInclude(v => v.Arrangement).ThenInclude(a => a.Song)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (asset?.MusicalVersion?.Arrangement?.Song is null)
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			if (!isEditor && !CatalogueVisibility.IsMemberVisible(asset.MusicalVersion.Arrangement.Song))
				return Results.Problem(statusCode: 404, title: AssetNotFoundMessage);
			var revision = asset.CurrentRevision;
			if (revision is null)
				return Results.Problem(statusCode: 404, title: NoCurrentRevisionMessage);
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

	private static string? CleanOptional(string? raw, string tooLongTitle, out IResult? error)
	{
		error = null;
		var trimmed = raw?.Trim();
		if (trimmed is { Length: > 200 })
		{
			error = Results.Problem(statusCode: 400, title: tooLongTitle);
			return null;
		}
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>Mirrors <see cref="CleanOptional"/> for PATCH semantics: absent means unchanged, empty clears.</summary>
	private static string? PatchOptional(string? raw, string tooLongTitle, out IResult? error)
		=> CleanOptional(raw, tooLongTitle, out error);

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

public sealed record PatchAssetRequest(string? AssetType, string? VoiceLabel, string? Description);

/// <summary>ARC-017: optional declared file identity at upload-session initiation.</summary>
public sealed record CreateUploadSessionRequest(long? SizeBytes, string? FileName);
