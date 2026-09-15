using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Archive.Backend.Auth;

public sealed record CodeRequest(string? Email);

public sealed record CodeVerify(string? Email, string? Code);

public static class AuthEndpoints
{
	public const string RequestMessage = "Falls die Adresse eingeladen ist, wurde ein Code gesendet. Bitte das Postfach prüfen.";

	public static void MapAuthEndpoints(this WebApplication app)
	{
		app.MapPost("/api/auth/code/request", async (
			HttpContext context, IAntiforgery antiforgery, SignInCodeService codes, TimeProvider time, CancellationToken token,
			CodeRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (!AuthSecurity.TryNormalizeEmail(body?.Email, out var normalized))
				return Results.Problem(statusCode: 400, title: "Die E-Mail-Adresse ist ungültig.");
			var outcome = await codes.RequestCodeAsync(
				normalized, AuthSecurity.HashIp(context.Connection.RemoteIpAddress?.ToString()), time.GetUtcNow(), token);
			context.Response.Headers.CacheControl = "no-store";
			return outcome switch
			{
				CodeRequestOutcome.RateLimited => Results.Problem(statusCode: 429, title: "Zu viele Anfragen. Bitte später erneut versuchen."),
				_ => Results.Accepted(value: new { message = RequestMessage }),
			};
		}).DisableAntiforgery();

		app.MapPost("/api/auth/code/verify", async (
			HttpContext context, IAntiforgery antiforgery, SignInCodeService codes, TimeProvider time, CancellationToken token,
			CodeVerify? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (!AuthSecurity.TryNormalizeEmail(body?.Email, out var normalized))
				return Results.Problem(statusCode: 400, title: "Der Code ist ungültig oder abgelaufen.");
			var code = AuthSecurity.NormalizeCode(body?.Code ?? string.Empty);
			if (code.Length != 6)
				return Results.Problem(statusCode: 400, title: "Der Code ist ungültig oder abgelaufen.");
			var (outcome, account, roles) = await codes.VerifyCodeAsync(
				normalized, code, AuthSecurity.HashIp(context.Connection.RemoteIpAddress?.ToString()), time.GetUtcNow(), token);
			context.Response.Headers.CacheControl = "no-store";
			if (outcome == CodeVerifyOutcome.RateLimited)
				return Results.Problem(statusCode: 429, title: "Zu viele Anfragen. Bitte später erneut versuchen.");
			if (outcome != CodeVerifyOutcome.Verified || account is null)
				return Results.Problem(statusCode: 400, title: "Der Code ist ungültig oder abgelaufen.");
			await AuthSetup.SignInMemberAsync(context, account, roles, time.GetUtcNow());
			return Results.Ok(new
			{
				message = "Anmeldung erfolgreich.",
				accountId = account.Id,
				displayName = account.DisplayName ?? account.Email,
				roles = roles.Distinct().Select(ArchiveRoleNames.ToClaim).ToArray(),
			});
		}).DisableAntiforgery();

		app.MapPost("/api/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (context.User.Identity?.IsAuthenticated != true)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			context.Response.Headers.CacheControl = "no-store";
			return Results.Ok(new { message = "Abmeldung erfolgreich." });
		}).DisableAntiforgery();

		app.MapGet("/api/auth/me", (HttpContext context, CurrentUserAccessor users) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var current = users.Current;
			if (current is null)
				return Results.Ok(new { authenticated = false });
			return Results.Ok(new
			{
				authenticated = true,
				accountId = current.AccountId,
				email = current.Email,
				displayName = current.DisplayName,
				roles = current.Roles,
				verifiedAt = current.AuthenticatedAt,
			});
		});
	}
}
