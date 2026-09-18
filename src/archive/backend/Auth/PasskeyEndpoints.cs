using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Archive.Backend.Auth;

public sealed record PasskeyVerifyRequest(string? Credential);

public sealed record PasskeyEnrollRequest(string? Credential, string? Name);

public sealed record PasskeyRenameRequest(string? CredentialId, string? Name);

public sealed record PasskeyRemoveRequest(string? CredentialId);

/// <summary>
/// ARC-011-1 passkey endpoints alongside the existing /api/auth/* contract.
/// Mutations keep the manual antiforgery mechanism; the login ceremony
/// itself requires WebAuthn user verification plus the challenge bound to
/// Identity's protected temp state, so the options endpoints stay GET-free
/// POSTs without an authenticated session.
/// </summary>
public static class PasskeyEndpoints
{
	public static void MapPasskeyEndpoints(this WebApplication app)
	{
		app.MapPost("/api/auth/passkeys/register/options", async (
			HttpContext context, IAntiforgery antiforgery, PasskeyService passkeys, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (context.User.Identity?.IsAuthenticated != true)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.MakeEnrollOptionsAsync(context.User, token);
		}).DisableAntiforgery();

		app.MapPost("/api/auth/passkeys/register/verify", async (
			HttpContext context, IAntiforgery antiforgery, PasskeyService passkeys, TimeProvider time, CancellationToken token,
			PasskeyEnrollRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (context.User.Identity?.IsAuthenticated != true)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.VerifyEnrollAsync(
				context.User, body?.Credential, body?.Name, time.GetUtcNow(), token);
		}).DisableAntiforgery();

		app.MapPost("/api/auth/passkeys/login/options", async (
			HttpContext context, PasskeyService passkeys, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.MakeLoginOptionsAsync(token);
		});

		app.MapPost("/api/auth/passkeys/login/verify", async (
			HttpContext context, IAntiforgery antiforgery, PasskeyService passkeys, TimeProvider time, CancellationToken token,
			PasskeyVerifyRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.VerifyLoginAsync(body?.Credential, time.GetUtcNow(), token);
		}).DisableAntiforgery();

		app.MapGet("/api/auth/passkeys", async (
			HttpContext context, PasskeyService passkeys) =>
		{
			if (context.User.Identity?.IsAuthenticated != true)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.ListPasskeysAsync(context.User);
		});

		app.MapPost("/api/auth/passkeys/rename", async (
			HttpContext context, IAntiforgery antiforgery, PasskeyService passkeys, TimeProvider time, CancellationToken token,
			PasskeyRenameRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (context.User.Identity?.IsAuthenticated != true)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.RenamePasskeyAsync(
				context.User, body?.CredentialId, body?.Name, time.GetUtcNow(), token);
		}).DisableAntiforgery();

		app.MapPost("/api/auth/passkeys/remove", async (
			HttpContext context, IAntiforgery antiforgery, PasskeyService passkeys, TimeProvider time, CancellationToken token,
			PasskeyRemoveRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			if (context.User.Identity?.IsAuthenticated != true)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			context.Response.Headers.CacheControl = "no-store";
			return await passkeys.RemovePasskeyAsync(
				context.User, body?.CredentialId, time.GetUtcNow(), token);
		}).DisableAntiforgery();
	}
}
