using System.Security.Claims;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Archive.Backend.Recordings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Tests;

public sealed class ArchiveAccessTests
{
	private static ArchiveAccessDecision ActiveMember() => new(
		IsAuthenticated: true,
		AccountId: Guid.CreateVersion7(),
		Email: "mitglied@liedertafel.at",
		Roles: [ArchiveRoles.Member],
		IsActive: true,
		IsAdministrator: false);

	[Fact]
	public void DecideMemberAllowsActiveMemberAndRejectsNullOrInactiveWith401()
	{
		var member = ActiveMember();
		var inactive = member with { IsAuthenticated = false, IsActive = false };

		var (allowedDecision, allowedError) = ArchiveAccess.DecideMember(member);
		Assert.Same(member, allowedDecision);
		Assert.Null(allowedError);
		Assert.True(ArchiveAccess.IsMember(member));

		var (nullDecision, nullError) = ArchiveAccess.DecideMember(null);
		Assert.Null(nullDecision);
		var nullProblem = Assert.IsType<ProblemHttpResult>(nullError);
		Assert.Equal(StatusCodes.Status401Unauthorized, nullProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, nullProblem.ProblemDetails.Title);
		Assert.False(ArchiveAccess.IsMember(null));

		var (inactiveDecision, inactiveError) = ArchiveAccess.DecideMember(inactive);
		Assert.Null(inactiveDecision);
		var inactiveProblem = Assert.IsType<ProblemHttpResult>(inactiveError);
		Assert.Equal(StatusCodes.Status401Unauthorized, inactiveProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, inactiveProblem.ProblemDetails.Title);
		Assert.False(ArchiveAccess.IsMember(inactive));
	}

	[Fact]
	public void DecideEditorAllowsEditorOrAdministratorAndDistinguishes401From403()
	{
		var member = ActiveMember();
		var editor = member with { Roles = [ArchiveRoles.Member, ArchiveRoles.Editor] };
		var admin = member with { Roles = [ArchiveRoles.Administrator], IsAdministrator = true };
		var inactiveEditor = editor with { IsAuthenticated = false, IsActive = false };

		Assert.True(ArchiveAccess.IsEditor(editor));
		var (editorDecision, editorError) = ArchiveAccess.DecideEditor(editor);
		Assert.Same(editor, editorDecision);
		Assert.Null(editorError);

		Assert.True(ArchiveAccess.IsEditor(admin));
		var (adminDecision, adminError) = ArchiveAccess.DecideEditor(admin);
		Assert.Same(admin, adminDecision);
		Assert.Null(adminError);

		Assert.False(ArchiveAccess.IsEditor(member));
		var (memberDecision, memberError) = ArchiveAccess.DecideEditor(member);
		Assert.Null(memberDecision);
		var memberProblem = Assert.IsType<ProblemHttpResult>(memberError);
		Assert.Equal(StatusCodes.Status403Forbidden, memberProblem.StatusCode);
		Assert.Equal(ArchiveAccess.ForbiddenMessage, memberProblem.ProblemDetails.Title);

		Assert.False(ArchiveAccess.IsEditor(null));
		var (nullDecision, nullError) = ArchiveAccess.DecideEditor(null);
		Assert.Null(nullDecision);
		var nullProblem = Assert.IsType<ProblemHttpResult>(nullError);
		Assert.Equal(StatusCodes.Status401Unauthorized, nullProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, nullProblem.ProblemDetails.Title);

		Assert.False(ArchiveAccess.IsEditor(inactiveEditor));
		var (inactiveDecision, inactiveError) = ArchiveAccess.DecideEditor(inactiveEditor);
		Assert.Null(inactiveDecision);
		var inactiveProblem = Assert.IsType<ProblemHttpResult>(inactiveError);
		Assert.Equal(StatusCodes.Status401Unauthorized, inactiveProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, inactiveProblem.ProblemDetails.Title);
	}

	[Fact]
	public async Task ValidateAntiforgeryAppliesNoStoreAndReturns400OnRejectionOrNullWhenValid()
	{
		var okContext = new DefaultHttpContext();
		var okError = await ArchiveAccess.ValidateAntiforgeryAsync(okContext, new StubAntiforgery(reject: false));
		Assert.Null(okError);
		Assert.Equal("no-store", okContext.Response.Headers.CacheControl.ToString());

		var rejectedContext = new DefaultHttpContext();
		var rejectedError = await ArchiveAccess.ValidateAntiforgeryAsync(rejectedContext, new StubAntiforgery(reject: true));
		Assert.Equal("no-store", rejectedContext.Response.Headers.CacheControl.ToString());
		var problem = Assert.IsType<ProblemHttpResult>(rejectedError);
		Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
		Assert.Equal(ArchiveAccess.InvalidAntiforgeryMessage, problem.ProblemDetails.Title);
	}

	[Fact]
	public async Task RequireMemberAndEditorEnforceNoStoreAntiforgeryCookieAndDatabaseDecision()
	{
		var accountId = Guid.CreateVersion7();
		var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

		var anonymousContext = new DefaultHttpContext();
		var (anonDecision, anonError) = await ArchiveAccess.RequireMemberAsync(
			anonymousContext, CreateAccessor(anonymousContext, now), CreateAccessService(null));
		Assert.Equal("no-store", anonymousContext.Response.Headers.CacheControl.ToString());
		Assert.Null(anonDecision);
		var anonProblem = Assert.IsType<ProblemHttpResult>(anonError);
		Assert.Equal(StatusCodes.Status401Unauthorized, anonProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, anonProblem.ProblemDetails.Title);

		var memberUser = new ArchiveUser { Id = accountId, Email = "mitglied@liedertafel.at", EmailConfirmed = true };
		var memberContext = AuthenticatedContext(accountId, now);
		var memberAccess = CreateAccessService(memberUser, lockedOut: false, [ArchiveRoles.Member]);
		var (memberDecision, memberError) = await ArchiveAccess.RequireMemberAsync(
			memberContext, new StubAntiforgery(reject: false), CreateAccessor(memberContext, now), memberAccess);
		Assert.Equal("no-store", memberContext.Response.Headers.CacheControl.ToString());
		Assert.Null(memberError);
		Assert.NotNull(memberDecision);
		Assert.Equal(accountId, memberDecision.AccountId);

		var csrfMemberContext = AuthenticatedContext(accountId, now);
		var (csrfMemberDecision, csrfMemberError) = await ArchiveAccess.RequireMemberAsync(
			csrfMemberContext, new StubAntiforgery(reject: true), CreateAccessor(csrfMemberContext, now), memberAccess);
		Assert.Equal("no-store", csrfMemberContext.Response.Headers.CacheControl.ToString());
		Assert.Null(csrfMemberDecision);
		var csrfMemberProblem = Assert.IsType<ProblemHttpResult>(csrfMemberError);
		Assert.Equal(StatusCodes.Status400BadRequest, csrfMemberProblem.StatusCode);
		Assert.Equal(ArchiveAccess.InvalidAntiforgeryMessage, csrfMemberProblem.ProblemDetails.Title);

		var revokedContext = AuthenticatedContext(accountId, now);
		var revokedAccess = CreateAccessService(memberUser, lockedOut: true, [ArchiveRoles.Member]);
		var (revokedDecision, revokedError) = await ArchiveAccess.RequireMemberAsync(
			revokedContext, CreateAccessor(revokedContext, now), revokedAccess);
		Assert.Null(revokedDecision);
		var revokedProblem = Assert.IsType<ProblemHttpResult>(revokedError);
		Assert.Equal(StatusCodes.Status401Unauthorized, revokedProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, revokedProblem.ProblemDetails.Title);

		var deniedEditorContext = AuthenticatedContext(accountId, now);
		var (deniedEditorDecision, deniedEditorError) = await ArchiveAccess.RequireEditorAsync(
			deniedEditorContext, CreateAccessor(deniedEditorContext, now), memberAccess);
		Assert.Equal("no-store", deniedEditorContext.Response.Headers.CacheControl.ToString());
		Assert.Null(deniedEditorDecision);
		var deniedEditorProblem = Assert.IsType<ProblemHttpResult>(deniedEditorError);
		Assert.Equal(StatusCodes.Status403Forbidden, deniedEditorProblem.StatusCode);
		Assert.Equal(ArchiveAccess.ForbiddenMessage, deniedEditorProblem.ProblemDetails.Title);

		var editorContext = AuthenticatedContext(accountId, now);
		var editorAccess = CreateAccessService(memberUser, lockedOut: false, [ArchiveRoles.Member, ArchiveRoles.Editor]);
		var (editorDecision, editorError) = await ArchiveAccess.RequireEditorAsync(
			editorContext, new StubAntiforgery(reject: false), CreateAccessor(editorContext, now), editorAccess);
		Assert.Equal("no-store", editorContext.Response.Headers.CacheControl.ToString());
		Assert.Null(editorError);
		Assert.NotNull(editorDecision);
		Assert.True(ArchiveAccess.IsEditor(editorDecision));

		var csrfEditorContext = AuthenticatedContext(accountId, now);
		var (csrfEditorDecision, csrfEditorError) = await ArchiveAccess.RequireEditorAsync(
			csrfEditorContext, new StubAntiforgery(reject: true), CreateAccessor(csrfEditorContext, now), editorAccess);
		Assert.Equal("no-store", csrfEditorContext.Response.Headers.CacheControl.ToString());
		Assert.Null(csrfEditorDecision);
		var csrfEditorProblem = Assert.IsType<ProblemHttpResult>(csrfEditorError);
		Assert.Equal(StatusCodes.Status400BadRequest, csrfEditorProblem.StatusCode);
		Assert.Equal(ArchiveAccess.InvalidAntiforgeryMessage, csrfEditorProblem.ProblemDetails.Title);
	}

	[Fact]
	public void IsAuftrittsAufnahmeMemberVisibleKeepsConcertRecordingsFromMembers()
	{
		var member = ActiveMember();
		var editor = member with { Roles = [ArchiveRoles.Member, ArchiveRoles.Editor] };
		var admin = member with { Roles = [ArchiveRoles.Administrator], IsAdministrator = true };

		// #171: the preserved original of an Auftritt's Aufnahme is not for
		// members — they play the recording through its own ticket.
		var auftrittAufnahme = new Datei
		{
			Kind = DateiKinds.Aufnahme,
			BlobName = "auftritte/aufnahme-original.m4a",
			ContentType = "audio/mp4",
			Event = new ChoirEvent { Kind = EventKinds.Known[0], Title = "Frühschoppen" },
		};

		Assert.False(ArchiveAccess.IsAuftrittsAufnahmeMemberVisible(member, auftrittAufnahme));
		Assert.True(ArchiveAccess.IsAuftrittsAufnahmeMemberVisible(editor, auftrittAufnahme));
		Assert.True(ArchiveAccess.IsAuftrittsAufnahmeMemberVisible(admin, auftrittAufnahme));

		// Documents and photos of the Auftritt follow the Auftritt's own visibility.
		var auftrittSonstiges = new Datei
		{
			Kind = DateiKinds.Sonstiges,
			BlobName = "auftritte/plakat.jpg",
			ContentType = "image/jpeg",
			Event = auftrittAufnahme.Event,
		};
		Assert.True(ArchiveAccess.IsAuftrittsAufnahmeMemberVisible(member, auftrittSonstiges));

		// A Lied-Aufnahme belongs to the Lied, not to the stop's file list.
		var liedAufnahme = new Datei
		{
			Kind = DateiKinds.Aufnahme,
			BlobName = "lieder/aufnahme.mp3",
			ContentType = "audio/mpeg",
		};
		Assert.True(ArchiveAccess.IsAuftrittsAufnahmeMemberVisible(member, liedAufnahme));
	}

	[Fact]
	public void IsAufnahmeMemberVisibleFollowsPublicationAndAccessTicketRule()
	{
		var member = ActiveMember();
		var editor = member with { Roles = [ArchiveRoles.Member, ArchiveRoles.Editor] };
		var admin = member with { Roles = [ArchiveRoles.Administrator], IsAdministrator = true };
		var auftritt = new ChoirEvent { Kind = EventKinds.Known[0], Title = "Frühschoppen" };
		var trashed = new ChoirEvent { Kind = EventKinds.Known[0], Title = "Gelöscht", DeletedAt = DateTimeOffset.UtcNow };
		var published = new Recording { Label = "Vormittag", Kind = RecordingKinds.Audio, PublishedAt = DateTimeOffset.UtcNow };
		var unpublished = new Recording { Label = "Rohfassung", Kind = RecordingKinds.Audio };

		// Published on a live Auftritt: the member walks in to the access ticket.
		Assert.True(ArchiveAccess.IsAufnahmeMemberVisible(member, published, auftritt));
		Assert.True(ArchiveAccess.IsAufnahmeMemberVisible(editor, published, auftritt));
		Assert.True(ArchiveAccess.IsAufnahmeMemberVisible(admin, published, auftritt));

		// Unpublished on the live Auftritt: the 404 path, except for editors.
		Assert.False(ArchiveAccess.IsAufnahmeMemberVisible(member, unpublished, auftritt));
		Assert.True(ArchiveAccess.IsAufnahmeMemberVisible(editor, unpublished, auftritt));
		Assert.True(ArchiveAccess.IsAufnahmeMemberVisible(admin, unpublished, auftritt));

		// Trashed Auftritt: no member access either way.
		Assert.False(ArchiveAccess.IsAufnahmeMemberVisible(member, published, trashed));
		Assert.True(ArchiveAccess.IsAufnahmeMemberVisible(editor, published, trashed));
		Assert.False(ArchiveAccess.IsAufnahmeMemberVisible(member, unpublished, trashed));
	}

	[Fact]
	public void CanChangeRecordingFilesRequiresTheCreatingAccount()
	{
		var creator = ActiveMember();
		var recording = new Recording { Label = "Vormittag", Kind = RecordingKinds.Audio, CreatedByAccountId = creator.AccountId };

		Assert.True(ArchiveAccess.CanChangeRecordingFiles(creator, recording));

		var stranger = creator with { AccountId = Guid.CreateVersion7() };
		Assert.False(ArchiveAccess.CanChangeRecordingFiles(stranger, recording));

		// Even an editor or Administrator who did not create the recording may not change its files.
		var editingStranger = stranger with { Roles = [ArchiveRoles.Member, ArchiveRoles.Editor] };
		Assert.False(ArchiveAccess.CanChangeRecordingFiles(editingStranger, recording));
		var administrationStranger = stranger with { Roles = [ArchiveRoles.Administrator], IsAdministrator = true };
		Assert.False(ArchiveAccess.CanChangeRecordingFiles(administrationStranger, recording));
	}

	[Fact]
	public void DecideAdministratorRequiresAnActiveAdministratorWithTheUnifiedForbiddenTitle()
	{
		var member = ActiveMember();
		var admin = member with { Roles = [ArchiveRoles.Administrator], IsAdministrator = true };
		var inactive = admin with { IsAuthenticated = false, IsActive = false };

		var (adminDecision, adminError) = ArchiveAccess.DecideAdministrator(admin);
		Assert.Same(admin, adminDecision);
		Assert.Null(adminError);

		var (memberDecision, memberError) = ArchiveAccess.DecideAdministrator(member);
		Assert.Null(memberDecision);
		var memberProblem = Assert.IsType<ProblemHttpResult>(memberError);
		Assert.Equal(StatusCodes.Status403Forbidden, memberProblem.StatusCode);
		Assert.Equal(ArchiveAccess.ForbiddenMessage, memberProblem.ProblemDetails.Title);

		var (nullDecision, nullError) = ArchiveAccess.DecideAdministrator(null);
		Assert.Null(nullDecision);
		var nullProblem = Assert.IsType<ProblemHttpResult>(nullError);
		Assert.Equal(StatusCodes.Status401Unauthorized, nullProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, nullProblem.ProblemDetails.Title);

		var (inactiveDecision, inactiveError) = ArchiveAccess.DecideAdministrator(inactive);
		Assert.Null(inactiveDecision);
		var inactiveProblem = Assert.IsType<ProblemHttpResult>(inactiveError);
		Assert.Equal(StatusCodes.Status401Unauthorized, inactiveProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, inactiveProblem.ProblemDetails.Title);
	}

	[Fact]
	public async Task RequireAdministratorAppliesNoStoreAntiforgeryAndTheAdministratorRule()
	{
		var accountId = Guid.CreateVersion7();
		var now = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
		var user = new ArchiveUser { Id = accountId, Email = "chef@liedertafel.at", EmailConfirmed = true };

		var memberContext = AuthenticatedContext(accountId, now);
		var memberAccess = CreateAccessService(user, lockedOut: false, [ArchiveRoles.Member]);
		var (memberDecision, memberError) = await ArchiveAccess.RequireAdministratorAsync(
			memberContext, CreateAccessor(memberContext, now), memberAccess);
		Assert.Equal("no-store", memberContext.Response.Headers.CacheControl.ToString());
		Assert.Null(memberDecision);
		var memberProblem = Assert.IsType<ProblemHttpResult>(memberError);
		Assert.Equal(StatusCodes.Status403Forbidden, memberProblem.StatusCode);
		Assert.Equal(ArchiveAccess.ForbiddenMessage, memberProblem.ProblemDetails.Title);

		var adminContext = AuthenticatedContext(accountId, now);
		var adminAccess = CreateAccessService(user, lockedOut: false, [ArchiveRoles.Administrator]);
		var (adminDecision, adminError) = await ArchiveAccess.RequireAdministratorAsync(
			adminContext, new StubAntiforgery(reject: false), CreateAccessor(adminContext, now), adminAccess);
		Assert.Equal("no-store", adminContext.Response.Headers.CacheControl.ToString());
		Assert.Null(adminError);
		Assert.NotNull(adminDecision);
		Assert.Equal(accountId, adminDecision.AccountId);

		var csrfAdminContext = AuthenticatedContext(accountId, now);
		var (csrfDecision, csrfError) = await ArchiveAccess.RequireAdministratorAsync(
			csrfAdminContext, new StubAntiforgery(reject: true), CreateAccessor(csrfAdminContext, now), adminAccess);
		Assert.Equal("no-store", csrfAdminContext.Response.Headers.CacheControl.ToString());
		Assert.Null(csrfDecision);
		var csrfProblem = Assert.IsType<ProblemHttpResult>(csrfError);
		Assert.Equal(StatusCodes.Status400BadRequest, csrfProblem.StatusCode);
		Assert.Equal(ArchiveAccess.InvalidAntiforgeryMessage, csrfProblem.ProblemDetails.Title);

		var anonymousContext = new DefaultHttpContext();
		var (anonDecision, anonError) = await ArchiveAccess.RequireAdministratorAsync(
			anonymousContext, CreateAccessor(anonymousContext, now), adminAccess);
		Assert.Equal("no-store", anonymousContext.Response.Headers.CacheControl.ToString());
		Assert.Null(anonDecision);
		var anonProblem = Assert.IsType<ProblemHttpResult>(anonError);
		Assert.Equal(StatusCodes.Status401Unauthorized, anonProblem.StatusCode);
		Assert.Equal(ArchiveAccess.UnauthorizedMessage, anonProblem.ProblemDetails.Title);
	}

	[Fact]
	public async Task MemberLiederAndMemberAuftritteResolveTheMemberVisibleRecords()
	{
		var now = DateTimeOffset.UtcNow;
		var db = CreateDb();
		db.Lieder.AddRange(new Lied { Title = "Lebendig" }, new Lied { Title = "Gelöscht", DeletedAt = now });
		db.Events.AddRange(
			new ChoirEvent { Kind = EventKinds.Known[0], Title = "Vormittag" },
			new ChoirEvent { Kind = EventKinds.Known[0], Title = "Gelöscht", DeletedAt = now });
		await db.SaveChangesAsync();

		var lieder = await ArchiveAccess.MemberLieder(db).ToListAsync();
		Assert.Equal(["Lebendig"], lieder.Select(l => l.Title));

		var auftritte = await ArchiveAccess.MemberAuftritte(db).ToListAsync();
		Assert.Equal(["Vormittag"], auftritte.Select(e => e.Title));
	}

	private static ArchiveDbContext CreateDb()
	{
		var identity = new IdentityOptions
		{
			Stores = { SchemaVersion = IdentitySchemaVersions.Version3 },
		};
		var services = new ServiceCollection();
		services.AddSingleton<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(
			new Microsoft.Extensions.Options.OptionsWrapper<IdentityOptions>(identity));
		return new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
			.UseInMemoryDatabase($"archive-access-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
			.UseApplicationServiceProvider(services.BuildServiceProvider()).Options);
	}

	private static DefaultHttpContext AuthenticatedContext(Guid accountId, DateTimeOffset now)
	{
		var claims = new[]
		{
			new Claim(ClaimTypes.NameIdentifier, accountId.ToString()),
			new Claim(AuthClaims.AccountId, accountId.ToString()),
			new Claim(AuthClaims.AuthenticatedAt, now.ToString("O")),
			new Claim(ClaimTypes.Email, "mitglied@liedertafel.at"),
			new Claim(ClaimTypes.Role, ArchiveRoles.Member),
		};
		return new DefaultHttpContext
		{
			User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
		};
	}

	private static CurrentUserAccessor CreateAccessor(HttpContext context, DateTimeOffset now)
		=> new(
			new HttpContextAccessor { HttpContext = context },
			Options.Create(new AuthOptions()),
			new FixedTimeProvider(now));

	private static ArchiveAccessService CreateAccessService(
		ArchiveUser? user, bool lockedOut = false, IList<string>? roles = null)
	{
		var store = new StubUserStore(user, lockedOut, roles ?? []);
		var manager = new UserManager<ArchiveUser>(
			store,
			Options.Create(new IdentityOptions()),
			new PasswordHasher<ArchiveUser>(),
			[],
			[],
			new UpperInvariantLookupNormalizer(),
			new IdentityErrorDescriber(),
			null!,
			NullLogger<UserManager<ArchiveUser>>.Instance);
		return new ArchiveAccessService(manager);
	}

	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}

	private sealed class StubUserStore(ArchiveUser? user, bool lockedOut, IList<string> roles)
		: IUserLockoutStore<ArchiveUser>, IUserRoleStore<ArchiveUser>
	{
		public void Dispose() { }
		public Task<string> GetUserIdAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(u.Id.ToString());
		public Task<string?> GetUserNameAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(u.UserName);
		public Task SetUserNameAsync(ArchiveUser u, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<string?> GetNormalizedUserNameAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(u.NormalizedUserName);
		public Task SetNormalizedUserNameAsync(ArchiveUser u, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<IdentityResult> CreateAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
		public Task<IdentityResult> UpdateAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
		public Task<IdentityResult> DeleteAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
		public Task<ArchiveUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
			=> Task.FromResult(user is not null && user.Id.ToString() == userId ? user : null);
		public Task<ArchiveUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => Task.FromResult(user);
		public Task<DateTimeOffset?> GetLockoutEndDateAsync(ArchiveUser u, CancellationToken cancellationToken)
			=> Task.FromResult<DateTimeOffset?>(lockedOut ? DateTimeOffset.MaxValue : null);
		public Task SetLockoutEndDateAsync(ArchiveUser u, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> IncrementAccessFailedCountAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task ResetAccessFailedCountAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> GetAccessFailedCountAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task<bool> GetLockoutEnabledAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task SetLockoutEnabledAsync(ArchiveUser u, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AddToRoleAsync(ArchiveUser u, string roleName, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task RemoveFromRoleAsync(ArchiveUser u, string roleName, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<IList<string>> GetRolesAsync(ArchiveUser u, CancellationToken cancellationToken) => Task.FromResult(roles);
		public Task<bool> IsInRoleAsync(ArchiveUser u, string roleName, CancellationToken cancellationToken) => Task.FromResult(roles.Contains(roleName));
		public Task<IList<ArchiveUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken) => Task.FromResult<IList<ArchiveUser>>([]);
	}

	private sealed class StubAntiforgery(bool reject) : IAntiforgery
	{
		public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => new("req", "cookie", "form", "header");
		public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new("req", "cookie", "form", "header");
		public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(!reject);
		public void SetCookieTokenAndHeader(HttpContext httpContext) { }
		public Task ValidateRequestAsync(HttpContext httpContext)
			=> reject
				? Task.FromException(new AntiforgeryValidationException("Invalid token."))
				: Task.CompletedTask;
	}
}
