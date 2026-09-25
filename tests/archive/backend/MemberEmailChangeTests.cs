using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-008 account repair: verified administrator email change keeps the
/// stable account ID and history, rejects collisions, kills old sessions, and
/// never attaches a new address to another account. The restricted
/// <c>--repair-admin</c> operator command invites/repairs administrators with
/// explicit actor/target logging and no public recovery bypass.
/// </summary>
public sealed class MemberEmailChangeTests
{
	private const string Admin = "verwaltung@liedertafel.test";
	private const string Member = "mitglied@liedertafel.test";

	[Fact]
	public async Task EmailChangeRetainsIdHistoryAndRotatesSessions()
	{
		await using var factory = new AuthApiFactory();
		var adminId = await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Invite + accept a new singer so invitation history exists.
		await InviteAsync(client, adminSession, "neu@liedertafel.test", "Neue Stimme", "Member", HttpStatusCode.Created);
		var acceptCode = await RequestCodeAsync(factory, client, "neu@liedertafel.test");
		var neuSession = await VerifyAndGetSessionAsync(client, "neu@liedertafel.test", acceptCode);
		var neuId = await AccountIdAsync(client, neuSession);
		var acceptedAtBefore = await InvitationAcceptedAtAsync(factory, neuId);
		Assert.NotNull(acceptedAtBefore);
		// An unconsumed sign-in challenge exists for the old address.
		var staleCode = await RequestCodeAsync(factory, client, "neu@liedertafel.test");

		var changeCode = await RequestEmailChangeAsync(factory, client, adminSession, neuId, "neu2@liedertafel.test", HttpStatusCode.OK);

		// Receiving the change mail alone attaches nothing: the new address
		// has no account, so the sign-in flow stays silent and the change
		// code is rejected there.
		var signInMailsBefore = factory.Mail.Sent.Count(m => string.Equals(m.Email, "neu2@liedertafel.test", StringComparison.OrdinalIgnoreCase));
		var (reqCookie, reqToken) = await GetCsrfAsync(client);
		using var signInRequest = AuthedPost("/api/auth/code/request", new { email = "neu2@liedertafel.test" }, reqCookie, reqToken);
		using var signInRequestResponse = await client.SendAsync(signInRequest);
		Assert.Equal(HttpStatusCode.Accepted, signInRequestResponse.StatusCode);
		Assert.Equal(signInMailsBefore, factory.Mail.Sent.Count(m => string.Equals(m.Email, "neu2@liedertafel.test", StringComparison.OrdinalIgnoreCase)));
		var (verifyCookie, verifyToken) = await GetCsrfAsync(client);
		using var misuse = AuthedPost("/api/auth/code/verify",
			new { email = "neu2@liedertafel.test", code = changeCode }, verifyCookie, verifyToken);
		using var misuseResponse = await client.SendAsync(misuse);
		Assert.Equal(HttpStatusCode.BadRequest, misuseResponse.StatusCode);

		await ConfirmEmailChangeAsync(client, adminSession, neuId, "neu2@liedertafel.test", changeCode, HttpStatusCode.OK);

		// Stable identity: same account ID, new address, roles and acceptance kept.
		var entry = await MemberEntryAsync(client, adminSession, "neu2@liedertafel.test");
		Assert.Equal(neuId.ToString(), entry.GetProperty("accountId").GetString());
		Assert.Equal("active", entry.GetProperty("status").GetString());
		Assert.Contains("Member", entry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		// Compare instants, not strings: JSON trims trailing-zero ticks that
		// the store preserves, so raw text differs one run in ten.
		Assert.Equal(
			DateTimeOffset.Parse(acceptedAtBefore),
			DateTimeOffset.Parse(entry.GetProperty("acceptedAt").GetString()!));
		Assert.Equal(adminId.ToString(), entry.GetProperty("invitedByAccountId").GetString());

		// Obsolete sign-in challenges are gone; the invitation follows the move.
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Empty(await db.SignInChallenges.Where(c => c.UserId == neuId).ToListAsync());
			var invitation = await db.MemberInvitations.SingleAsync(i => i.UserId == neuId);
			Assert.Equal(AuthSecurity.NormalizeEmail("neu2@liedertafel.test"), invitation.NormalizedEmail);
			Assert.NotNull(invitation.AcceptedAt);
			var audit = await db.MemberAdminActions
				.Where(a => a.TargetUserId == neuId && a.Action == MemberAdminActionType.EmailChanged)
				.SingleAsync();
			Assert.Equal(adminId, audit.ActorAccountId);
		}

		// The target's open session dies on its next request; the acting
		// admin session is untouched.
		var meAfter = await MeAsync(client, neuSession);
		Assert.False(meAfter.GetProperty("authenticated").GetBoolean());
		var adminMe = await MeAsync(client, adminSession);
		Assert.True(adminMe.GetProperty("authenticated").GetBoolean());

		// The old address no longer signs in (stale code and fresh attempts fail).
		var oldMails = factory.Mail.Sent.Count(m => string.Equals(m.Email, "neu@liedertafel.test", StringComparison.OrdinalIgnoreCase));
		var (oldCookie, oldToken) = await GetCsrfAsync(client);
		using var oldRequest = AuthedPost("/api/auth/code/request", new { email = "neu@liedertafel.test" }, oldCookie, oldToken);
		using var oldRequestResponse = await client.SendAsync(oldRequest);
		Assert.Equal(HttpStatusCode.Accepted, oldRequestResponse.StatusCode);
		Assert.Equal(oldMails, factory.Mail.Sent.Count(m => string.Equals(m.Email, "neu@liedertafel.test", StringComparison.OrdinalIgnoreCase)));
		var (staleCookie, staleToken) = await GetCsrfAsync(client);
		using var staleVerify = AuthedPost("/api/auth/code/verify",
			new { email = "neu@liedertafel.test", code = staleCode }, staleCookie, staleToken);
		using var staleResponse = await client.SendAsync(staleVerify);
		Assert.Equal(HttpStatusCode.BadRequest, staleResponse.StatusCode);

		// Fresh sign-in at the new address returns the same stable ID.
		var freshCode = await RequestCodeAsync(factory, client, "neu2@liedertafel.test");
		var freshSession = await VerifyAndGetSessionAsync(client, "neu2@liedertafel.test", freshCode);
		var freshMe = await MeAsync(client, freshSession);
		Assert.True(freshMe.GetProperty("authenticated").GetBoolean());
		Assert.Equal(neuId.ToString(), freshMe.GetProperty("accountId").GetString());
	}

	[Fact]
	public async Task ChangingOwnAddressSignsOutTheActingAdmin()
	{
		await using var factory = new AuthApiFactory();
		var adminId = await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var changeCode = await RequestEmailChangeAsync(factory, client, adminSession, adminId, "neu-verwaltung@liedertafel.test", HttpStatusCode.OK);
		await ConfirmEmailChangeAsync(client, adminSession, adminId, "neu-verwaltung@liedertafel.test", changeCode, HttpStatusCode.OK);

		var meAfter = await MeAsync(client, adminSession);
		Assert.False(meAfter.GetProperty("authenticated").GetBoolean());

		var freshCode = await RequestCodeAsync(factory, client, "neu-verwaltung@liedertafel.test");
		var freshSession = await VerifyAndGetSessionAsync(client, "neu-verwaltung@liedertafel.test", freshCode);
		var freshMe = await MeAsync(client, freshSession);
		Assert.True(freshMe.GetProperty("authenticated").GetBoolean());
		Assert.Equal(adminId.ToString(), freshMe.GetProperty("accountId").GetString());
		Assert.Contains("Administrator", freshMe.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
	}

	[Fact]
	public async Task EmailChangeRejectsCollisionWithoutSideEffects()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var memberId = await SeedAsync(factory, Member, ArchiveRoles.Member);
		var otherId = await SeedAsync(factory, "zweite@liedertafel.test", ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await RequestEmailChangeAsync(factory, client, adminSession, memberId, "zweite@liedertafel.test", HttpStatusCode.Conflict);

		// No mail, no challenge, no audit, nothing changed.
		Assert.Empty(factory.Mail.SentEmailChanges);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			Assert.Empty(await db.MemberEmailChanges.Where(c => c.UserId == memberId).ToListAsync());
			Assert.Empty(await db.MemberAdminActions.Where(a => a.TargetUserId == memberId).ToListAsync());
			var users = scope.ServiceProvider.GetRequiredService<UserManager<ArchiveUser>>();
			Assert.Equal(Member, (await users.FindByIdAsync(memberId.ToString()))!.Email);
			Assert.Equal(otherId, (await users.FindByEmailAsync("zweite@liedertafel.test"))!.Id);
		}
	}

	[Fact]
	public async Task EmailChangeRejectsInvalidUnknownSameAndBadCodes()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var memberId = await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		// Invalid ID, unknown ID, invalid and same address on request.
		await RequestEmailChangeRawAsync(client, adminSession, "keine-kennung", Member, HttpStatusCode.BadRequest);
		await RequestEmailChangeRawAsync(client, adminSession, Guid.NewGuid().ToString(), Member, HttpStatusCode.NotFound);
		await RequestEmailChangeRawAsync(client, adminSession, memberId.ToString(), "keine-mail", HttpStatusCode.BadRequest);
		await RequestEmailChangeRawAsync(client, adminSession, memberId.ToString(), Member, HttpStatusCode.BadRequest);

		// Confirm without a pending challenge, with a wrong code, and past
		// the per-code attempt cap the correct code dies as well.
		await ConfirmEmailChangeRawAsync(client, adminSession, memberId, "neu@liedertafel.test", "123456", HttpStatusCode.BadRequest);
		var changeCode = await RequestEmailChangeAsync(factory, client, adminSession, memberId, "neu@liedertafel.test", HttpStatusCode.OK);
		for (var i = 0; i < 5; i++)
			await ConfirmEmailChangeRawAsync(client, adminSession, memberId, "neu@liedertafel.test", "000000", HttpStatusCode.BadRequest);
		await ConfirmEmailChangeRawAsync(client, adminSession, memberId, "neu@liedertafel.test", changeCode, HttpStatusCode.BadRequest);

		// Expired challenges are rejected like invalid ones.
		var secondCode = await RequestEmailChangeAsync(factory, client, adminSession, memberId, "spaet@liedertafel.test", HttpStatusCode.OK);
		using (var scope = factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var pending = await db.MemberEmailChanges
				.Where(c => c.UserId == memberId && c.ConsumedAt == null)
				.OrderByDescending(c => c.CreatedAt)
				.FirstAsync();
			pending.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
			await db.SaveChangesAsync();
		}
		await ConfirmEmailChangeRawAsync(client, adminSession, memberId, "spaet@liedertafel.test", secondCode, HttpStatusCode.BadRequest);

		// A newer request supersedes the earlier code.
		var superseded = await RequestEmailChangeAsync(factory, client, adminSession, memberId, "frisch@liedertafel.test", HttpStatusCode.OK);
		_ = superseded;
		var fresh = await RequestEmailChangeAsync(factory, client, adminSession, memberId, "frisch@liedertafel.test", HttpStatusCode.OK);
		await ConfirmEmailChangeRawAsync(client, adminSession, memberId, "frisch@liedertafel.test", superseded, HttpStatusCode.BadRequest);
		await ConfirmEmailChangeAsync(client, adminSession, memberId, "frisch@liedertafel.test", fresh, HttpStatusCode.OK);
	}

	[Fact]
	public async Task EmailChangeMailFailureKeepsChangePending()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var memberId = await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		factory.Mail.FailInvitations(_ => new InvalidOperationException("smtp down"));
		try
		{
			await RequestEmailChangeAsync(factory, client, adminSession, memberId, "neu@liedertafel.test", HttpStatusCode.BadGateway);
		}
		finally
		{
			factory.Mail.FailInvitations(_ => null);
		}
		var retryCode = await RequestEmailChangeAsync(factory, client, adminSession, memberId, "neu@liedertafel.test", HttpStatusCode.OK);
		await ConfirmEmailChangeAsync(client, adminSession, memberId, "neu@liedertafel.test", retryCode, HttpStatusCode.OK);
	}

	[Fact]
	public async Task EmailChangeMutationsRequireFreshVerification()
	{
		var root = new InMemoryDatabaseRoot();
		var database = $"emailchange-fresh-{Guid.NewGuid():N}";
		await using var factory = new AuthApiFactory(
			databaseName: database, root: root, freshVerificationWindow: TimeSpan.FromMilliseconds(1));
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var memberId = await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		await Task.Delay(50);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, adminSession);
		using var request = AuthedPost("/api/admin/members/email/request",
			new { accountId = memberId, newEmail = "neu@liedertafel.test" }, $"{cookie}; {adminSession}", token);
		using var requestResponse = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, requestResponse.StatusCode);

		var (cookie2, token2) = await GetCsrfAsync(client, adminSession);
		using var confirm = AuthedPost("/api/admin/members/email/confirm",
			new { accountId = memberId, newEmail = "neu@liedertafel.test", code = "123456" }, $"{cookie2}; {adminSession}", token2);
		using var confirmResponse = await client.SendAsync(confirm);
		Assert.Equal(HttpStatusCode.Forbidden, confirmResponse.StatusCode);
	}

	[Theory]
	[InlineData(Member, ArchiveRoles.Member)]
	public async Task MembersAndEditorsCannotChangeEmails(string email, string role)
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		await SeedAsync(factory, email, role);
		var session = await SignInAsync(factory, email);
		var targetId = await AccountIdAsync(factory, email);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		var (cookie, token) = await GetCsrfAsync(client, session);
		using var request = AuthedPost("/api/admin/members/email/request",
			new { accountId = targetId, newEmail = "neu@liedertafel.test" }, $"{cookie}; {session}", token);
		using var requestResponse = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, requestResponse.StatusCode);
		Assert.Empty(factory.Mail.SentEmailChanges);
	}

	[Fact]
	public async Task RepairCreatesInvitesAndRepairsAdministrators()
	{
		var services = RepairServices("Development", new Dictionary<string, string?>());
		await using var provider = services.BuildServiceProvider();
		var configuration = provider.GetRequiredService<IConfiguration>();

		// New address: invite path creates a confirmed admin with history.
		var invitedId = await OperatorConfiguration.RepairAdminAsync(provider, configuration,
			["--email", "reparatur@liedertafel.test", "--name", "Reparatur", "--operator", "Wartung"], CancellationToken.None);
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var db = provider.GetRequiredService<ArchiveDbContext>();
		var invited = await users.FindByEmailAsync("reparatur@liedertafel.test");
		Assert.NotNull(invited);
		Assert.Equal(invitedId, invited.Id);
		Assert.True(invited.EmailConfirmed);
		Assert.True(await users.IsInRoleAsync(invited, ArchiveRoles.Administrator));
		var invitation = await db.MemberInvitations.SingleAsync(i => i.UserId == invitedId);
		Assert.NotNull(invitation.AcceptedAt);
		var createdAudit = await db.MemberAdminActions
			.Where(a => a.TargetUserId == invitedId && a.Action == MemberAdminActionType.AdministratorRepaired)
			.SingleAsync();
		Assert.Equal(Guid.Empty, createdAudit.ActorAccountId);
		Assert.Contains("Wartung", createdAudit.Note);

		// Idempotent: an already active administrator gains no second audit row.
		var sameId = await OperatorConfiguration.RepairAdminAsync(provider, configuration,
			["--email", "reparatur@liedertafel.test", "--operator", "Wartung"], CancellationToken.None);
		Assert.Equal(invitedId, sameId);
		Assert.Equal(1, await db.MemberAdminActions.CountAsync(
			a => a.TargetUserId == invitedId && a.Action == MemberAdminActionType.AdministratorRepaired));

		// Deactivated sole administrator: repair reactivates with the same ID.
		var roleManager = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roleManager.RoleExistsAsync(ArchiveRoles.Member))
			Assert.True((await roleManager.CreateAsync(new ArchiveRole(ArchiveRoles.Member))).Succeeded);
		var member = new ArchiveUser { UserName = Member, Email = Member, DisplayName = "Test", EmailConfirmed = true };
		Assert.True((await users.CreateAsync(member)).Succeeded);
		Assert.True((await users.AddToRoleAsync(member, ArchiveRoles.Member)).Succeeded);
		member.LockoutEnabled = true;
		member.LockoutEnd = DateTimeOffset.UtcNow.AddYears(100);
		Assert.True((await users.UpdateAsync(member)).Succeeded);
		Assert.True(await users.IsLockedOutAsync(member));
		// ARC-011-1 compromise recovery: a repair revokes registered passkeys.
		Assert.True((await users.AddOrUpdatePasskeyAsync(member, new UserPasskeyInfo(
			[1, 2, 3, 4], [5, 6], DateTimeOffset.UtcNow, 1, null, true, false, false, [7], [8]))).Succeeded);
		Assert.Single(await users.GetPasskeysAsync(member));
		var repairedMemberId = await OperatorConfiguration.RepairAdminAsync(provider, configuration,
			["--email", Member, "--operator", "Wartung"], CancellationToken.None);
		Assert.Equal(member.Id, repairedMemberId);
		var repaired = await users.FindByEmailAsync(Member);
		Assert.NotNull(repaired);
		Assert.True(repaired.EmailConfirmed);
		Assert.False(await users.IsLockedOutAsync(repaired));
		Assert.True(await users.IsInRoleAsync(repaired, ArchiveRoles.Administrator));
		Assert.Empty(await users.GetPasskeysAsync(repaired!));
		var repairAudit = await db.MemberAdminActions
			.Where(a => a.TargetUserId == member.Id && a.Action == MemberAdminActionType.AdministratorRepaired)
			.SingleAsync();
		Assert.Equal(Guid.Empty, repairAudit.ActorAccountId);
		Assert.Contains("Member", repairAudit.OldRoles);
		Assert.Contains(ArchiveRoles.Administrator, repairAudit.NewRoles);
	}

	[Fact]
	public async Task RepairMovesAnAccountToAFreeAddress()
	{
		var services = RepairServices("Development", new Dictionary<string, string?>());
		await using var provider = services.BuildServiceProvider();
		var configuration = provider.GetRequiredService<IConfiguration>();
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var db = provider.GetRequiredService<ArchiveDbContext>();
		var roleManager = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		if (!await roleManager.RoleExistsAsync(ArchiveRoles.Member))
			Assert.True((await roleManager.CreateAsync(new ArchiveRole(ArchiveRoles.Member))).Succeeded);

		var member = new ArchiveUser { UserName = Member, Email = Member, DisplayName = "Test", EmailConfirmed = true };
		Assert.True((await users.CreateAsync(member)).Succeeded);
		Assert.True((await users.AddToRoleAsync(member, ArchiveRoles.Member)).Succeeded);
		db.MemberInvitations.Add(new MemberInvitation
		{
			UserId = member.Id,
			NormalizedEmail = AuthSecurity.NormalizeEmail(Member),
			Role = ArchiveRoles.Member,
			InvitedAt = DateTimeOffset.UtcNow,
			AcceptedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
		// ARC-011-1 compromise recovery: moving an account revokes passkeys
		// even though credentials belong to the stable account ID.
		Assert.True((await users.AddOrUpdatePasskeyAsync(member, new UserPasskeyInfo(
			[1, 2, 3, 4], [5, 6], DateTimeOffset.UtcNow, 1, null, true, false, false, [7], [8]))).Succeeded);
		Assert.Single(await users.GetPasskeysAsync(member));
		var other = new ArchiveUser { UserName = "anderer@liedertafel.test", Email = "anderer@liedertafel.test", EmailConfirmed = true };
		Assert.True((await users.CreateAsync(other)).Succeeded);

		// Collision with another account is refused without changes.
		await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorConfiguration.RepairAdminAsync(
			provider, configuration,
			["--accountId", member.Id.ToString(), "--email", "anderer@liedertafel.test", "--operator", "Wartung"],
			CancellationToken.None));
		Assert.Equal(Member, (await users.FindByIdAsync(member.Id.ToString()))!.Email);

		var movedId = await OperatorConfiguration.RepairAdminAsync(provider, configuration,
			["--accountId", member.Id.ToString(), "--email", "umgezogen@liedertafel.test", "--operator", "Wartung"],
			CancellationToken.None);
		Assert.Equal(member.Id, movedId);
		var moved = await users.FindByIdAsync(member.Id.ToString());
		Assert.NotNull(moved);
		Assert.Equal("umgezogen@liedertafel.test", moved.Email);
		Assert.True(moved.EmailConfirmed);
		Assert.True(await users.IsInRoleAsync(moved, ArchiveRoles.Administrator));
		Assert.Equal(
			AuthSecurity.NormalizeEmail("umgezogen@liedertafel.test"),
			(await db.MemberInvitations.SingleAsync(i => i.UserId == member.Id)).NormalizedEmail);
		Assert.Null(await users.FindByEmailAsync(Member));
		var audit = await db.MemberAdminActions
			.Where(a => a.TargetUserId == member.Id && a.Action == MemberAdminActionType.AdministratorRepaired)
			.SingleAsync();
		Assert.Contains("Wartung", audit.Note);
		Assert.Empty(await users.GetPasskeysAsync(moved!));
	}

	[Fact]
	public async Task RepairOutsideDevelopmentRequiresOperatorToken()
	{
		var services = RepairServices("Production", new Dictionary<string, string?>
		{
			["Archive:OperatorToken"] = "geheim",
		});
		await using var provider = services.BuildServiceProvider();
		var configuration = provider.GetRequiredService<IConfiguration>();
		await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorConfiguration.RepairAdminAsync(
			provider, configuration, ["--email", "admin@liedertafel.test"], CancellationToken.None));
		var id = await OperatorConfiguration.RepairAdminAsync(provider, configuration,
			["--email", "admin@liedertafel.test", "--operator-token", "geheim", "--operator", "Wartung"],
			CancellationToken.None);
		Assert.NotEqual(Guid.Empty, id);
		// Repair returns the account ID only: no code, password or session
		// secret is ever printed or returned.
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var admin = await users.FindByEmailAsync("admin@liedertafel.test");
		Assert.NotNull(admin);
		Assert.Null(admin.PasswordHash);
	}

	[Fact]
	public async Task EmailChangeCodesAreNeverStoredPlaintext()
	{
		await using var factory = new AuthApiFactory();
		await SeedAsync(factory, Admin, ArchiveRoles.Administrator);
		var memberId = await SeedAsync(factory, Member, ArchiveRoles.Member);
		var adminSession = await SignInAsync(factory, Admin);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		await RequestEmailChangeRawAsync(client, adminSession, memberId.ToString(), "neu@liedertafel.test", HttpStatusCode.OK);
		var code = Assert.Single(factory.Mail.SentEmailChanges).Code;
		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var row = await db.MemberEmailChanges.SingleAsync(c => c.UserId == memberId);
		Assert.Equal(32, row.CodeHash.Length);
		Assert.Equal(16, row.Salt.Length);
		Assert.True(AuthSecurity.VerifyCode(code, row.Salt, row.CodeHash));
		Assert.False(row.CodeHash.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(code)));
	}

	private static ServiceCollection RepairServices(string environment, Dictionary<string, string?> settings)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IHostEnvironment>(new RepairHostEnvironment(environment));
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
		// The Identity schema version rides IdentityOptions through the EF
		// application service provider. Without one the model silently falls
		// back to Version 1 (no passkey tables) and passkey operations in the
		// repair flow throw — EF also caches the first built model process-wide,
		// so this must not depend on test order. Mirrors the design-time
		// factory in ArchiveDbContext (ARC-011-1).
		var identityOptions = new OptionsWrapper<IdentityOptions>(new IdentityOptions
		{
			Stores = { SchemaVersion = IdentitySchemaVersions.Version3 },
		});
		var applicationServices = new ServiceCollection()
			.AddSingleton<IOptions<IdentityOptions>>(identityOptions)
			.BuildServiceProvider();
		services.AddIdentity<ArchiveUser, ArchiveRole>()
			.AddEntityFrameworkStores<ArchiveDbContext>()
			.AddDefaultTokenProviders()
			.AddTokenProvider<EmailCodeTokenProvider>(EmailCodeTokenProvider.ProviderName);
		var root = new InMemoryDatabaseRoot();
		services.AddDbContext<ArchiveDbContext>(options =>
			options.UseInMemoryDatabase($"repair-{Guid.NewGuid():N}", root)
				.UseApplicationServiceProvider(applicationServices));
		return services;
	}

	private sealed class RepairHostEnvironment(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;
		public string ApplicationName { get; set; } = "Archive.Backend.Tests";
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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

	private static async Task<string> RequestEmailChangeAsync(
		AuthApiFactory factory, HttpClient client, string session, Guid target, string newEmail, HttpStatusCode expected)
	{
		await RequestEmailChangeRawAsync(client, session, target.ToString(), newEmail, expected);
		if (expected != HttpStatusCode.OK)
			return string.Empty;
		return factory.Mail.SentEmailChanges
			.Last(m => string.Equals(m.Email, newEmail, StringComparison.OrdinalIgnoreCase)).Code;
	}

	private static async Task RequestEmailChangeRawAsync(
		HttpClient client, string session, string accountId, string newEmail, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var request = AuthedPost("/api/admin/members/email/request",
			new { accountId, newEmail }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(request);
		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task ConfirmEmailChangeAsync(
		HttpClient client, string session, Guid target, string newEmail, string code, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var confirm = AuthedPost("/api/admin/members/email/confirm",
			new { accountId = target, newEmail, code }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(confirm);
		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task ConfirmEmailChangeRawAsync(
		HttpClient client, string session, Guid target, string newEmail, string code, HttpStatusCode expected)
	{
		var (cookie, token) = await GetCsrfAsync(client, session);
		using var confirm = AuthedPost("/api/admin/members/email/confirm",
			new { accountId = target.ToString(), newEmail, code }, $"{cookie}; {session}", token);
		using var response = await client.SendAsync(confirm);
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
