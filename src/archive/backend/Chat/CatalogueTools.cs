using System.ComponentModel;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Archive.Backend.Chat;

/// <summary>
/// The chat tool allow-list (ARC-021 first slice): authorized retrieval
/// functions only, built per request so the delegates capture the scoped
/// <see cref="ArchiveDbContext"/>. Authorization is
/// enforced INSIDE every tool — results are restricted to member-visible
/// (published) songs exactly like the member catalogue API, so drafts and
/// unpublished records can never reach the model or the thread content. No
/// editing tools, no web access.
/// </summary>
public static class CatalogueTools
{
	public const string SearchToolName = "catalogue_search";

	public const string DetailsToolName = "song_details";

	private const int MaxQueryChars = 200;

	private const int MaxPage = 5;

	private const int PageSize = 10;

	private const int ExcerptChars = 300;

	/// <summary>Sung text handed to the model per score and per song.</summary>
	private const int ScoreTextChars = 1500;

	private const int ScoreTextBudget = 6000;

	private const string SearchToolDescription =
		"Sucht im veröffentlichten Liedverzeichnis nach Titeln, Komponisten, Textdichtern und Liedtexten. "
		+ "Ein leerer Suchtext listet vorhandene Lieder auf. Liefert höchstens 10 Lieder pro Seite, "
		+ "totalCount, nextPage und limitReached; höchstens 5 Seiten. Nur zurückgegebene Lieder zitieren.";

	/// <summary>Creates the bounded catalogue search tool for one chat run.</summary>
	public static AITool CreateCatalogueSearchTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("Suchtext; leer (\"\") für eine Übersicht aller Lieder. Sonst müssen alle Begriffe im Titel, in einem anderen Titel, beim Komponisten, Textdichter oder im Liedtext vorkommen.")] string query,
				[Description("Seitennummer ab 1, höchstens 5; für weitere Treffer nextPage aus dem Ergebnis verwenden.")] int page = 1,
				CancellationToken cancellationToken = default) => await SearchAsync(db, query, page, cancellationToken),
			name: SearchToolName,
			description: SearchToolDescription);

	/// <summary>Creates the single-song lookup tool for one chat run.</summary>
	public static AITool CreateSongDetailsTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("ID des gesuchten Liedes aus dem Liedverzeichnis.")] string songId) => await DetailsAsync(db, songId),
			name: DetailsToolName,
			description: "Liest ein einzelnes veröffentlichtes Lied aus dem Verzeichnis, mit Arrangements, Fassungen und "
				+ "Material. Zu Noten liefert scoreFacts die aus dem PDF gelesenen Angaben (Stimme, Tonart, Taktart, "
				+ "Tempo, Urheber) und scoreText den gesungenen Text aus den aktuellen Noten.");

	private static async Task<string> SearchAsync(ArchiveDbContext db, string query, int page, CancellationToken token)
	{
		// Same folding and tokenized AND semantics as the member catalogue
		// search; bounds are enforced inside the tool so the model cannot
		// push past them.
		var queryText = (query ?? string.Empty).Trim();
		if (queryText.Length > MaxQueryChars)
			queryText = queryText[..MaxQueryChars];
		var requestedPage = Math.Max(1, page);
		if (requestedPage > MaxPage)
			return JsonSerializer.Serialize(new { songs = Array.Empty<object>(), error = "Höchstens 5 Seiten abrufbar. Bitte die Suche eingrenzen.", limitReached = true });
		var tokens = queryText
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(CatalogueText.Fold)
			.Where(t => t.Length > 0)
			.ToList();
		if (tokens.Count == 0)
		{
			// Browsing is a bounded database page, not an empty search result.
			// Count and page both apply the member-visible predicate before reading data.
			var catalogue = db.Songs.AsNoTracking().Where(s => s.PublishedAt != null);
			var total = await catalogue.CountAsync(token);
			var songs = await catalogue.OrderBy(s => s.Title).ThenBy(s => s.Id)
				.Skip((requestedPage - 1) * PageSize).Take(PageSize)
				.Select(s => new SearchRow(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt, s.Lyrics))
				.ToListAsync(token);
			return SerializeSearch(songs, requestedPage, total);
		}

		var visible = await db.Songs.AsNoTracking()
			.Where(s => s.PublishedAt != null)
			.OrderBy(s => s.Id)
			.Select(s => new SearchRow(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt, s.Lyrics))
			.ToListAsync(token);
		var visibleIds = visible.Select(s => s.Id).ToList();
		var alternateTitles = await db.SongTitles.AsNoTracking()
			.Where(t => visibleIds.Contains(t.SongId))
			.OrderBy(t => t.SongId).ThenBy(t => t.Position).ThenBy(t => t.Id)
			.Select(t => new { t.SongId, t.Value })
			.ToListAsync(token);
		var titlesBySong = alternateTitles
			.GroupBy(t => t.SongId)
			.ToDictionary(g => g.Key, g => g.Select(t => t.Value).ToList());

		var queryFold = CatalogueText.Fold(queryText);
		var matches = new List<(SearchRow Song, int Rank)>();
		foreach (var song in visible)
		{
			var titleFold = CatalogueText.Fold(song.Title);
			var titles = titlesBySong.TryGetValue(song.Id, out var titles_) ? titles_.Select(CatalogueText.Fold).ToList() : [];
			var composerFold = CatalogueText.Fold(song.Composer);
			var lyricistFold = CatalogueText.Fold(song.Lyricist);
			var lyricsFold = CatalogueText.Fold(song.Lyrics);
			if (!tokens.All(t => titleFold.Contains(t) || titles.Any(x => CatalogueText.Fold(x).Contains(t))
				|| composerFold.Contains(t) || lyricistFold.Contains(t) || lyricsFold.Contains(t)))
				continue;
			var rank = titleFold == queryFold ? 0
				: tokens.All(t => titleFold.Contains(t)) ? 1
				: 2;
			matches.Add((song, rank));
		}
		var ordered = matches
			.OrderBy(m => m.Rank)
			.ThenBy(m => m.Song.Id)
			.Skip((requestedPage - 1) * PageSize)
			.Take(PageSize)
			.ToList();
		return SerializeSearch(ordered.Select(m => m.Song), requestedPage, matches.Count);
	}

	private static async Task<string> DetailsAsync(ArchiveDbContext db, string songId)
	{
		if (!Guid.TryParse(songId, out var id))
			return "{}";
		// Invisible or unknown ids return the same empty payload: the chat
		// surface never distinguishes drafts from nonexistent songs.
		var song = await db.Songs.AsNoTracking()
			.Where(s => s.Id == id && s.PublishedAt != null)
			.Select(s => new SearchRow(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt, s.Lyrics))
			.FirstOrDefaultAsync();
		if (song is null)
			return "{}";
		return JsonSerializer.Serialize(new
		{
			songs = SongPayload([song]),
			arrangements = await ArrangementPayloadAsync(db, id),
		});
	}

	/// <summary>
	/// Arrangements, versions and current material of one published song.
	/// Scores carry the facts and sung text read from their current
	/// revision's extraction; the text is bounded per score and per song, and
	/// identical text (the same lyrics in every voice part) is sent once.
	/// </summary>
	private static async Task<List<object>> ArrangementPayloadAsync(ArchiveDbContext db, Guid songId)
	{
		var arrangements = await db.Arrangements.AsNoTracking()
			.Include(a => a.MusicalVersions)
			.Where(a => a.SongId == songId)
			.OrderBy(a => a.Id)
			.ToListAsync();
		var versionIds = arrangements.SelectMany(a => a.MusicalVersions).Select(v => v.Id).ToList();
		var assets = await db.Assets.AsNoTracking()
			.Where(a => a.MusicalVersionId != null && versionIds.Contains(a.MusicalVersionId.Value)
				&& a.CurrentRevisionId != null)
			.OrderBy(a => a.Id)
			.ToListAsync();
		var revisionIds = assets
			.Where(a => a.AssetType == AssetEndpoints.ScoreAssetType)
			.Select(a => a.CurrentRevisionId!.Value)
			.ToList();
		var texts = await db.ExtractionJobs.AsNoTracking()
			.Where(j => revisionIds.Contains(j.RevisionId) && j.Status == ExtractionStatus.Completed)
			.Select(j => new { j.RevisionId, j.Text })
			.ToListAsync();
		var analyses = texts.ToDictionary(t => t.RevisionId, t => ScoreTextAnalyzer.Analyze(t.Text));
		var budget = ScoreTextBudget;
		var sent = new HashSet<string>();
		return arrangements.Select(a => (object)new
		{
			label = a.Label,
			arranger = a.Arranger,
			voiceConfiguration = a.VoiceConfiguration,
			accompaniment = a.Accompaniment,
			versions = a.MusicalVersions.OrderBy(v => v.Id).Select(v => new
			{
				label = v.Label,
				creator = v.Creator,
				musicalKey = v.MusicalKey,
				material = assets.Where(asset => asset.MusicalVersionId == v.Id).Select(asset =>
				{
					var analysis = analyses.GetValueOrDefault(asset.CurrentRevisionId!.Value);
					string? scoreText = null;
					if (analysis is { CleanText.Length: > 0 } && budget > 0 && sent.Add(analysis.CleanText))
					{
						var length = Math.Min(Math.Min(analysis.CleanText.Length, ScoreTextChars), budget);
						scoreText = analysis.CleanText[..length];
						budget -= length;
					}
					return new
					{
						type = asset.AssetType,
						voice = asset.VoiceLabel ?? analysis?.Facts.Voice,
						description = asset.Description,
						scoreFacts = ExtractionEndpoints.FactsPayload(analysis?.Facts),
						scoreText,
					};
				}).ToList(),
			}).ToList(),
		}).ToList();
	}

	private static string SerializeSearch(IEnumerable<SearchRow> songs, int page, int totalCount) => JsonSerializer.Serialize(new
	{
		songs = SongPayload(songs),
		page,
		pageSize = PageSize,
		totalCount,
		hasMore = page * PageSize < totalCount,
		nextPage = page < MaxPage && page * PageSize < totalCount ? (int?)(page + 1) : null,
		limitReached = page == MaxPage && page * PageSize < totalCount,
	});

	private static IEnumerable<object> SongPayload(IEnumerable<SearchRow> songs) => songs.Select(s => new
		{
			id = s.Id,
			title = s.Title,
			composer = s.Composer,
			lyricist = s.Lyricist,
			publishedYear = s.PublishedAt?.Year,
			lyricsExcerpt = s.Lyrics is { Length: > 0 }
				? (s.Lyrics.Length > ExcerptChars ? s.Lyrics[..ExcerptChars] : s.Lyrics)
				: null,
		});

	private sealed record SearchRow(Guid Id, string Title, string? Composer, string? Lyricist,
		DateTimeOffset? PublishedAt, string? Lyrics);
}
