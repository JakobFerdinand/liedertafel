using System.ComponentModel;
using System.Text.Json;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
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

	private const string SearchToolDescription =
		"Sucht im veröffentlichten Liedverzeichnis des Vereins nach Titeln, Komponisten, Textdichtern und Liedtexten.";

	/// <summary>Creates the bounded catalogue search tool for one chat run.</summary>
	public static AITool CreateCatalogueSearchTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("Suchtext; alle Begriffe müssen im Titel, in einem anderen Titel, beim Komponisten, Textdichter oder im Liedtext vorkommen.")] string query,
				[Description("Seitennummer ab 1, höchstens 5.")] int page = 1) => await SearchAsync(db, query, page),
			name: SearchToolName,
			description: SearchToolDescription);

	/// <summary>Creates the single-song lookup tool for one chat run.</summary>
	public static AITool CreateSongDetailsTool(ArchiveDbContext db) =>
		AIFunctionFactory.Create(
			async ([Description("ID des gesuchten Liedes aus dem Liedverzeichnis.")] string songId) => await DetailsAsync(db, songId),
			name: DetailsToolName,
			description: "Liest ein einzelnes veröffentlichtes Lied aus dem Verzeichnis.");

	private static async Task<string> SearchAsync(ArchiveDbContext db, string query, int page)
	{
		// Same folding and tokenized AND semantics as the member catalogue
		// search; bounds are enforced inside the tool so the model cannot
		// push past them.
		var queryText = (query ?? string.Empty).Trim();
		if (queryText.Length > MaxQueryChars)
			queryText = queryText[..MaxQueryChars];
		var requestedPage = page < 1 ? 1 : Math.Min(page, MaxPage);
		var tokens = queryText
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(CatalogueText.Fold)
			.Where(t => t.Length > 0)
			.ToList();
		if (tokens.Count == 0)
			return SerializeSongs([]);

		var visible = await db.Songs.AsNoTracking()
			.Where(s => s.PublishedAt != null)
			.OrderBy(s => s.Id)
			.Select(s => new SearchRow(s.Id, s.Title, s.Composer, s.Lyricist, s.PublishedAt, s.Lyrics))
			.ToListAsync();
		var visibleIds = visible.Select(s => s.Id).ToList();
		var alternateTitles = await db.SongTitles.AsNoTracking()
			.Where(t => visibleIds.Contains(t.SongId))
			.OrderBy(t => t.SongId).ThenBy(t => t.Position).ThenBy(t => t.Id)
			.Select(t => new { t.SongId, t.Value })
			.ToListAsync();
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
		return SerializeSongs(ordered.Select(m => m.Song));
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
		return SerializeSongs([song]);
	}

	private static string SerializeSongs(IEnumerable<SearchRow> songs) => JsonSerializer.Serialize(new
	{
		songs = songs.Select(s => new
		{
			id = s.Id,
			title = s.Title,
			composer = s.Composer,
			lyricist = s.Lyricist,
			publishedYear = s.PublishedAt?.Year,
			lyricsExcerpt = s.Lyrics is { Length: > 0 }
				? (s.Lyrics.Length > ExcerptChars ? s.Lyrics[..ExcerptChars] : s.Lyrics)
				: null,
		}),
	});

	private sealed record SearchRow(Guid Id, string Title, string? Composer, string? Lyricist,
		DateTimeOffset? PublishedAt, string? Lyrics);
}
