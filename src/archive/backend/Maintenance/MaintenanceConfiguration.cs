using Microsoft.AspNetCore.Http;

namespace Archive.Backend.Maintenance;

/// <summary>
/// Shared maintenance state for controlled database releases (ARC-012).
///
/// The flag is <c>Archive:MaintenanceMode</c> (environment
/// <c>Archive__MaintenanceMode</c>). Bicep owns the declared value and the
/// infrastructure workflow preserves the live value on re-runs, so an
/// infrastructure change cannot silently reopen member access mid-window.
/// The release workflow toggles the live value with
/// <c>az containerapp update --set-env-vars</c> before migrations and clears
/// it after smoke checks pass.
///
/// Contract for future jobs (ARC-032/035): before conflicting work, read
/// <c>GET /api/maintenance</c> (or the same configuration flag) and pause
/// while <c>maintenance</c> is true. Diagnostics and probes
/// (<c>/alive</c>, <c>/health</c>, <c>/api/build</c>,
/// <c>/api/maintenance</c>) always stay available.
/// </summary>
public static class MaintenanceConfiguration
{
	public const string ModeKey = "Archive:MaintenanceMode";

	public const string MaintenanceEndpoint = "/api/maintenance";

	public const string MaintenanceMessage =
		"Wartungsarbeiten: Das Archiv ist vorübergehend nicht verfügbar. Bitte versuchen Sie es später erneut.";

	public static bool IsEnabled(IConfiguration configuration) =>
		configuration.GetValue<bool>(ModeKey);

	public static void MapMaintenanceEndpoints(this WebApplication app)
	{
		app.MapGet(MaintenanceEndpoint, (HttpContext context, IConfiguration configuration) =>
		{
			// Releases toggle this within minutes; never let it go stale.
			context.Response.Headers.CacheControl = "no-store";
			var enabled = IsEnabled(configuration);
			return Results.Ok(new
			{
				maintenance = enabled,
				message = enabled ? MaintenanceMessage : "Das Archiv ist verfügbar.",
			});
		});
	}

	/// <summary>
	/// Pauses conflicting API work while the maintenance flag is set. Every
	/// <c>/api/*</c> request except the release contract endpoints answers
	/// 503 with the German maintenance message, so no member write or job
	/// trigger can race a migration. The frontend and its status page keep
	/// serving so members see the maintenance banner.
	/// </summary>
	public static void UseArchiveMaintenanceMode(this WebApplication app)
	{
		app.Use(async (context, next) =>
		{
			if (IsMaintenanceRequest(context.Request.Path) && IsEnabled(app.Configuration))
			{
				context.Response.Headers.RetryAfter = "600";
				await Results.Problem(statusCode: 503, title: MaintenanceMessage).ExecuteAsync(context);
				return;
			}
			await next(context);
		});
	}

	internal static bool IsMaintenanceRequest(PathString path)
	{
		if (!path.StartsWithSegments("/api"))
			return false;
		if (path.StartsWithSegments("/api/build"))
			return false;
		return !path.StartsWithSegments(MaintenanceEndpoint);
	}
}
