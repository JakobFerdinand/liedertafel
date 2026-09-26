using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-028 performance evidence: editors record occurrences of a song at one
/// event as confirmed or unconfirmed programme mention (closed evidence set,
/// required note for mentions, unknown chain as first-class null), with
/// stable occurrence identity, editor attribution, appended positions, the
/// per-event idempotency key returning its stored occurrence and the content
/// duplicate guard answering 409. Edits bump attribution/RowVersion under
/// the stale-token guard, a delete touches only its own row and evidence
/// writes never touch the referenced event row (no programme rows either).
/// Members receive only the minimal detail embed (never a source note) while
/// the occurrence reads stay editor-only; all validation keeps German
/// ProblemDetails titles.
/// </summary>
public sealed class PerformanceApiTests
{
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";
	private const string Zweitredaktion = "zweitredaktion@liedertafel.test";

	[Fact]
	public async Task CreatingConfirmedOccurrenceWithUnknownKeepsChainOptional()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Historisches Lied",
			versionLabel: "Grundtonart", musicalKey: "G-Dur");
		var transposed = await CreateVersionAsync(client, editorSession, song.ArrangementId,
			"Tiefe Tonart", "F-Dur");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Frühjahrskonzert" });

		// First occurrence without a known chain: both columns stay null
		// ("Fassung unbekannt") and the entry appends at position 1.
		using var first = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		var firstEmbed = PerformanceOf(first);
		Assert.Equal(eventId, Guid.Parse(firstEmbed.GetProperty("eventId").GetString()!));
		Assert.Equal(song.SongId, Guid.Parse(firstEmbed.GetProperty("songId").GetString()!));
		Assert.True(firstEmbed.GetProperty("musicalVersionId").ValueKind is JsonValueKind.Null);
		Assert.True(firstEmbed.GetProperty("arrangementId").ValueKind is JsonValueKind.Null);
		Assert.Equal("confirmed", firstEmbed.GetProperty("evidenceStatus").GetString());
		Assert.True(firstEmbed.GetProperty("sourceNote").ValueKind is JsonValueKind.Null);
		Assert.Equal(1, firstEmbed.GetProperty("position").GetInt32());
		var firstId = firstEmbed.GetProperty("id").GetString()!;

		// Second occurrence with the explicit version: the resolved chain
		// fills both columns and the position appends.
		using var second = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, musicalVersionId = transposed, evidenceStatus = "confirmed" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, second.StatusCode);
		var secondEmbed = PerformanceOf(second);
		Assert.Equal(2, secondEmbed.GetProperty("position").GetInt32());
		Assert.Equal(transposed, Guid.Parse(secondEmbed.GetProperty("musicalVersionId").GetString()!));
		Assert.Equal(song.ArrangementId, Guid.Parse(secondEmbed.GetProperty("arrangementId").GetString()!));

		// The editor list shows both in position order.
		var rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		Assert.Equal(2, rows.Count);
		Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.GetProperty("position").GetInt32()).ToList());
		Assert.Equal(firstId, rows[0].GetProperty("id").GetString());
	}

	[Fact]
	public async Task OccurrenceWithMismatchedVersionIsRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Erstes Lied");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Fassungstest" });

		// The version exists but belongs to the other song.
		using var mismatch = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = songA.SongId, musicalVersionId = songB.VersionId, evidenceStatus = "confirmed" },
			editorSession);
		Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
		var mismatchProblem = await mismatch.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.FassungPasstNichtMessage,
			mismatchProblem.GetProperty("title").GetString());

		// A genuine occurrence is rejected when a PATCH tries to move it to
		// another song's version; the refusal writes nothing.
		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = songA.SongId, evidenceStatus = "confirmed" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var embed = PerformanceOf(create);
		var id = embed.GetProperty("id").GetString()!;
		var rowVersion = embed.GetProperty("rowVersion").GetUInt32();

		using var patch = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion, musicalVersionId = songB.VersionId }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
		var patchProblem = await patch.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.FassungPasstNichtMessage,
			patchProblem.GetProperty("title").GetString());

		// The refusal left the row exactly as before: no chain, no bump.
		var rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		var kept = rows.Single(r => r.GetProperty("id").GetString() == id);
		Assert.True(kept.GetProperty("musicalVersionId").ValueKind is JsonValueKind.Null);
		Assert.Equal(rowVersion, kept.GetProperty("rowVersion").GetUInt32());
	}

	[Fact]
	public async Task MentionRequiresSourceNoteWhileConfirmedCanOmitIt()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Programmlied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Programmtest" });

		// A mention without a usable note is refused.
		using var bareMention = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "mention", sourceNote = "" }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, bareMention.StatusCode);
		var bareProblem = await bareMention.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.EvidenceNoteRequiredMessage,
			bareProblem.GetProperty("title").GetString());

		// A mention with a note within the limit lands (the note trims).
		using var notedMention = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "mention", sourceNote = "  Programmheft 1972, Blatt 3.  " },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, notedMention.StatusCode);
		var notedEmbed = PerformanceOf(notedMention);
		Assert.Equal("Programmheft 1972, Blatt 3.", notedEmbed.GetProperty("sourceNote").GetString());
		var notedId = notedEmbed.GetProperty("id").GetString()!;
		var notedRowVersion = notedEmbed.GetProperty("rowVersion").GetUInt32();

		// A confirmed performance may omit the note entirely.
		using var confirmed = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
		var confirmedEmbed = PerformanceOf(confirmed);
		Assert.True(confirmedEmbed.GetProperty("sourceNote").ValueKind is JsonValueKind.Null);

		// An oversized note is refused and an unknown value out of the
		// closed evidence set likewise.
		using var tooLong = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", sourceNote = new string('x', 2001) },
			editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
		var tooLongProblem = await tooLong.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.NoteTooLongMessage, tooLongProblem.GetProperty("title").GetString());
		var unknown = "certainly-sung";
		Assert.DoesNotContain(unknown, PerformanceEvidenceStatus.Known);
		using var unknownStatus = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = unknown }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, unknownStatus.StatusCode);
		var unknownProblem = await unknownStatus.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.InvalidEvidenceStatusMessage,
			unknownProblem.GetProperty("title").GetString());

		// Nothing of the refused writes reached the list (still 2 rows).
		var rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		Assert.Equal(2, rows.Count);
		Assert.Equal("mention", rows[0].GetProperty("evidenceStatus").GetString());
		Assert.Equal(notedId, rows[0].GetProperty("id").GetString());
		Assert.Equal(notedRowVersion, rows[0].GetProperty("rowVersion").GetUInt32());
	}

	[Fact]
	public async Task IdempotentRetriesDoNotInflateTotals()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Wiederholungslied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Wiederholungstest" });

		// First submission with a retry key creates the occurrence.
		using var first = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "erster-versuch" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		var storedId = PerformanceOf(first).GetProperty("id").GetString()!;

		// An oversized retry key is refused before any lookup or write.
		using var tooLongKey = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = new string('x', 201) },
			editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, tooLongKey.StatusCode);
		var tooLongKeyProblem = await tooLongKey.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.IdempotencyKeyTooLongMessage,
			tooLongKeyProblem.GetProperty("title").GetString());

		// The identical retry with the same key returns the stored row (200,
		// not a second 201) without inflating the list or mutating it.
		using var retry = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "erster-versuch" },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		Assert.Equal(storedId, PerformanceOf(retry).GetProperty("id").GetString());
		var rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		Assert.Single(rows);
		Assert.Equal(storedId, rows[0].GetProperty("id").GetString());

		// A different retry key marks a new submission: a fresh row lands.
		using var secondKey = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "zweiter-versuch" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, secondKey.StatusCode);
		rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		Assert.Equal(2, rows.Count);

		// The direct duplicate guard: the same content without a retry key
		// is refused with the German 409 title and the total stays the same.
		using var duplicate = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
		var duplicateProblem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.DuplicateOccurrenceMessage,
			duplicateProblem.GetProperty("title").GetString());
		rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		Assert.Equal(2, rows.Count);

		// A distinguishing note separates a genuine repeat from a duplicate.
		using var genuine = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", sourceNote = "Zugabe" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, genuine.StatusCode);
	}

	[Fact]
	public async Task GenuineRepeatOccurrenceIsDistinctFromRepeatedSubmission()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Zweimal gesungen");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Wiederholung" });

		// Two genuine occurrences of the same song differ by their notes.
		using var first = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", sourceNote = "Stück Remark" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		var firstId = PerformanceOf(first).GetProperty("id").GetString()!;
		using var second = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", sourceNote = "2. Teil des Abends" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, second.StatusCode);
		var secondEmbed = PerformanceOf(second);
		var secondId = secondEmbed.GetProperty("id").GetString()!;
		Assert.NotEqual(firstId, secondId);
		var secondRowVersion = secondEmbed.GetProperty("rowVersion").GetUInt32();

		// Notes are editable: aligning entry 2's note with entry 1's is an
		// allowed edit (the duplicate guard applies at creation time only);
		// it bumps only that row's own RowVersion/attribution.
		using var aligned = await PatchJsonAsync(client, $"/api/performances/{secondId}",
			new { rowVersion = secondRowVersion, sourceNote = "Stück Remark" }, editorSession);
		Assert.Equal(HttpStatusCode.OK, aligned.StatusCode);
		var alignedEmbed = PerformanceOf(aligned);
		Assert.Equal("Stück Remark", alignedEmbed.GetProperty("sourceNote").GetString());
		Assert.Equal(secondId, alignedEmbed.GetProperty("id").GetString());
		Assert.Equal(secondRowVersion + 1, alignedEmbed.GetProperty("rowVersion").GetUInt32());
	}

	[Fact]
	public async Task EditsUpdateAttributionAndEvidenceStatus()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Nachweislied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Bearbeitungstest" });
		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "basis" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var embed = PerformanceOf(create);
		var id = embed.GetProperty("id").GetString()!;
		var rowVersion = embed.GetProperty("rowVersion").GetUInt32();
		var capturedAt = DateTimeOffset.Parse(embed.GetProperty("capturedAt").GetString()!);

		// Flipping a confirmed occurrence to a mention without a note is
		// refused (a mention needs its source note), then succeeds with one.
		using var bareFlip = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion, evidenceStatus = "mention" }, editorSession);
		Assert.Equal(HttpStatusCode.BadRequest, bareFlip.StatusCode);
		var bareProblem = await bareFlip.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.EvidenceNoteRequiredMessage,
			bareProblem.GetProperty("title").GetString());
		using var flip = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion, evidenceStatus = "mention", sourceNote = "Programmblatt ohne Bestätigung" },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, flip.StatusCode);
		var flipped = PerformanceOf(flip);
		Assert.Equal("mention", flipped.GetProperty("evidenceStatus").GetString());
		Assert.Equal("Programmblatt ohne Bestätigung", flipped.GetProperty("sourceNote").GetString());
		Assert.Equal(rowVersion + 1, flipped.GetProperty("rowVersion").GetUInt32());
		Assert.True(DateTimeOffset.Parse(flipped.GetProperty("capturedAt").GetString()!) >= capturedAt);

		// Patching back to confirmed keeps the note.
		using var back = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion = flipped.GetProperty("rowVersion").GetUInt32(), evidenceStatus = "confirmed" },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, back.StatusCode);
		var restored = PerformanceOf(back);
		Assert.Equal("confirmed", restored.GetProperty("evidenceStatus").GetString());
		Assert.Equal("Programmblatt ohne Bestätigung", restored.GetProperty("sourceNote").GetString());
		Assert.Equal(rowVersion + 2, restored.GetProperty("rowVersion").GetUInt32());

		// A stale patch conflicts with the German concurrency title.
		using var stale = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion, evidenceStatus = "mention", sourceNote = "Veraltet" }, editorSession);
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		var staleProblem = await stale.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.ConcurrencyMessage, staleProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ExplicitNullPatchClearsChainAndNote()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Nullierungslied",
			versionLabel: "Grundtonart", musicalKey: "G-Dur");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Nulltest" });
		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, musicalVersionId = song.VersionId, evidenceStatus = "confirmed",
				sourceNote = "Chorsaalnotiz", idempotencyKey = "basis" }, editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var embed = PerformanceOf(create);
		var id = embed.GetProperty("id").GetString()!;
		var rowVersion = embed.GetProperty("rowVersion").GetUInt32();

		// An explicit null note clears the stored note; the rowVersion on the
		// body is sent as null too, so the JSON binding must not confuse
		// "explicit null" with "absent" (absent fields stay unchanged).
		using var clearNote = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion, sourceNote = (string?)null }, editorSession);
		Assert.Equal(HttpStatusCode.OK, clearNote.StatusCode);
		var clearedNote = PerformanceOf(clearNote);
		Assert.True(clearedNote.GetProperty("sourceNote").ValueKind is JsonValueKind.Null);
		Assert.Equal(rowVersion + 1, clearedNote.GetProperty("rowVersion").GetUInt32());

		// An explicit null chain clears BOTH columns ("Fassung unbekannt").
		using var clearChain = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion = clearedNote.GetProperty("rowVersion").GetUInt32(),
				musicalVersionId = (Guid?)null }, editorSession);
		Assert.Equal(HttpStatusCode.OK, clearChain.StatusCode);
		var clearedChain = PerformanceOf(clearChain);
		Assert.True(clearedChain.GetProperty("musicalVersionId").ValueKind is JsonValueKind.Null);
		Assert.True(clearedChain.GetProperty("arrangementId").ValueKind is JsonValueKind.Null);
		Assert.Equal(song.SongId, Guid.Parse(clearedChain.GetProperty("songId").GetString()!));
		Assert.Equal(rowVersion + 2, clearedChain.GetProperty("rowVersion").GetUInt32());

		// Absent fields stay unchanged: a patch without the note field keeps
		// the cleared state instead of restoring anything.
		using var absent = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion = clearedChain.GetProperty("rowVersion").GetUInt32(),
				evidenceStatus = "confirmed" }, editorSession);
		Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
		var kept = PerformanceOf(absent);
		Assert.True(kept.GetProperty("sourceNote").ValueKind is JsonValueKind.Null);
		Assert.True(kept.GetProperty("musicalVersionId").ValueKind is JsonValueKind.Null);
	}

	[Fact]
	public async Task DeleteRemovesOneOccurrenceOnly()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Tilgungslied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Tilgungstest" });
		using var first = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", sourceNote = "Erster Eintrag" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		var firstId = PerformanceOf(first).GetProperty("id").GetString()!;
		using var second = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", sourceNote = "Zweiter Eintrag" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, second.StatusCode);
		var secondId = PerformanceOf(second).GetProperty("id").GetString()!;
		DateTimeOffset eventUpdatedAtBefore;
		uint eventRowVersionBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			eventUpdatedAtBefore = persisted.UpdatedAt;
			eventRowVersionBefore = persisted.RowVersion;
		}

		// Deleting the first occurrence answers 204 without a rowVersion.
		using var deleted = await PostJsonAsync(client, $"/api/performances/{firstId}/delete",
			new { }, editorSession);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		// Only the second occurrence remains; the event row is untouched.
		var rows = (await GetPerformancesAsync(client, editorSession,
			$"/api/events/{eventId}/performances")).EnumerateArray().ToList();
		Assert.Single(rows);
		Assert.Equal(secondId, rows[0].GetProperty("id").GetString());
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.Equal(eventUpdatedAtBefore, persisted.UpdatedAt);
			Assert.Equal(eventRowVersionBefore, persisted.RowVersion);
		}

		// Repeating the delete answers the German 404, not a 500.
		using var repeated = await PostJsonAsync(client, $"/api/performances/{firstId}/delete",
			new { }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
		var repeatedProblem = await repeated.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.NotFoundMessage, repeatedProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task MemberSeesEvidenceWithoutSourceNote()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Mitgliedslied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Mitgliederblick" });
		await PublishEventAsync(client, editorSession, eventId);
		using var confirmedPost = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "bestätigt" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, confirmedPost.StatusCode);
		var confirmedId = PerformanceOf(confirmedPost).GetProperty("id").GetString()!;
		using var mentionPost = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "mention", sourceNote = "Programm von 1968", idempotencyKey = "hinweis" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, mentionPost.StatusCode);
		var mentionId = PerformanceOf(mentionPost).GetProperty("id").GetString()!;

		// The member detail carries both occurrences with stable ids and
		// positions but never a source note.
		var memberDetail = await GetEventDetailAsync(client, memberSession, eventId);
		var memberRows = memberDetail.GetProperty("performances").EnumerateArray().ToList();
		Assert.Equal(2, memberRows.Count);
		Assert.Equal(new[] { 1, 2 }, memberRows.Select(r => r.GetProperty("position").GetInt32()).ToList());
		Assert.Equal(new[] { confirmedId, mentionId },
			memberRows.Select(r => r.GetProperty("id").GetString()!).ToList());
		Assert.Equal(new[] { "confirmed", "mention" },
			memberRows.Select(r => r.GetProperty("evidenceStatus").GetString()!).ToList());
		Assert.Equal(song.SongId, Guid.Parse(memberRows[0].GetProperty("songId").GetString()!));
		// The occurrence embeds never carry a source note (or any audit
		// field); the event's own top-level sourceNote field is outside this
		// slice, so the recursive scan runs over the evidence embeds only.
		AssertNoSourceNote(memberDetail.GetProperty("performances"));
		foreach (var row in memberRows)
		{
			Assert.False(row.TryGetProperty("capturedAt", out _), "Unexpected audit field capturedAt.");
			Assert.False(row.TryGetProperty("createdAt", out _), "Unexpected audit field createdAt.");
			Assert.False(row.TryGetProperty("updatedAt", out _), "Unexpected audit field updatedAt.");
			Assert.False(row.TryGetProperty("rowVersion", out _), "Unexpected audit field rowVersion.");
		}

		// Occurrence reads stay editor-only: members receive 403 and
		// anonymous callers 401.
		using var memberList = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}/performances");
		memberList.Headers.Add("Cookie", memberSession);
		using var memberListResponse = await client.SendAsync(memberList);
		Assert.Equal(HttpStatusCode.Forbidden, memberListResponse.StatusCode);
		var memberListProblem = await memberListResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.ForbiddenMessage, memberListProblem.GetProperty("title").GetString());
		using var memberSongList = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{song.SongId}/performances");
		memberSongList.Headers.Add("Cookie", memberSession);
		using var memberSongListResponse = await client.SendAsync(memberSongList);
		Assert.Equal(HttpStatusCode.Forbidden, memberSongListResponse.StatusCode);
		using var anonymousList = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}/performances");
		using var anonymousListResponse = await client.SendAsync(anonymousList);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousListResponse.StatusCode);
	}

	[Fact]
	public async Task EvidenceNeverTouchesEventRow()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Unberührtes Lied");
		var eventId = await CreateEventAsync(client, editorSession, new
		{
			kind = "concert",
			title = "Unberührter Auftritt",
			dateYear = 1950,
			dateMonth = 5,
			dateDay = 12,
		});
		await PublishEventAsync(client, editorSession, eventId);
		DateTimeOffset eventPublishedAtBefore;
		DateTimeOffset eventUpdatedAtBefore;
		uint eventRowVersionBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			eventPublishedAtBefore = persisted.PublishedAt!.Value;
			eventUpdatedAtBefore = persisted.UpdatedAt;
			eventRowVersionBefore = persisted.RowVersion;
			// ARC-026 boundary: a published programme exists first.
			var programme = new EventProgramme
			{
				EventId = eventId,
				CreatedAt = persisted.CreatedAt,
				CreatedByAccountId = persisted.CreatedByAccountId,
				UpdatedAt = persisted.UpdatedAt,
				UpdatedByAccountId = persisted.UpdatedByAccountId,
			};
			var revision = new ProgrammeRevision
			{
				ProgrammeId = programme.Id,
				Number = 1,
				CreatedAt = persisted.CreatedAt,
				CreatedByAccountId = persisted.CreatedByAccountId,
				PublishedAt = persisted.PublishedAt,
				PublishedByAccountId = persisted.PublishedByAccountId,
			};
			revision.Items.Add(new ProgrammeItem
			{
				RevisionId = revision.Id,
				Position = 1,
				SongId = song.SongId,
				ArrangementId = song.ArrangementId,
				MusicalVersionId = song.VersionId,
			});
			programme.Revisions.Add(revision);
			db.Programmes.Add(programme);
			await db.SaveChangesAsync();
		}

		// Add, patch and delete evidence; the event row keeps every stamp.
		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "eins" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var embed = PerformanceOf(create);
		var id = embed.GetProperty("id").GetString()!;
		using var patch = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion = embed.GetProperty("rowVersion").GetUInt32(), evidenceStatus = "mention", sourceNote = "Hinweis" },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
		using var delete = await PostJsonAsync(client, $"/api/performances/{id}/delete",
			new { }, editorSession);
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

		// A fresh occurrence re-enters at position 1 after the delete, and
		// the member detail still shows the untouched programme embed.
		using var recreated = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "mention", sourceNote = "Noch ein Hinweis" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
		Assert.Equal(1, PerformanceOf(recreated).GetProperty("position").GetInt32());
		var memberDetail = await GetEventDetailAsync(client, memberSession, eventId);
		Assert.Equal(1, memberDetail.GetProperty("programme").GetProperty("published")
			.GetProperty("number").GetInt32());
		Assert.Single(memberDetail.GetProperty("programme").GetProperty("published")
			.GetProperty("items").EnumerateArray());
		Assert.Single(memberDetail.GetProperty("performances").EnumerateArray());
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var persisted = await db.Events.SingleAsync(e => e.Id == eventId);
			Assert.Equal(eventPublishedAtBefore, persisted.PublishedAt);
			Assert.Equal(eventUpdatedAtBefore, persisted.UpdatedAt);
			Assert.Equal(eventRowVersionBefore, persisted.RowVersion);
		}
	}

	[Fact]
	public async Task DraftEventEvidenceStaysInvisibleForMembers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Entwurfslied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Entwurfsauftritt" });

		// Evidence on a draft event is allowed for editors (entry first,
		// publication later, draft songs included).
		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "entwurf" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var embed = PerformanceOf(create);
		var id = embed.GetProperty("id").GetString()!;

		// The editor sees the occurrence on the draft event detail.
		var editorDetail = await GetEventDetailAsync(client, editorSession, eventId);
		var editorRows = editorDetail.GetProperty("performances").EnumerateArray().ToList();
		Assert.Single(editorRows);
		Assert.Equal(id, editorRows[0].GetProperty("id").GetString());
		Assert.Equal("confirmed", editorRows[0].GetProperty("evidenceStatus").GetString());

		// The member detail answers the existing indistinguishable 404: the
		// draft event itself never reaches members (no invented filters).
		using var memberDetail = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		memberDetail.Headers.Add("Cookie", memberSession);
		using var memberDetailResponse = await client.SendAsync(memberDetail);
		Assert.Equal(HttpStatusCode.NotFound, memberDetailResponse.StatusCode);
		var memberProblem = await memberDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(EventEndpoints.NotFoundMessage, memberProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ProgrammeConfirmPublishDoesNotCreateOccurrences()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var songA = await CreateSongAsync(client, editorSession, "Erstes Lied");
		var songB = await CreateSongAsync(client, editorSession, "Zweites Lied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Programm ohne Nachweis" });
		var items = new object[]
		{
			new { songId = songA.SongId, musicalVersionId = songA.VersionId },
			new { songId = songB.SongId, musicalVersionId = songB.VersionId },
		};
		using var saveResponse = await PutJsonAsync(client, $"/api/events/{eventId}/programme/items",
			new { items }, editorSession);
		Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
		using var publishResponse = await PostJsonAsync(client,
			$"/api/events/{eventId}/programme/publish",
			new { rowVersion = (await ProgrammeOfAsync(saveResponse)).GetProperty("rowVersion").GetUInt32() },
			editorSession);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

		// The programme publish wrote no evidence rows (the ARC-026
		// boundary holds) and the evidence embed stays empty in the detail.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.False(await db.Performances.AnyAsync());
		}
		var detail = await GetEventDetailAsync(client, editorSession, eventId);
		Assert.Empty(detail.GetProperty("performances").EnumerateArray());
	}

	[Fact]
	public async Task ApiProblemDetailGermanErrors()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, Member);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Fehlerlied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Fehlertest" });

		// A mutation without the CSRF token is refused.
		var (anonymousCookie, anonymousToken) = await GetCsrfAsync(client);
		using var noCsrf = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{eventId}/performances")
		{
			Content = JsonContent.Create(new { songId = song.SongId, evidenceStatus = "confirmed" }),
		};
		noCsrf.Headers.Add("Cookie", anonymousCookie);
		using var noCsrfResponse = await client.SendAsync(noCsrf);
		Assert.Equal(HttpStatusCode.BadRequest, noCsrfResponse.StatusCode);
		var noCsrfProblem = await noCsrfResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Ungültiger Sicherheitstoken.", noCsrfProblem.GetProperty("title").GetString());

		// A member cannot record occurrences (403) and an anonymous caller
		// cannot either (401) — the anonymous request reuses the fresh CSRF
		// pair so the antiforgery gate passes before the auth decision.
		using var memberPost = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" }, memberSession);
		Assert.Equal(HttpStatusCode.Forbidden, memberPost.StatusCode);
		var memberProblem = await memberPost.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.ForbiddenMessage, memberProblem.GetProperty("title").GetString());
		using var anonymousPost = AuthedPost($"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" },
			anonymousCookie, anonymousToken);
		using var anonymousPostResponse = await client.SendAsync(anonymousPost);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousPostResponse.StatusCode);
		var anonymousProblem = await anonymousPostResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Anmeldung erforderlich.", anonymousProblem.GetProperty("title").GetString());

		// An unknown occurrence id answers the German 404 on PATCH.
		var (editorCookie, editorToken) = await GetCsrfAsync(client, editorSession);
		using var unknownPatch = AuthedPatch($"/api/performances/{Guid.NewGuid()}",
			new { rowVersion = 0u }, $"{editorCookie}; {editorSession}", editorToken);
		using var unknownPatchResponse = await client.SendAsync(unknownPatch);
		Assert.Equal(HttpStatusCode.NotFound, unknownPatchResponse.StatusCode);
		var unknownProblem = await unknownPatchResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.NotFoundMessage, unknownProblem.GetProperty("title").GetString());

		// An unknown song id answers the German 404 on create.
		using var unknownSong = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = Guid.NewGuid(), evidenceStatus = "confirmed" }, editorSession);
		Assert.Equal(HttpStatusCode.NotFound, unknownSong.StatusCode);
		var unknownSongProblem = await unknownSong.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PerformanceEndpoints.SongNotFoundMessage,
			unknownSongProblem.GetProperty("title").GetString());
	}

	[Fact]
	public async Task SongHistoryOrdersByEventDateDescending()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Geschichtslied");
		var newer = await CreateEventAsync(client, editorSession, new
		{
			kind = "concert",
			title = "Späterer Auftritt",
			dateYear = 1975,
			dateMonth = 6,
			dateDay = 1,
		});
		var older = await CreateEventAsync(client, editorSession, new
		{
			kind = "service",
			title = "Früherer Auftritt",
			dateYear = 1950,
			dateMonth = 5,
			dateDay = 12,
		});
		var undated = await CreateEventAsync(client, editorSession,
			new { kind = "other", title = "Undatierter Auftritt" });
		await PostJsonAsync(client, $"/api/events/{older}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" }, editorSession);
		await PostJsonAsync(client, $"/api/events/{undated}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed" }, editorSession);
		await PostJsonAsync(client, $"/api/events/{newer}/performances",
			new { songId = song.SongId, evidenceStatus = "mention", sourceNote = "Programm 1975" }, editorSession);

		// The song history reads newest first, unknown-year events last.
		var history = (await GetPerformancesAsync(client, editorSession,
			$"/api/songs/{song.SongId}/performances")).EnumerateArray().ToList();
		Assert.Equal(3, history.Count);
		Assert.Equal(newer, Guid.Parse(history[0].GetProperty("eventId").GetString()!));
		Assert.Equal(undated, Guid.Parse(history[2].GetProperty("eventId").GetString()!));
		Assert.Equal("Früherer Auftritt", history[1].GetProperty("eventTitle").GetString());
		Assert.Equal("1. Juni 1975", history[0].GetProperty("dateDisplay").GetString());
		Assert.Equal("12. Mai 1950", history[1].GetProperty("dateDisplay").GetString());
		Assert.Equal("day", history[0].GetProperty("datePrecision").GetString());
		Assert.Equal("day", history[1].GetProperty("datePrecision").GetString());
		Assert.Equal("Programm 1975", history[0].GetProperty("sourceNote").GetString());
		Assert.Equal("confirmed", history[1].GetProperty("evidenceStatus").GetString());
		Assert.Equal("Datum unbekannt", history[2].GetProperty("dateDisplay").GetString());
		Assert.Equal("unknown", history[2].GetProperty("datePrecision").GetString());
	}

	[Fact]
	public async Task AttributionRecordsEditorAccount()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Editor, ArchiveRoles.Editor);
		await SeedAsync(factory, Zweitredaktion, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, Editor);
		var secondSession = await SignInAsync(factory, Zweitredaktion);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var song = await CreateSongAsync(client, editorSession, "Autorenlied");
		var eventId = await CreateEventAsync(client, editorSession,
			new { kind = "concert", title = "Autorentest" });
		using var create = await PostJsonAsync(client, $"/api/events/{eventId}/performances",
			new { songId = song.SongId, evidenceStatus = "confirmed", idempotencyKey = "urheber" },
			editorSession);
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		var embed = PerformanceOf(create);
		var id = embed.GetProperty("id").GetString()!;
		var editorId = await UserIdAsync(factory, Editor);
		var secondEditorId = await UserIdAsync(factory, Zweitredaktion);

		// Created and last-updated attribution both record the creator.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var row = await db.Performances.SingleAsync(p => p.Id == Guid.Parse(id));
			Assert.Equal(editorId, row.CreatedByAccountId);
			Assert.Equal(editorId, row.UpdatedByAccountId);
		}

		// The second editor's PATCH rewrites UpdatedBy only.
		using var patch = await PatchJsonAsync(client, $"/api/performances/{id}",
			new { rowVersion = embed.GetProperty("rowVersion").GetUInt32(), sourceNote = "Überarbeitet" },
			secondSession);
		Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var row = await db.Performances.SingleAsync(p => p.Id == Guid.Parse(id));
			Assert.Equal(editorId, row.CreatedByAccountId);
			Assert.Equal(secondEditorId, row.UpdatedByAccountId);
		}
	}

	// Helpers, copied per-file in the style of the other API test suites.

	private static JsonElement PerformanceOf(HttpResponseMessage response)
	{
		response.EnsureSuccessStatusCode();
		return (response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult())
			.GetProperty("performance");
	}

	private static void AssertNoSourceNote(JsonElement element)
	{
		if (element.ValueKind is JsonValueKind.Object)
		{
			foreach (var property in element.EnumerateObject())
			{
				Assert.False(string.Equals(property.Name, "sourceNote", StringComparison.Ordinal),
					$"Unexpected field {property.Name}.");
				AssertNoSourceNote(property.Value);
			}
		}
		else if (element.ValueKind is JsonValueKind.Array)
		{
			foreach (var item in element.EnumerateArray())
				AssertNoSourceNote(item);
		}
	}

	private static async Task<JsonElement> GetEventDetailAsync(HttpClient client, string session, Guid eventId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("event");
	}

	private static async Task<JsonElement> GetPerformancesAsync(HttpClient client, string session, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("performances");
	}

	private static async Task<Guid> CreateEventAsync(HttpClient client, string editorSession, object body)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/events", body, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(parsed.GetProperty("event").GetProperty("id").GetString()!);
	}

	private static async Task PublishEventAsync(HttpClient client, string editorSession, Guid eventId)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/events/{eventId}/publish", new { },
			$"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	/// <summary>
	/// Creates a song with its POST /api/songs default chain (one arrangement
	/// carrying one musical version), optionally labels both and publishes
	/// the song, so evidence can reference the chain like the programme does.
	/// </summary>
	private static async Task<(Guid SongId, Guid ArrangementId, Guid VersionId)> CreateSongAsync(
		HttpClient client, string editorSession, string title,
		string? arrangementLabel = null, string? voiceConfiguration = null,
		string? versionLabel = null, string? musicalKey = null, bool publish = true)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var createResponse = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		var song = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		var songId = Guid.Parse(song.GetProperty("id").GetString()!);
		var arrangement = song.GetProperty("arrangements").EnumerateArray().Single();
		var arrangementId = Guid.Parse(arrangement.GetProperty("id").GetString()!);
		var version = arrangement.GetProperty("musicalVersions").EnumerateArray().Single();
		var versionId = Guid.Parse(version.GetProperty("id").GetString()!);
		if (arrangementLabel is not null || voiceConfiguration is not null)
		{
			using var patch = AuthedPatch($"/api/arrangements/{arrangementId}",
				new { label = arrangementLabel, voiceConfiguration }, $"{cookie}; {editorSession}", token);
			using var patchResponse = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		}
		if (versionLabel is not null || musicalKey is not null)
		{
			using var patch = AuthedPatch($"/api/musical-versions/{versionId}",
				new { label = versionLabel, musicalKey }, $"{cookie}; {editorSession}", token);
			using var patchResponse = await client.SendAsync(patch);
			Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		}
		if (publish)
		{
			using var publishSong = AuthedPost($"/api/songs/{songId}/publish", new { },
				$"{cookie}; {editorSession}", token);
			using var publishResponse = await client.SendAsync(publishSong);
			Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
		}
		return (songId, arrangementId, versionId);
	}

	/// <summary>Adds a further labelled musical version to the arrangement.</summary>
	private static async Task<Guid> CreateVersionAsync(
		HttpClient client, string editorSession, Guid arrangementId, string label, string? musicalKey = null)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost($"/api/arrangements/{arrangementId}/versions",
			new { label, musicalKey }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var song = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");
		var arrangement = song.GetProperty("arrangements").EnumerateArray()
			.Single(a => Guid.Parse(a.GetProperty("id").GetString()!) == arrangementId);
		var version = arrangement.GetProperty("musicalVersions").EnumerateArray()
			.Single(v => v.GetProperty("label").GetString() == label);
		return Guid.Parse(version.GetProperty("id").GetString()!);
	}

	private static async Task<JsonElement> ProgrammeOfAsync(HttpResponseMessage response)
		=> (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programme");

	private static async Task<HttpResponseMessage> PutJsonAsync(
		HttpClient client, string path, object body, string session)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		return await client.SendAsync(AuthedPut(path, body, $"{cookie}; {session}", token));
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

	private static HttpRequestMessage AuthedPut(string path, object body, string cookie, string token)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, path);
		request.Headers.Add("Cookie", cookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = JsonContent.Create(body);
		return request;
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

	private static async Task<Guid> UserIdAsync(AuthApiFactory factory, string email)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		return (await users.FindByEmailAsync(email))!.Id;
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
}
