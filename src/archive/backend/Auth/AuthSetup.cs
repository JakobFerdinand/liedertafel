using System.Security.Claims;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

public static class AuthSetup
{
	public static IServiceCollection AddArchiveAuth(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
	{
		// ARC-010: local SMTP capture must never serve production, and sender
		// secrets must never reach production. Fails startup loudly otherwise.
		MailGuards.ValidateProductionMail(configuration, environment);
		// ARC-011-1: passkey ceremonies need a stable relying-party ID in
		// production; deriving it from request host headers is forbidden.
		if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Authentication:PasskeyRelyingPartyId"]))
			throw new InvalidOperationException(
				"Authentication:PasskeyRelyingPartyId ist ausserhalb von Development erforderlich.");
		services.AddArchiveIdentity(configuration);
		services.ConfigureApplicationCookie(cookie =>
		{
			cookie.Cookie.Name = "archive.auth";
			cookie.Cookie.HttpOnly = true;
			cookie.Cookie.SameSite = SameSiteMode.Strict;
			cookie.Cookie.SecurePolicy = environment.IsDevelopment()
				? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
			cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
			cookie.SlidingExpiration = true;
			var stampValidation = cookie.Events.OnValidatePrincipal;
			cookie.Events.OnValidatePrincipal = async context =>
			{
				if (stampValidation is not null)
					await stampValidation(context);
				if (context.Principal?.Identity?.IsAuthenticated != true)
					return;
				var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ArchiveUser>>();
				var user = await users.GetUserAsync(context.Principal);
				if (user is null || !user.EmailConfirmed || await users.IsLockedOutAsync(user))
				{
					context.RejectPrincipal();
					await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
					return;
				}
				var currentRoles = await users.GetRolesAsync(user);
				if (currentRoles.Count == 0)
				{
					context.RejectPrincipal();
					await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
					return;
				}
				// Enforce current membership server-side on every request:
				// a stale cookie role must never survive until its 30-day
				// expiry. When the database roles differ, replace the
				// principal for this request and renew the ticket so all
				// replicas agree on the next authorized request.
				var cookieRoles = context.Principal.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r).ToArray();
				var freshRoles = currentRoles.OrderBy(r => r).ToArray();
				if (!cookieRoles.SequenceEqual(freshRoles))
				{
					var identity = (ClaimsIdentity?)context.Principal.Identity;
					var authenticationType = identity?.AuthenticationType ?? IdentityConstants.ApplicationScheme;
					var claims = context.Principal.Claims.Where(c => c.Type != ClaimTypes.Role).ToList();
					foreach (var role in freshRoles)
						claims.Add(new Claim(ClaimTypes.Role, role));
					context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType)));
					context.ShouldRenew = true;
				}
				// No session revival after reactivation or repair: tickets issued
				// before the latest reactivation, email change or maintainer
				// repair stay dead and require a fresh email-code sign-in. The
				// stamp validator alone would allow a revival within its
				// 5-minute window because deactivation and reactivation both
				// bump the stamp but throttled validation does not compare on
				// every request.
				var authenticatedAtValue = context.Principal.FindFirst(AuthClaims.AuthenticatedAt)?.Value;
				if (DateTimeOffset.TryParse(authenticatedAtValue, out var authenticatedAt))
				{
					var db = context.HttpContext.RequestServices.GetRequiredService<ArchiveDbContext>();
					var lastSessionReset = await db.MemberAdminActions.AsNoTracking()
						.Where(a => a.TargetUserId == user.Id
							&& (a.Action == MemberAdminActionType.Reactivated
								|| a.Action == MemberAdminActionType.EmailChanged
								|| a.Action == MemberAdminActionType.AdministratorRepaired))
						.OrderByDescending(a => a.OccurredAt)
						.Select(a => (DateTimeOffset?)a.OccurredAt)
						.FirstOrDefaultAsync();
					if (lastSessionReset.HasValue && authenticatedAt < lastSessionReset.Value)
					{
						context.RejectPrincipal();
						await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
					}
				}
			};
			// API contract: never redirect browsers to HTML; use Problem status codes.
			cookie.Events.OnRedirectToLogin = context =>
			{
				context.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return Task.CompletedTask;
			};
			cookie.Events.OnRedirectToAccessDenied = context =>
			{
				context.Response.StatusCode = StatusCodes.Status403Forbidden;
				return Task.CompletedTask;
			};
		});
		services.AddAuthorizationBuilder()
			.AddPolicy(AuthPolicies.Member, policy => policy.RequireAuthenticatedUser())
			.AddPolicy(AuthPolicies.Editor, policy => policy.RequireRole(ArchiveRoles.Editor, ArchiveRoles.Administrator))
			.AddPolicy(AuthPolicies.Administrator, policy => policy.RequireRole(ArchiveRoles.Administrator));
		return services;
	}

	/// <summary>
	/// Identity services without web cookie configuration, for finite operator
	/// commands (<c>--bootstrap-admin</c>, <c>--repair-admin</c>,
	/// <c>--seed-dev-auth</c>) that run on a
	/// generic host without <see cref="IWebHostEnvironment"/>.
	/// </summary>
	public static IServiceCollection AddArchiveIdentity(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
		services.Configure<MailOptions>(configuration.GetSection(MailOptions.SectionName));
		services.AddScoped<SignInCodeService>();
		services.AddScoped<MemberInvitationService>();
		services.AddScoped<MemberRevocationService>();
		services.AddScoped<MemberEmailChangeService>();
		services.AddScoped<ArchiveAccessService>();
		services.AddScoped<EmailCodeTokenProvider>();
		// ARC-010 transport selection: SMTP capture stays the default so
		// ordinary AppHost startup is unchanged; Mail:Provider=Azure selects
		// the Communication Services sender. The EmailClient transport is a
		// lazily built singleton (SDK clients are thread-safe) and is only
		// ever constructed on the Azure path.
		services.AddScoped<SmtpSignInCodeSender>();
		services.AddScoped<AzureCommunicationMailSender>();
		services.AddScoped<IArchiveMailSender>(provider =>
			provider.GetRequiredService<IOptions<MailOptions>>().Value.IsAzure
				? provider.GetRequiredService<AzureCommunicationMailSender>()
				: provider.GetRequiredService<SmtpSignInCodeSender>());
		services.AddSingleton<IAzureEmailTransport>(provider =>
		{
			var environment = provider.GetRequiredService<IHostEnvironment>();
			var mail = provider.GetRequiredService<IOptions<MailOptions>>().Value;
			if (!environment.IsDevelopment() && !string.IsNullOrWhiteSpace(mail.AzureConnectionString))
				throw new InvalidOperationException(
					"Mail:AzureConnectionString is a local development credential and is forbidden outside Development.");
			return EmailClientTransport.Create(mail);
		});
		services.AddScoped<CurrentUserAccessor>();
		services.AddScoped<PasskeyService>();

		services.AddIdentity<ArchiveUser, ArchiveRole>(options =>
			{
				options.User.RequireUniqueEmail = true;
				// Member emails may contain German umlauts; keep UserName (= email)
				// creatable for those addresses. Sign-in looks users up by
				// email, never by UserName.
				options.User.AllowedUserNameCharacters =
					"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+äöüÄÖÜß";
				// Invited members sign in before confirmation; the service and
				// the principal validator enforce confirmation explicitly.
				options.SignIn.RequireConfirmedEmail = false;
				// Account-level backstop next to the per-code attempt caps and
				// the AuthRequestLog abuse limits.
				options.Lockout.AllowedForNewUsers = true;
				options.Lockout.MaxFailedAccessAttempts = 20;
				options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
				// ARC-011-1: Identity schema version 3 persists
				// IdentityUserPasskey<Guid> (AspNetUserPasskeys). The
				// DbContext override keeps design-time migrations on the
				// same version when no IdentityOptions provider exists.
				options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
			})
			.AddEntityFrameworkStores<ArchiveDbContext>()
			.AddDefaultTokenProviders()
			.AddTokenProvider<EmailCodeTokenProvider>(EmailCodeTokenProvider.ProviderName);

		// Per-request membership/role checks above enforce revocation on the
		// next request; the stamp validator stays on a tight interval so
		// deactivation/reactivation bumps kill tickets without per-request
		// stamp I/O. Role changes sync via principal replacement.
		// ARC-011-1: stable account claims come from ArchiveClaimsFactory;
		// session claims (AuthenticatedAt, auth method) are copied from the
		// expiring ticket so refresh never resets freshness.
		services.AddScoped<IUserClaimsPrincipalFactory<ArchiveUser>, ArchiveClaimsFactory>();
		services.AddSingleton<IConfigureOptions<IdentityPasskeyOptions>, PasskeyOptionsSetup>();
		services.Configure<SecurityStampValidatorOptions>(options =>
		{
			options.ValidationInterval = TimeSpan.FromMinutes(5);
			options.OnRefreshingPrincipal = context =>
			{
				var current = context.CurrentPrincipal;
				var identity = context.NewPrincipal?.Identity as ClaimsIdentity;
				if (identity is null)
					return Task.CompletedTask;
				foreach (var type in new[] { AuthClaims.AuthenticatedAt, AuthClaims.AuthenticationMethod })
				{
					if (identity.FindFirst(type) is not null)
						continue;
					var carried = current?.FindFirst(type)?.Value;
					if (!string.IsNullOrEmpty(carried))
						identity.AddClaim(new Claim(type, carried));
				}
				// Backwards compatibility: tickets issued before ARC-011-1
				// carry no method claim and count as email-code sessions.
				if (identity.FindFirst(AuthClaims.AuthenticationMethod) is null)
					identity.AddClaim(new Claim(AuthClaims.AuthenticationMethod, AuthClaims.EmailCode));
				return Task.CompletedTask;
			};
		});
		return services;
	}

	public static Task SignInMemberAsync(
		SignInManager<ArchiveUser> signIn, ArchiveUser user, IList<string> roles, DateTimeOffset now)
		=> SignInMemberAsync(signIn, user, now, AuthClaims.EmailCode);

	public static async Task SignInMemberAsync(
		SignInManager<ArchiveUser> signIn, ArchiveUser user, DateTimeOffset now, string authenticationMethod)
	{
		var method = authenticationMethod == AuthClaims.Passkey ? AuthClaims.Passkey : AuthClaims.EmailCode;
		var claims = new List<Claim>
		{
			new(AuthClaims.AccountId, user.Id.ToString()),
			new(AuthClaims.AuthenticatedAt, now.ToString("O")),
			new(AuthClaims.AuthenticationMethod, method),
		};
		var displayName = user.DisplayName ?? user.Email;
		if (!string.IsNullOrWhiteSpace(displayName))
			claims.Add(new Claim(AuthClaims.DisplayName, displayName));
		await signIn.SignInWithClaimsAsync(
			user,
			new AuthenticationProperties { IsPersistent = true, AllowRefresh = true },
			claims);
	}
}
