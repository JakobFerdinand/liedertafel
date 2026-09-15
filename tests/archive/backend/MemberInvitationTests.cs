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
/// ARC-006 member invitations: admin list/invite/resend, fresh-verification
/// guard, role isolation, duplicate idempotency, retryable mail failures and
/// acceptance attribution to the stable account ID.
/// </summary>
public sealed class MemberInvitationTests
{
	private const string Admin = "verwaltung@liedertafel.test";
	private const string Member = "mitglied@liedertafel.test";
	private const string Editor = "redaktion@liedertafel.test";

	[Fact]
	public async Task AdminListsMembershipStates()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
		request.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		var members = body.GetProperty("members").EnumerateArray().ToList();
		Assert.Equal(2, members.Count);
		var admin = members.Single(m => m.GetProperty("email").GetString() == Admin);
		Assert.Equal("active", admin.GetProperty("status").GetString());
		Assert.Contains("Administrator", admin.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		Assert.NotEmpty(admin.GetProperty("accountId").GetString()!);
	}

	[Fact]
	public async Task AdminInvitesNewMemberWithRoleAndMail()
	{
		await using var factory = new AuthApiFactory();
		var adminId = await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email = "neu@liedertafel.test", displayName = "Neue Stimme", role = "Member" },
			$"{cookie}; {session}", token);
		using var response = await client.SendAsync(invite);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Contains("zum Versand angenommen", body.GetProperty("message").GetString());
		Assert.Contains("Zustellung wird nicht bestätigt", body.GetProperty("message").GetString());
		var accountId = body.GetProperty("accountId").GetString()!;
		var invitationId = body.GetProperty("invitationId").GetString()!;
		Assert.NotEmpty(accountId);
		Assert.NotEmpty(invitationId);

		var sent = Assert.Single(factory.Mail.SentInvitations);
		Assert.Equal("neu@liedertafel.test", sent.Email);
		Assert.Equal("Member", sent.Role);

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var invitation = await db.MemberInvitations.SingleAsync(i => i.Id == Guid.Parse(invitationId));
		Assert.Equal(Guid.Parse(accountId), invitation.UserId);
		Assert.Equal(adminId, invitation.InvitedByAccountId);
		Assert.Equal(InvitationMailStatus.Sent, invitation.MailStatus);
		Assert.Null(invitation.AcceptedAt);
		Assert.NotNull(invitation.LastSentAt);
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var user = await users.FindByEmailAsync("neu@liedertafel.test");
		Assert.NotNull(user);
		Assert.False(user.EmailConfirmed);
		Assert.True(await users.IsInRoleAsync(user, ArchiveRoles.Member));
	}

	[Fact]
	public async Task InviteRequiresFreshVerification()
	{
		var root = new InMemoryDatabaseRoot();
		var database = $"invite-fresh-{Guid.NewGuid():N}";
		await using var factory = new AuthApiFactory(
			databaseName: database, root: root, freshVerificationWindow: TimeSpan.FromMilliseconds(1));
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		await Task.Delay(50);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email = "spaet@liedertafel.test", role = "Member" },
			$"{cookie}; {session}", token);
		using var response = await client.SendAsync(invite);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(MemberInvitationService.FreshVerificationMessage, problem.GetProperty("title").GetString());
		Assert.Empty(factory.Mail.SentInvitations);
	}

	[Theory]
	[InlineData(Member, ArchiveRoles.Member)]
	[InlineData(Editor, ArchiveRoles.Editor)]
	public async Task MembersAndEditorsCannotInvokeInvitationAdmin(string email, string role)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, email, role);
		var session = await SignInAsync(factory, email);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var list = new HttpRequestMessage(HttpMethod.Get, "/api/admin/members");
		list.Headers.Add("Cookie", session);
		using var listResponse = await client.SendAsync(list);
		Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email = "versuch@liedertafel.test", role = "Member" },
			$"{cookie}; {session}", token);
		using var inviteResponse = await client.SendAsync(invite);
		Assert.Equal(HttpStatusCode.Forbidden, inviteResponse.StatusCode);
		var problem = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(MemberInvitationService.ForbiddenMessage, problem.GetProperty("title").GetString());

		using var resend = AuthedPost("/api/admin/invitations/resend",
			new { email },
			$"{cookie}; {session}", token);
		using var resendResponse = await client.SendAsync(resend);
		Assert.Equal(HttpStatusCode.Forbidden, resendResponse.StatusCode);
		Assert.Empty(factory.Mail.SentInvitations);
	}

	[Fact]
	public async Task UnauthenticatedAdminCallsAreRejected()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var list = await client.GetAsync("/api/admin/members");
		Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
	}

	[Fact]
	public async Task DuplicateInviteWithSameRoleResendsWithoutDuplicating()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await InviteAsync(client, session, "doppel@liedertafel.test", "Doppel", "Member", HttpStatusCode.Created);
		await InviteAsync(client, session, "doppel@liedertafel.test", "Doppel", "Member", HttpStatusCode.OK);

		Assert.Equal(2, factory.Mail.SentInvitations.Count);
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var normalized = AuthSecurity.NormalizeEmail("doppel@liedertafel.test");
		Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == normalized));
		Assert.Equal(1, await db.MemberInvitations.CountAsync(i => i.NormalizedEmail == normalized));
	}

	[Fact]
	public async Task DuplicateInviteWithDifferentRoleKeepsOriginalRole()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await InviteAsync(client, session, "rolle@liedertafel.test", null, "Member", HttpStatusCode.Created);
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var conflict = AuthedPost("/api/admin/invitations",
			new { email = "rolle@liedertafel.test", role = "Editor" },
			$"{cookie}; {session}", token);
		using var response = await client.SendAsync(conflict);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(MemberInvitationService.RoleConflictMessage, problem.GetProperty("title").GetString());
		Assert.Single(factory.Mail.SentInvitations);

		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var user = (await users.FindByEmailAsync("rolle@liedertafel.test"))!;
		Assert.True(await users.IsInRoleAsync(user, ArchiveRoles.Member));
		Assert.False(await users.IsInRoleAsync(user, ArchiveRoles.Editor));
	}

	[Fact]
	public async Task InviteExistingActiveMemberChangesNothing()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email = Member, role = "Editor" },
			$"{cookie}; {session}", token);
		using var response = await client.SendAsync(invite);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(MemberInvitationService.AlreadyActiveMessage, problem.GetProperty("title").GetString());
		Assert.Empty(factory.Mail.SentInvitations);

		using var scope = factory.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
		var user = (await users.FindByEmailAsync(Member))!;
		Assert.True(await users.IsInRoleAsync(user, ArchiveRoles.Member));
		Assert.False(await users.IsInRoleAsync(user, ArchiveRoles.Editor));
	}

	[Fact]
	public async Task ResendPendingAndRejectAcceptedAndUnknown()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await InviteAsync(client, session, "erneut@liedertafel.test", null, "Member", HttpStatusCode.Created);
		await ResendAsync(client, session, "erneut@liedertafel.test", HttpStatusCode.OK);
		Assert.Equal(2, factory.Mail.SentInvitations.Count);

		await ResendAsync(client, session, Member, HttpStatusCode.Conflict);
		await ResendAsync(client, session, "unbekannt@liedertafel.test", HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task MailFailureKeepsRetryableInvitationAndResendRecovers()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		factory.Mail.FailInvitations(_ => new InvalidOperationException("SMTP rejected"));
		var (failCookie, failToken) = await GetCsrfAsync(client, session);
		using var failed = AuthedPost("/api/admin/invitations",
			new { email = "fehler@liedertafel.test", role = "Member" },
			$"{failCookie}; {session}", failToken);
		using var failedResponse = await client.SendAsync(failed);

		Assert.Equal(HttpStatusCode.BadGateway, failedResponse.StatusCode);
		var problem = await failedResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(MemberInvitationService.MailFailedMessage, problem.GetProperty("title").GetString());
		Assert.Empty(factory.Mail.SentInvitations);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var invitation = await db.MemberInvitations
				.SingleAsync(i => i.NormalizedEmail == AuthSecurity.NormalizeEmail("fehler@liedertafel.test"));
			Assert.Equal(InvitationMailStatus.Failed, invitation.MailStatus);
			Assert.NotNull(invitation.LastError);
		}

		factory.Mail.FailInvitations(_ => null);
		await ResendAsync(client, session, "fehler@liedertafel.test", HttpStatusCode.OK);
		Assert.Single(factory.Mail.SentInvitations);

		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var invitation = await db.MemberInvitations
				.SingleAsync(i => i.NormalizedEmail == AuthSecurity.NormalizeEmail("fehler@liedertafel.test"));
			Assert.Equal(InvitationMailStatus.Sent, invitation.MailStatus);
			Assert.Null(invitation.LastError);
			Assert.NotNull(invitation.LastSentAt);
		}
	}

	[Fact]
	public async Task AcceptanceLinksToStableAccountIdWithAttribution()
	{
		await using var factory = new AuthApiFactory();
		var adminId = await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await InviteAsync(client, session, "neuankömmling@liedertafel.test", "Neuankömmling", "Editor", HttpStatusCode.Created);
		var invitedMail = Assert.Single(factory.Mail.SentInvitations);

		var code = await RequestCodeAsync(factory, client, invitedMail.Email);
		var invitedSession = await VerifyAndGetSessionAsync(client, invitedMail.Email, code);

		using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
		me.Headers.Add("Cookie", invitedSession);
		using var meResponse = await client.SendAsync(me);
		var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(meBody.GetProperty("authenticated").GetBoolean());
		var accountId = meBody.GetProperty("accountId").GetString()!;
		Assert.Contains("Editor", meBody.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var invitation = await db.MemberInvitations.SingleAsync(i => i.UserId == Guid.Parse(accountId));
		Assert.Equal(adminId, invitation.InvitedByAccountId);
		Assert.NotNull(invitation.AcceptedAt);
		Assert.Equal("Editor", invitation.Role);
	}

	[Fact]
	public async Task InvitationMutationsRejectMissingOrForgedCsrf()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var missing = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
		missing.Headers.Add("Cookie", session);
		missing.Content = JsonContent.Create(new { email = "csrf@liedertafel.test", role = "Member" });
		using var missingResponse = await client.SendAsync(missing);
		Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);

		var (cookie, _) = await GetCsrfAsync(client, session);
		using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations");
		forged.Headers.Add("Cookie", cookie);
		forged.Headers.Add("X-CSRF-TOKEN", "forged");
		forged.Content = JsonContent.Create(new { email = "csrf@liedertafel.test", role = "Member" });
		using var forgedResponse = await client.SendAsync(forged);
		Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);
	}

	[Theory]
	[InlineData("keine-mail", "Member")]
	[InlineData("ok@liedertafel.test", "Superadmin")]
	[InlineData("", "Member")]
	public async Task InviteRejectsInvalidEmailAndRole(string email, string role)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var session = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email, role }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(invite);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Mail.SentInvitations);
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

	private static async Task InviteAsync(
		HttpClient client, string session, string email, string? displayName, string role, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var invite = AuthedPost("/api/admin/invitations",
			new { email, displayName, role }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(invite);
		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task ResendAsync(HttpClient client, string session, string email, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var resend = AuthedPost("/api/admin/invitations/resend",
			new { email }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(resend);
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
