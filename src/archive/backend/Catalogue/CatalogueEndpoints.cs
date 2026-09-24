using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Catalogue;

public sealed record CreateSongRequest(string? Title, string? Composer, string? Lyricist, string? ArrangementLabel, string? VersionLabel, string? Language, string? Occasion, List<string?>? Tags);

public sealed record PatchSongRequest(string? Title, string? Composer, string? Lyricist, string? Lyrics, List<string?>? AlternateTitles, string? Language, string? Occasion, List<string?>? Tags);

public sealed record CreateArrangementRequest(string? Label, string? Arranger, string? VoiceConfiguration, string? Accompaniment);

public sealed record PatchArrangementRequest(string? Label, string? Arranger, string? VoiceConfiguration, string? Accompaniment);

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

	public const string SearchTooLongMessage = "Die Suche ist zu lang.";

	public const string LyricsTooLongMessage = "Der Liedtext ist zu lang.";

	public const string AlternateTitleEmptyMessage = "Ein anderer Titel darf nicht leer sein.";

	public const string AlternateTitleTooLongMessage = "Ein anderer Titel ist zu lang.";

	public const string AlternateTitleTooManyMessage = "Es sind höchstens 10 andere Titel möglich.";

	public const string LanguageTooLongMessage = "Die Sprache ist zu lang.";

	public const string OccasionTooLongMessage = "Der Anlass ist zu lang.";

	public const string TagEmptyMessage = "Ein Schlagwort darf nicht leer sein.";

	public const string TagTooLongMessage = "Das Schlagwort ist zu lang.";

	public const string TagTooManyMessage = "Es sind höchstens 10 Schlagwörter möglich.";

	public const string AccompanimentTooLongMessage = "Die Begleitung ist zu lang.";

	public const string FilterTooLongMessage = "Der Filter ist zu lang.";

	public const string UnknownMaterialFilterMessage = "Unbekannter Materialfilter.";

	/// <summary>Entry maximum per tag (alternate titles keep the 200 field limit).</summary>
	public const int TagMaxLength = 60;

	/// <summary>Entry maximum per alternate title.</summary>
	public const int MaxTitleListEntryLength = 200;

	public static void MapCatalogueEndpoints(this WebApplication app)
	{
		app.MapGet("/api/songs", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, CancellationToken token,
			string? q, int? page, string? voiceConfiguration, string? accompaniment,
			string? musicalKey, string? language, string? occasion, string? tag, string? material) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireMemberAsync(context, accessor, access);
			if (error is not null)
				return error;
			var isEditor = IsEditor(decision!);
			const int pageSize = 20;
			var requestedPage = page is null or < 1 ? 1 : page.Value;
			var query = (q ?? string.Empty).Trim();
			if (query.Length > 200)
				return Results.Problem(statusCode: 400, title: SearchTooLongMessage);
			var (filter, filterError) = ParseRepertoireFilter(
				voiceConfiguration, accompaniment, musicalKey, language, occasion, tag, material);
			if (filterError is not null)
				return filterError;
			if (query.Length == 0 && filter is null)
			{
				// No query and no filter: existing list behaviour (plain Id
				// order) with the search response fields added around it.
				var allSongs = await QueryVisible(db.Songs, isEditor)
					.OrderBy(s => s.Id)
					.Select(s => new SongSummary(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt))
					.ToListAsync(token);
				var totalAll = allSongs.Count;
				var plainPage = allSongs
					.Skip((requestedPage - 1) * pageSize)
					.Take(pageSize)
					.ToList();
				var plainIds = plainPage.Select(s => s.Id).ToList();
				var plainArrangements = await LoadArrangementSummariesAsync(db, plainIds, token);
				var plainAlternateTitles = await LoadAlternateTitlesAsync(db, plainIds, token);
				return Results.Ok(new
				{
					query = (string?)null,
					page = requestedPage,
					pageSize = pageSize,
					total = totalAll,
					filters = FilterEcho(null),
					songs = plainPage.Select(s => SongItem(s.Id, s.Title, s.Composer, s.Lyricist,
						s.PublishedAt,
						plainAlternateTitles.GetValueOrDefault(s.Id, []),
						plainArrangements.GetValueOrDefault(s.Id, []),
						[],
						// No arrangement-level filter: every arrangement is a match hint.
						plainArrangements.GetValueOrDefault(s.Id, []))),
				});
			}
			if (filter is null)
			{
				// Pure search (ARC-020): unchanged matching, fields and paging.
				var searchResult = await SearchSongsAsync(db, isEditor, query, requestedPage, pageSize, token);
				return Results.Ok(new
				{
					query = searchResult.Query,
					page = requestedPage,
					pageSize = pageSize,
					total = searchResult.Total,
					filters = FilterEcho(null),
					songs = searchResult.PageItems.Select(m => SongItem(m.Song.Id, m.Song.Title, m.Song.Composer,
						m.Song.Lyricist, m.Song.PublishedAt,
						searchResult.AlternateTitles.GetValueOrDefault(m.Song.Id, []),
						searchResult.Arrangements.GetValueOrDefault(m.Song.Id, []),
						m.MatchedIn.ToArray(),
						// No arrangement-level filter: every arrangement is a match hint.
						searchResult.Arrangements.GetValueOrDefault(m.Song.Id, []))),
				});
			}
			// ARC-023 filter evaluation over one database-projected visible
			// set; matching runs in C# so InMemory tests and PostgreSQL agree.
			var hasQuery = query.Length > 0;
			var queryFold = CatalogueText.Fold(query);
			var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(CatalogueText.Fold)
				.Where(t => t.Length > 0)
				.ToList();
			var visible = await QueryVisible(db.Songs, isEditor)
				.OrderBy(s => s.Id)
				.Select(s => new FilterRow(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt,
					s.Lyrics, s.Language, s.Occasion))
				.ToListAsync(token);
			var visibleIds = visible.Select(s => s.Id).ToList();
			var alternateTitleMap = await LoadAlternateTitlesAsync(db, visibleIds, token);
			var tagMap = filter.Tag is not null
				? await LoadSongTagsAsync(db, visibleIds, token)
				: [];
			var arrangementRows = await db.Arrangements.AsNoTracking()
				.Where(a => visibleIds.Contains(a.SongId))
				.OrderBy(a => a.Id)
				.Select(a => new { a.Id, a.SongId, a.Label, a.Arranger, a.VoiceConfiguration, a.Accompaniment })
				.ToListAsync(token);
			var arrangementMap = arrangementRows
				.GroupBy(a => a.SongId)
				.ToDictionary(g => g.Key, g => g.ToList());
			Dictionary<Guid, List<RepertoireVersionRow>> versionsByArrangement = [];
			if (filter.HasVersionConditions)
			{
				var arrangementIds = arrangementRows.Select(a => a.Id).ToList();
				var versionRows = await db.MusicalVersions.AsNoTracking()
					.Where(v => arrangementIds.Contains(v.ArrangementId))
					.OrderBy(v => v.Id)
					.Select(v => new { v.Id, v.ArrangementId, v.MusicalKey })
					.ToListAsync(token);
				var versionIds = versionRows.Select(v => v.Id).ToList();
				// Material availability counts only current revisions; assets
				// without a current revision never reach the projection.
				var assetRows = await db.Assets.AsNoTracking()
					.Where(asset => versionIds.Contains(asset.MusicalVersionId)
						&& asset.CurrentRevisionId != null)
					.Select(asset => new { asset.MusicalVersionId, asset.AssetType })
					.ToListAsync(token);
				var assetTypesByVersion = assetRows
					.GroupBy(asset => asset.MusicalVersionId)
					.ToDictionary(
						g => g.Key,
						g => g.Select(asset => asset.AssetType).Distinct().ToList());
				versionsByArrangement = versionRows
					.GroupBy(v => v.ArrangementId)
					.ToDictionary(
						g => g.Key,
						g => g.Select(v => new RepertoireVersionRow(v.MusicalKey,
								assetTypesByVersion.TryGetValue(v.Id, out var assetTypes)
									? assetTypes
									: []))
							.ToList());
			}
			var matches = new List<(FilterRow Song, int Rank, SortedSet<string> MatchedIn,
				List<ArrangementSummary> MatchedArrangements)>();
			foreach (var song in visible)
			{
				var matchedIn = new SortedSet<string>();
				var titleFold = CatalogueText.Fold(song.Title);
				var titles = alternateTitleMap.TryGetValue(song.Id, out var titles_)
					? titles_.Select(CatalogueText.Fold).ToList()
					: [];
				var composerFold = CatalogueText.Fold(song.Composer);
				var lyricistFold = CatalogueText.Fold(song.Lyricist);
				var lyricsFold = CatalogueText.Fold(song.Lyrics);
				var arrangementTexts = arrangementMap.TryGetValue(song.Id, out var arrangements_)
					? arrangements_.SelectMany(a => new[] { CatalogueText.Fold(a.Label), CatalogueText.Fold(a.Arranger) }).ToList()
					: [];
				var allFound = true;
				if (hasQuery)
				{
					// AND semantics: every token must occur (substring) in at
					// least one of the searchable fields; matchedIn lists every
					// field a token hit, sorted alphabetically.
					foreach (var current in tokens)
					{
						var found = false;
						if (titleFold.Contains(current))
						{
							found = true;
							matchedIn.Add("title");
						}
						if (titles.Any(t => t.Contains(current)))
						{
							found = true;
							matchedIn.Add("alternateTitles");
						}
						if (composerFold.Contains(current))
						{
							found = true;
							matchedIn.Add("composer");
						}
						if (lyricistFold.Contains(current))
						{
							found = true;
							matchedIn.Add("lyricist");
						}
						if (lyricsFold.Contains(current))
						{
							found = true;
							matchedIn.Add("lyrics");
						}
						if (arrangementTexts.Any(t => t.Contains(current)))
						{
							found = true;
							matchedIn.Add("arrangements");
						}
						if (!found)
						{
							allFound = false;
							break;
						}
					}
				}
				// Song-level conditions hold on the song itself.
				if (allFound && !filter.MatchesSong(song.Language, song.Occasion,
						tagMap.TryGetValue(song.Id, out var songTags) ? songTags : []))
				{
					allFound = false;
				}
				// Arrangement-level conditions hold on one qualifying
				// arrangement; siblings never combine into a match.
				List<ArrangementSummary> matchedArrangements;
				if (allFound && filter.HasArrangementConditions)
				{
					matchedArrangements = (arrangementMap.TryGetValue(song.Id, out var arrangements2)
							? arrangements2
							: [])
						.Where(a => filter.MatchesArrangement(a.VoiceConfiguration, a.Accompaniment,
							versionsByArrangement.TryGetValue(a.Id, out var versions_)
								? versions_
								: []))
						.Select(a => new ArrangementSummary(a.Id, a.Label, a.Arranger))
						.ToList();
					if (matchedArrangements.Count == 0)
						allFound = false;
				}
				else
				{
					// No arrangement-level conditions: the hint mirrors all
					// arrangements; songs failing earlier conditions are skipped.
					matchedArrangements = allFound
						? (arrangementMap.TryGetValue(song.Id, out var arrangements3)
								? arrangements3
								: [])
							.Select(a => new ArrangementSummary(a.Id, a.Label, a.Arranger))
							.ToList()
						: [];
				}
				if (!allFound)
					continue;
				var rank = !hasQuery ? 0
					: titleFold == queryFold ? 0
					: tokens.All(t => titleFold.Contains(t)) ? 1
					: 2;
				matches.Add((song, rank, matchedIn, matchedArrangements));
			}
			var ordered = matches
				.OrderBy(m => m.Rank)
				.ThenBy(m => m.Song.Id)
				.ToList();
			var total = ordered.Count;
			var pageItems = ordered
				.Skip((requestedPage - 1) * pageSize)
				.Take(pageSize)
				.ToList();
			var pageIds = pageItems.Select(m => m.Song.Id).ToList();
			var pageAlternateTitles = await LoadAlternateTitlesAsync(db, pageIds, token);
			var pageArrangements = pageIds.ToDictionary(
				id => id,
				id => (arrangementMap.TryGetValue(id, out var allArrangements)
						? allArrangements
						: [])
					.Select(a => new ArrangementSummary(a.Id, a.Label, a.Arranger))
					.ToList());
			return Results.Ok(new
			{
				query = hasQuery ? query : (string?)null,
				page = requestedPage,
				pageSize = pageSize,
				total,
				filters = FilterEcho(filter),
				songs = pageItems.Select(m => SongItem(m.Song.Id, m.Song.Title, m.Song.Composer,
					m.Song.Lyricist, m.Song.PublishedAt,
					pageAlternateTitles.GetValueOrDefault(m.Song.Id, []),
					pageArrangements.GetValueOrDefault(m.Song.Id, []),
					m.MatchedIn.ToArray(),
					m.MatchedArrangements)),
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
				.Include(s => s.AlternateTitles)
				.Include(s => s.Tags)
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
			var language = CleanOptional(body?.Language, LanguageTooLongMessage, out var languageError);
			if (languageError is not null)
				return languageError;
			var occasion = CleanOptional(body?.Occasion, OccasionTooLongMessage, out var occasionError);
			if (occasionError is not null)
				return occasionError;
			if (body?.Tags is not null)
			{
				var tagValidationError = ValidateEntryList(body.Tags, TagTooManyMessage, TagEmptyMessage, TagTooLongMessage, TagMaxLength);
				if (tagValidationError is not null)
					return tagValidationError;
			}
			var now = time.GetUtcNow();
			var song = new Song
			{
				Title = title,
				Composer = composer,
				Lyricist = lyricist,
				Language = language,
				Occasion = occasion,
				CreatedAt = now,
				CreatedByAccountId = decision!.AccountId,
				UpdatedAt = now,
				UpdatedByAccountId = decision.AccountId,
			};
			if (body?.Tags is not null)
			{
				for (var position = 0; position < body.Tags.Count; position++)
				{
					song.Tags.Add(new SongTag
					{
						Song = song,
						Value = body.Tags[position]!.Trim(),
						Position = position,
						CreatedAt = now,
						CreatedByAccountId = decision.AccountId,
					});
				}
			}
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
			var language = PatchOptional(body?.Language, LanguageTooLongMessage, out var languageError);
			if (languageError is not null)
				return languageError;
			var occasion = PatchOptional(body?.Occasion, OccasionTooLongMessage, out var occasionError);
			if (occasionError is not null)
				return occasionError;
			string? lyrics = null;
			if (body?.Lyrics is not null)
			{
				var trimmedLyrics = body.Lyrics.Trim();
				if (trimmedLyrics.Length > 5000)
					return Results.Problem(statusCode: 400, title: LyricsTooLongMessage);
				lyrics = trimmedLyrics.Length == 0 ? null : trimmedLyrics;
			}
			if (body?.AlternateTitles is not null)
			{
				var alternateError = ValidateEntryList(body.AlternateTitles, AlternateTitleTooManyMessage, AlternateTitleEmptyMessage, AlternateTitleTooLongMessage, MaxTitleListEntryLength);
				if (alternateError is not null)
					return alternateError;
			}
			if (body?.Tags is not null)
			{
				var tagValidationError = ValidateEntryList(body.Tags, TagTooManyMessage, TagEmptyMessage, TagTooLongMessage, TagMaxLength);
				if (tagValidationError is not null)
					return tagValidationError;
			}
			var song = await db.Songs
				.Include(s => s.AlternateTitles)
				.Include(s => s.Tags)
				.FirstOrDefaultAsync(s => s.Id == id, token);
			if (song is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			if (body?.Title is not null)
				song.Title = body.Title.Trim();
			if (body?.Composer is not null)
				song.Composer = composer;
			if (body?.Lyricist is not null)
				song.Lyricist = lyricist;
			if (body?.Lyrics is not null)
				song.Lyrics = lyrics;
			if (body?.Language is not null)
				song.Language = language;
			if (body?.Occasion is not null)
				song.Occasion = occasion;
			if (body?.AlternateTitles is not null)
			{
				// Full replacement: delete the existing rows and re-add them in
				// list order with fresh attribution and an explicit position
				// (Guid v7 IDs share the same millisecond within this save).
				db.SongTitles.RemoveRange(song.AlternateTitles);
				var now_ = time.GetUtcNow();
				for (var position = 0; position < body.AlternateTitles.Count; position++)
				{
					db.SongTitles.Add(new SongTitle
					{
						SongId = song.Id,
						Value = body.AlternateTitles[position]!.Trim(),
						Position = position,
						CreatedAt = now_,
						CreatedByAccountId = decision!.AccountId,
					});
				}
			}
			if (body?.Tags is not null)
			{
				// Full replacement: delete the existing rows and re-add them in
				// list order with fresh attribution and an explicit position
				// (Guid v7 IDs share the same millisecond within this save).
				db.SongTags.RemoveRange(song.Tags);
				var now_ = time.GetUtcNow();
				for (var position = 0; position < body.Tags.Count; position++)
				{
					db.SongTags.Add(new SongTag
					{
						SongId = song.Id,
						Value = body.Tags[position]!.Trim(),
						Position = position,
						CreatedAt = now_,
						CreatedByAccountId = decision!.AccountId,
					});
				}
			}
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
			var accompaniment = CleanOptional(body?.Accompaniment, AccompanimentTooLongMessage, out var accompanimentError);
			if (accompanimentError is not null)
				return accompanimentError;
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
			Accompaniment = accompaniment,
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
			var accompaniment = PatchOptional(body?.Accompaniment, AccompanimentTooLongMessage, out var accompanimentError);
			if (accompanimentError is not null)
				return accompanimentError;
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
			if (body?.Accompaniment is not null)
				arrangement.Accompaniment = accompaniment;
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
			.Include(s => s.AlternateTitles)
			.Include(s => s.Tags)
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
			lyrics = song.Lyrics,
			language = song.Language,
			occasion = song.Occasion,
			tags = song.Tags
				.OrderBy(t => t.Position).ThenBy(t => t.Id)
				.Select(t => t.Value),
			alternateTitles = song.AlternateTitles
				.OrderBy(t => t.Position).ThenBy(t => t.Id)
				.Select(t => t.Value),
			arrangements = song.Arrangements.OrderBy(a => a.Id).Select(a => new
			{
				id = a.Id,
				label = a.Label,
				arranger = a.Arranger,
				voiceConfiguration = a.VoiceConfiguration,
				accompaniment = a.Accompaniment,
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

	/// <summary>
	/// Validates a string list replacement (ARC-020 alternate titles,
	/// ARC-023 tags): entries are trimmed, must be non-empty and within the
	/// per-entry maximum, at most 10 entries. Returns the German
	/// ProblemDetails error or null when valid.
	/// </summary>
	private static IResult? ValidateEntryList(List<string?>? entries,
		string tooManyTitle, string emptyTitle, string tooLongTitle, int maxLength)
	{
		if (entries is null)
			return null;
		if (entries.Count > 10)
			return Results.Problem(statusCode: 400, title: tooManyTitle);
		foreach (var entry in entries)
		{
			var trimmed = entry?.Trim();
			if (string.IsNullOrEmpty(trimmed))
				return Results.Problem(statusCode: 400, title: emptyTitle);
			if (trimmed.Length > maxLength)
				return Results.Problem(statusCode: 400, title: tooLongTitle);
		}
		return null;
	}

	/// <summary>
	/// Loads the arrangement summaries (id, label, arranger ordered by id) for
	/// the given song ids into a per-song map for list/search responses.
	/// </summary>
	private static async Task<Dictionary<Guid, List<ArrangementSummary>>> LoadArrangementSummariesAsync(
		ArchiveDbContext db, List<Guid> songIds, CancellationToken token)
	{
		if (songIds.Count == 0)
			return [];
		var arrangements = await db.Arrangements.AsNoTracking()
			.Where(a => songIds.Contains(a.SongId))
			.OrderBy(a => a.Id)
			.Select(a => new { a.SongId, a.Id, a.Label, a.Arranger })
			.ToListAsync(token);
		return arrangements
			.GroupBy(a => a.SongId)
			.ToDictionary(
				g => g.Key,
				g => g
					.Select(a => new ArrangementSummary(a.Id, a.Label, a.Arranger))
					.ToList());
	}

	/// <summary>
	/// Loads the alternate titles (values in entry position, id as tiebreak)
	/// for the given song ids into a per-song map for list/search responses.
	/// </summary>
	private static async Task<Dictionary<Guid, List<string>>> LoadAlternateTitlesAsync(
		ArchiveDbContext db, List<Guid> songIds, CancellationToken token)
	{
		if (songIds.Count == 0)
			return [];
		var titles = await db.SongTitles.AsNoTracking()
			.Where(t => songIds.Contains(t.SongId))
			.OrderBy(t => t.SongId).ThenBy(t => t.Position).ThenBy(t => t.Id)
			.Select(t => new { t.SongId, t.Value })
			.ToListAsync(token);
		return titles
			.GroupBy(t => t.SongId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(t => t.Value).ToList());
	}

	/// <summary>
	/// Loads the tags (values in entry position, id as tiebreak) for the given
	/// song ids into a per-song map for the repertoire filter evaluation.
	/// </summary>
	private static async Task<Dictionary<Guid, List<string>>> LoadSongTagsAsync(
		ArchiveDbContext db, List<Guid> songIds, CancellationToken token)
	{
		if (songIds.Count == 0)
			return [];
		var tags = await db.SongTags.AsNoTracking()
			.Where(t => songIds.Contains(t.SongId))
			.OrderBy(t => t.SongId).ThenBy(t => t.Position).ThenBy(t => t.Id)
			.Select(t => new { t.SongId, t.Value })
			.ToListAsync(token);
		return tags
			.GroupBy(t => t.SongId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(t => t.Value).ToList());
	}

	/// <summary>
	/// Pure ARC-020 tokenized AND search over the member-visible set: rank
	/// then Id order, the paged matches plus the page's response maps.
	/// </summary>
	private sealed record SearchResult(
		int Total,
		List<(SearchRow Song, int Rank, SortedSet<string> MatchedIn)> PageItems,
		string Query,
		Dictionary<Guid, List<string>> AlternateTitles,
		Dictionary<Guid, List<ArrangementSummary>> Arrangements);

	private static async Task<SearchResult> SearchSongsAsync(
		ArchiveDbContext db, bool isEditor, string query, int requestedPage, int pageSize, CancellationToken token)
	{
		// Tokenized AND search over one database-projected visible set;
		// matching runs in C# so InMemory tests and PostgreSQL agree.
		var queryFold = CatalogueText.Fold(query);
		var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(CatalogueText.Fold)
			.Where(t => t.Length > 0)
			.ToList();
		if (tokens.Count == 0)
			return new(0, [], query, [], []);
		var visible = await QueryVisible(db.Songs, isEditor)
			.OrderBy(s => s.Id)
			.Select(s => new SearchRow(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt, s.Lyrics))
			.ToListAsync(token);
		var visibleIds = visible.Select(s => s.Id).ToList();
		var alternateTitleMap = await LoadAlternateTitlesAsync(db, visibleIds, token);
		var arrangementRows = await db.Arrangements.AsNoTracking()
			.Where(a => visibleIds.Contains(a.SongId))
			.OrderBy(a => a.Id)
			.Select(a => new { a.Id, a.SongId, a.Label, a.Arranger })
			.ToListAsync(token);
		var arrangementMap = arrangementRows
			.GroupBy(a => a.SongId)
			.ToDictionary(g => g.Key, g => g.ToList());
		var matches = new List<(SearchRow Song, int Rank, SortedSet<string> MatchedIn)>();
		foreach (var song in visible)
		{
			var matchedIn = new SortedSet<string>();
			var titleFold = CatalogueText.Fold(song.Title);
			var titles = alternateTitleMap.TryGetValue(song.Id, out var titles_)
				? titles_.Select(CatalogueText.Fold).ToList()
				: [];
			var composerFold = CatalogueText.Fold(song.Composer);
			var lyricistFold = CatalogueText.Fold(song.Lyricist);
			var lyricsFold = CatalogueText.Fold(song.Lyrics);
			var arrangementTexts = arrangementMap.TryGetValue(song.Id, out var arrangements_)
				? arrangements_.SelectMany(a => new[] { CatalogueText.Fold(a.Label), CatalogueText.Fold(a.Arranger) }).ToList()
				: [];
			// AND semantics: every token must occur (substring) in at least
			// one of the searchable fields; matchedIn lists every field a
			// token hit, sorted alphabetically.
			var allFound = true;
			foreach (var current in tokens)
			{
				var found = false;
				if (titleFold.Contains(current))
				{
					found = true;
					matchedIn.Add("title");
				}
				if (titles.Any(t => t.Contains(current)))
				{
					found = true;
					matchedIn.Add("alternateTitles");
				}
				if (composerFold.Contains(current))
				{
					found = true;
					matchedIn.Add("composer");
				}
				if (lyricistFold.Contains(current))
				{
					found = true;
					matchedIn.Add("lyricist");
				}
				if (lyricsFold.Contains(current))
				{
					found = true;
					matchedIn.Add("lyrics");
				}
				if (arrangementTexts.Any(t => t.Contains(current)))
				{
					found = true;
					matchedIn.Add("arrangements");
				}
				if (!found)
				{
					allFound = false;
					break;
				}
			}
			if (!allFound)
				continue;
			var rank = titleFold == queryFold ? 0
				: tokens.All(t => titleFold.Contains(t)) ? 1
				: 2;
			matches.Add((song, rank, matchedIn));
		}
		var ordered = matches
			.OrderBy(m => m.Rank)
			.ThenBy(m => m.Song.Id)
			.ToList();
		var pageItems = ordered
			.Skip((requestedPage - 1) * pageSize)
			.Take(pageSize)
			.ToList();
		var pageIds = pageItems.Select(m => m.Song.Id).ToList();
		var pageArrangements = await LoadArrangementSummariesAsync(db, pageIds, token);
		var pageAlternateTitles = await LoadAlternateTitlesAsync(db, pageIds, token);
		return new(ordered.Count, pageItems, query, pageAlternateTitles, pageArrangements);
	}

	/// <summary>
	/// Parses and validates the ARC-023 filter query parameters: text filters
	/// are trimmed (empty = absent) and fold at match time; the material list
	/// is comma-separated, trimmed, lowercased, deduplicated and restricted to
	/// the recognized material types. Returns null with an error result for
	/// invalid input.
	/// </summary>
	private static (RepertoireFilter? Filter, IResult? Error) ParseRepertoireFilter(
		string? voiceConfiguration, string? accompaniment, string? musicalKey,
		string? language, string? occasion, string? tag, string? material)
	{
		string? ParseText(string? raw)
		{
			var trimmed = raw?.Trim();
			return string.IsNullOrEmpty(trimmed) ? null : trimmed;
		}
		var voice = ParseText(voiceConfiguration);
		var arrangement = ParseText(accompaniment);
		var key = ParseText(musicalKey);
		var songLanguage = ParseText(language);
		var songOccasion = ParseText(occasion);
		var songTag = ParseText(tag);
		if (voice is { Length: > 200 } || arrangement is { Length: > 200 } || key is { Length: > 200 }
			|| songLanguage is { Length: > 200 } || songOccasion is { Length: > 200 } || songTag is { Length: > 200 })
			return (null, Results.Problem(statusCode: 400, title: FilterTooLongMessage));
		var materials = new List<string>();
		var materialParam = ParseText(material);
		if (materialParam is not null)
		{
			if (materialParam.Length > 200)
				return (null, Results.Problem(statusCode: 400, title: FilterTooLongMessage));
			var entries = materialParam.Split(',').Select(entry => entry.Trim()).ToList();
			if (entries.Any(entry => entry.Length == 0))
				return (null, Results.Problem(statusCode: 400, title: UnknownMaterialFilterMessage));
			materials = entries
				.Select(entry => entry.ToLowerInvariant())
				.Distinct()
				.OrderBy(entry => entry, StringComparer.Ordinal)
				.ToList();
			if (materials.Any(entry => !RepertoireFilter.KnownMaterials.Contains(entry)))
				return (null, Results.Problem(statusCode: 400, title: UnknownMaterialFilterMessage));
		}
		var filter = new RepertoireFilter(voice, arrangement, key, songLanguage, songOccasion, songTag, materials);
		return (filter.HasConditions ? filter : null, null);
	}

	/// <summary>Top-level filters echo (absent conditions are null; materials sorted unique, empty without a material filter).</summary>
	private static object FilterEcho(RepertoireFilter? filter) => new
	{
		voiceConfiguration = filter?.VoiceConfiguration,
		accompaniment = filter?.Accompaniment,
		musicalKey = filter?.MusicalKey,
		language = filter?.Language,
		occasion = filter?.Occasion,
		tag = filter?.Tag,
		materials = filter?.Materials ?? (IReadOnlyList<string>)[],
	};

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

	/// <summary>Arrangement context attached to list/search song items (ARC-020).</summary>
	private sealed record ArrangementSummary(Guid Id, string Label, string? Arranger);

	/// <summary>Database-projected row for C#-side catalogue search (ARC-020).</summary>
	private sealed record SearchRow(Guid Id, string Title, string? Composer, string? Lyricist,
		DateTimeOffset? PublishedAt, string? Lyrics);

	/// <summary>Database-projected row for C#-side repertoire filtering (ARC-023).</summary>
	private sealed record FilterRow(Guid Id, string Title, string? Composer, string? Lyricist,
		DateTimeOffset? PublishedAt, string? Lyrics, string? Language, string? Occasion);

	/// <summary>
	/// Shared list/search song item shape: the ARC-013 summary fields plus the
	/// ARC-020 search fields (matched-in keys empty without a query, lyric
	/// snippet reserved for ARC-033) and the ARC-023 matched-arrangement hints.
	/// </summary>
	private static object SongItem(Guid id, string title, string? composer, string? lyricist,
		DateTimeOffset? publishedAt, List<string> alternateTitles,
		List<ArrangementSummary> arrangements, string[] matchedIn,
		List<ArrangementSummary> matchedArrangements) => new
	{
		id,
		title,
		composer,
		lyricist,
		published = publishedAt is not null,
		publishedAt,
		alternateTitles,
		arrangements,
		matchedIn,
		matchedArrangements,
		lyricsSnippet = (string?)null,
	};
}
