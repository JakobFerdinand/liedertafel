using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Lieder;

public sealed record CreateDateiUploadRequest(
	string? Kind, bool? IsPartitur, string? Stimme, string? Description, string? FileName, long? SizeBytes,
	Guid? ReplacesDateiId, Guid? RecordedAtEventId, string? ContentType = null);

/// <summary>
/// Upload sessions that end in a <see cref="Datei"/>. They live in the shared
/// <c>upload_sessions</c> table beside the old asset sessions (owner: exactly
/// one of asset, Lied or Auftritt), so <c>/api/upload-sessions/{id}/renew</c>,
/// <c>finalize</c> and <c>DELETE</c> serve both and the browser upload engine
/// is unchanged. Finalize of a Datei session is <see cref="FinalizeAsync"/>;
/// kinds plug in through <see cref="DateiKindRules"/>, follow-up work through
/// <see cref="IDateiFinalizedHook"/>.
/// </summary>
public static class DateiUploads
{
	/// <summary>Datei blobs live under this prefix, apart from the old <c>revisions/</c>, so #172 can clean up by prefix.</summary>
	public const string BlobPrefix = "dateien/";

	public const string StimmeRequiredMessage = "Bitte eine Stimme angeben oder die Datei als Partitur kennzeichnen.";

	public const string ReplacedMeanwhileMessage = "Die Datei wurde inzwischen ersetzt oder gelöscht.";

	public const string RecordedAtEventNotFoundMessage = "Auftritt nicht gefunden.";

	public const string DateiNotFoundMessage = "Datei nicht gefunden.";

	/// <summary>Creates the session for a new Datei of a Lied (the Lied exists, the caller is an editor).</summary>
	public static async Task<IResult> CreateForLiedAsync(
		ArchiveDbContext db, TimeProvider time, AssetStorageOptions storageOptions, IAssetStorageAdapter storage,
		Guid accountId, Guid liedId, CreateDateiUploadRequest? body, CancellationToken token)
	{
		if (!await db.Lieder.AnyAsync(l => l.Id == liedId, token))
			return Results.Problem(statusCode: 404, title: LiedEndpoints.NotFoundMessage);
		// Replacing (#168): the new Datei keeps the target's kind, Stimme and
		// Partitur flag; only a live Datei of this very Lied can be replaced.
		Datei? replaces = null;
		if (body?.ReplacesDateiId is { } replacesId)
		{
			replaces = await DateiQueries.Visible(db).AsNoTracking()
				.FirstOrDefaultAsync(d => d.Id == replacesId && d.LiedId == liedId, token);
			if (replaces is null)
				return Results.Problem(statusCode: 404, title: DateiNotFoundMessage);
		}
		var kind = replaces?.Kind ?? (body?.Kind ?? DateiKinds.Noten).Trim().ToLowerInvariant();
		if (!DateiKinds.IsKnown(kind))
			return Results.Problem(statusCode: 422, title: "Unbekannter Dateityp.");
		var rule = DateiKindRules.For(kind);
		if (rule is null)
			return Results.Problem(statusCode: 422, title: DateiKindRules.UnsupportedKindMessage);

		var contentType = Clean(body?.ContentType)?.ToLowerInvariant() ?? rule.ContentTypes[0];
		if (!rule.ContentTypes.Contains(contentType, StringComparer.Ordinal))
			return Results.Problem(statusCode: 422, title: DateiKindRules.UnsupportedContentTypeMessage);
		Guid? recordedAtEventId = null;
		if (replaces is not null)
			recordedAtEventId = replaces.RecordedAtEventId;
		else if (kind == DateiKinds.Aufnahme && body?.RecordedAtEventId is { } eventId)
		{
			if (!await db.Events.AnyAsync(e => e.Id == eventId, token))
				return Results.Problem(statusCode: 400, title: RecordedAtEventNotFoundMessage);
			recordedAtEventId = eventId;
		}

		var isPartitur = replaces?.IsPartitur ?? (kind == DateiKinds.Noten && body?.IsPartitur == true);
		var stimme = replaces is not null ? replaces.Stimme
			: isPartitur || kind is DateiKinds.Aufnahme or DateiKinds.Sonstiges ? null : Clean(body?.Stimme);
		if (stimme is { Length: > 200 })
			return Results.Problem(statusCode: 400, title: LiedEndpoints.FieldTooLongMessage);
		if (kind is DateiKinds.Noten or DateiKinds.Uebe && !isPartitur && stimme is null)
			return Results.Problem(statusCode: 400, title: StimmeRequiredMessage);
		var description = Clean(body?.Description) ?? replaces?.Description;
		if (description is { Length: > 500 })
			return Results.Problem(statusCode: 400, title: AssetEndpoints.DescriptionTooLongMessage);
		var fileName = Clean(body?.FileName);
		if (fileName is { Length: > 300 })
			return Results.Problem(statusCode: 400, title: AssetEndpoints.FileNameTooLongMessage);
		var declaredSize = body?.SizeBytes;
		if (declaredSize > storageOptions.MaxUploadBytes)
			return Results.Problem(statusCode: 413, title: AssetEndpoints.UploadTooLargeMessage);

		// Collection budget per Lied: stored Dateien (the trash still holds
		// bytes) plus the other pending sessions plus this declaration.
		var pendingBytes = await db.UploadSessions
			.Where(s => s.LiedId == liedId && s.State == PendingUploadState.Pending)
			.SumAsync(s => (long?)(s.DeclaredSizeBytes ?? s.MaxSizeBytes), token) ?? 0;
		var storedBytes = await db.Dateien.Where(d => d.LiedId == liedId)
			.SumAsync(d => (long?)d.SizeBytes, token) ?? 0;
		if ((declaredSize ?? 0) + pendingBytes + storedBytes > storageOptions.MaxCollectionBytes)
			return Results.Problem(statusCode: 413, title: AssetEndpoints.UploadTooLargeMessage);

		var now = time.GetUtcNow();
		var pending = new PendingUpload
		{
			LiedId = liedId,
			DateiKind = kind,
			IsPartitur = isPartitur,
			Stimme = stimme,
			Description = description,
			RecordedAtEventId = recordedAtEventId,
			ReplacesDateiId = replaces?.Id,
			BlobName = $"pending/{Guid.CreateVersion7()}",
			ContentType = contentType,
			MaxSizeBytes = storageOptions.MaxUploadBytes,
			DeclaredFileName = fileName,
			DeclaredSizeBytes = declaredSize,
			UploadTicketExpiresAt = now + storageOptions.UploadSessionLifetime,
			State = PendingUploadState.Pending,
			CreatedByAccountId = accountId,
			CreatedAt = now,
		};
		db.UploadSessions.Add(pending);
		await db.SaveChangesAsync(token);
		string uploadUrl;
		try
		{
			uploadUrl = await storage.CreateUploadTicketAsync(pending.BlobName, storageOptions.UploadSessionLifetime, token);
		}
		catch (InvalidOperationException)
		{
			return Results.Problem(statusCode: 502, title: AssetEndpoints.StorageFailureMessage);
		}
		return Results.Created($"/api/lieder/{liedId}", new
		{
			uploadSessionId = pending.Id,
			blobName = (string?)null,
			uploadUrl,
			expiresAt = pending.UploadTicketExpiresAt,
			maxBytes = pending.MaxSizeBytes,
			blockBytes = storageOptions.UploadBlockBytes,
		});
	}

	/// <summary>
	/// Finalizes a Datei session: verifies the object against the declaration
	/// and the kind's rule, promotes the blob under <see cref="BlobPrefix"/> and
	/// creates the Datei. Mirrors the asset finalize semantics (idempotent
	/// re-entry, terminal rejections, mismatch keeps the session pending).
	/// </summary>
	public static async Task<IResult> FinalizeAsync(
		ArchiveDbContext db, TimeProvider time, AssetStorageOptions storageOptions, IAssetStorageAdapter storage,
		IEnumerable<IDateiFinalizedHook> hooks, PendingUpload session, Guid accountId,
		FinalizeUploadRequest? body, CancellationToken token)
	{
		if (session.State == PendingUploadState.Finalized)
		{
			var existing = session.FinalizedDateiId is { } finalizedId
				? await db.Dateien.AsNoTracking().FirstOrDefaultAsync(d => d.Id == finalizedId, token)
				: null;
			return existing is null
				? Results.Problem(statusCode: 409, title: AssetEndpoints.ConcurrencyMessage)
				: Results.Ok(new { datei = DateiPayloads.Summary(existing) });
		}
		if (session.State == PendingUploadState.Cancelled)
			return Results.Problem(statusCode: 409, title: AssetEndpoints.UploadCancelledMessage);
		if (session.State != PendingUploadState.Pending)
			return Results.Problem(statusCode: 409, title: AssetEndpoints.UploadAbandonedMessage);
		if (session.UploadTicketExpiresAt < time.GetUtcNow())
		{
			session.State = PendingUploadState.Abandoned;
			await db.SaveChangesAsync(token);
			return Results.Problem(statusCode: 409, title: AssetEndpoints.UploadExpiredMessage);
		}
		var declaredSize = body?.SizeBytes;
		var declaredFileName = body?.FileName?.Trim();
		if (declaredSize is long requestedSize && session.DeclaredSizeBytes is long storedSize && requestedSize != storedSize)
			return Results.Problem(statusCode: 409, title: AssetEndpoints.UploadMismatchMessage);
		if (!string.IsNullOrEmpty(declaredFileName) && session.DeclaredFileName is string storedName
			&& !string.Equals(declaredFileName, storedName, StringComparison.Ordinal))
			return Results.Problem(statusCode: 409, title: AssetEndpoints.UploadMismatchMessage);
		var kind = session.DateiKind ?? DateiKinds.Noten;
		var rule = DateiKindRules.For(kind);
		if (rule is null || session.LiedId is null)
		{
			await AbandonAsync(db, storage, session, token);
			return Results.Problem(statusCode: 422, title: DateiKindRules.UnsupportedKindMessage);
		}

		if (session.ReplacesDateiId is { } replacesId
			&& !await DateiQueries.Visible(db).AnyAsync(d => d.Id == replacesId, token))
		{
			await AbandonAsync(db, storage, session, token);
			return Results.Problem(statusCode: 409, title: ReplacedMeanwhileMessage);
		}

		AssetObjectInfo? probe;
		byte[]? header;
		try
		{
			probe = await storage.ProbeAsync(session.BlobName, token);
			header = await storage.ReadHeaderAsync(session.BlobName, DateiKindRules.HeaderBytes, token);
		}
		catch (InvalidOperationException)
		{
			return Results.Problem(statusCode: 502, title: AssetEndpoints.StorageFailureMessage);
		}
		if (probe is null)
			return Results.Problem(statusCode: 409, title: AssetEndpoints.UploadMissingMessage);
		if (probe.SizeBytes > session.MaxSizeBytes)
		{
			await AbandonAsync(db, storage, session, token);
			return Results.Problem(statusCode: 413, title: AssetEndpoints.UploadTooLargeMessage);
		}
		var storedBytes = await db.Dateien.Where(d => d.LiedId == session.LiedId)
			.SumAsync(d => (long?)d.SizeBytes, token) ?? 0;
		if (probe.SizeBytes + storedBytes > storageOptions.MaxCollectionBytes)
		{
			await AbandonAsync(db, storage, session, token);
			return Results.Problem(statusCode: 413, title: AssetEndpoints.UploadTooLargeMessage);
		}
		// A block-list commit reports no blob type, so the session's declared
		// whitelisted type stands in, exactly as for asset revisions.
		var storedContentType = probe.ContentType is { Length: > 0 } type
			&& !string.Equals(type, AssetEndpoints.StorageDefaultContentType, StringComparison.OrdinalIgnoreCase)
				? type
				: null;
		var effectiveContentType = storedContentType ?? session.ContentType;
		if (!rule.ContentTypes.Contains(effectiveContentType, StringComparer.OrdinalIgnoreCase)
			|| !rule.HasValidHeader(header, effectiveContentType))
		{
			await AbandonAsync(db, storage, session, token);
			return Results.Problem(statusCode: 422, title: rule.InvalidMessage);
		}

		var now = time.GetUtcNow();
		var datei = new Datei
		{
			LiedId = session.LiedId,
			Kind = kind,
			IsPartitur = session.IsPartitur,
			Stimme = session.Stimme,
			Description = session.Description,
			RecordedAtEventId = session.RecordedAtEventId,
			BlobName = string.Empty,
			ContentType = effectiveContentType,
			SizeBytes = probe.SizeBytes,
			OriginalFileName = session.DeclaredFileName,
			CreatedByAccountId = accountId,
			CreatedAt = now,
			RowVersion = 1,
		};
		datei.BlobName = $"{BlobPrefix}{datei.Id}/{Guid.CreateVersion7()}";
		try
		{
			await storage.PromoteAsync(session.BlobName, datei.BlobName, token);
		}
		catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
		{
			return Results.Problem(statusCode: 502, title: AssetEndpoints.StorageFailureMessage);
		}
		db.Dateien.Add(datei);
		session.State = PendingUploadState.Finalized;
		session.FinalizedDateiId = datei.Id;
		var context = new PendingUploadContext(session.ReplacesDateiId, accountId, now);
		var hookList = hooks.ToList();
		foreach (var hook in hookList)
			await hook.BeforeSaveAsync(datei, context, db, token);
		await db.SaveChangesAsync(token);
		try
		{
			await storage.DeleteAsync(session.BlobName, token);
		}
		catch (InvalidOperationException)
		{
			// The staged copy is garbage once promoted; cleanup catches leftovers.
		}
		foreach (var hook in hookList)
		{
			try
			{
				await hook.AfterSaveAsync(datei, db, token);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Best effort by contract: the committed upload never fails here.
			}
		}
		return Results.Ok(new { datei = DateiPayloads.Summary(datei) });
	}

	private static async Task AbandonAsync(
		ArchiveDbContext db, IAssetStorageAdapter storage, PendingUpload session, CancellationToken token)
	{
		try
		{
			await storage.DeleteAsync(session.BlobName, token);
		}
		catch (InvalidOperationException)
		{
		}
		session.State = PendingUploadState.Abandoned;
		await db.SaveChangesAsync(token);
	}

	private static string? Clean(string? value)
	{
		var trimmed = value?.Trim();
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}
}
