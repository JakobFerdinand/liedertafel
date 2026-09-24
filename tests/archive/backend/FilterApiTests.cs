using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-023 repertoire filters: song-level language/occasion/tag filters,
/// arrangement-level voice-configuration/accompaniment filters and
/// version-level key/material conditions that must hold on one matching
/// arrangement/version (never on siblings that collectively qualify), with
/// German folding, draft protection, paging and the matched-arrangement echo.
/// </summary>
public sealed class FilterApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task TwoArrangementsSatisfyingDifferentFiltersDoNotCombineIntoAMatch()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreatePublishedSongAsync(factory, client, editorSession, "Doppelfassung");
		// Arrangement A (Standardfassung): SATB with an Es-Dur version.
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementAId = Guid.Parse(detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var versionAId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		using (var patchVoice = await PatchJsonAsync(client, $"/api/arrangements/{arrangementAId}",
			new { voiceConfiguration = "SATB" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, patchVoice.StatusCode);
		}
		using (var patchKey = await PatchJsonAsync(client, $"/api/musical-versions/{versionAId}",
			new { musicalKey = "Es-Dur" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, patchKey.StatusCode);
		}
		await FinalizeAssetAsync(factory, client, editorSession, versionAId, AssetEndpoints.ScoreAssetType);
		// Arrangement B: TTBB with an F-Dur version.
		var arrangementBId = await AddArrangementAsync(client, editorSession, songId, "Männerchor", voiceConfiguration: "TTBB");
		var versionBId = await AddVersionAsync(client, editorSession, arrangementBId, "B-Ausgabe", musicalKey: "F-Dur");
		await FinalizeAssetAsync(factory, client, editorSession, versionBId, AssetEndpoints.ScoreAssetType);
		var memberSession = await SignInAsync(factory, Member);

		// Each arrangement-level filter alone matches the song with its own
		// arrangement as the match hint.
		var (byVoice, _) = await ListAsync(client, memberSession, ("voiceConfiguration", "satb"));
		Assert.Equal(1, byVoice.GetProperty("total").GetInt32());
		Assert.Equal([arrangementAId.ToString()], MatchedArrangementIds(byVoice));
		var (byOtherVoice, _) = await ListAsync(client, memberSession, ("voiceConfiguration", "ttbb"));
		Assert.Equal(1, byOtherVoice.GetProperty("total").GetInt32());
		Assert.Equal([arrangementBId.ToString()], MatchedArrangementIds(byOtherVoice));
		var (byKey, _) = await ListAsync(client, memberSession, ("musicalKey", "es-dur"));
		Assert.Equal(1, byKey.GetProperty("total").GetInt32());
		Assert.Equal([arrangementAId.ToString()], MatchedArrangementIds(byKey));

		// The combination of the siblings' conditions matches nothing: no
		// single arrangement carries SATB and the F-Dur key together.
		var (combined, _) = await ListAsync(client, memberSession,
			("voiceConfiguration", "satb"), ("musicalKey", "f-dur"));
		Assert.Equal(0, combined.GetProperty("total").GetInt32());
		Assert.Empty(combined.GetProperty("songs").EnumerateArray());
	}

	[Fact]
	public async Task KeyAndMaterialMustHoldOnTheSameVersion()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreatePublishedSongAsync(factory, client, editorSession, "Tonartenprüfung");
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var keyVersionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		using (var patchKey = await PatchJsonAsync(client, $"/api/musical-versions/{keyVersionId}",
			new { musicalKey = "Es-Dur" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, patchKey.StatusCode);
		}
		await FinalizeAssetAsync(factory, client, editorSession, keyVersionId, AssetEndpoints.AudioAssetType);
		var otherVersionId = await AddVersionAsync(client, editorSession, arrangementId, "Andere Tonart", musicalKey: "F-Dur");
		await FinalizeAssetAsync(factory, client, editorSession, otherVersionId, AssetEndpoints.ScoreAssetType);
		var memberSession = await SignInAsync(factory, Member);

		// Score only exists on the other-key version: no match.
		var (keyWithScore, _) = await ListAsync(client, memberSession, ("musicalKey", "es-dur"), ("material", "score"));
		Assert.Equal(0, keyWithScore.GetProperty("total").GetInt32());

		// Audio exists on the key version: match.
		var (keyWithAudio, _) = await ListAsync(client, memberSession, ("musicalKey", "es-dur"), ("material", "audio"));
		Assert.Equal(1, keyWithAudio.GetProperty("total").GetInt32());
		Assert.Equal(songId.ToString(), FirstSong(keyWithAudio).GetProperty("id").GetString());

		// Score on the F-Dur version: match.
		var (otherKeyWithScore, _) = await ListAsync(client, memberSession, ("musicalKey", "f-dur"), ("material", "score"));
		Assert.Equal(1, otherKeyWithScore.GetProperty("total").GetInt32());

		// Without a key filter every material type may sit on a different
		// version of the same arrangement.
		var (materialsAcrossVersions, _) = await ListAsync(client, memberSession, ("material", "score,audio"));
		Assert.Equal(1, materialsAcrossVersions.GetProperty("total").GetInt32());

		// With the key filter both materials must share one version: none does.
		var (materialsOnKeyVersion, _) = await ListAsync(client, memberSession,
			("musicalKey", "es-dur"), ("material", "score,audio"));
		Assert.Equal(0, materialsOnKeyVersion.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task MaterialTypesMatchOnCurrentAssetsAndCombine()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreatePublishedSongAsync(factory, client, editorSession, "Materialmix");
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		await FinalizeAssetAsync(factory, client, editorSession, versionId, AssetEndpoints.ScoreAssetType);
		await FinalizeAssetAsync(factory, client, editorSession, versionId, AssetEndpoints.AudioAssetType);
		await FinalizeAssetAsync(factory, client, editorSession, versionId, AssetEndpoints.MidiAssetType);
		var memberSession = await SignInAsync(factory, Member);

		foreach (var material in new[] { "score", "audio", "midi" })
		{
			var (body, _) = await ListAsync(client, memberSession, ("material", material));
			Assert.Equal(1, body.GetProperty("total").GetInt32());
		}

		// Multiple material types combine on the same arrangement, normalized
		// case-insensitively and echoed sorted and unique.
		var (combined, response) = await ListAsync(client, memberSession, ("material", "Audio,SCORE,audio"));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(1, combined.GetProperty("total").GetInt32());
		Assert.Equal(songId.ToString(), FirstSong(combined).GetProperty("id").GetString());
		var filters = combined.GetProperty("filters");
		Assert.Equal(["audio", "score"], filters.GetProperty("materials").EnumerateArray()
			.Select(e => e.GetString()).ToList());
	}

	[Fact]
	public async Task AssetsWithoutCurrentRevisionNeverCountAsMaterial()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreateSongAsync(factory, client, editorSession, "Materialfreigabe");
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);

		// Pending score asset without any upload: never counts.
		var scoreAssetId = await CreateAssetAsync(client, editorSession, versionId, AssetEndpoints.ScoreAssetType);
		await FinalizeAssetUploadAsync(factory, client, editorSession, await CreateAssetAsync(client, editorSession, versionId, AssetEndpoints.AudioAssetType), AssetEndpoints.AudioAssetType);
		var (noScore, _) = await ListAsync(client, editorSession, ("material", "score"));
		Assert.Equal(0, noScore.GetProperty("total").GetInt32());
		var (audioOnly, _) = await ListAsync(client, editorSession, ("material", "audio"));
		Assert.Equal(1, audioOnly.GetProperty("total").GetInt32());

		// A finalized upload makes the asset count.
		await FinalizeAssetUploadAsync(factory, client, editorSession, scoreAssetId, AssetEndpoints.ScoreAssetType);
		var (withScore, _) = await ListAsync(client, editorSession, ("material", "score"));
		Assert.Equal(1, withScore.GetProperty("total").GetInt32());
		var (both, _) = await ListAsync(client, editorSession, ("material", "score,audio"));
		Assert.Equal(1, both.GetProperty("total").GetInt32());

		// A withdrawn current pointer (revisions remain, pointer cleared):
		// revisions alone never count.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.SingleAsync(a => a.Id == scoreAssetId);
			asset.CurrentRevisionId = null;
			await db.SaveChangesAsync();
		}
		var (withdrawn, _) = await ListAsync(client, editorSession, ("material", "score"));
		Assert.Equal(0, withdrawn.GetProperty("total").GetInt32());
		var (mixedWithoutScore, _) = await ListAsync(client, editorSession, ("material", "score,audio"));
		Assert.Equal(0, mixedWithoutScore.GetProperty("total").GetInt32());

		// Restoring the pointer counts the asset again.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var asset = await db.Assets.Include(a => a.Revisions).SingleAsync(a => a.Id == scoreAssetId);
			asset.CurrentRevisionId = asset.Revisions.Single().Id;
			await db.SaveChangesAsync();
		}
		var (restored, _) = await ListAsync(client, editorSession, ("material", "score,audio"));
		Assert.Equal(1, restored.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task SongLevelFiltersMatchExplicitlyAndLeaveUnknownsUnmatched()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var germanSong = await CreatePublishedSongAsync(factory, client, editorSession, "Deutsches Weihnachtslied",
			language: "Deutsch", occasion: "Weihnachten", tags: ["Choral", "Tradition"]);
		var englishSong = await CreatePublishedSongAsync(factory, client, editorSession, "Englisches Weihnachtslied",
			language: "Englisch", occasion: "Weihnachten");
		// Third song with all unknown values (null stays explicit).
		_ = await CreatePublishedSongAsync(factory, client, editorSession, "Ohne Angaben");
		var memberSession = await SignInAsync(factory, Member);

		var (byLanguage, _) = await ListAsync(client, memberSession, ("language", "deutsch"));
		Assert.Equal(1, byLanguage.GetProperty("total").GetInt32());
		Assert.Equal(germanSong.ToString(), FirstSong(byLanguage).GetProperty("id").GetString());

		// The song with null language never matches a language filter.
		var (byOtherLanguage, _) = await ListAsync(client, memberSession, ("language", "englisch"));
		Assert.Equal(1, byOtherLanguage.GetProperty("total").GetInt32());
		Assert.Equal(englishSong.ToString(), FirstSong(byOtherLanguage).GetProperty("id").GetString());

		// Occasion matches folded and case-insensitively; two songs share it.
		var (byOccasion, _) = await ListAsync(client, memberSession, ("occasion", "weihnachten"));
		Assert.Equal(2, byOccasion.GetProperty("total").GetInt32());

		// Song-level filters combine with AND.
		var (combined, _) = await ListAsync(client, memberSession, ("language", "deutsch"), ("occasion", "weihnachten"));
		Assert.Equal(1, combined.GetProperty("total").GetInt32());
		Assert.Equal(germanSong.ToString(), FirstSong(combined).GetProperty("id").GetString());

		// Tags match via the song_tags list.
		var (byTag, _) = await ListAsync(client, memberSession, ("tag", "tradition"));
		Assert.Equal(1, byTag.GetProperty("total").GetInt32());
		Assert.Equal(germanSong.ToString(), FirstSong(byTag).GetProperty("id").GetString());
		var (byUnknownTag, _) = await ListAsync(client, memberSession, ("tag", "unbekannt"));
		Assert.Equal(0, byUnknownTag.GetProperty("total").GetInt32());

		// An empty filter value counts as absent (plain list).
		var (absent, _) = await ListAsync(client, memberSession, ("language", ""));
		Assert.Equal(3, absent.GetProperty("total").GetInt32());
		Assert.True(absent.GetProperty("filters").GetProperty("language").ValueKind is JsonValueKind.Null);
	}

	[Theory]
	[InlineData("language", "turkisch", 1)]
	[InlineData("language", "Türkisch", 1)]
	[InlineData("language", "englisch", 0)]
	[InlineData("occasion", "fruhlingsfest", 1)]
	[InlineData("occasion", "Frühlingsfest", 1)]
	[InlineData("musicalKey", "es-dur", 1)]
	[InlineData("musicalKey", "ES-DUR", 1)]
	[InlineData("musicalKey", "g dur", 0)]
	[InlineData("accompaniment", "klavier", 1)]
	[InlineData("accompaniment", "Klavier", 1)]
	[InlineData("tag", "chore", 1)]
	public async Task GermanFoldEquivalenceInFilters(string parameter, string value, int expectedTotal)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreatePublishedSongAsync(factory, client, editorSession, "Faltungsprüfung",
			language: "Türkisch", occasion: "Frühlingsfest", tags: ["Chöre"]);
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var versionId = Guid.Parse(detail.GetProperty("arrangements")[0]
			.GetProperty("musicalVersions")[0].GetProperty("id").GetString()!);
		using (var patchKey = await PatchJsonAsync(client, $"/api/musical-versions/{versionId}",
			new { musicalKey = "Es-Dur" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, patchKey.StatusCode);
		}
		var arrangementId = Guid.Parse(detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		using (var patchAccompaniment = await PatchJsonAsync(client, $"/api/arrangements/{arrangementId}",
			new { accompaniment = "Klavier" }, editorSession))
		{
			Assert.Equal(HttpStatusCode.OK, patchAccompaniment.StatusCode);
		}
		var memberSession = await SignInAsync(factory, Member);

		var (body, _) = await ListAsync(client, memberSession, (parameter, value));
		Assert.Equal(expectedTotal, body.GetProperty("total").GetInt32());
		if (expectedTotal == 1)
			Assert.Equal(songId.ToString(), FirstSong(body).GetProperty("id").GetString());
	}

	[Fact]
	public async Task TagReplacementRoundTripKeepsPositionOrder()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Schlagwortprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { tags = new[] { "Erstes Schlagwort", "Zweites Schlagwort" } }, $"{cookie}; {editorSession}", token);
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		var patched = (await patchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal(["Erstes Schlagwort", "Zweites Schlagwort"], patched.GetProperty("tags").EnumerateArray()
			.Select(t => t.GetString()).ToList());

		// Re-read detail: entries persist in list order with explicit positions.
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		Assert.Equal(["Erstes Schlagwort", "Zweites Schlagwort"], detail.GetProperty("tags").EnumerateArray()
			.Select(t => t.GetString()).ToList());
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.SongTags.Where(t => t.SongId == songId).OrderBy(t => t.Position).ToListAsync();
			Assert.Equal([0, 1], persisted.Select(t => t.Position).ToList());
			Assert.All(persisted, t => Assert.NotEqual(Guid.Empty, t.CreatedByAccountId));
		}

		// The filter finds the tag.
		var (byTag, _) = await ListAsync(client, editorSession, ("tag", "schlagwort"));
		Assert.Equal(1, byTag.GetProperty("total").GetInt32());

		// Full replacement: one entry, position 0.
		using var replace = AuthedPatch($"/api/songs/{songId}",
			new { tags = new[] { "Neu" } }, $"{cookie}; {editorSession}", token);
		using var replaceResponse = await client.SendAsync(replace);
		Assert.Equal(HttpStatusCode.OK, replaceResponse.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.SongTags.Where(t => t.SongId == songId).ToListAsync();
			var entry = Assert.Single(persisted);
			Assert.Equal(0, entry.Position);
			Assert.Equal("Neu", entry.Value);
		}

		// null tags leaves the list unchanged.
		using var nullPatch = AuthedPatch($"/api/songs/{songId}",
			new { tags = (string[]?)null }, $"{cookie}; {editorSession}", token);
		using var nullPatchResponse = await client.SendAsync(nullPatch);
		Assert.Equal(HttpStatusCode.OK, nullPatchResponse.StatusCode);
		var afterNull = (await nullPatchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal(["Neu"], afterNull.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());

		// Empty array clears the list and the filter stops matching.
		using var clearPatch = AuthedPatch($"/api/songs/{songId}",
			new { tags = Array.Empty<string>() }, $"{cookie}; {editorSession}", token);
		using var clearPatchResponse = await client.SendAsync(clearPatch);
		Assert.Equal(HttpStatusCode.OK, clearPatchResponse.StatusCode);
		var afterClear = (await clearPatchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Empty(afterClear.GetProperty("tags").EnumerateArray());
		var (byClearedTag, _) = await ListAsync(client, editorSession, ("tag", "Neu"));
		Assert.Equal(0, byClearedTag.GetProperty("total").GetInt32());
	}

	[Theory]
	[InlineData("vinyl")]
	[InlineData("recording")]
	[InlineData("score,")]
	[InlineData(",midi")]
	[InlineData("score,recording")]
	public async Task InvalidMaterialFiltersAreRejected(string material)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (body, response) = await ListAsync(client, memberSession, ("material", material));
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(CatalogueEndpoints.UnknownMaterialFilterMessage, body.GetProperty("title").GetString());
	}

	[Theory]
	[InlineData("voiceConfiguration")]
	[InlineData("accompaniment")]
	[InlineData("musicalKey")]
	[InlineData("language")]
	[InlineData("occasion")]
	[InlineData("tag")]
	[InlineData("material")]
	public async Task OverlongFiltersAreRejected(string parameter)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (body, response) = await ListAsync(client, memberSession, (parameter, new string('x', 201)));
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(CatalogueEndpoints.FilterTooLongMessage, body.GetProperty("title").GetString());
	}

	[Fact]
	public async Task DraftsNeverLeakIntoMemberFilters()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		// Draft with distinctive language and tag.
		await CreateSongAsync(factory, client, editorSession, "Geheimes Winterlied",
			language: "Deutsch", occasion: "Winter", tags: ["strenggeheim"]);
		await CreatePublishedSongAsync(factory, client, editorSession, "Öffentliches Sommerlied",
			language: "Deutsch", occasion: "Sommer");
		var memberSession = await SignInAsync(factory, Member);

		var (memberBody, _) = await ListAsync(client, memberSession, ("language", "deutsch"));
		Assert.Equal(1, memberBody.GetProperty("total").GetInt32());
		Assert.Equal("Öffentliches Sommerlied", FirstSong(memberBody).GetProperty("title").GetString());

		var (memberTagBody, _) = await ListAsync(client, memberSession, ("tag", "strenggeheim"));
		Assert.Equal(0, memberTagBody.GetProperty("total").GetInt32());
		Assert.Empty(memberTagBody.GetProperty("songs").EnumerateArray());

		var (memberOccasionBody, _) = await ListAsync(client, memberSession, ("occasion", "winter"));
		Assert.Equal(0, memberOccasionBody.GetProperty("total").GetInt32());

		// Editors keep the draft view with filters.
		var (editorBody, _) = await ListAsync(client, editorSession, ("language", "deutsch"));
		Assert.Equal(2, editorBody.GetProperty("total").GetInt32());
		var (editorTagBody, _) = await ListAsync(client, editorSession, ("tag", "strenggeheim"));
		Assert.Equal(1, editorTagBody.GetProperty("total").GetInt32());
		Assert.Equal("Geheimes Winterlied", FirstSong(editorTagBody).GetProperty("title").GetString());
	}

	[Fact]
	public async Task CombinedQueryAndFiltersIntersect()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var latinSong = await CreatePublishedSongAsync(factory, client, editorSession, "Weihnachtsoratorium",
			language: "Latein", occasion: "Weihnachten");
		_ = await CreatePublishedSongAsync(factory, client, editorSession, "Weihnachtslied",
			language: "Deutsch", occasion: "Sommer");
		var memberSession = await SignInAsync(factory, Member);

		var (byTitleAndLanguage, _) = await ListAsync(client, memberSession,
			("q", "weihnachts"), ("language", "latein"));
		Assert.Equal(1, byTitleAndLanguage.GetProperty("total").GetInt32());
		var song = FirstSong(byTitleAndLanguage);
		Assert.Equal(latinSong.ToString(), song.GetProperty("id").GetString());
		Assert.Equal(["title"], song.GetProperty("matchedIn").EnumerateArray().Select(e => e.GetString()).ToList());

		// Composer token combined with a contradicting language: no match.
		var (composerGerman, _) = await ListAsync(client, memberSession,
			("q", "oratorium"), ("language", "deutsch"));
		Assert.Equal(0, composerGerman.GetProperty("total").GetInt32());

		// Query plus song-level tag filter.
		var (queryTag, _) = await ListAsync(client, memberSession,
			("q", "weihnachts"), ("occasion", "weihnachten"));
		Assert.Equal(1, queryTag.GetProperty("total").GetInt32());
		Assert.Equal(latinSong.ToString(), FirstSong(queryTag).GetProperty("id").GetString());
	}

	[Fact]
	public async Task PaginationIsDeterministicWithFilters()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var created = new List<Guid>();
		for (var index = 0; index < 21; index++)
		{
			var songId = await CreatePublishedSongAsync(factory, client, editorSession, $"Seitenlied {index:00}",
				language: "Deutsch");
			created.Add(songId);
		}
		_ = await CreatePublishedSongAsync(factory, client, editorSession, "Anderes Seitenlied",
			language: "Englisch");
		var memberSession = await SignInAsync(factory, Member);

		var (page1, response1) = await ListAsync(client, memberSession, ("language", "deutsch"), ("page", "1"));
		Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
		Assert.Equal(21, page1.GetProperty("total").GetInt32());
		Assert.Equal(1, page1.GetProperty("page").GetInt32());
		var page1Songs = page1.GetProperty("songs").EnumerateArray().ToList();
		Assert.Equal(20, page1Songs.Count);
		// Order is the stable Id order.
		var page1Ids = page1Songs.Select(s => Guid.Parse(s.GetProperty("id").GetString()!)).ToList();
		Assert.Equal(page1Ids.OrderBy(id => id).ToList(), page1Ids);

		var (page2, _) = await ListAsync(client, memberSession, ("language", "deutsch"), ("page", "2"));
		Assert.Equal(21, page2.GetProperty("total").GetInt32());
		var page2Songs = page2.GetProperty("songs").EnumerateArray().ToList();
		Assert.Single(page2Songs);
		var page2Ids = page2Songs.Select(s => Guid.Parse(s.GetProperty("id").GetString()!)).ToList();
		Assert.Empty(page1Ids.Intersect(page2Ids));
		Assert.Equal(21, page1Ids.Union(page2Ids).Count());

		// Page beyond the end is empty with the correct total.
		var (page9, _) = await ListAsync(client, memberSession, ("language", "deutsch"), ("page", "9"));
		Assert.Empty(page9.GetProperty("songs").EnumerateArray());
		Assert.Equal(21, page9.GetProperty("total").GetInt32());

		// page=0 clamps to page 1.
		var (page0, _) = await ListAsync(client, memberSession, ("language", "deutsch"), ("page", "0"));
		Assert.Equal(1, page0.GetProperty("page").GetInt32());
		Assert.Equal(20, page0.GetProperty("songs").EnumerateArray().Count());
	}

	[Fact]
	public async Task MatchedArrangementsEchoAllArrangementsWithoutArrangementFilters()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songId = await CreatePublishedSongAsync(factory, client, editorSession, "Echoprüfung", language: "Deutsch");
		await AddArrangementAsync(client, editorSession, songId, "Männerchor",
			voiceConfiguration: "TTBB", accompaniment: "Klavier");
		var memberSession = await SignInAsync(factory, Member);
		var detail = await GetSongDetailAsync(client, memberSession, songId);
		var arrangementIds = detail.GetProperty("arrangements").EnumerateArray()
			.Select(a => a.GetProperty("id").GetString()!).ToList();

		// Plain list: filters echo all null and both arrangements are hints.
		var (plain, plainResponse) = await ListAsync(client, memberSession);
		Assert.Equal(HttpStatusCode.OK, plainResponse.StatusCode);
		Assert.True(plainResponse.Headers.CacheControl.NoStore);
		AssertFiltersAbsent(plain);
		var plainSong = FirstSong(plain);
		Assert.Equal(2, plainSong.GetProperty("matchedArrangements").EnumerateArray().Count());
		Assert.Equal(arrangementIds, plainSong.GetProperty("matchedArrangements").EnumerateArray()
			.Select(a => a.GetProperty("id").GetString()!).ToList());
		Assert.Equal("Männerchor", plainSong.GetProperty("matchedArrangements").EnumerateArray().Last()
			.GetProperty("label").GetString());

		// Search branch echoes the same hints without arrangement-level filters.
		var (searched, _) = await ListAsync(client, memberSession, ("q", "echo"));
		AssertFiltersAbsent(searched);
		Assert.Equal(2, FirstSong(searched).GetProperty("matchedArrangements").EnumerateArray().Count());

		// Song-level filter keeps all arrangements as hints.
		var (songFiltered, _) = await ListAsync(client, memberSession, ("language", "deutsch"));
		Assert.Equal("deutsch", songFiltered.GetProperty("filters").GetProperty("language").GetString());
		Assert.Equal(2, FirstSong(songFiltered).GetProperty("matchedArrangements").EnumerateArray().Count());

		// An arrangement-level filter narrows the hints to the matched subset.
		var (voiceFiltered, _) = await ListAsync(client, memberSession, ("voiceConfiguration", "ttbb"));
		var voiceFilters = voiceFiltered.GetProperty("filters");
		Assert.Equal("ttbb", voiceFilters.GetProperty("voiceConfiguration").GetString());
		Assert.True(voiceFilters.GetProperty("musicalKey").ValueKind is JsonValueKind.Null);
		var matched = FirstSong(voiceFiltered).GetProperty("matchedArrangements").EnumerateArray().ToList();
		var hint = Assert.Single(matched);
		Assert.Equal("Männerchor", hint.GetProperty("label").GetString());
		Assert.True(hint.GetProperty("arranger").ValueKind is JsonValueKind.Null);
	}

	private static void AssertFiltersAbsent(JsonElement body)
	{
		var filters = body.GetProperty("filters");
		Assert.True(filters.GetProperty("voiceConfiguration").ValueKind is JsonValueKind.Null);
		Assert.True(filters.GetProperty("accompaniment").ValueKind is JsonValueKind.Null);
		Assert.True(filters.GetProperty("musicalKey").ValueKind is JsonValueKind.Null);
		Assert.True(filters.GetProperty("language").ValueKind is JsonValueKind.Null);
		Assert.True(filters.GetProperty("occasion").ValueKind is JsonValueKind.Null);
		Assert.True(filters.GetProperty("tag").ValueKind is JsonValueKind.Null);
		Assert.Empty(filters.GetProperty("materials").EnumerateArray());
	}

	[Fact]
	public async Task EditorSongFieldsRoundTrip()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Neuaufnahme",
			language: "Deutsch", occasion: "Weihnachten", tags: ["Choral"]);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		var created = await GetSongDetailAsync(client, editorSession, songId);
		Assert.Equal("Deutsch", created.GetProperty("language").GetString());
		Assert.Equal("Weihnachten", created.GetProperty("occasion").GetString());
		Assert.Equal(["Choral"], created.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());
		Assert.True(created.GetProperty("arrangements")[0].GetProperty("accompaniment").ValueKind is JsonValueKind.Null);

		// PATCH single field: others stay unchanged.
		using var patchLanguage = AuthedPatch($"/api/songs/{songId}",
			new { language = "Latein" }, $"{cookie}; {editorSession}", token);
		using var patchLanguageResponse = await client.SendAsync(patchLanguage);
		Assert.Equal(HttpStatusCode.OK, patchLanguageResponse.StatusCode);
		var patched = (await patchLanguageResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal("Latein", patched.GetProperty("language").GetString());
		Assert.Equal("Weihnachten", patched.GetProperty("occasion").GetString());
		Assert.Equal(["Choral"], patched.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());

		// Empty string clears; null leaves unchanged.
		using var clearPatch = AuthedPatch($"/api/songs/{songId}",
			new { language = "", occasion = "" }, $"{cookie}; {editorSession}", token);
		using var clearResponse = await client.SendAsync(clearPatch);
		Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
		var cleared = (await clearResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.True(cleared.GetProperty("language").ValueKind is JsonValueKind.Null);
		Assert.True(cleared.GetProperty("occasion").ValueKind is JsonValueKind.Null);
		Assert.Equal(["Choral"], cleared.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());

		using var setPatch = AuthedPatch($"/api/songs/{songId}",
			new { language = "Deutsch", occasion = "Ostern", tags = new[] { "A", "B" } },
			$"{cookie}; {editorSession}", token);
		using var setResponse = await client.SendAsync(setPatch);
		Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);
		var set = (await setResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal("Deutsch", set.GetProperty("language").GetString());
		Assert.Equal("Ostern", set.GetProperty("occasion").GetString());

		// Explicit nulls leave everything unchanged.
		using var nullPatch = AuthedPatch($"/api/songs/{songId}",
			new { language = (string?)null, occasion = (string?)null, tags = (string[]?)null },
			$"{cookie}; {editorSession}", token);
		using var nullResponse = await client.SendAsync(nullPatch);
		Assert.Equal(HttpStatusCode.OK, nullResponse.StatusCode);
		var unchanged = (await nullResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.Equal("Deutsch", unchanged.GetProperty("language").GetString());
		Assert.Equal("Ostern", unchanged.GetProperty("occasion").GetString());
		Assert.Equal(["A", "B"], unchanged.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());

		// Fresh detail from the database carries the persisted values.
		var persisted = await GetSongDetailAsync(client, editorSession, songId);
		Assert.Equal("Deutsch", persisted.GetProperty("language").GetString());
		Assert.Equal("Ostern", persisted.GetProperty("occasion").GetString());
		Assert.Equal(["A", "B"], persisted.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());
	}

	[Fact]
	public async Task ArrangementAccompanimentRoundTrip()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Begleitungsprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using var create = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Bläserfassung", accompaniment = "Klavier und Bläser" },
			$"{cookie}; {editorSession}", token);
		using var createResponse = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		var createdDetail = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		var createdArrangement = createdDetail.GetProperty("arrangements").EnumerateArray()
			.Single(a => a.GetProperty("label").GetString() == "Bläserfassung");
		Assert.Equal("Klavier und Bläser", createdArrangement.GetProperty("accompaniment").GetString());
		var arrangementId = Guid.Parse(createdArrangement.GetProperty("id").GetString()!);

		// Empty string clears; null leaves unchanged.
		using var clearPatch = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { accompaniment = "" }, $"{cookie}; {editorSession}", token);
		using var clearResponse = await client.SendAsync(clearPatch);
		Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
		var cleared = (await clearResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		Assert.True(cleared.GetProperty("arrangements").EnumerateArray()
			.Single(a => a.GetProperty("id").GetString() == arrangementId.ToString())
			.GetProperty("accompaniment").ValueKind is JsonValueKind.Null);

		using var setPatch = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { accompaniment = "Streicher" }, $"{cookie}; {editorSession}", token);
		using var setResponse = await client.SendAsync(setPatch);
		Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

		using var nullPatch = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { accompaniment = (string?)null }, $"{cookie}; {editorSession}", token);
		using var nullResponse = await client.SendAsync(nullPatch);
		Assert.Equal(HttpStatusCode.OK, nullResponse.StatusCode);
		var unchanged = await GetSongDetailAsync(client, editorSession, songId);
		Assert.Equal("Streicher", unchanged.GetProperty("arrangements").EnumerateArray()
			.Single(a => a.GetProperty("id").GetString() == arrangementId.ToString())
			.GetProperty("accompaniment").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var persisted = await db.Arrangements.SingleAsync(a => a.Id == arrangementId);
		Assert.Equal("Streicher", persisted.Accompaniment);
	}

	[Fact]
	public async Task NewFieldsCarryGermanValidationMessages()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreateSongAsync(factory, client: null, editorSession, "Nachrichtenprüfung");
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var detail = await GetSongDetailAsync(client, editorSession, songId);
		var arrangementId = Guid.Parse(detail.GetProperty("arrangements")[0].GetProperty("id").GetString()!);
		var (cookie, token) = await GetCsrfAsync(client, editorSession);

		using (var create = AuthedPost("/api/songs",
			new { title = "Zu lang", language = new string('x', 201) }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.LanguageTooLongMessage, problem.GetProperty("title").GetString());
		}

		using (var create = AuthedPost("/api/songs",
			new { title = "Zu lang", occasion = new string('x', 201) }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.OccasionTooLongMessage, problem.GetProperty("title").GetString());
		}

		using (var create = AuthedPost("/api/songs",
			new { title = "Leer", tags = new[] { "", "   " } }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.TagEmptyMessage, problem.GetProperty("title").GetString());
		}

		using (var create = AuthedPost("/api/songs",
			new { title = "Zu lang", tags = new[] { new string('x', 61) } }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.TagTooLongMessage, problem.GetProperty("title").GetString());
		}

		using (var create = AuthedPost("/api/songs",
			new { title = "Zu viele", tags = Enumerable.Range(1, 11).Select(i => $"Tag {i}").ToArray() },
			$"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.TagTooManyMessage, problem.GetProperty("title").GetString());
		}

		using (var patch = AuthedPatch($"/api/songs/{songId}",
			new { language = new string('x', 201) }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.LanguageTooLongMessage, problem.GetProperty("title").GetString());
		}

		using (var patch = AuthedPatch($"/api/songs/{songId}",
			new { occasion = new string('x', 201) }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.OccasionTooLongMessage, problem.GetProperty("title").GetString());
		}

		using (var create = AuthedPost($"/api/songs/{songId}/arrangements",
			new { label = "Zu lang", accompaniment = new string('x', 201) }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(create);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.AccompanimentTooLongMessage, problem.GetProperty("title").GetString());
		}

		using (var patch = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { accompaniment = new string('x', 201) }, $"{cookie}; {editorSession}", token))
		{
			using var response = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal(CatalogueEndpoints.AccompanimentTooLongMessage, problem.GetProperty("title").GetString());
		}

		// At the limit the fields are accepted.
		using (var okay = AuthedPatch($"/api/songs/{songId}",
			new { language = new string('x', 200), occasion = new string('x', 200), tags = new[] { new string('x', 60) } },
			$"{cookie}; {editorSession}", token))
		{
			Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(okay)).StatusCode);
		}
	}

	[Fact]
	public async Task CatalogueItemsCarryTheNewSongMetadataForVisibleEditing()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var songId = await CreatePublishedSongAsync(factory, client: null, editorSession, "Sichtbarkeit",
			language: "Deutsch", occasion: "Jahreskonzert", tags: ["Choral", "A cappella"]);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// List, search and filtered items carry the metadata so catalogue-row
		// editing never replaces values the editor cannot see.
		foreach (var url in new[] { "/api/songs", "/api/songs?q=Sichtbarkeit", "/api/songs?tag=choral" })
		{
			var (body, response) = await ListAsyncWithUrl(client, editorSession, url);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			var song = FirstSong(body);
			Assert.Equal(songId.ToString(), song.GetProperty("id").GetString());
			Assert.Equal("Deutsch", song.GetProperty("language").GetString());
			Assert.Equal("Jahreskonzert", song.GetProperty("occasion").GetString());
			Assert.Equal(["Choral", "A cappella"],
				song.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());
		}
	}

	private static async Task<(JsonElement Body, HttpResponseMessage Response)> ListAsyncWithUrl(
		HttpClient client, string session, string url)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Add("Cookie", session);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (body, response);
	}

	private static async Task<(JsonElement Body, HttpResponseMessage Response)> ListAsync(
		HttpClient client, string session, params (string Key, string Value)[] filters)
	{
		var parameters = filters.Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value)}").ToList();
		var url = "/api/songs" + (parameters.Count > 0 ? $"?{string.Join('&', parameters)}" : string.Empty);
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Add("Cookie", session);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (body, response);
	}

	private static JsonElement FirstSong(JsonElement body)
		=> body.GetProperty("songs").EnumerateArray().First();

	private static List<string> MatchedArrangementIds(JsonElement body)
		=> FirstSong(body).GetProperty("matchedArrangements").EnumerateArray()
			.Select(a => a.GetProperty("id").GetString()!).ToList();

	private static async Task<JsonElement> GetSongDetailAsync(HttpClient client, string session, Guid songId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("song");
	}

	private static async Task<Guid> CreatePublishedSongAsync(AuthApiFactory factory, HttpClient client,
		string editorSession, string title, string? language = null, string? occasion = null, string[]? tags = null)
	{
		var songId = await CreateSongAsync(factory, client, editorSession, title,
			language: language, occasion: occasion, tags: tags);
		await PublishAsync(factory, editorSession, songId);
		return songId;
	}

	private static async Task PublishAsync(AuthApiFactory factory, string editorSession, Guid songId)
	{
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<Guid> CreateSongAsync(
		AuthApiFactory factory, HttpClient? client, string editorSession, string title,
		string? language = null, string? occasion = null, string[]? tags = null)
	{
		client ??= factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title, language, occasion, tags },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);
	}

	private static async Task<Guid> AddArrangementAsync(HttpClient client, string editorSession,
		Guid songId, string label, string? voiceConfiguration = null, string? accompaniment = null)
	{
		using var response = await PostJsonAsync(client, $"/api/songs/{songId}/arrangements",
			new { label, voiceConfiguration, accompaniment }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		return Guid.Parse(detail.GetProperty("arrangements").EnumerateArray()
			.Single(a => a.GetProperty("label").GetString() == label)
			.GetProperty("id").GetString()!);
	}

	private static async Task<Guid> AddVersionAsync(HttpClient client, string editorSession,
		Guid arrangementId, string label, string? musicalKey = null)
	{
		using var response = await PostJsonAsync(client, $"/api/arrangements/{arrangementId}/versions",
			new { label, musicalKey }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		return Guid.Parse(detail.GetProperty("arrangements").EnumerateArray()
			.SelectMany(a => a.GetProperty("musicalVersions").EnumerateArray())
			.Single(v => v.GetProperty("label").GetString() == label)
			.GetProperty("id").GetString()!);
	}

	private static async Task<Guid> CreateAssetAsync(HttpClient client, string editorSession,
		Guid versionId, string assetType)
	{
		using var create = await PostJsonAsync(client, $"/api/musical-versions/{versionId}/assets",
			new { assetType }, editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		return Guid.Parse((await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
	}

	/// <summary>Uploads valid content for the asset type and finalizes, so the asset counts as current material.</summary>
	private static async Task<Guid> FinalizeAssetUploadAsync(AuthApiFactory factory, HttpClient client,
		string editorSession, Guid assetId, string assetType)
	{
		var (sessionId, uploadUrl, _) = await CreateUploadSessionAsync(client, editorSession, assetId);
		factory.Storage.Store(factory.Storage.Find(uploadUrl)!.BlobName,
			AssetContentFor(assetType), AssetContentTypeFor(assetType));
		using var finalize = await PostJsonAsync(client, $"/api/upload-sessions/{sessionId}/finalize",
			new { }, editorSession);
		Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
		return assetId;
	}

	private static async Task<(Guid SessionId, string UploadUrl, JsonElement Body)> CreateUploadSessionAsync(
		HttpClient client, string editorSession, Guid assetId)
	{
		using var response = await PostJsonAsync(client, $"/api/assets/{assetId}/upload-session",
			new { }, editorSession);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return (
			Guid.Parse(body.GetProperty("uploadSessionId").GetString()!),
			body.GetProperty("uploadUrl").GetString()!,
			body);
	}

	/// <summary>Creates an asset on a version and finalizes a valid upload, so the asset counts as current material.</summary>
	private static async Task<Guid> FinalizeAssetAsync(AuthApiFactory factory, HttpClient client,
		string editorSession, Guid versionId, string assetType)
		=> await FinalizeAssetUploadAsync(factory, client, editorSession,
			await CreateAssetAsync(client, editorSession, versionId, assetType), assetType);

	private static byte[] AssetContentFor(string assetType) => assetType switch
	{
		AssetEndpoints.AudioAssetType => new byte[512],
		AssetEndpoints.MidiAssetType => new byte[256],
		_ => ValidPdf(1024),
	};

	private static string AssetContentTypeFor(string assetType) => assetType switch
	{
		AssetEndpoints.AudioAssetType => AssetEndpoints.Mp3ContentType,
		AssetEndpoints.MidiAssetType => AssetEndpoints.XMidiContentType,
		_ => AssetEndpoints.PdfContentType,
	};

	private static byte[] ValidPdf(long size)
	{
		var bytes = new byte[size];
		"%PDF-1.7\n"u8.CopyTo(bytes);
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

	private static HttpRequestMessage AuthedPatch(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Patch, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
	}
}
