using Microsoft.AspNetCore.Antiforgery;

namespace Archive.Backend.Auth;

/// <summary>
/// Unified access module for archive endpoints (#191): owns the request-level
/// antiforgery check, the <c>no-store</c> cache header, the member and editor
/// decisions, and their German ProblemDetails responses.
/// </summary>
public static class ArchiveAccess
{
	public const string InvalidAntiforgeryMessage = "Ungültiger Sicherheitstoken.";

	public const string UnauthorizedMessage = "Anmeldung erforderlich.";

	public const string ForbiddenMessage = "Keine Berechtigung für das Liedverzeichnis.";

	public static void NoStore(HttpContext context)
		=> context.Response.Headers.CacheControl = "no-store";

	/// <summary>Applies <c>no-store</c> and validates antiforgery, returning a 400 Problem on failure or null when valid.</summary>
	public static async Task<IResult?> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
	{
		NoStore(context);
		try
		{
			await antiforgery.ValidateRequestAsync(context);
			return null;
		}
		catch (AntiforgeryValidationException)
		{
			return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: InvalidAntiforgeryMessage);
		}
	}

	public static bool IsMember(ArchiveAccessDecision? decision)
		=> decision is { IsActive: true };

	public static bool IsEditor(ArchiveAccessDecision? decision)
		=> IsMember(decision) && (decision!.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor));

	public static (ArchiveAccessDecision? Decision, IResult? Error) DecideMember(ArchiveAccessDecision? decision)
		=> IsMember(decision)
			? (decision, null)
			: (null, Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: UnauthorizedMessage));

	public static (ArchiveAccessDecision? Decision, IResult? Error) DecideEditor(ArchiveAccessDecision? decision)
	{
		var (member, error) = DecideMember(decision);
		if (error is not null)
			return (null, error);
		if (!IsEditor(member))
			return (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ForbiddenMessage));
		return (member, null);
	}

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		NoStore(context);
		if (accessor.Current is null)
			return DecideMember(null);
		var decision = await access.GetDecisionAsync(context.User);
		return DecideMember(decision);
	}

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireMemberAsync(
		HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (await ValidateAntiforgeryAsync(context, antiforgery) is { } rejected)
			return (null, rejected);
		return await RequireMemberAsync(context, accessor, access);
	}

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		var (decision, error) = await RequireMemberAsync(context, accessor, access);
		if (error is not null)
			return (null, error);
		return DecideEditor(decision);
	}

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (await ValidateAntiforgeryAsync(context, antiforgery) is { } rejected)
			return (null, rejected);
		return await RequireEditorAsync(context, accessor, access);
	}
}
