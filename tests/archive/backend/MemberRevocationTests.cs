using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-007 membership revocation: deactivation/reactivation/role changes with
/// seeded members, stable identity, server-side enforcement on the next
/// request, audit attribution and last-administrator protection.
/// </summary>
public sealed class MemberRevocationTests
{
	private const string Admin = "verwaltung@liedertafel.test";
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task DeactivateRevokesSessionPreservesHistoryAndAuditsActor()
	{
		await using var factory = new AuthApiFactory();
		var adminId = await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		var memberSession = await SignInAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Invite + accept a new singer so invitation history exists.
		await InviteAsync(client, adminSession, "neu@liedertafel.test", "Neue Stimme", "Member", HttpStatusCode.Created);
		var code = await RequestCodeAsync(factory, client, "neu@liedertafel.test");
		var neuSession = await VerifyAndGetSessionAsync(client, "neu@liedertafel.test", code);
		var neuId = await AccountIdAsync(client, neuSession);
		var acceptedAtBefore = await InvitationAcceptedAtAsync(factory, neuId);
		Assert.NotNull(acceptedAtBefore);
		string stampBefore;
		using (var scope = factory.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			stampBefore = (await users.FindByIdAsync(neuId.ToString()))!.SecurityStamp!;
		}

		var (cookie, token) = await GetCsrfAsync(client, adminSession);
		using var deactivate = AuthedPost("/api/admin/members/deactivate",
			new { accountId = neuId }, $"{cookie}; {adminSession}", token);
		using var response = await client.SendAsync(deactivate);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Contains("deaktiviert", body.GetProperty("message").GetString());
		Assert.Equal("deactivated", body.GetProperty("status").GetString());

		// Stable identity: same account ID, roles preserved, acceptance kept.
		var entry = await MemberEntryAsync(client, adminSession, "neu@liedertafel.test");
		Assert.Equal(neuId.ToString(), entry.GetProperty("accountId").GetString());
		Assert.Equal("deactivated", entry.GetProperty("status").GetString());
		Assert.Contains("Member", entry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		Assert.Equal(acceptedAtBefore, entry.GetProperty("acceptedAt").GetString());
		var acceptedAtAfter = await InvitationAcceptedAtAsync(factory, neuId);
		Assert.Equal(acceptedAtBefore, acceptedAtAfter);

		// Audit records who performed the change.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var audit = await db.MemberAdminActions
				.Where(a => a.TargetUserId == neuId && a.Action == MemberAdminActionType.Deactivated)
				.SingleAsync();
			Assert.Equal(adminId, audit.ActorAccountId);
			Assert.Contains("Member", audit.OldRoles);
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			var user = (await users.FindByIdAsync(neuId.ToString()))!;
			Assert.NotEqual(stampBefore, user.SecurityStamp);
			Assert.True(await users.IsLockedOutAsync(user));
		}

		// Existing session obeys revocation on its next request: signed-out.
		var meAfter = await MeAsync(client, neuSession);
		Assert.False(meAfter.GetProperty("authenticated").GetBoolean());
		using var adminProbe = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
		adminProbe.Headers.Add("Cookie", neuSession);
		using var probeResponse = await client.SendAsync(adminProbe);
		Assert.Equal(HttpStatusCode.Unauthorized, probeResponse.StatusCode);

		// Inactive accounts get no code mail (no disclosure) and verify fails.
		var mailsBefore = factory.Mail.Sent.Count(m => string.Equals(m.Email, "neu@liedertafel.test", StringComparison.OrdinalIgnoreCase));
		var (reqCookie, reqToken) = await GetCsrfAsync(client);
		using var codeRequest = AuthedPost("/api/auth/code/request", new { email = "neu@liedertafel.test" }, reqCookie, reqToken);
		using var codeResponse = await client.SendAsync(codeRequest);
		Assert.Equal(HttpStatusCode.Accepted, codeResponse.StatusCode);
		Assert.Equal(mailsBefore, factory.Mail.Sent.Count(m => string.Equals(m.Email, "neu@liedertafel.test", StringComparison.OrdinalIgnoreCase)));
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify",
			new { email = "neu@liedertafel.test", code = "123456" }, verifyCookie, verifyToken);
		using var verifyResponse = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.BadRequest, verifyResponse.StatusCode);

		// Seeded member session is untouched by another member's revocation.
		var memberMe = await MeAsync(client, memberSession);
		Assert.True(memberMe.GetProperty("authenticated").GetBoolean());
	}

	[Fact]
	public async Task ReactivationKeepsOldSessionsDeadAndRequiresFreshSignIn()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		var memberSession = await SignInAsync(factory, Member);
		var memberId = await AccountIdAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await DeactivateAsync(client, adminSession, memberId, HttpStatusCode.OK);
		var meRevoked = await MeAsync(client, memberSession);
		Assert.False(meRevoked.GetProperty("authenticated").GetBoolean());

		var (cookie, token) = await GetCsrfAsync(client, adminSession);
		using var reactivate = AuthedPost("/api/admin/members/reactivate",
			new { accountId = memberId }, $"{cookie}; {adminSession}", token);
		using var response = await client.SendAsync(reactivate);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Contains("erneute Anmeldung", body.GetProperty("message").GetString());
		Assert.Equal("active", body.GetProperty("status").GetString());

		var entry = await MemberEntryAsync(client, adminSession, Member);
		Assert.Equal("active", entry.GetProperty("status").GetString());
		Assert.Equal(memberId.ToString(), entry.GetProperty("accountId").GetString());

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var audit = await db.MemberAdminActions
				.Where(a => a.TargetUserId == memberId && a.Action == MemberAdminActionType.Reactivated)
				.SingleAsync();
			Assert.NotEqual(Guid.Empty, audit.ActorAccountId);
		}

		// Documented session behaviour: the pre-revocation ticket stays dead.
		var meStale = await MeAsync(client, memberSession);
		Assert.False(meStale.GetProperty("authenticated").GetBoolean());

		// Fresh email-code sign-in works again with the same stable ID.
		var freshCode = await RequestCodeAsync(factory, client, Member);
		var freshSession = await VerifyAndGetSessionAsync(client, Member, freshCode);
		var freshMe = await MeAsync(client, freshSession);
		Assert.True(freshMe.GetProperty("authenticated").GetBoolean());
		Assert.Equal(memberId.ToString(), freshMe.GetProperty("accountId").GetString());
		Assert.Contains("Member", freshMe.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
	}

	[Fact]
	public async Task RoleChangeTakesEffectOnNextRequestWithoutRelogin()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		var memberSession = await SignInAsync(factory, Member);
		var memberId = await AccountIdAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, adminSession);
		using var change = AuthedPost("/api/admin/members/role",
			new { accountId = memberId, role = "Editor" }, $"{cookie}; {adminSession}", token);
		using var response = await client.SendAsync(change);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Contains("Rolle aktualisiert", body.GetProperty("message").GetString());
		Assert.Contains("Editor", body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

		// Same stale cookie obeys the new permission on its next request.
		var meAfter = await MeAsync(client, memberSession);
		Assert.True(meAfter.GetProperty("authenticated").GetBoolean());
		Assert.Contains("Editor", meAfter.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var audit = await db.MemberAdminActions
				.Where(a => a.TargetUserId == memberId && a.Action == MemberAdminActionType.RoleChanged)
				.SingleAsync();
			Assert.Contains("Member", audit.OldRoles);
			Assert.Contains("Editor", audit.NewRoles);
		}

		// Same-role change is idempotent without a second audit row.
		var (cookie2, token2) = await GetCsrfAsync(client, adminSession);
		using var same = AuthedPost("/api/admin/members/role",
			new { accountId = memberId, role = "Editor" }, $"{cookie2}; {adminSession}", token2);
		using var sameResponse = await client.SendAsync(same);
		Assert.Equal(HttpStatusCode.OK, sameResponse.StatusCode);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Equal(1, await db.MemberAdminActions.CountAsync(
				a => a.TargetUserId == memberId && a.Action == MemberAdminActionType.RoleChanged));
		}
	}

	[Fact]
	public async Task DemotedAdministratorLosesAdminAccessOnNextRequest()
	{
		await using var factory = new AuthApiFactory();
		var secondAdmin = "zweite-verwaltung@liedertafel.test";
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, secondAdmin, ArchiveRoles.Administrator);
		var adminSession = await SignInAsync(factory, Admin);
		var secondSession = await SignInAsync(factory, secondAdmin);
		var secondId = await AccountIdAsync(factory, secondAdmin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, adminSession);
		using var change = AuthedPost("/api/admin/members/role",
			new { accountId = secondId, role = "Member" }, $"{cookie}; {adminSession}", token);
		using var response = await client.SendAsync(change);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var meAfter = await MeAsync(client, secondSession);
		Assert.True(meAfter.GetProperty("authenticated").GetBoolean());
		Assert.Contains("Member", meAfter.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

		using var list = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
		list.Headers.Add("Cookie", secondSession);
		using var listResponse = await client.SendAsync(list);
		Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
	}

	[Fact]
	public async Task LastAdministratorCannotBeDeactivatedOrDemoted()
	{
		await using var factory = new AuthApiFactory();
		var adminId = await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await DeactivateAsync(client, adminSession, adminId, HttpStatusCode.Conflict);
		await ChangeRoleAsync(client, adminSession, adminId, "Member", HttpStatusCode.Conflict);

		var entry = await MemberEntryAsync(client, adminSession, Admin);
		Assert.Equal("active", entry.GetProperty("status").GetString());
		Assert.Contains("Administrator", entry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Empty(await db.MemberAdminActions.Where(a => a.TargetUserId == adminId).ToListAsync());
		}

		// With a second active administrator the change succeeds; the new
		// last administrator is then protected again.
		var secondAdmin = "zweite-verwaltung@liedertafel.test";
		var secondId = await SeedAsync(factory, secondAdmin, ArchiveRoles.Administrator);
		var adminSession2 = await SignInAsync(factory, Admin);
		await DeactivateAsync(client, adminSession2, secondId, HttpStatusCode.OK);
		await DeactivateAsync(client, adminSession2, adminId, HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task RevocationMutationsRequireFreshVerification()
	{
		var root = new InMemoryDatabaseRoot();
		var database = $"revoke-fresh-{Guid.NewGuid():N}";
		await using var factory = new AuthApiFactory(
			databaseName: database, root: root, freshVerificationWindow: TimeSpan.FromMilliseconds(1));
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		var memberId = await AccountIdAsync(factory, Member);
		await Task.Delay(50);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, adminSession);
		using var deactivate = AuthedPost("/api/admin/members/deactivate",
			new { accountId = memberId }, $"{cookie}; {adminSession}", token);
		using var deactivateResponse = await client.SendAsync(deactivate);
		Assert.Equal(HttpStatusCode.Forbidden, deactivateResponse.StatusCode);
		var problem = await deactivateResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(MemberInvitationService.FreshVerificationMessage, problem.GetProperty("title").GetString());

		var (cookie2, token2) = await GetCsrfAsync(client, adminSession);
		using var reactivate = AuthedPost("/api/admin/members/reactivate",
			new { accountId = memberId }, $"{cookie2}; {adminSession}", token2);
		using var reactivateResponse = await client.SendAsync(reactivate);
		Assert.Equal(HttpStatusCode.Forbidden, reactivateResponse.StatusCode);

		var (cookie3, token3) = await GetCsrfAsync(client, adminSession);
		using var role = AuthedPost("/api/admin/members/role",
			new { accountId = memberId, role = "Editor" }, $"{cookie3}; {adminSession}", token3);
		using var roleResponse = await client.SendAsync(role);
		Assert.Equal(HttpStatusCode.Forbidden, roleResponse.StatusCode);
	}

	[Theory]
	[InlineData(Member, ArchiveRoles.Member)]
	[InlineData(Editor, ArchiveRoles.Editor)]
	public async Task MembersAndEditorsCannotRevokeOrChangeRoles(string email, string role)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, email, role);
		var session = await SignInAsync(factory, email);
		var targetId = await AccountIdAsync(factory, email);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var deactivate = AuthedPost("/api/admin/members/deactivate",
			new { accountId = targetId }, $"{cookie}; {session}", token);
		using var deactivateResponse = await client.SendAsync(deactivate);
		Assert.Equal(HttpStatusCode.Forbidden, deactivateResponse.StatusCode);

		var (cookie2, token2) = await GetCsrfAsync(client, session);
		using var roleChange = AuthedPost("/api/admin/members/role",
			new { accountId = targetId, role = "Editor" }, $"{cookie2}; {session}", token2);
		using var roleResponse = await client.SendAsync(roleChange);
		Assert.Equal(HttpStatusCode.Forbidden, roleResponse.StatusCode);
		Assert.Empty(await AuditsAsync(factory, targetId));
	}

	[Fact]
	public async Task RevocationRejectsInvalidUnknownAndDuplicateStates()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		var memberId = await AccountIdAsync(factory, Member);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (badCookie, badToken) = await GetCsrfAsync(client, adminSession);
		using var badId = AuthedPost("/api/admin/members/deactivate",
			new { accountId = "keine-kennung" }, $"{badCookie}; {adminSession}", badToken);
		using var badIdResponse = await client.SendAsync(badId);
		Assert.Equal(HttpStatusCode.BadRequest, badIdResponse.StatusCode);

		var (unknownCookie, unknownToken) = await GetCsrfAsync(client, adminSession);
		using var unknown = AuthedPost("/api/admin/members/deactivate",
			new { accountId = Guid.NewGuid() }, $"{unknownCookie}; {adminSession}", unknownToken);
		using var unknownResponse = await client.SendAsync(unknown);
		Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);

		var (roleCookie, roleToken) = await GetCsrfAsync(client, adminSession);
		using var badRole = AuthedPost("/api/admin/members/role",
			new { accountId = memberId, role = "Superadmin" }, $"{roleCookie}; {adminSession}", roleToken);
		using var badRoleResponse = await client.SendAsync(badRole);
		Assert.Equal(HttpStatusCode.BadRequest, badRoleResponse.StatusCode);

		await DeactivateAsync(client, adminSession, memberId, HttpStatusCode.OK);
		await DeactivateAsync(client, adminSession, memberId, HttpStatusCode.Conflict);
		await ReactivateAsync(client, adminSession, memberId, HttpStatusCode.OK);
		await ReactivateAsync(client, adminSession, memberId, HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task UnauthenticatedRevocationCallsAreRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		using var deactivate = new HttpRequestMessage(HttpMethod.Post, "/api/admin/members/deactivate");
		deactivate.Content = JsonContent.Create(new { accountId = Guid.NewGuid() });
		using var response = await client.SendAsync(deactivate);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	private static async Task<IReadOnlyList<MemberAdminAction>> AuditsAsync(AuthApiFactory factory, Guid target)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		return await db.MemberAdminActions.Where(a => a.TargetUserId == target).ToListAsync();
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

	private static async Task<Guid> AccountIdAsync(AuthApiFactory factory, string email)
	{
		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		return (await users.FindByEmailAsync(email))!.Id;
	}

	private static async Task<string> SignInAsync(AuthApiFactory factory, string email)
	{
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var code = await RequestCodeAsync(factory, client, email);
		return await VerifyAndGetSessionAsync(client, email, code);
	}

	private static async Task<string> RequestCodeAsync(AuthApiFactory factory, HttpClient client, string email)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var request = AuthedPost("/api/auth/code/request", new { email }, cookie, token);
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		return factory.Mail.Sent.Last(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)).Code;
	}

	private static async Task<string> VerifyAndGetSessionAsync(HttpClient client, string email, string code)
	{
		var (cookie, token) = await GetCsrfAsync(client);
		using var verify = AuthedPost("/api/auth/code/verify", new { email, code }, cookie, token);
		using var response = await client.SendAsync(verify);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
	}

	private static async Task<JsonElement> MeAsync(HttpClient client, string session)
	{
		using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		me.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(me);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static async Task<Guid> AccountIdAsync(HttpClient client, string session)
	{
		var me = await MeAsync(client, session);
		return Guid.Parse(me.GetProperty("accountId").GetString()!);
	}

	private static async Task<JsonElement> MemberEntryAsync(HttpClient client, string adminSession, string email)
	{
		using var list = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
		list.Headers.Add("Cookie", adminSession);
		using var response = await client.SendAsync(list);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return body.GetProperty("members").EnumerateArray()
			.Single(m => string.Equals(m.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase));
	}

	private static async Task<string?> InvitationAcceptedAtAsync(AuthApiFactory factory, Guid accountId)
	{
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var invitation = await db.MemberInvitations.SingleOrDefaultAsync(i => i.UserId == accountId);
		return invitation?.AcceptedAt?.ToString("O");
	}

	private static async Task InviteAsync(
		HttpClient client, string session, string email, string? displayName, string role, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email, displayName, role }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(invite);
		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task DeactivateAsync(HttpClient client, string session, Guid target, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var request = AuthedPost("/api/admin/members/deactivate",
			new { accountId = target }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(request);
		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task ReactivateAsync(HttpClient client, string session, Guid target, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var request = AuthedPost("/api/admin/members/reactivate",
			new { accountId = target }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(request);
		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task ChangeRoleAsync(HttpClient client, string session, Guid target, string role, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var request = AuthedPost("/api/admin/members/role",
			new { accountId = target, role }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(request);
		Assert.Equal(expected, response.StatusCode);
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
}
