using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Provenance;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-013-1 field provenance and proposals: automated writes above the
/// confidence threshold apply themselves and record provenance, below the
/// threshold (or on locked fields) they become open proposals, human edits
/// lock fields, reverting restores and locks, song/arrangement/version
/// patches carry a row version and answer 409 on stale edits, and members
/// never reach the queue, the revert or the provenance.
/// </summary>
public sealed class ProvenanceProposalTests
{
	[Fact]
	public async Task AutomatedWriteAboveThresholdAppliesAndRecordsProvenance()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "S atanu");

		var devWrite = await ProvenanceWriteAsync(client, "song", songId, "composer", "Franz Xaver Gruber", "sicher");
		Assert.Equal("Applied", devWrite.GetProperty("result").GetString());

		var song = await GetSongAsync(client, editorSession, songId);
		Assert.Equal("Franz Xaver Gruber", song.GetProperty("composer").GetString());
		var provenance = ProvenanceOf(song);
		var composerRow = ProvenanceEntry(provenance, "song", "composer");
		Assert.Equal("Regex", composerRow.GetProperty("source").GetString());
		Assert.Equal("Sicher", composerRow.GetProperty("confidence").GetString());
		Assert.False(composerRow.GetProperty("locked").GetBoolean());
	}

	[Fact]
	public async Task AutomatedWriteBelowThresholdCreatesProposalThatEditorAccepts()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Vorschlagslied");

		var devWrite = await ProvenanceWriteAsync(client, "song", songId, "language", "Deutsch", "unsicher");
		Assert.Equal("Proposed", devWrite.GetProperty("result").GetString());
		var proposalId = devWrite.GetProperty("proposalId").GetString();
		Assert.NotNull(proposalId);

		var song = await GetSongAsync(client, editorSession, songId);
		Assert.True(song.GetProperty("language").ValueKind is JsonValueKind.Null);

		var queue = await GetProposalsAsync(client, editorSession);
		var proposal = Assert.Single(queue.GetProperty("proposals").EnumerateArray());
		Assert.Equal("FieldSuggestion", proposal.GetProperty("kind").GetString());
		Assert.Equal("Feldvorschlag", proposal.GetProperty("kindLabel").GetString());
		Assert.Equal("song", proposal.GetProperty("targetEntityType").GetString());
		Assert.False(proposal.GetProperty("isStale").GetBoolean());

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var accept = AuthedPost($"/api/proposals/{proposalId}/accept", new { }, $"{cookie}; {editorSession}", token);
		using var acceptResponse = await client.SendAsync(accept);
		Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);

		var acceptedSong = await GetSongAsync(client, editorSession, songId);
		Assert.Equal("Deutsch", acceptedSong.GetProperty("language").GetString());
		var row = ProvenanceEntry(ProvenanceOf(acceptedSong), "song", "language");
		Assert.Equal("Regex", row.GetProperty("source").GetString());
		Assert.False(row.GetProperty("locked").GetBoolean());

		var emptyQueue = await GetProposalsAsync(client, editorSession);
		Assert.Empty(emptyQueue.GetProperty("proposals").EnumerateArray());
	}

	[Fact]
	public async Task HumanEditLocksFieldAndAutomatedRunOnlyProposes()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Gesperrtes Lied");

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		var song = await GetSongAsync(client, editorSession, songId);
		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { composer = "Franz Xaver Gruber", rowVersion = song.GetProperty("rowVersion").GetUInt32() },
			$"{cookie}; {editorSession}", token);
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

		var devWrite = await ProvenanceWriteAsync(client, "song", songId, "composer", "Joseph Mohr", "sicher");
		Assert.Equal("Proposed", devWrite.GetProperty("result").GetString());

		var lockedSong = await GetSongAsync(client, editorSession, songId);
		Assert.Equal("Franz Xaver Gruber", lockedSong.GetProperty("composer").GetString());
		var row = ProvenanceEntry(ProvenanceOf(lockedSong), "song", "composer");
		Assert.True(row.GetProperty("locked").GetBoolean());
		Assert.Equal("Human", row.GetProperty("source").GetString());

		// The rerun above the threshold never overwrites the human value.
		Assert.Equal("Proposed", (await ProvenanceWriteAsync(client, "song", songId, "composer", "Josef Gruber", "sicher")).GetProperty("result").GetString());
		var stillLocked = await GetSongAsync(client, editorSession, songId);
		Assert.Equal("Franz Xaver Gruber", stillLocked.GetProperty("composer").GetString());
	}

	[Fact]
	public async Task RevertRestoresPreviousValueAndLocksTheField()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Zurückgeholtes Lied");

		Assert.Equal("Applied", (await ProvenanceWriteAsync(client, "song", songId, "composer", "Franz Xaver Gruber", "sicher")).GetProperty("result").GetString());
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		var song = await GetSongAsync(client, editorSession, songId);
		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { composer = "Joseph Mohr", rowVersion = song.GetProperty("rowVersion").GetUInt32() },
			$"{cookie}; {editorSession}", token);
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

		using var revert = AuthedPost("/api/provenance/revert",
			new { entityType = "song", entityId = songId, field = "composer" },
			$"{cookie}; {editorSession}", token);
		using var revertResponse = await client.SendAsync(revert);
		Assert.Equal(HttpStatusCode.OK, revertResponse.StatusCode);

		var reverted = await GetSongAsync(client, editorSession, songId);
		Assert.Equal("Franz Xaver Gruber", reverted.GetProperty("composer").GetString());
		var row = ProvenanceEntry(ProvenanceOf(reverted), "song", "composer");
		Assert.True(row.GetProperty("locked").GetBoolean());
		Assert.Equal("Human", row.GetProperty("source").GetString());

		Assert.Equal("Proposed", (await ProvenanceWriteAsync(client, "song", songId, "composer", "Anders", "sicher")).GetProperty("result").GetString());
	}

	[Fact]
	public async Task StaleSongPatchAnswers409WithoutChangingAnything()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Frühe Änderung");

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		var stale = await GetSongAsync(client, editorSession, songId);
		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { composer = "Einagement A", rowVersion = stale.GetProperty("rowVersion").GetUInt32() + 5 },
			$"{cookie}; {editorSession}", token);
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.Conflict, patchResponse.StatusCode);
		var problem = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Eintrag wurde zwischenzeitlich geändert.", problem.GetProperty("title").GetString());

		var unchanged = await GetSongAsync(client, editorSession, songId);
		Assert.True(unchanged.GetProperty("composer").ValueKind is JsonValueKind.Null);

		using var fresh = AuthedPatch($"/api/songs/{songId}",
			new { composer = "Composer B", rowVersion = unchanged.GetProperty("rowVersion").GetUInt32() },
			$"{cookie}; {editorSession}", token);
		using var freshResponse = await client.SendAsync(fresh);
		Assert.Equal(HttpStatusCode.OK, freshResponse.StatusCode);
		Assert.Equal("Composer B", (await freshResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song").GetProperty("composer").GetString());
	}

	[Fact]
	public async Task ArrangementAndVersionPatchesCarryTheirRowVersion()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Fassungen mit Stand");

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		var before = await GetSongAsync(client, editorSession, songId);
		var arrangementRowVersion = before.GetProperty("arrangements").EnumerateArray().Single().GetProperty("rowVersion").GetUInt32();
		var versionRowVersion = before.GetProperty("arrangements").EnumerateArray().Single()
			.GetProperty("musicalVersions").EnumerateArray().Single().GetProperty("rowVersion").GetUInt32();
		var versionId = before.GetProperty("arrangements").EnumerateArray().Single()
			.GetProperty("musicalVersions").EnumerateArray().Single().GetProperty("id").GetString()!;
		var arrangementId = before.GetProperty("arrangements").EnumerateArray().Single().GetProperty("id").GetString()!;

		using var staleArrangement = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { label = "Stale Label", rowVersion = arrangementRowVersion + 1 },
			$"{cookie}; {editorSession}", token);
		using var staleArrangementResponse = await client.SendAsync(staleArrangement);
		Assert.Equal(HttpStatusCode.Conflict, staleArrangementResponse.StatusCode);
		var staleProblem = await staleArrangementResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Die Fassung wurde zwischenzeitlich geändert.", staleProblem.GetProperty("title").GetString());

		using var staleVersion = AuthedPatch($"/api/musical-versions/{versionId}",
			new { label = "Stale Fassung", rowVersion = versionRowVersion + 1 },
			$"{cookie}; {editorSession}", token);
		using var staleVersionResponse = await client.SendAsync(staleVersion);
		Assert.Equal(HttpStatusCode.Conflict, staleVersionResponse.StatusCode);

		using var freshArrangement = AuthedPatch($"/api/arrangements/{arrangementId}",
			new { label = "Neue Bezeichnung", rowVersion = arrangementRowVersion },
			$"{cookie}; {editorSession}", token);
		using var freshArrangementResponse = await client.SendAsync(freshArrangement);
		Assert.Equal(HttpStatusCode.OK, freshArrangementResponse.StatusCode);
		var after = (await freshArrangementResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("song");

		var freshVersion = await GetSongAsync(client, editorSession, songId);
		var arrangement = freshVersion.GetProperty("arrangements").EnumerateArray().Single();
		Assert.Equal("Neue Bezeichnung", arrangement.GetProperty("label").GetString());
		Assert.Equal(arrangementRowVersion + 1, arrangement.GetProperty("rowVersion").GetUInt32());
		var version = arrangement.GetProperty("musicalVersions").EnumerateArray().Single();
		Assert.Equal(versionRowVersion, version.GetProperty("rowVersion").GetUInt32());
	}

	[Fact]
	public async Task SongCreationProposalAppliesThroughSharedWriteService()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var proposalId = await InsertProposalAsync(factory, new Proposal
		{
			Kind = ProposalKind.SongCreation,
			Payload = JsonSerializer.Serialize(new
			{
				title = "Aus dem Leselauf",
				composer = "Komponist X",
				lyricist = "Texter Y",
				arrangementLabel = "Normalsatz",
				versionLabel = "Erste Fassung",
			}),
			Reason = "Leselauf schlägt ein neues Lied vor.",
			Source = ProvenanceSource.Regex,
			Confidence = ProvenanceConfidence.Unsicher,
			SourceDescription = "Notentext-Ordner 47",
			Status = ProposalStatus.Open,
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var accept = AuthedPost($"/api/proposals/{proposalId}/accept", new { }, $"{cookie}; {editorSession}", token);
		using var acceptResponse = await client.SendAsync(accept);
		Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);

		var listing = await GetSongsAsync(client, editorSession);
		var created = Assert.Single(listing.GetProperty("songs").EnumerateArray(), s =>
			s.GetProperty("title").GetString() == "Aus dem Leselauf");
		Assert.Equal("Komponist X", created.GetProperty("composer").GetString());
	}

	[Fact]
	public async Task StaleProposalAcceptShowsCurrentBesideProposedInsteadOfApplying()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Veraltetes Ziel");

		var devWrite = await ProvenanceWriteAsync(client, "song", songId, "language", "Deutsch", "unsicher");
		Assert.Equal("Proposed", devWrite.GetProperty("result").GetString());
		var proposalId = devWrite.GetProperty("proposalId").GetString()!;

		// A human edit moves the target past the proposal's row version.
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		var song = await GetSongAsync(client, editorSession, songId);
		using var patch = AuthedPatch($"/api/songs/{songId}",
			new { title = "Umbenanntes Ziel", rowVersion = song.GetProperty("rowVersion").GetUInt32() },
			$"{cookie}; {editorSession}", token);
		using var patchResponse = await client.SendAsync(patch);
		Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

		using var accept = AuthedPost($"/api/proposals/{proposalId}/accept", new { }, $"{cookie}; {editorSession}", token);
		using var acceptResponse = await client.SendAsync(accept);
		Assert.Equal(HttpStatusCode.Conflict, acceptResponse.StatusCode);
		var problem = await acceptResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Das Ziel wurde zwischenzeitlich geändert.", problem.GetProperty("title").GetString());
		Assert.Equal(string.Empty, problem.GetProperty("currentValue").ToString());
		Assert.Equal("Deutsch", problem.GetProperty("proposedValue").ToString());

		// Nothing was applied; the queue still shows the proposal as stale.
		var queue = await GetProposalsAsync(client, editorSession);
		var staleEntry = Assert.Single(queue.GetProperty("proposals").EnumerateArray());
		Assert.True(staleEntry.GetProperty("isStale").GetBoolean());

		// After a fresh look the editor re-bases the proposal, then accepts.
		using var refresh = AuthedPost($"/api/proposals/{proposalId}/refresh", new { }, $"{cookie}; {editorSession}", token);
		using var refreshResponse = await client.SendAsync(refresh);
		Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
		using var freshAccept = AuthedPost($"/api/proposals/{proposalId}/accept", new { }, $"{cookie}; {editorSession}", token);
		using var freshAcceptResponse = await client.SendAsync(freshAccept);
		Assert.Equal(HttpStatusCode.OK, freshAcceptResponse.StatusCode);
		Assert.Equal("Deutsch", (await GetSongAsync(client, editorSession, songId)).GetProperty("language").GetString());
	}

	[Fact]
	public async Task MembersNeverReachQueueRevertOrProvenance()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, MemberEmail, ArchiveRoles.Member);
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var memberSession = await SignInAsync(factory, MemberEmail);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Nur Redaktion");
		Assert.Equal("Applied", (await ProvenanceWriteAsync(client, "song", songId, "composer", "Komponist", "sicher")).GetProperty("result").GetString());

		// Published so the member can legitimately read the song itself.
		var (publishCookie, publishToken) = await GetCsrfAsync(client, editorSession);
		using var publish = AuthedPost($"/api/songs/{songId}/publish", new { }, $"{publishCookie}; {editorSession}", publishToken);
		using var publishResponse = await client.SendAsync(publish);
		Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

		using var queueGet = new HttpRequestMessage(HttpMethod.Get, "/api/proposals");
		queueGet.Headers.Add("Cookie", memberSession);
		using var queueResponse = await client.SendAsync(queueGet);
		Assert.Equal(HttpStatusCode.Forbidden, queueResponse.StatusCode);

		var (memberCookie, memberToken) = await GetCsrfAsync(client, memberSession);
		using var memberAccept = AuthedPost("/api/proposals" + $"/{Guid.NewGuid()}/accept", new { }, $"{memberCookie}; {memberSession}", memberToken);
		using var memberAcceptResponse = await client.SendAsync(memberAccept);
		Assert.Equal(HttpStatusCode.Forbidden, memberAcceptResponse.StatusCode);

		using var memberRevert = AuthedPost("/api/provenance/revert",
			new { entityType = "song", entityId = songId, field = "composer" },
			$"{memberCookie}; {memberSession}", memberToken);
		using var memberRevertResponse = await client.SendAsync(memberRevert);
		Assert.Equal(HttpStatusCode.Forbidden, memberRevertResponse.StatusCode);

		var memberSong = await GetSongAsync(client, memberSession, songId);
		Assert.True(memberSong.TryGetProperty("provenance", out _) is false || memberSong.GetProperty("provenance").ValueKind is JsonValueKind.Null);

		using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/proposals");
		using var anonymousResponse = await client.SendAsync(anonymous);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

		var editorSong = await GetSongAsync(client, editorSession, songId);
		Assert.NotEqual(JsonValueKind.Null, editorSong.GetProperty("provenance").ValueKind);
		Assert.Equal("Komponist", editorSong.GetProperty("composer").GetString());
	}

	[Fact]
	public async Task AutomatedWriterRefusesIdentityAndVisibilityFieldsServerSide()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Geschütztes Lied");

		var refused = await ProvenanceWriteAsync(client, "song", songId, "published", "true", "sicher");
		Assert.Equal("Refused", refused.GetProperty("result").GetString());

		var song = await GetSongAsync(client, editorSession, songId);
		Assert.False(song.GetProperty("published").GetBoolean());
		var queue = await GetProposalsAsync(client, editorSession);
		Assert.Empty(queue.GetProperty("proposals").EnumerateArray());
	}

	[Fact]
	public async Task ThresholdNeverTurnsEveryAutomatedWriteIntoAProposal()
	{
		await using var factory = new AuthApiFactory(settings: new Dictionary<string, string?>
		{
			["Archive:Provenance:AutoApplyConfidence"] = "never",
		});
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Immer Vorschlag");

		var devWrite = await ProvenanceWriteAsync(client, "song", songId, "composer", "Komponist", "sicher");
		Assert.Equal("Proposed", devWrite.GetProperty("result").GetString());
		var queue = await GetProposalsAsync(client, editorSession);
		Assert.Single(queue.GetProperty("proposals").EnumerateArray());
	}

	[Fact]
	public async Task RepeatedAcceptIs409AndProposalRowCarriesTheDecision()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, EditorEmail, ArchiveRoles.Editor);
		var editorSession = await SignInAsync(factory, EditorEmail);
		using var client = NewClient(factory);
		var songId = await CreateSongAsync(client, editorSession, "Entschiedener Vorschlag");
		var devWrite = await ProvenanceWriteAsync(client, "song", songId, "occasion", "Konzert", "unsicher");
		var proposalId = devWrite.GetProperty("proposalId").GetString()!;

		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var accept = AuthedPost($"/api/proposals/{proposalId}/accept", new { }, $"{cookie}; {editorSession}", token);
		using var acceptResponse = await client.SendAsync(accept);
		Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);
		using var second = AuthedPost($"/api/proposals/{proposalId}/accept", new { }, $"{cookie}; {editorSession}", token);
		using var secondResponse = await client.SendAsync(second);
		Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
		var problem = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Der Vorschlag wurde bereits entschieden.", problem.GetProperty("title").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var stored = await db.Proposals.SingleAsync(p => p.Id == Guid.Parse(proposalId!));
		Assert.Equal(ProposalStatus.Accepted, stored.Status);
		Assert.NotNull(stored.DecidedAt);
		Assert.NotNull(stored.DecidedByAccountId);
	}

	// ---- helpers -----------------------------------------------------------

	private const string MemberEmail = "mitglied@liedertafel.test";
	private const string EditorEmail = "redaktion@liedertafel.test";

	private static HttpClient NewClient(AuthApiFactory factory)
		=> factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

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

	private static async Task<Guid> CreateSongAsync(HttpClient client, string editorSession, string title)
	{
		var (cookie, token) = await GetCsrfAsync(client, editorSession);
		using var create = AuthedPost("/api/songs", new { title }, $"{cookie}; {editorSession}", token);
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("song").GetProperty("id").GetString()!);
	}

	private static async Task<JsonElement> GetSongAsync(HttpClient client, string session, Guid songId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{songId}");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("song");
	}

	private static async Task<JsonElement> GetSongsAsync(HttpClient client, string session)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/songs");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static async Task<JsonElement> GetProposalsAsync(HttpClient client, string session)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/proposals");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	/// <summary>The development diagnostic that drives the real AutomatedFieldWriter.</summary>
	private static async Task<JsonElement> ProvenanceWriteAsync(
		HttpClient client, string entityType, Guid entityId, string field, string? value, string confidence)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var write = AuthedPost("/api/dev/provenance-write",
			new { entityType, entityId, field, value, confidence },
			cookie, token);
		using var response = await client.SendAsync(write);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static JsonElement ProvenanceOf(JsonElement song)
		=> song.GetProperty("provenance");

	private static JsonElement ProvenanceEntry(JsonElement provenance, string entityType, string field)
		=> Assert.Single(provenance.EnumerateArray(), row =>
			row.GetProperty("entityType").GetString() == entityType
			&& row.GetProperty("field").GetString() == field);

	private static async Task<Guid> InsertProposalAsync(AuthApiFactory factory, Proposal proposal)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		db.Proposals.Add(proposal);
		await db.SaveChangesAsync();
		return proposal.Id;
	}
}
