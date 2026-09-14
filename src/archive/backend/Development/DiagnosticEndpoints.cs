using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Development;

public static class DiagnosticEndpoints
{
    public static void MapDevelopmentDiagnostics(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.MapGet("/api/dev/database", async (ArchiveDbContext db, CancellationToken token) =>
        {
            var result = await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync(token);
            var pending = (await db.Database.GetPendingMigrationsAsync(token)).ToArray();
            return Results.Ok(new { connected = result == 1, pendingMigrations = pending });
        });

        app.MapPost("/api/dev/exercise", async (HttpContext context, IAntiforgery antiforgery,
            LocalServices services, ILoggerFactory loggerFactory, CancellationToken token) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await services.ExerciseAsync(timeout.Token);
            loggerFactory.CreateLogger("Archive.Diagnostics").LogInformation("Local Blob, queue and mail exercise completed");
            return Results.Ok(new { message = "Blob und Warteschlange geprüft. Testmail wurde gesendet." });
        });

        app.MapPost("/api/dev/auth/seed", async (HttpContext context, IAntiforgery antiforgery,
            ArchiveDbContext db, TimeProvider time, CancellationToken token) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
            }
            var accounts = await AuthSeed.EnsureTestAccountsAsync(db, time.GetUtcNow(), token);
            return Results.Ok(new
            {
                message = "Testkonten sind bereit.",
                accounts = accounts.Select(a => new { email = a.Email, role = a.Role.ToString(), accountId = a.AccountId }),
            });
        });
    }
}
