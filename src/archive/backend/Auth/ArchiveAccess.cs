using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Archive.Backend.Recordings;
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

	public static (ArchiveAccessDecision? Decision, IResult? Error) DecideAdministrator(ArchiveAccessDecision? decision)
	{
		var (member, error) = DecideMember(decision);
		if (error is not null)
			return (null, error);
		if (!member!.IsAdministrator)
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

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireAdministratorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		var (decision, error) = await RequireMemberAsync(context, accessor, access);
		if (error is not null)
			return (null, error);
		return DecideAdministrator(decision);
	}

	public static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireAdministratorAsync(
		HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (await ValidateAntiforgeryAsync(context, antiforgery) is { } rejected)
			return (null, rejected);
		return await RequireAdministratorAsync(context, accessor, access);
	}

	/// <summary>
	/// #171: Dateien of an Auftritt follow the Auftritt's visibility; members
	/// get concert files only through their recording's own ticket
	/// (publication and download switch), editors may also fetch the
	/// preserved original here. A Datei of a Lied (no Auftritt) is not affected.
	/// </summary>
	public static bool IsAuftrittsAufnahmeMemberVisible(ArchiveAccessDecision decision, Datei datei)
		=> datei.Event is null || IsEditor(decision) || datei.Kind != DateiKinds.Aufnahme;

	/// <summary>
	/// Only the account that created an Aufnahme may change its files — an
	/// editor or Administrator who did not create it still may not (the rule
	/// of the attachment endpoint, decided here for every caller).
	/// </summary>
	public static bool CanChangeRecordingFiles(ArchiveAccessDecision decision, Recording recording)
		=> recording.CreatedByAccountId == decision.AccountId;

	/// <summary>The Lieder a member may see: every saved one, none in the trash (#146).</summary>
	public static IQueryable<Lied> MemberLieder(ArchiveDbContext db) => LiedQueries.Visible(db);

	/// <summary>
	/// The Auftritte a member may see: every saved one — ADR-0001 has no draft
	/// or published state for the Auftritte — none in the trash (#146).
	/// </summary>
	public static IQueryable<ChoirEvent> MemberAuftritte(ArchiveDbContext db) => EventQueries.Visible(db);
}
