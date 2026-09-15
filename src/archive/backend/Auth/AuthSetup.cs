using System.Security.Claims;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Auth;

public static class AuthSetup
{
	public static IServiceCollection AddArchiveAuth(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
	{
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
				if ((await users.GetRolesAsync(user)).Count == 0)
				{
					context.RejectPrincipal();
					await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
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
	/// commands (<c>--bootstrap-admin</c>, <c>--seed-dev-auth</c>) that run on a
	/// generic host without <see cref="IWebHostEnvironment"/>.
	/// </summary>
	public static IServiceCollection AddArchiveIdentity(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
		services.AddScoped<SignInCodeService>();
		services.AddScoped<EmailCodeTokenProvider>();
		services.AddScoped<IArchiveMailSender, SmtpSignInCodeSender>();
		services.AddScoped<CurrentUserAccessor>();

		services.AddIdentity<ArchiveUser, ArchiveRole>(options =>
			{
				options.User.RequireUniqueEmail = true;
				// Invited members sign in before confirmation; the service and
				// the principal validator enforce confirmation explicitly.
				options.SignIn.RequireConfirmedEmail = false;
				// Account-level backstop next to the per-code attempt caps and
				// the AuthRequestLog abuse limits.
				options.Lockout.AllowedForNewUsers = true;
				options.Lockout.MaxFailedAccessAttempts = 20;
				options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
			})
			.AddEntityFrameworkStores<ArchiveDbContext>()
			.AddDefaultTokenProviders()
			.AddTokenProvider<EmailCodeTokenProvider>(EmailCodeTokenProvider.ProviderName);

		// Tight revalidation for a tiny user base: revocation takes effect
		// within minutes without per-request stamp checks.
		services.Configure<SecurityStampValidatorOptions>(options =>
			options.ValidationInterval = TimeSpan.FromMinutes(5));
		return services;
	}

	public static async Task SignInMemberAsync(
		SignInManager<ArchiveUser> signIn, ArchiveUser user, IList<string> roles, DateTimeOffset now)
	{
		var claims = new List<Claim>
		{
			new(AuthClaims.AccountId, user.Id.ToString()),
			new(AuthClaims.AuthenticatedAt, now.ToString("O")),
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
