using System.Security.Claims;
using Archive.Backend.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
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
