using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Extraction;

/// <summary>
/// ARC-034 editor surface for revision text extraction: a bounded batch
/// status query (for the upload lists' polling) and the explicit retry
/// action for failed runs. Both are editor-only with the shared archive
/// access decision; responses are no-store and failures answer with German
/// ProblemDetails. Unknown ids are silently skipped so the batch query leaks
/// nothing about other people's revisions.
/// </summary>
public static class ExtractionEndpoints
{
	public const string TooManyIdsMessage = "Es können höchstens 100 Revisionen gleichzeitig angefragt werden.";

	public const string RevisionNotFoundMessage = "Revision nicht gefunden.";

	public const string ExtractionNotSupportedMessage = "Die Textauswertung ist nur für PDF-Dateien möglich.";

	public const string ExtractionRunningMessage = "Die Auswertung läuft bereits.";

	/// <summary>Batch polling bound: one list shows at most this many entries.</summary>
	private const int MaxIdsPerRequest = 100;

	public static void MapExtractionEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/revisions/extraction", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			var (_, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var ids = ParseIds(context.Request.Query["ids"]);
			if (ids.Count > MaxIdsPerRequest)
				return Results.Problem(statusCode: 400, title: TooManyIdsMessage);
			var jobs = new List<ExtractionJob>();
			if (ids.Count > 0)
			{
				jobs = await db.ExtractionJobs.AsNoTracking()
					.Include(j => j.Revision).ThenInclude(r => r.Asset)
					.Where(j => ids.Contains(j.RevisionId))
					.ToListAsync(token);
			}
			var byRevision = jobs.ToDictionary(j => j.RevisionId);
			return Results.Ok(new
			{
				// Request order with unknown ids silently skipped.
				results = ids.Where(byRevision.ContainsKey)
					.Select(revisionId => StatusPayload(byRevision[revisionId]))
					.ToList(),
			});
		});

		app.MapPost("/api/revisions/{id}/extraction/retry", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			ILoggerFactory loggerFactory, IExtractionQueue queue, IOptions<ExtractionOptions> options,
			Guid id, CancellationToken token) =>
		{
			context.Response.Headers.CacheControl = "no-store";
			try { await antiforgery.ValidateRequestAsync(context); }
			catch (AntiforgeryValidationException)
			{
				return Results.Problem(statusCode: 400, title: "Ungültiger Sicherheitstoken.");
			}
			var (decision, error) = await RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var revision = await db.FileRevisions
				.Include(r => r.Asset)
				.FirstOrDefaultAsync(r => r.Id == id, token);
			if (revision is null)
				return Results.Problem(statusCode: 404, title: RevisionNotFoundMessage);
			if (!ExtractionService.IsExtractable(revision.Asset.AssetType))
				return Results.Problem(statusCode: 422, title: ExtractionNotSupportedMessage);
			var now = time.GetUtcNow();
			for (var pass = 0; pass < 2; pass++)
			{
				var job = await db.ExtractionJobs
					.Include(j => j.Revision).ThenInclude(r => r.Asset)
					.FirstOrDefaultAsync(j => j.RevisionId == id, token);
				var creating = job is null;
				var enqueue = false;
				if (job is null)
				{
					// Revisions finalized before ARC-034 gain their row here.
					job = ExtractionService.CreateForRevision(revision.Asset, revision, decision!.AccountId, now);
					job.Revision = revision;
					db.ExtractionJobs.Add(job);
					enqueue = true;
				}
				else if (job.Status == ExtractionStatus.Running && !ExtractionService.HasStaleLease(job, now, options.Value))
				{
					return Results.Problem(statusCode: 409, title: ExtractionRunningMessage);
				}
				else if (job.Status is ExtractionStatus.Completed or ExtractionStatus.NoText)
				{
					// Terminal results are idempotent replies: no new work, no enqueue.
					return Results.Ok(StatusPayload(job));
				}
				else if (job.Status is ExtractionStatus.Failed or ExtractionStatus.Running)
				{
					// An explicit retry starts a fresh bounded attempt budget.
					job.Status = ExtractionStatus.Queued;
					job.FailureReason = null;
					job.AttemptCount = 0;
					job.LastAttemptAt = null;
					job.LastEnqueuedAt = null;
					job.UpdatedAt = now;
					job.RowVersion++;
					enqueue = true;
				}
				else if (job.Status == ExtractionStatus.Queued && job.LastEnqueuedAt is null)
				{
					// Queued without an accepted send: rediscover the enqueue.
					enqueue = true;
				}
				try
				{
					await db.SaveChangesAsync(token);
				}
				catch (DbUpdateConcurrencyException)
				{
					return Results.Problem(statusCode: 409, title: AssetEndpoints.ConcurrencyMessage);
				}
				catch (DbUpdateException) when (creating || pass == 1)
				{
					if (pass == 1)
						return Results.Problem(statusCode: 409, title: AssetEndpoints.ConcurrencyMessage);
					// A concurrent retry may have inserted this primary key.
					// Detach the losing insert, reload, and reapply the state matrix.
					db.Entry(job).State = EntityState.Detached;
					continue;
				}
				if (enqueue)
				{
					// Quiet: a send failure leaves the Queued row for the dispatch sweep.
					await ExtractionService.TryEnqueueAsync(db, queue, loggerFactory.CreateLogger("Archive.Extraction"), id, time, token);
				}
				return Results.Ok(StatusPayload(job));
			}
			return Results.Problem(statusCode: 409, title: AssetEndpoints.ConcurrencyMessage);
		}).DisableAntiforgery();
	}

	/// <summary>Single status item, shared by the batch query and the retry reply.</summary>
	private static object StatusPayload(ExtractionJob job)
	{
		var analysis = job.Status == ExtractionStatus.Completed ? ScoreTextAnalyzer.Analyze(job.Text) : null;
		return new
		{
			revisionId = job.RevisionId,
			assetId = job.AssetId,
			revisionNumber = job.Revision.RevisionNumber,
			status = ToStatusString(job.Status),
			text = job.Text,
			// Readable text and facts are derived on read from the stored text.
			cleanText = analysis?.CleanText,
			// Voice, key and credits only describe scores, never event documents.
			facts = job.Revision.Asset?.AssetType == AssetEndpoints.ScoreAssetType ? FactsPayload(analysis?.Facts) : null,
			failureReason = job.FailureReason,
			attemptCount = job.AttemptCount,
			completedAt = job.CompletedAt,
			lastAttemptAt = job.LastAttemptAt,
			lastEnqueuedAt = job.LastEnqueuedAt,
			updatedAt = job.UpdatedAt,
			rowVersion = job.RowVersion,
		};
	}

	/// <summary>Wire shape of <see cref="ScoreFacts"/>; null when nothing was recognised.</summary>
	public static object? FactsPayload(ScoreFacts? facts) => facts is null || facts.IsEmpty
		? null
		: new
		{
			voice = facts.Voice,
			musicalKey = facts.MusicalKey,
			timeSignature = facts.TimeSignature,
			tempo = facts.Tempo,
			composer = facts.Composer,
			lyricist = facts.Lyricist,
			arranger = facts.Arranger,
			copyright = facts.Copyright,
		};

	private static string ToStatusString(ExtractionStatus status) => status switch
	{
		ExtractionStatus.Queued => "queued",
		ExtractionStatus.Running => "running",
		ExtractionStatus.Completed => "completed",
		ExtractionStatus.NoText => "noText",
		ExtractionStatus.Failed => "failed",
		_ => status.ToString().ToLowerInvariant(),
	};

	/// <summary>Comma-separated GUIDs; invalid tokens are ignored, duplicates collapse in request order.</summary>
	private static List<Guid> ParseIds(string? raw)
	{
		var ids = new List<Guid>();
		var seen = new HashSet<Guid>();
		if (string.IsNullOrWhiteSpace(raw))
			return ids;
		foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (Guid.TryParse(part, out var id) && seen.Add(id))
				ids.Add(id);
		}
		return ids;
	}

	private static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	private static async Task<(ArchiveAccessDecision? Decision, IResult? Error)> RequireEditorAsync(
		HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access)
	{
		if (accessor.Current is null)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		var decision = await access.GetDecisionAsync(context.User);
		if (decision is null || !decision.IsActive)
			return (null, Results.Problem(statusCode: 401, title: "Anmeldung erforderlich."));
		if (!IsEditor(decision))
			return (null, Results.Problem(statusCode: 403, title: "Keine Berechtigung für das Liedverzeichnis."));
		return (decision, null);
	}
}
