using System.Text.Json;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Events;

public sealed record SaveAuftrittEntwurfRequest(Guid? EntwurfId, string? Titel, int? Dateien, JsonElement? Formular);

/// <summary>
/// "Angefangen" for Auftritte (#131): the persisted draft of a programme scan
/// or concert video that is not saved yet. Editor-only, owned by the editor who
/// started it, CSRF-protected on every change. The browser sends its whole form
/// (<c>PUT</c>, last write wins) and gets it back to resume. See
/// <see cref="AuftrittEntwurf"/> for what is kept.
/// </summary>
public static class AuftrittEntwurfEndpoints
{
	public const string NotFoundMessage = "Der angefangene Eintrag wurde nicht gefunden.";

	public const string InvalidMessage = "Der Entwurf ist ungültig.";

	public const string TooManyMessage = "Es sind zu viele Einträge angefangen. Bitte erst welche verwerfen.";

	public static void MapAuftrittEntwurfEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/auftritt-entwuerfe", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var rows = await db.AuftrittEntwuerfe.AsNoTracking()
				.Where(e => e.OwnerAccountId == decision!.AccountId)
				.Select(e => new { e.Id, e.Title, e.DateiCount, e.UpdatedAt })
				.ToListAsync(token);
			return Results.Ok(new
			{
				entwuerfe = rows.OrderByDescending(r => r.UpdatedAt)
					.Select(r => new { entwurfId = r.Id, titel = r.Title, dateien = r.DateiCount, aktualisiertAm = r.UpdatedAt }),
			});
		});

		app.MapGet("/api/auftritt-entwuerfe/{id:guid}", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access, ArchiveDbContext db,
			Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var entwurf = await db.AuftrittEntwuerfe.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == id && e.OwnerAccountId == decision!.AccountId, token);
			if (entwurf is null)
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			using var form = JsonDocument.Parse(entwurf.Form);
			return Results.Ok(new
			{
				entwurf = new
				{
					entwurfId = entwurf.Id,
					titel = entwurf.Title,
					dateien = entwurf.DateiCount,
					formular = form.RootElement.Clone(),
					aktualisiertAm = entwurf.UpdatedAt,
				},
			});
		});

		app.MapPut("/api/auftritt-entwuerfe/{id:guid}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, Guid id, CancellationToken token, SaveAuftrittEntwurfRequest? body) =>
		{
			if (await LiedAccess.ValidateAntiforgeryAsync(antiforgery, context) is { } csrf)
				return csrf;
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			if (Validate(id, body) is { } invalid)
				return Results.Problem(statusCode: 400, title: invalid);

			var entwurf = await db.AuftrittEntwuerfe.FirstOrDefaultAsync(e => e.Id == id, token);
			var now = time.GetUtcNow();
			if (entwurf is null)
			{
				var owned = await db.AuftrittEntwuerfe.CountAsync(e => e.OwnerAccountId == decision!.AccountId, token);
				if (owned >= AuftrittEntwurfRules.MaxEntwuerfeProKonto)
					return Results.Problem(statusCode: 409, title: TooManyMessage);
				entwurf = new AuftrittEntwurf { Id = id, OwnerAccountId = decision!.AccountId, CreatedAt = now };
				db.AuftrittEntwuerfe.Add(entwurf);
			}
			else if (entwurf.OwnerAccountId != decision!.AccountId)
			{
				return Results.Problem(statusCode: 404, title: NotFoundMessage);
			}
			entwurf.Title = body!.Titel!.Trim();
			entwurf.DateiCount = body.Dateien!.Value;
			entwurf.Form = body.Formular!.Value.GetRawText();
			entwurf.UpdatedAt = now;
			await db.SaveChangesAsync(token);
			return Results.Ok(new { aktualisiertAm = entwurf.UpdatedAt });
		}).DisableAntiforgery();

		app.MapDelete("/api/auftritt-entwuerfe/{id:guid}", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, Guid id, CancellationToken token) =>
		{
			if (await LiedAccess.ValidateAntiforgeryAsync(antiforgery, context) is { } csrf)
				return csrf;
			context.Response.Headers.CacheControl = "no-store";
			var (decision, error) = await LiedAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var entwurf = await db.AuftrittEntwuerfe
				.FirstOrDefaultAsync(e => e.Id == id && e.OwnerAccountId == decision!.AccountId, token);
			if (entwurf is not null)
			{
				db.AuftrittEntwuerfe.Remove(entwurf);
				await db.SaveChangesAsync(token);
			}
			// Discarding twice, or discarding what is gone, is still discarded.
			return Results.NoContent();
		}).DisableAntiforgery();
	}

	private static string? Validate(Guid id, SaveAuftrittEntwurfRequest? body)
	{
		if (body is null || body.EntwurfId != id || body.Titel is null || body.Titel.Length > EventEndpoints.TitleMaxLength)
			return InvalidMessage;
		if (body.Dateien is null or < 0 or > AuftrittEntwurfRules.MaxDateien)
			return InvalidMessage;
		if (body.Formular is not { ValueKind: JsonValueKind.Object } form)
			return InvalidMessage;
		if (!form.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
			|| !version.TryGetInt32(out var number) || number != AuftrittEntwurfRules.FormVersion)
			return InvalidMessage;
		if (form.GetRawText().Length > AuftrittEntwurfRules.MaxFormLength)
			return InvalidMessage;
		return null;
	}
}
