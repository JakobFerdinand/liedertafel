using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Recordings;

public sealed record CreateRecordingRequest(string? Label, string? Kind);

public sealed record AttachDateiRequest(Guid? DateiId);

/// <summary>
/// Absent fields stay unchanged. <c>ExpectedVersion</c> is the version the
/// editor's form was built from; when present, a changed recording is refused
/// instead of being overwritten.
/// </summary>
public sealed record PatchRecordingRequest(
	string? Label, bool? IsPublished, bool? DownloadEnabled, double? DurationSeconds, uint? ExpectedVersion);

/// <summary>
/// ARC-030 whole event recordings.
/// - Members list and play recordings that are published on a published
///   event; anything else answers the same 404 as an unknown id. Editors see
///   and play everything.
/// - Any editor may relabel, publish and switch downloads; only the editor
///   who created a recording may change its files, the ARC-025 rule for
///   event-owned assets (<c>MayChangeCurrentFile</c>).
/// - Files are Aufnahme Dateien of the Auftritt (#171): the browser uploads
///   through <c>POST /api/events/{id}/dateien/upload-session</c> and the
///   shared finalize, then attaches the Datei to a slot with
///   <c>PUT /api/recordings/{id}/dateien/{slot}</c>; this class owns
///   identity, labels, publication, slots and tickets.
/// - Tickets come from <c>GET /api/recordings/{id}/access</c> and nowhere
///   else for members: the generic Datei access refuses Aufnahmen of an
///   Auftritt, so publication and the download switch cannot be bypassed.
/// Extension points: ARC-032 keys passages on the recording id and reads
/// <c>durationSeconds</c> and the playback <c>revisionId</c>; ARC-041 moves
/// a preserved original that is not the playback file to another tier —
/// <see cref="RecordingFiles.Resolve"/> is the one place that says which file
/// members actually read.
/// </summary>
public static class RecordingEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für die Aufnahmen.";

	public const string NotFoundMessage = "Aufnahme nicht gefunden.";

	public const string LabelRequiredMessage = "Die Bezeichnung ist erforderlich.";

	public const string LabelTooLongMessage = "Die Bezeichnung ist zu lang.";

	public const string KindUnknownMessage = "Unbekannte Aufnahmeart.";

	public const string DurationInvalidMessage = "Die Dauer ist ungültig.";

	/// <summary>Stale state: the editor acted on an outdated copy and should reload.</summary>
	public const string ConcurrencyMessage = "Die Aufnahme wurde zwischenzeitlich geändert.";

	/// <summary>Not allowed in this state: reloading does not help, a file is needed first.</summary>
	public const string NotPublishableMessage =
		"Die Aufnahme kann erst veröffentlicht werden, wenn eine Datei hochgeladen ist.";

	public const string FilesOwnerMessage = "Nur die anlegende Person kann die Dateien dieser Aufnahme ändern.";

	public const string NoFileMessage = "Für diese Aufnahme wurde noch keine Datei hochgeladen.";

	public const string DateiRequiredMessage = "Bitte eine hochgeladene Datei angeben.";

	public const string DateiInUseMessage = "Diese Datei gehört bereits zu einer Aufnahme.";

	public const string OriginalSlot = "original";

	public const string PlaybackSlotName = "playback";

	public const int LabelMaxLength = 200;

	/// <summary>Upper bound for a declared duration: two days, far beyond any event.</summary>
	public const double MaxDurationSeconds = 172_800;

	public static void MapRecordingEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/events/{id}/recordings", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			var choirEvent = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, token);
			if (choirEvent is null || (!isEditor && !EventVisibility.IsMemberVisible(choirEvent)))
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			var recordings = await WithFiles(db.Recordings.AsNoTracking())
				.Where(r => r.EventId == id)
				.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
				.ToListAsync(token);
			return Results.Ok(new
			{
				eventId = id,
				recordings = recordings
					.Where(r => isEditor || r.PublishedAt is not null)
					.Select(r => Payload(r, decision!, isEditor))
					.ToList(),
			});
		});

		app.MapPost("/api/events/{id}/recordings", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreateRecordingRequest? body) =>
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
			if (!TryValidateLabel(body?.Label, out var label, out var labelError))
				return labelError!;
			var kind = (body?.Kind ?? string.Empty).Trim();
			if (!RecordingKinds.Known.Contains(kind))
				return Results.Problem(statusCode: 400, title: KindUnknownMessage);
			if (!await db.Events.AnyAsync(e => e.Id == id, token))
				return Results.Problem(statusCode: 404, title: EventEndpoints.NotFoundMessage);
			var now = time.GetUtcNow();
			// The slots stay empty until the browser attaches uploaded
			// Dateien; the event row is not touched and no programme or
			// performance record is created.
			var recording = new Recording
			{
				EventId = id,
				Label = label,
				Kind = kind,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
				UpdatedAt = now,
				UpdatedByAccountId = decision.AccountId,
			};
			db.Recordings.Add(recording);
			await db.SaveChangesAsync(token);
			return Results.Created($"/api/recordings/{recording.Id}",
				new { recording = Payload(recording, decision, isEditor: true) });
		}).DisableAntiforgery();

		app.MapPatch("/api/recordings/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchRecordingRequest? body) =>
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
			var label = string.Empty;
			if (body?.Label is not null && !TryValidateLabel(body.Label, out label, out var labelError))
				return labelError!;
			if (body?.DurationSeconds is { } duration
				&& (!double.IsFinite(duration) || duration <= 0 || duration > MaxDurationSeconds))
				return Results.Problem(statusCode: 400, title: DurationInvalidMessage);
			var recording = await WithFiles(db.Recordings).FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body?.ExpectedVersion is { } expected && expected != recording.RowVersion)
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			var files = RecordingFiles.Resolve(recording);
			if (body?.IsPublished == true && recording.PublishedAt is null && files.State == RecordingFiles.Missing)
				return Results.Problem(statusCode: 409, title: NotPublishableMessage);
			// A request that changes nothing leaves version and stamps alone,
			// so repeating a save never invalidates someone else's form.
			var relabel = body?.Label is not null && label != recording.Label;
			var publish = body?.IsPublished == true && recording.PublishedAt is null;
			var withdraw = body?.IsPublished == false && recording.PublishedAt is not null;
			var switchDownload = body?.DownloadEnabled is { } wanted && wanted != recording.DownloadEnabled;
			var measure = body?.DurationSeconds is { } measured && measured != recording.DurationSeconds;
			if (!(relabel || publish || withdraw || switchDownload || measure))
				return Results.Ok(new { recording = Payload(recording, decision!, isEditor: true) });
			var now = time.GetUtcNow();
			if (relabel)
				recording.Label = label;
			if (publish)
			{
				recording.PublishedAt = now;
				recording.PublishedByAccountId = decision!.AccountId;
			}
			else if (withdraw)
			{
				recording.PublishedAt = null;
				recording.PublishedByAccountId = null;
			}
			if (switchDownload)
				recording.DownloadEnabled = body!.DownloadEnabled!.Value;
			if (measure)
				recording.DurationSeconds = body!.DurationSeconds;
			recording.UpdatedAt = now;
			recording.UpdatedByAccountId = decision!.AccountId;
			recording.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { recording = Payload(recording, decision, isEditor: true) });
		}).DisableAntiforgery();

		/// <summary>
		/// Attaches an uploaded Aufnahme Datei of this Auftritt to the
		/// <c>original</c> or <c>playback</c> slot. A Datei already in the slot
		/// goes to the trash, replaced by the new one (#168 rule). A playback
		/// copy must be a container browsers play; otherwise it is refused and
		/// the unusable Datei goes to the trash, the earlier copy stays.
		/// </summary>
		app.MapPut("/api/recordings/{id}/dateien/{slot}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, string slot,
			CancellationToken token, AttachDateiRequest? body) =>
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
			var isOriginal = slot == OriginalSlot;
			if (!isOriginal && slot != PlaybackSlotName)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			var recording = await WithFiles(db.Recordings).FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (recording.CreatedByAccountId != decision!.AccountId)
				return Results.Problem(statusCode: 403, title: FilesOwnerMessage);
			if (body?.DateiId is not { } dateiId)
				return Results.Problem(statusCode: 400, title: DateiRequiredMessage);
			var datei = await DateiQueries.Visible(db).FirstOrDefaultAsync(d => d.Id == dateiId
				&& d.EventId == recording.EventId && d.Kind == DateiKinds.Aufnahme, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiUploads.DateiNotFoundMessage);
			var currentId = isOriginal ? recording.OriginalDateiId : recording.PlaybackDateiId;
			var now = time.GetUtcNow();
			if (currentId == datei.Id)
				return Results.Ok(new { recording = Payload(recording, decision, isEditor: true) });
			if (await db.Recordings.AnyAsync(r => r.Id != id
				&& (r.OriginalDateiId == datei.Id || r.PlaybackDateiId == datei.Id), token)
				|| (isOriginal ? recording.PlaybackDateiId : recording.OriginalDateiId) == datei.Id)
				return Results.Problem(statusCode: 409, title: DateiInUseMessage);
			if (datei.SizeBytes == 0)
			{
				DateiTrash.MoveToTrash(datei, decision.AccountId, now);
				await db.SaveChangesAsync(token);
				return Results.Problem(statusCode: 422, title: AssetEndpoints.EmptyRecordingMessage);
			}
			if (!isOriginal && !RecordingFormats.IsPlayable(datei.ContentType, recording.Kind))
			{
				DateiTrash.MoveToTrash(datei, decision.AccountId, now);
				await db.SaveChangesAsync(token);
				return Results.Problem(statusCode: 422, title: AssetEndpoints.InvalidPlaybackMessage);
			}
			var playedBefore = RecordingFiles.Resolve(recording).Playable?.Id;
			var replaced = isOriginal ? recording.OriginalDatei : recording.PlaybackDatei;
			if (replaced is { DeletedAt: null })
			{
				DateiTrash.MoveToTrash(replaced, decision.AccountId, now);
				replaced.ReplacedByDateiId = datei.Id;
			}
			if (isOriginal)
			{
				recording.OriginalDateiId = datei.Id;
				recording.OriginalDatei = datei;
			}
			else
			{
				recording.PlaybackDateiId = datei.Id;
				recording.PlaybackDatei = datei;
			}
			// A measured duration describes the file members play; the
			// uploading browser reports the new one.
			if (RecordingFiles.Resolve(recording).Playable?.Id != playedBefore)
				recording.DurationSeconds = null;
			recording.UpdatedAt = now;
			recording.UpdatedByAccountId = decision.AccountId;
			recording.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException || IsUniqueViolation(exception))
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { recording = Payload(recording, decision, isEditor: true) });
		}).DisableAntiforgery();

		/// <summary>
		/// The renewable ticket for one recording; renewal is this same call
		/// again, with membership, both publications and the download switch
		/// re-read every time. <c>viewUrl</c> is null while no playable file
		/// exists; <c>downloadUrl</c> is null unless an editor enabled
		/// downloads — no download ticket is even issued then.
		/// </summary>
		app.MapGet("/api/recordings/{id}/access", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, IOptions<AssetStorageOptions> options,
			IAssetStorageAdapter storage, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var recording = await WithFiles(db.Recordings.AsNoTracking())
				.Include(r => r.Event)
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (recording is null
				|| (!IsEditor(decision!) && !RecordingVisibility.IsMemberVisible(recording, recording.Event)))
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			var files = RecordingFiles.Resolve(recording);
			var file = files.Playable ?? files.Preserved;
			if (file is null)
				return Results.Problem(statusCode: 404, title: NoFileMessage);
			var lifetime = options.Value.ReadTicketLifetime;
			string? viewUrl = null;
			string? downloadUrl = null;
			try
			{
				if (files.Playable is not null)
					viewUrl = await storage.CreateReadTicketAsync(
						file.BlobName, lifetime, asDownload: false, contentType: file.ContentType, token);
				if (recording.DownloadEnabled)
					downloadUrl = await storage.CreateReadTicketAsync(
						file.BlobName, lifetime, asDownload: true, contentType: file.ContentType, token);
			}
			catch (InvalidOperationException)
			{
				return Results.Problem(statusCode: 502, title: AssetEndpoints.StorageFailureMessage);
			}
			return Results.Ok(new
			{
				recordingId = recording.Id,
				kind = recording.Kind,
				playbackState = files.State,
				source = files.Source,
				dateiId = file.Id,
				contentType = file.ContentType,
				sizeBytes = file.SizeBytes,
				durationSeconds = recording.DurationSeconds,
				viewUrl,
				downloadEnabled = recording.DownloadEnabled,
				downloadUrl,
				expiresAt = time.GetUtcNow() + lifetime,
			});
		});
	}

	/// <summary>Both Datei slots (<see cref="RecordingFiles.Resolve"/> needs them).</summary>
	internal static IQueryable<Recording> WithFiles(IQueryable<Recording> recordings) => recordings
		.Include(r => r.OriginalDatei)
		.Include(r => r.PlaybackDatei);

	private static bool IsUniqueViolation(DbUpdateException exception) =>
		exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

	/// <summary>
	/// One wire shape for lists and write responses. Requires both asset
	/// slots (<see cref="WithFiles"/>). File names, versions and attribution
	/// stay in the editor block.
	/// </summary>
	private static object Payload(Recording recording, ArchiveAccessDecision decision, bool isEditor)
	{
		var files = RecordingFiles.Resolve(recording);
		return new
		{
			id = recording.Id,
			eventId = recording.EventId,
			label = recording.Label,
			kind = recording.Kind,
			isPublished = recording.PublishedAt is not null,
			downloadEnabled = recording.DownloadEnabled,
			durationSeconds = recording.DurationSeconds,
			playback = new
			{
				state = files.State,
				source = files.Source,
				dateiId = files.Playable?.Id,
				contentType = files.Playable?.ContentType,
				sizeBytes = files.Playable?.SizeBytes,
			},
			createdAt = recording.CreatedAt,
			editor = !isEditor
				? null
				: (object)new
				{
					version = recording.RowVersion,
					canChangeFiles = recording.CreatedByAccountId == decision.AccountId,
					publishedAt = recording.PublishedAt,
					original = SlotPayload(RecordingFiles.Live(recording.OriginalDatei), recording.Kind),
					playbackCopy = SlotPayload(RecordingFiles.Live(recording.PlaybackDatei), recording.Kind),
				},
		};
	}

	private static object SlotPayload(Datei? datei, string kind) => new
	{
		file = datei is null
			? null
			: (object)new
			{
				dateiId = datei.Id,
				contentType = datei.ContentType,
				sizeBytes = datei.SizeBytes,
				fileName = datei.OriginalFileName,
				createdAt = datei.CreatedAt,
				playable = RecordingFormats.IsPlayable(datei.ContentType, kind),
			},
	};

	private static bool TryValidateLabel(string? raw, out string label, out IResult? error)
	{
		label = (raw ?? string.Empty).Trim();
		error = label.Length == 0
			? Results.Problem(statusCode: 400, title: LabelRequiredMessage)
			: label.Length > LabelMaxLength
				? Results.Problem(statusCode: 400, title: LabelTooLongMessage)
				: null;
		return error is null;
	}

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
		var (decision, error) = await RequireMemberAsync(context, accessor, access);
		if (error is not null)
			return (null, error);
		if (!IsEditor(decision!))
			return (null, Results.Problem(statusCode: 403, title: ForbiddenMessage));
		return (decision, null);
	}
}

/// <summary>
/// Which of a recording's files plays what role right now (ARC-030).
/// <see cref="Playable"/> is the Datei members stream: the validated playback
/// copy when one exists, otherwise a playable original serving both roles.
/// <see cref="Preserved"/> is the Datei that exists when nothing is playable.
/// A slot whose Datei sits in the trash counts as empty.
/// </summary>
public sealed record RecordingFiles(string State, string? Source, Datei? Playable, Datei? Preserved)
{
	/// <summary>A playable file exists.</summary>
	public const string Ready = "ready";

	/// <summary>A file exists, but browsers cannot play it: a converted copy is needed.</summary>
	public const string NeedsPlaybackCopy = "needsPlaybackCopy";

	/// <summary>No file has been transferred yet.</summary>
	public const string Missing = "missing";

	public const string OriginalSource = "original";

	public const string PlaybackCopySource = "playbackCopy";

	/// <summary>The slot's Datei unless it is in the trash.</summary>
	public static Datei? Live(Datei? datei) => datei is { DeletedAt: null } ? datei : null;

	/// <summary>Requires both Datei slots loaded.</summary>
	public static RecordingFiles Resolve(Recording recording)
	{
		var copy = Live(recording.PlaybackDatei);
		var original = Live(recording.OriginalDatei);
		if (copy is not null && RecordingFormats.IsPlayable(copy.ContentType, recording.Kind))
			return new RecordingFiles(Ready, PlaybackCopySource, copy, null);
		if (original is not null && RecordingFormats.IsPlayable(original.ContentType, recording.Kind))
			return new RecordingFiles(Ready, OriginalSource, original, null);
		var preserved = original ?? copy;
		return preserved is null
			? new RecordingFiles(Missing, null, null, null)
			: new RecordingFiles(NeedsPlaybackCopy, null, null, preserved);
	}
}
