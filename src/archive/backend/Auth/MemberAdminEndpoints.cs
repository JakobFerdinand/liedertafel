using Microsoft.AspNetCore.Antiforgery;

namespace Archive.Backend.Auth;

public sealed record InviteRequest(string? Email, string? DisplayName, string? Role);

public sealed record ResendRequest(string? Email);

/// <summary>
/// Administrator member administration (ARC-006). All mutations require the
/// Administrator role, a fresh code verification (10-minute window) and CSRF.
/// Members/Editors receive 403; unauthenticated callers receive 401.
/// </summary>
public static class MemberAdminEndpoints
{
	public static void MapMemberAdminEndpoints(this WebApplication app)
	{
		app.MapGet("/api/admin/members", async (HttpContext context, CurrentUserAccessor accessor,
			MemberInvitationService invitations, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var current = accessor.Current;
			if (current is null)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			if (!current.IsInRole(ArchiveRoles.Administrator))
				return Results.Problem(statusCode: 403, title: MemberInvitationService.ForbiddenMessage);
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
			MemberInvitationService invitations, TimeProvider time, CancellationToken token,
			InviteRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var current = accessor.Current;
			if (current is null)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			if (!current.IsInRole(ArchiveRoles.Administrator))
				return Results.Problem(statusCode: 403, title: MemberInvitationService.ForbiddenMessage);
			if (!accessor.RequireFreshVerification())
				return Results.Problem(statusCode: 403, title: MemberInvitationService.FreshVerificationMessage);
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
				normalized, rawEmail, displayName, role, current.AccountId, time.GetUtcNow(), token);
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
			MemberInvitationService invitations, TimeProvider time, CancellationToken token,
			ResendRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var current = accessor.Current;
			if (current is null)
				return Results.Problem(statusCode: 401, title: "Anmeldung erforderlich.");
			if (!current.IsInRole(ArchiveRoles.Administrator))
				return Results.Problem(statusCode: 403, title: MemberInvitationService.ForbiddenMessage);
			if (!accessor.RequireFreshVerification())
				return Results.Problem(statusCode: 403, title: MemberInvitationService.FreshVerificationMessage);
			if (!AuthSecurity.TryNormalizeEmail(body?.Email, out var normalized))
				return Results.Problem(statusCode: 400, title: "Die E-Mail-Adresse ist ungültig.");
			var (outcome, accountId, invitationId) = await invitations.ResendAsync(
				normalized, current.AccountId, time.GetUtcNow(), token);
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
	}
}
