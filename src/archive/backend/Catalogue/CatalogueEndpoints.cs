using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Catalogue;

public sealed record CreateSongRequest(string? Title, string? Composer, string? Lyricist, string? ArrangementLabel, string? VersionLabel);

public sealed record PatchSongRequest(string? Title, string? Composer, string? Lyricist);

public sealed record CreateArrangementRequest(string? Label, string? Arranger, string? VoiceConfiguration);

public sealed record PatchArrangementRequest(string? Label, string? Arranger, string? VoiceConfiguration);

public sealed record CreateMusicalVersionRequest(string? Label, string? Creator, string? MusicalKey);

public sealed record PatchMusicalVersionRequest(string? Label, string? Creator, string? MusicalKey);

/// <summary>
/// Member catalogue API (ARC-013). Reads use the shared database decision:
/// an active member sees published songs, editors/administrators also see
/// drafts (see <see cref="CatalogueVisibility"/>). All mutations require the
/// Editor or Administrator role from that decision, CSRF and antiforgery
/// validation; actors and timestamps are attributed from the decision and the
/// injected TimeProvider. Members receive 403; unauthenticated or revoked
/// callers receive 401.
/// </summary>
public static class CatalogueEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für das Liedverzeichnis.";

	public const string ConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string NotFoundMessage = "Das Lied wurde nicht gefunden.";

	public const string ArrangementNotFoundMessage = "Die Fassung wurde nicht gefunden.";

	public const string DefaultLabel = "Standardfassung";

	public static void MapCatalogueEndpoints(this WebApplication app)
	{
		app.MapGet("/api/songs", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			var songs = await QueryVisible(db.Songs, isEditor)
				.OrderBy(s => s.Id)
				.Select(s => new SongSummary(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt))
				.ToListAsync(token);
			return Results.Ok(new
			{
				songs = songs.Select(s => new
				{
					id = s.Id,
					title = s.Title,
					composer = s.Composer,
					lyricist = s.Lyricist,
					published = s.PublishedAt is not null,
					publishedAt = s.PublishedAt,
				}),
			});
		});

		app.MapGet("/api/songs/{id}", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			var song = await db.Songs.AsNoTracking()
				.Include(s => s.Arrangements).ThenInclude(a => a.MusicalVersions)
				.FirstOrDefaultAsync(s => s.Id == id, token);
			if (song is null || (!isEditor && !CatalogueVisibility.IsMemberVisible(song)))
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			var versionIds = song.Arrangements
				.SelectMany(a => a.MusicalVersions)
				.Select(v => v.Id)
				.ToList();
			var versionAssets = await LoadVersionAssetsAsync(db, versionIds, token);
			return Results.Ok(new { song = SongDetail(song, versionAssets) });
		});

		app.MapPost("/api/songs", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, CancellationToken token,
			CreateSongRequest? body) =>
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
			if (!TryValidateTitle(body?.Title, out var title, out var titleError))
				return titleError;
			var composer = CleanOptional(body?.Composer, "Der Komponist ist zu lang.", out var composerError);
			if (composerError is not null)
				return composerError;
			var lyricist = CleanOptional(body?.Lyricist, "Der Textdichter ist zu lang.", out var lyricistError);
			if (lyricistError is not null)
				return lyricistError;
			var arrangementLabel = CleanOptional(body?.ArrangementLabel, "Die Bezeichnung ist zu lang.", out var arrangementError);
			if (arrangementError is not null)
				return arrangementError;
			var versionLabel = CleanOptional(body?.VersionLabel, "Die Bezeichnung ist zu lang.", out var versionError);
			if (versionError is not null)
				return versionError;
			var now = time.GetUtcNow();
			var song = new Song
			{
				Title = title,
				Composer = composer,
				Lyricist = lyricist,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
				UpdatedAt = now,
				UpdatedByAccountId = decision.AccountId,
			};
			song.Arrangements.Add(new Arrangement
			{
				Song = song,
				Label = arrangementLabel ?? DefaultLabel,
				CreatedAt = now,
				CreatedByAccountId = decision.AccountId,
				MusicalVersions =
				[
					new MusicalVersion
					{
						Label = versionLabel ?? DefaultLabel,
						CreatedAt = now,
						CreatedByAccountId = decision.AccountId,
					},
				],
			});
			db.Songs.Add(song);
			await db.SaveChangesAsync(token);
			return Results.Created($"/api/songs/{song.Id}", new { song = SongDetail(song) });
		}).DisableAntiforgery();

		app.MapPatch("/api/songs/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchSongRequest? body) =>
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
			if (body?.Title is not null && !TryValidateTitle(body.Title, out _, out var titleError))
				return titleError;
			var composer = PatchOptional(body?.Composer, "Der Komponist ist zu lang.", out var composerError);
			if (composerError is not null)
				return composerError;
			var lyricist = PatchOptional(body?.Lyricist, "Der Textdichter ist zu lang.", out var lyricistError);
			if (lyricistError is not null)
				return lyricistError;
			var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id, token);
			if (song is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body?.Title is not null)
				song.Title = body.Title.Trim();
			if (body?.Composer is not null)
				song.Composer = composer;
			if (body?.Lyricist is not null)
				song.Lyricist = lyricist;
			var now = time.GetUtcNow();
			song.UpdatedAt = now;
			song.UpdatedByAccountId = decision!.AccountId;
			song.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { song = SongDetail((await LoadDetailAsync(db, id, token))!) });
		}).DisableAntiforgery();

		app.MapPost("/api/songs/{id}/publish", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token) =>
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
			var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id, token);
			if (song is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (song.PublishedAt is null)
			{
				song.PublishedAt = time.GetUtcNow();
				song.PublishedByAccountId = decision!.AccountId;
				song.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.Ok(new { song = SongDetail((await LoadDetailAsync(db, id, token))!) });
		}).DisableAntiforgery();

		app.MapPost("/api/songs/{id}/unpublish", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token) =>
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
			var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id, token);
			if (song is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (song.PublishedAt is not null)
			{
				song.PublishedAt = null;
				song.PublishedByAccountId = null;
				song.RowVersion++;
				await db.SaveChangesAsync(token);
			}
			return Results.Ok(new { song = SongDetail((await LoadDetailAsync(db, id, token))!) });
		}).DisableAntiforgery();

		app.MapPost("/api/songs/{id}/arrangements", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreateArrangementRequest? body) =>
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
				return labelError;
			var arranger = CleanOptional(body?.Arranger, "Der Arrangeur ist zu lang.", out var arrangerError);
			if (arrangerError is not null)
				return arrangerError;
			var voiceConfiguration = CleanOptional(body?.VoiceConfiguration, "Die Stimmverteilung ist zu lang.", out var voiceError);
			if (voiceError is not null)
				return voiceError;
			var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id, token);
			if (song is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
		var now = time.GetUtcNow();
		var arrangement = new Arrangement
		{
			SongId = song.Id,
			Label = label,
			Arranger = arranger,
			VoiceConfiguration = voiceConfiguration,
			CreatedAt = now,
			CreatedByAccountId = decision!.AccountId,
		};
		db.Arrangements.Add(arrangement);
		song.UpdatedAt = now;
			song.UpdatedByAccountId = decision.AccountId;
			song.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Created($"/api/songs/{song.Id}", new { song = SongDetail((await LoadDetailAsync(db, song.Id, token))!) });
		}).DisableAntiforgery();

		app.MapPatch("/api/arrangements/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchArrangementRequest? body) =>
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
			if (body?.Label is not null && !TryValidateLabel(body.Label, out _, out var labelError))
				return labelError;
			var arranger = PatchOptional(body?.Arranger, "Der Arrangeur ist zu lang.", out var arrangerError);
			if (arrangerError is not null)
				return arrangerError;
			var voiceConfiguration = PatchOptional(body?.VoiceConfiguration, "Die Stimmverteilung ist zu lang.", out var voiceError);
			if (voiceError is not null)
				return voiceError;
			var arrangement = await db.Arrangements.Include(a => a.Song)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (arrangement is null || arrangement.Song is null)
				return Results.Problem(statusCode: 404, title: ArrangementNotFoundMessage);
			if (body?.Label is not null)
				arrangement.Label = body.Label.Trim();
			if (body?.Arranger is not null)
				arrangement.Arranger = arranger;
			if (body?.VoiceConfiguration is not null)
				arrangement.VoiceConfiguration = voiceConfiguration;
			var now = time.GetUtcNow();
			arrangement.Song.UpdatedAt = now;
			arrangement.Song.UpdatedByAccountId = decision!.AccountId;
			arrangement.Song.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { song = SongDetail((await LoadDetailAsync(db, arrangement.Song.Id, token))!) });
		}).DisableAntiforgery();

		app.MapPost("/api/arrangements/{id}/versions", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			CreateMusicalVersionRequest? body) =>
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
				return labelError;
			var creator = CleanOptional(body?.Creator, "Der Ersteller ist zu lang.", out var creatorError);
			if (creatorError is not null)
				return creatorError;
			var musicalKey = CleanOptional(body?.MusicalKey, "Die Tonart ist zu lang.", out var keyError);
			if (keyError is not null)
				return keyError;
			var arrangement = await db.Arrangements.Include(a => a.Song)
				.FirstOrDefaultAsync(a => a.Id == id, token);
			if (arrangement is null || arrangement.Song is null)
				return Results.Problem(statusCode: 404, title: ArrangementNotFoundMessage);
			var now = time.GetUtcNow();
			var version = new MusicalVersion
			{
				ArrangementId = arrangement.Id,
				Label = label,
				Creator = creator,
				MusicalKey = musicalKey,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
			};
			db.MusicalVersions.Add(version);
			arrangement.Song.UpdatedAt = now;
			arrangement.Song.UpdatedByAccountId = decision.AccountId;
			arrangement.Song.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Created($"/api/songs/{arrangement.Song.Id}", new { song = SongDetail((await LoadDetailAsync(db, arrangement.Song.Id, token))!) });
		}).DisableAntiforgery();

		app.MapPatch("/api/musical-versions/{id}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token,
			PatchMusicalVersionRequest? body) =>
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
			if (body?.Label is not null && !TryValidateLabel(body.Label, out _, out var labelError))
				return labelError;
			var creator = PatchOptional(body?.Creator, "Der Ersteller ist zu lang.", out var creatorError);
			if (creatorError is not null)
				return creatorError;
			var musicalKey = PatchOptional(body?.MusicalKey, "Die Tonart ist zu lang.", out var keyError);
			if (keyError is not null)
				return keyError;
			var version = await db.MusicalVersions
				.Include(v => v.Arrangement).ThenInclude(a => a.Song)
				.FirstOrDefaultAsync(v => v.Id == id, token);
			if (version is null || version.Arrangement?.Song is null)
				return Results.Problem(statusCode: 404, title: ArrangementNotFoundMessage);
			if (body?.Label is not null)
				version.Label = body.Label.Trim();
			if (body?.Creator is not null)
				version.Creator = creator;
			if (body?.MusicalKey is not null)
				version.MusicalKey = musicalKey;
			var now = time.GetUtcNow();
			version.Arrangement.Song.UpdatedAt = now;
			version.Arrangement.Song.UpdatedByAccountId = decision!.AccountId;
			version.Arrangement.Song.RowVersion++;
			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
			}
			return Results.Ok(new { song = SongDetail((await LoadDetailAsync(db, version.Arrangement.Song.Id, token))!) });
		}).DisableAntiforgery();
	}

	private static IQueryable<Song> QueryVisible(IQueryable<Song> songs, bool isEditor)
		=> isEditor ? songs : songs.Where(s => s.PublishedAt != null);

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<Song?> LoadDetailAsync(ArchiveDbContext db, Guid id, CancellationToken token)
		=> await db.Songs.AsNoTracking()
			.Include(s => s.Arrangements).ThenInclude(a => a.MusicalVersions)
			.FirstOrDefaultAsync(s => s.Id == id, token);

	/// <summary>
	/// Loads the per-version asset summaries (ARC-015) for a song detail
	/// payload: only current revisions are exposed, pending or older revisions
	/// never appear (revision history inspection is ARC-031).
	/// </summary>
	public static async Task<List<(Guid VersionId, List<object> Assets)>> LoadVersionAssetsAsync(
		ArchiveDbContext db, IReadOnlyCollection<Guid> versionIds, CancellationToken token)
	{
		if (versionIds.Count == 0)
			return [];
		var assets = await db.Assets.AsNoTracking()
			.Include(a => a.CurrentRevision)
			.Where(a => versionIds.Contains(a.MusicalVersionId))
			.OrderBy(a => a.Id)
			.ToListAsync(token);
		return assets
			.GroupBy(a => a.MusicalVersionId)
			.Select(group => (group.Key, group
				.Select(a => (object)new
				{
					id = a.Id,
					assetType = a.AssetType,
					voiceLabel = a.VoiceLabel,
					description = a.Description,
					currentRevision = a.CurrentRevision is null
						? null
						: (object)new
						{
							revisionId = a.CurrentRevision.Id,
							revisionNumber = a.CurrentRevision.RevisionNumber,
							contentType = a.CurrentRevision.ContentType,
							sizeBytes = a.CurrentRevision.SizeBytes,
							createdAt = a.CurrentRevision.CreatedAt,
						},
				})
				.ToList()))
			.ToList();
	}

	private static object SongDetail(Song song, List<(Guid VersionId, List<object> Assets)>? versionAssets = null)
	{
		var assetMap = versionAssets?.ToDictionary(entry => entry.VersionId, entry => entry.Assets);
		return new
		{
			id = song.Id,
			title = song.Title,
			composer = song.Composer,
			lyricist = song.Lyricist,
			published = song.PublishedAt is not null,
			publishedAt = song.PublishedAt,
			createdAt = song.CreatedAt,
			updatedAt = song.UpdatedAt,
			arrangements = song.Arrangements.OrderBy(a => a.Id).Select(a => new
			{
				id = a.Id,
				label = a.Label,
				arranger = a.Arranger,
				voiceConfiguration = a.VoiceConfiguration,
				musicalVersions = a.MusicalVersions.OrderBy(v => v.Id).Select(v => new
				{
					id = v.Id,
					label = v.Label,
					creator = v.Creator,
					musicalKey = v.MusicalKey,
					assets = assetMap is not null && assetMap.TryGetValue(v.Id, out var assets)
						? assets
						: (List<object>)[],
				}),
			}),
		};
	}

	private static bool TryValidateTitle(string? raw, out string title, out IResult? error)
	{
		var title_ = (raw ?? string.Empty).Trim();
		if (title_.Length == 0)
		{
			title = string.Empty;
			error = Results.Problem(statusCode: 400, title: "Der Titel ist erforderlich.");
			return false;
		}
		if (title_.Length > 200)
		{
			title = string.Empty;
			error = Results.Problem(statusCode: 400, title: "Der Titel ist zu lang.");
			return false;
		}
		title = title_;
		error = null;
		return true;
	}

	private static bool TryValidateLabel(string? raw, out string label, out IResult? error)
	{
		var label_ = (raw ?? string.Empty).Trim();
		if (label_.Length == 0)
		{
			label = string.Empty;
			error = Results.Problem(statusCode: 400, title: "Das Label ist erforderlich.");
			return false;
		}
		if (label_.Length > 200)
		{
			label = string.Empty;
			error = Results.Problem(statusCode: 400, title: "Das Label ist zu lang.");
			return false;
		}
		label = label_;
		error = null;
		return true;
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

	private static string? PatchOptional(string? raw, string tooLongTitle, out IResult? error)
		=> CleanOptional(raw, tooLongTitle, out error);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		// Cookie presence first for a useful signed-out state: revoked or
		// signed-out callers have no principal and get 401.
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
			return (null, Results.Problem(statusCode: 403, title: ForbiddenMessage));
		return (decision, null);
	}

	private sealed record SongSummary(Guid Id, string Title, string? Composer, string? Lyricist, DateTimeOffset? PublishedAt);
}
