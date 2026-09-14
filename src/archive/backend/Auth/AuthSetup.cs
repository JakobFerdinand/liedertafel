using System.Security.Claims;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Auth;

public static class AuthSetup
{
	public static IServiceCollection AddArchiveAuth(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
	{
		services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
		services.AddScoped<SignInCodeService>();
		services.AddScoped<IArchiveMailSender, SmtpSignInCodeSender>();
		services.AddScoped<CurrentUserAccessor>();

		var options = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
		services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
			.AddCookie(cookie =>
			{
				cookie.Cookie.Name = options.CookieName;
				cookie.Cookie.HttpOnly = true;
				cookie.Cookie.SameSite = SameSiteMode.Strict;
				cookie.Cookie.SecurePolicy = environment.IsDevelopment()
					? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
				cookie.ExpireTimeSpan = options.SessionLifetime;
				cookie.SlidingExpiration = true;
				cookie.Events.OnValidatePrincipal = async context =>
				{
					var db = context.HttpContext.RequestServices.GetRequiredService<ArchiveDbContext>();
					var idValue = context.Principal?.FindFirst(AuthClaims.AccountId)?.Value;
					if (!Guid.TryParse(idValue, out var accountId))
					{
						context.RejectPrincipal();
						await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
						return;
					}
					var active = await db.Memberships.AnyAsync(
						m => m.AccountId == accountId && m.Status == MembershipStatus.Active,
						context.HttpContext.RequestAborted);
					if (!active)
					{
						context.RejectPrincipal();
						await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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
			.AddPolicy(AuthPolicies.Editor, policy => policy.RequireRole("Editor", "Administrator"))
			.AddPolicy(AuthPolicies.Administrator, policy => policy.RequireRole("Administrator"));
		return services;
	}

	public static async Task SignInMemberAsync(
		HttpContext context, Account account, IEnumerable<ArchiveRole> roles, DateTimeOffset now)
	{
		var claims = new List<Claim>
		{
			new(AuthClaims.AccountId, account.Id.ToString()),
			new(ClaimTypes.Email, account.Email),
			new(ClaimTypes.Name, account.DisplayName ?? account.Email),
			new(AuthClaims.AuthenticatedAt, now.ToString("O")),
		};
		claims.AddRange(roles.Distinct().Select(role => new Claim(ClaimTypes.Role, ArchiveRoleNames.ToClaim(role))));
		var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
		await context.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity),
			new AuthenticationProperties { IsPersistent = true, AllowRefresh = true });
	}
}
