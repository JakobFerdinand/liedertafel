using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Archive.Backend.Auth;

public sealed record InviteRequest(string? Email, string? DisplayName, string? Role);

public sealed record ResendRequest(string? Email);

public sealed record DeactivateRequest(string? AccountId);

public sealed record ReactivateRequest(string? AccountId);

public sealed record RoleChangeRequest(string? AccountId, string? Role);

/// <summary>
/// Administrator member administration (ARC-006 invitations, ARC-007
/// revocation). Reads use the shared database decision; all mutations require
/// the Administrator role from that decision (never a stale cookie role), a
/// fresh code verification (10-minute window) and CSRF. Members/Editors
/// receive 403; unauthenticated or revoked callers receive 401.
/// </summary>
public static class MemberAdminEndpoints
{
	public static void MapMemberAdminEndpoints(this WebApplication app)
	{
		app.MapGet("/api/admin/members", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, MemberInvitationService invitations, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireAdministratorAsync(context, accessor, access, requireFresh: false);
			if (error is not null)
				return error;
			_ = decision;
			var members = await invitations.ListAsync(token);
			return Results.Ok(new
			{
				members = members.Select(m => new
				{
					accountId = m.AccountId,
					email = m.Email,
					displayName = m.DisplayName,
					roles = m.Roles,
					status = m.Status,
					invitationId = m.InvitationId,
					invitedAt = m.InvitedAt,
					invitedByAccountId = m.InvitedByAccountId,
					acceptedAt = m.AcceptedAt,
					lastInvitationSentAt = m.LastInvitationSentAt,
					invitationMailStatus = m.InvitationMailStatus,
				}),
			});
		});

		app.MapPost("/api/admin/invitations", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, MemberInvitationService invitations, TimeProvider time, CancellationToken token,
			InviteRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireAdministratorAsync(context, accessor, access, requireFresh: true);
			if (error is not null)
				return error;
			if (!AuthSecurity.TryNormalizeEmail(body?.Email, out var normalized))
				return Results.Problem(statusCode: 400, title: "Die E-Mail-Adresse ist ungültig.");
			var role = (body?.Role ?? string.Empty).Trim();
			if (!ArchiveRoles.All.Contains(role))
				return Results.Problem(statusCode: 400, title: "Die Rolle ist ungültig.");
			var displayName = string.IsNullOrWhiteSpace(body?.DisplayName) ? null : body.DisplayName.Trim();
			if (displayName is not null && displayName.Length > 200)
				return Results.Problem(statusCode: 400, title: "Der Name ist zu lang.");
			var rawEmail = body!.Email!.Trim();
			var (outcome, accountId, invitationId) = await invitations.InviteAsync(
				normalized, rawEmail, displayName, role, decision!.AccountId, time.GetUtcNow(), token);
			return outcome switch
			{
				InviteOutcome.Invited => Results.Created(
					$"/api/admin/members",
					new
					{
						message = MemberInvitationService.MailAcceptedMessage,
						accountId,
						invitationId,
						status = "invited",
						mailStatus = "sent",
					}),
				InviteOutcome.Resent => Results.Ok(new
				{
					message = MemberInvitationService.MailResentMessage,
					accountId,
					invitationId,
					status = "invited",
					mailStatus = "sent",
				}),
				InviteOutcome.AlreadyActive => Results.Problem(
					statusCode: 409, title: MemberInvitationService.AlreadyActiveMessage),
				InviteOutcome.RoleConflict => Results.Problem(
					statusCode: 409, title: MemberInvitationService.RoleConflictMessage),
				InviteOutcome.Deactivated => Results.Problem(
					statusCode: 409, title: MemberInvitationService.DeactivatedMessage),
				_ => Results.Problem(statusCode: 502, title: MemberInvitationService.MailFailedMessage),
			};
		}).DisableAntiforgery();

		app.MapPost("/api/admin/invitations/resend", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, MemberInvitationService invitations, TimeProvider time, CancellationToken token,
			ResendRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireAdministratorAsync(context, accessor, access, requireFresh: true);
			if (error is not null)
				return error;
			if (!AuthSecurity.TryNormalizeEmail(body?.Email, out var normalized))
				return Results.Problem(statusCode: 400, title: "Die E-Mail-Adresse ist ungültig.");
			var (outcome, accountId, invitationId) = await invitations.ResendAsync(
				normalized, decision!.AccountId, time.GetUtcNow(), token);
			return outcome switch
			{
				ResendOutcome.Resent => Results.Ok(new
				{
					message = MemberInvitationService.MailResentMessage,
					accountId,
					invitationId,
					status = "invited",
					mailStatus = "sent",
				}),
				ResendOutcome.AlreadyAccepted => Results.Problem(
					statusCode: 409, title: MemberInvitationService.AlreadyAcceptedMessage),
				ResendOutcome.Deactivated => Results.Problem(
					statusCode: 409, title: MemberInvitationService.DeactivatedMessage),
				ResendOutcome.NotFound => Results.Problem(
					statusCode: 404, title: "Für diese Adresse liegt keine Einladung vor."),
				_ => Results.Problem(statusCode: 502, title: MemberInvitationService.MailFailedMessage),
			};
		}).DisableAntiforgery();

		app.MapPost("/api/admin/members/deactivate", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, MemberRevocationService revocation, TimeProvider time, CancellationToken token,
			DeactivateRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireAdministratorAsync(context, accessor, access, requireFresh: true);
			if (error is not null)
				return error;
			if (!Guid.TryParse(body?.AccountId, out var targetId) || targetId == Guid.Empty)
				return Results.Problem(statusCode: 400, title: "Die Kennung ist ungültig.");
			var (outcome, _) = await revocation.DeactivateAsync(targetId, decision!.AccountId, time.GetUtcNow(), token);
			return outcome switch
			{
				DeactivateOutcome.Deactivated => Results.Ok(new
				{
					message = MemberRevocationService.DeactivatedMessage,
					accountId = targetId,
					status = "deactivated",
				}),
				DeactivateOutcome.AlreadyDeactivated => Results.Problem(
					statusCode: 409, title: MemberRevocationService.AlreadyDeactivatedMessage),
				DeactivateOutcome.LastAdministrator => Results.Problem(
					statusCode: 409, title: MemberRevocationService.LastAdministratorMessage),
				_ => Results.Problem(statusCode: 404, title: MemberRevocationService.NotFoundMessage),
			};
		}).DisableAntiforgery();

		app.MapPost("/api/admin/members/reactivate", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, MemberRevocationService revocation,
			UserManager<ArchiveUser> users, TimeProvider time, CancellationToken token,
			ReactivateRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireAdministratorAsync(context, accessor, access, requireFresh: true);
			if (error is not null)
				return error;
			if (!Guid.TryParse(body?.AccountId, out var targetId) || targetId == Guid.Empty)
				return Results.Problem(statusCode: 400, title: "Die Kennung ist ungültig.");
			var (outcome, _) = await revocation.ReactivateAsync(targetId, decision!.AccountId, time.GetUtcNow(), token);
			if (outcome == ReactivateOutcome.NotFound)
				return Results.Problem(statusCode: 404, title: MemberRevocationService.NotFoundMessage);
			if (outcome == ReactivateOutcome.AlreadyActive)
				return Results.Problem(statusCode: 409, title: MemberRevocationService.AlreadyActiveMessage);
			var user = await users.FindByIdAsync(targetId.ToString());
			var status = user is not null && user.EmailConfirmed ? "active" : "invited";
			return Results.Ok(new
			{
				message = MemberRevocationService.ReactivatedMessage,
				accountId = targetId,
				status,
			});
		}).DisableAntiforgery();

		app.MapPost("/api/admin/members/role", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, MemberRevocationService revocation, TimeProvider time, CancellationToken token,
			RoleChangeRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireAdministratorAsync(context, accessor, access, requireFresh: true);
			if (error is not null)
				return error;
			if (!Guid.TryParse(body?.AccountId, out var targetId) || targetId == Guid.Empty)
				return Results.Problem(statusCode: 400, title: "Die Kennung ist ungültig.");
			var role = (body?.Role ?? string.Empty).Trim();
			if (!ArchiveRoles.All.Contains(role))
				return Results.Problem(statusCode: 400, title: "Die Rolle ist ungültig.");
			var (outcome, roles) = await revocation.ChangeRoleAsync(targetId, role, decision!.AccountId, time.GetUtcNow(), token);
			return outcome switch
			{
				RoleChangeOutcome.Changed => Results.Ok(new
				{
					message = MemberRevocationService.RoleChangedMessage,
					accountId = targetId,
					roles,
				}),
				RoleChangeOutcome.Unchanged => Results.Ok(new
				{
					message = MemberRevocationService.RoleChangedMessage,
					accountId = targetId,
					roles,
				}),
				RoleChangeOutcome.LastAdministrator => Results.Problem(
					statusCode: 409, title: MemberRevocationService.LastAdministratorMessage),
				_ => Results.Problem(statusCode: 404, title: MemberRevocationService.NotFoundMessage),
			};
		}).DisableAntiforgery();
	}

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireAdministratorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, bool requireFresh)
	{
		// Cookie presence first for a useful signed-out state: revoked or
		// signed-out callers have no principal and get 401.
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!decision.IsAdministrator)
			return (null, Results.Problem(statusCode: 403, title: MemberInvitationService.ForbiddenMessage));
		if (requireFresh && !accessor.RequireFreshVerification())
			return (null, Results.Problem(statusCode: 403, title: MemberInvitationService.FreshVerificationMessage));
		return (decision, null);
	}
}
