using Microsoft.AspNetCore.Http;
using Archive.Backend.Auth;
using Archive.Backend.Data;

namespace Archive.Backend.Ai;

/// <summary>
/// The maintainer-facing view of the AI budget (ARC-043 #150). The ledger
/// (ARC-022-3) holds the spend per feature and model; this editor-only
/// endpoint reads the current month for the Verwaltung and never pays a
/// cent: no reservation, no model call, no threshold logging.
/// </summary>
public static class AiBudgetEndpoints
{
	/// <summary>Endpoint path. Diagnostics always stay available (ARC-012 contract).</summary>
	public const string AiBudgetEndpoint = "/api/maintenance/ai-budget";

	public static void MapAiBudgetEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet(AiBudgetEndpoint, async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			AiCostNotice notices, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var state = await notices.StateAsync(token);
			return Results.Ok(new
			{
				month = state.Month,
				capEur = state.CapEur,
				thresholdsEur = state.ThresholdsEur,
				spentEur = decimal.Round(state.SpentEur, 4, MidpointRounding.AwayFromZero),
				notifiedEur = state.NotifiedMicroEur / 1_000_000m,
				features = state.Entries.Select(e => new
				{
					e.Feature,
					e.Model,
					e.Calls,
					e.InputTokens,
					e.OutputTokens,
					spentEur = (e.CostMicroEur + e.ReservedMicroEur) / 1_000_000m,
				}),
			});
		});
	}
}
