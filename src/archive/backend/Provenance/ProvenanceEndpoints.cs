using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Provenance;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Provenance;

/// <summary>
/// ARC-013-1 editor endpoints: the "Vorschläge" queue (listing, accept,
/// reject, refresh) and the per-field provenance revert. Everything is
/// editor-only: members receive 403, anonymous or revoked callers 401, and
/// members never learn that any of it exists beyond that uniform answer.
/// </summary>
public static class ProvenanceEndpoints
{
	public const string ForbiddenMessage = "Keine Berechtigung für die Verwaltung.";

	public const string NotFoundMessage = "Der Vorschlag wurde nicht gefunden.";

	public const string RevertNotFoundMessage = "Für dieses Feld ist kein früherer Wert bekannt.";

	public const string RevertUnknownFieldMessage = "Unbekanntes Feld.";

	public const string RevertUnknownTargetMessage = "Das Ziel des Feldes wurde nicht gefunden.";

	public const string UnknownEntityTypeMessage = "Unbekannter Eintragstyp.";

	public static void MapProvenanceEndpoints(this WebApplication app)
	{
		app.MapGet("/api/proposals", async (HttpContext context, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, ProposalDecisions decisions,
			ProposalHandlers handlers, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var open = await db.Proposals
				.Where(p => p.Status == ProposalStatus.Open)
				.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
				.ToListAsync(token);
			var items = new List<object>();
			foreach (var proposal in open)
			{
				var handler = handlers.Resolve(proposal.Kind);
				ProposalTargetState? target = null;
				if (proposal.TargetEntityId is not null && proposal.TargetEntityType is not null && handler is not null)
					target = await handler.LoadTargetAsync(db, proposal, token);
				items.Add(new
				{
					id = proposal.Id,
					kind = proposal.Kind.ToString(),
					kindLabel = ProposalKindText.Of(proposal.Kind),
					targetEntityType = proposal.TargetEntityType,
					targetEntityId = proposal.TargetEntityId,
					payload = ParsePayload(proposal.Payload),
					reason = proposal.Reason,
					confidence = proposal.Confidence.ToString(),
					source = proposal.Source.ToString(),
					model = proposal.Model,
					promptVersion = proposal.PromptVersion,
					sourceDescription = proposal.SourceDescription,
					targetRowVersion = proposal.TargetRowVersion,
					targetCurrentRowVersion = target?.RowVersion,
					targetCurrentSummary = target?.CurrentValueSummary,
					isStale = target is not null && proposal.TargetRowVersion is not null
						&& proposal.TargetRowVersion != target.RowVersion,
					createdAt = proposal.CreatedAt,
				});
			}
			return Results.Ok(new { proposals = items });
		});

		app.MapPost("/api/proposals/{id}/accept", async (HttpContext context, IAntiforgery antiforgery,
			CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			ProposalDecisions decisions, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var result = await decisions.AcceptAsync(id, decision!.AccountId, token);
			if (result.Ok)
				return Results.Ok(new { accepted = true });
			Dictionary<string, object?>? extensions = null;
			if (result.TargetState is not null)
			{
				extensions = new()
				{
					["currentValue"] = result.TargetState.CurrentValueSummary,
					["proposedValue"] = result.ProposedValue,
					["currentRowVersion"] = result.TargetState.RowVersion,
				};
			}
			return Results.Problem(statusCode: result.HttpCode, title: result.Title, extensions: extensions);
		}).DisableAntiforgery();

		app.MapPost("/api/proposals/{id}/reject", async (HttpContext context, IAntiforgery antiforgery,
			CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			ProposalDecisions decisions, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var result = await decisions.RejectAsync(id, decision!.AccountId, token);
			if (result.Ok)
				return Results.Ok(new { rejected = true });
			return Results.Problem(statusCode: result.HttpCode, title: result.Title);
		}).DisableAntiforgery();

		app.MapPost("/api/proposals/{id}/refresh", async (HttpContext context, IAntiforgery antiforgery,
			CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			ProposalDecisions decisions, Guid id, CancellationToken token) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var result = await decisions.RefreshAsync(id, token);
			if (result.Ok)
				return Results.Ok(new { refreshed = true });
			return Results.Problem(statusCode: result.HttpCode, title: result.Title);
		}).DisableAntiforgery();

		app.MapPost("/api/provenance/revert", async (HttpContext context, IAntiforgery antiforgery,
			CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			CatalogueWriteService writes, TimeProvider time, CancellationToken token,
			RevertFieldRequest? body) =>
		{
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			if (body?.EntityType is null || body.EntityId == Guid.Empty || body.Field is null)
				return Results.Problem(statusCode: 400, title: "Das Feld wurde nicht angegeben.");
			if (!FieldCatalog.IsEntityType(body.EntityType))
				return Results.Problem(statusCode: 400, title: UnknownEntityTypeMessage);
			var actor = new WriteActor(decision!.AccountId, time.GetUtcNow());
			var outcome = await writes.RevertFieldAsync(body.EntityType, body.EntityId, body.Field, actor, token);
			if (outcome.Outcome.Status == WriteOutcomeStatus.Invalid)
				return Results.Problem(statusCode: 400, title: outcome.Outcome.Title);
			if (!outcome.Outcome.IsSaved)
				return Results.Problem(statusCode: outcome.Outcome.HttpCode, title: RevertNotFoundMessage);
			return Results.Ok(new { reverted = true });
		}).DisableAntiforgery();

		// ARC-013-1 diagnostic: runs one automated field write through the
		// real AutomatedFieldWriter so apply/proposal behaviour above and
		// below the threshold is observable end to end in development.
		if (app.Environment.IsDevelopment())
		{
			app.MapPost("/api/dev/provenance-write", async (HttpContext context, IAntiforgery antiforgery,
				AutomatedFieldWriter writer, CancellationToken token,
				DevProvenanceWriteRequest? body) =>
			{
				try { await antiforgery.ValidateRequestAsync(context); }
				catch (AntiforgeryValidationException)
				{
					return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
				}
				if (body is null
					|| body.EntityType is null || body.EntityId == Guid.Empty || body.Field is null
					|| string.IsNullOrWhiteSpace(body.Confidence))
					return Results.Problem(statusCode: 400, title: "Unvollständiger Schreibzugriff.");
				if (!FieldCatalog.IsEntityType(body.EntityType))
					return Results.Problem(statusCode: 400, title: UnknownEntityTypeMessage);
				ProvenanceSource source = body.Source is null ? ProvenanceSource.Regex
					: body.Source == "ai" ? ProvenanceSource.Ai : ProvenanceSource.Regex;
				var confidence = body.Confidence == "unsicher" ? ProvenanceConfidence.Unsicher
					: body.Confidence == "sicher" ? ProvenanceConfidence.Sicher
					: ProvenanceConfidence.Unsicher;
				var outcome = await writer.WriteFieldAsync(new AutomatedFieldWriter.FieldWriteRequest(
					EntityType: body.EntityType,
					EntityId: body.EntityId,
					Field: body.Field,
					Value: body.Value,
					Source: source,
					Confidence: confidence,
					Model: body.Model,
					PromptVersion: "dev-1",
					Reason: body.Reason ?? "Automatische Auswertung der Schulungsauswertung",
					SourceDescription: "Entwicklungsdiagnose"), null, token);
				return Results.Ok(new
				{
					result = outcome.Result.ToString(),
					proposalId = outcome.ProposalId,
					error = outcome.ErrorTitle,
				});
			}).DisableAntiforgery();
		}
	}

	private static object? ParsePayload(string payload)
	{
		try
		{
			return JsonSerializer.Deserialize<JsonElement>(payload);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!CatalogueWriteService.IsEditor(decision))
			return (null, Results.Problem(statusCode: 403, title: ForbiddenMessage));
		return (decision, null);
	}
}

public sealed record RevertFieldRequest(string? EntityType, Guid EntityId, string? Field);

public sealed record DevProvenanceWriteRequest(
	string? EntityType, Guid EntityId, string? Field, string? Value,
	string? Source, string? Confidence, string? Model, string? Reason);
