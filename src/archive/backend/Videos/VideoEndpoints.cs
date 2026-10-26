using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Videos;

/// <summary>
/// Editor surface for the MP4 playback copies (#161): a bounded batch status
/// query (the upload lists' polling) and the explicit retry action for failed
/// runs. Both are editor-only with the shared archive access decision;
/// responses are no-store and failures answer with German ProblemDetails.
/// Unknown ids are silently skipped so the batch query leaks nothing about
/// other Dateien.
/// </summary>
public static class VideoEndpoints
{
	public const string TooManyIdsMessage = "Es können höchstens 100 Dateien gleichzeitig angefragt werden.";

	public const string DateiNotFoundMessage = "Datei nicht gefunden.";

	public const string VideoNotSupportedMessage = "Die Konvertierung ist nur für Videoaufnahmen möglich.";

	public const string ConcurrencyMessage = "Die Datei wurde zwischenzeitlich geändert. Bitte neu laden.";

	public const string TranscodeRunningMessage = "Die Konvertierung läuft bereits.";

	/// <summary>Batch polling bound: one list shows at most this many entries.</summary>
	private const int MaxIdsPerRequest = 100;

	public static void MapVideoEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/dateien/videos", async (
			HttpContext context, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, CancellationToken token) =>
		{
			var (_, error) = await ArchiveAccess.RequireEditorAsync(context, accessor, access);
			if (error is not null)
				return error;
			var ids = ParseIds(context.Request.Query["ids"]);
			if (ids.Count > MaxIdsPerRequest)
				return Results.Problem(statusCode: 400, title: TooManyIdsMessage);
			var jobs = new List<VideoJob>();
			if (ids.Count > 0)
			{
				jobs = await db.VideoJobs.AsNoTracking()
					.Include(j => j.PlaybackDatei)
					.Where(j => ids.Contains(j.DateiId) && j.Datei.DeletedAt == null)
					.ToListAsync(token);
			}
			var byDatei = jobs.ToDictionary(j => j.DateiId);
			return Results.Ok(new
			{
				// Request order with unknown ids silently skipped.
				results = ids.Where(byDatei.ContainsKey)
					.Select(dateiId => StatusPayload(byDatei[dateiId]))
					.ToList(),
			});
		});

		app.MapPost("/api/dateien/{id}/video/retry", async (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor,
			ArchiveAccessService access, ArchiveDbContext db, TimeProvider time,
			ILoggerFactory loggerFactory, IVideoQueue queue, IOptions<VideoOptions> options,
			Guid id, CancellationToken token) =>
		{
			var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
			if (error is not null)
				return error;
			var datei = await DateiQueries.Visible(db).FirstOrDefaultAsync(d => d.Id == id, token);
			if (datei is null)
				return Results.Problem(statusCode: 404, title: DateiNotFoundMessage);
			if (!VideoService.IsVideoMaterial(datei.Kind, datei.ContentType))
				return Results.Problem(statusCode: 422, title: VideoNotSupportedMessage);
			var now = time.GetUtcNow();
			for (var pass = 0; pass < 2; pass++)
			{
				var job = await db.VideoJobs
					.FirstOrDefaultAsync(j => j.DateiId == id, token);
				var creating = job is null;
				var enqueue = false;
				if (job is null)
				{
					// Dateien finalized before #161 gain their row here.
					job = VideoService.CreateForDatei(datei, now);
					job.TriggeredByAccountId = decision!.AccountId;
					db.VideoJobs.Add(job);
					enqueue = true;
				}
				else if (job.Status == VideoStatus.Running && !VideoService.HasStaleLease(job, now, options.Value))
				{
					return Results.Problem(statusCode: 409, title: TranscodeRunningMessage);
				}
				else if (job.Status is VideoStatus.Completed or VideoStatus.NotNeeded)
				{
					// Terminal results are idempotent replies: no new work, no enqueue.
					return Results.Ok(StatusPayload(job));
				}
				else if (job.Status is VideoStatus.Failed or VideoStatus.Running)
				{
					// An explicit retry starts a fresh bounded attempt budget.
					job.Status = VideoStatus.Queued;
					job.FailureReason = null;
					job.AttemptCount = 0;
					job.LastAttemptAt = null;
					job.LastEnqueuedAt = null;
					job.UpdatedAt = now;
					job.RowVersion++;
					enqueue = true;
				}
				else if (job.Status == VideoStatus.Queued && job.LastEnqueuedAt is null)
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
					return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
				}
				catch (DbUpdateException) when (creating || pass == 1)
				{
					if (pass == 1)
						return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
					// A concurrent retry may have inserted this primary key.
					// Detach the losing insert, reload, and reapply the state matrix.
					db.Entry(job).State = EntityState.Detached;
					continue;
				}
				if (enqueue)
				{
					// Quiet: a send failure leaves the Queued row for the dispatch sweep.
					await VideoService.TryEnqueueAsync(db, queue, loggerFactory.CreateLogger("Archive.Videos"), id, time, token);
				}
				return Results.Ok(StatusPayload(job));
			}
			return Results.Problem(statusCode: 409, title: ConcurrencyMessage);
		}).DisableAntiforgery();
	}

	/// <summary>Single status item, shared by the batch query and the retry reply.</summary>
	private static object StatusPayload(VideoJob job) => new
	{
		dateiId = job.DateiId,
		status = ToStatusString(job.Status),
		playbackDateiId = job.PlaybackDateiId,
		failureReason = job.FailureReason,
		attemptCount = job.AttemptCount,
		completedAt = job.CompletedAt,
		lastAttemptAt = job.LastAttemptAt,
		lastEnqueuedAt = job.LastEnqueuedAt,
		updatedAt = job.UpdatedAt,
		rowVersion = job.RowVersion,
	};

	private static string ToStatusString(VideoStatus status) => status switch
	{
		VideoStatus.Queued => "queued",
		VideoStatus.Running => "running",
		VideoStatus.Completed => "completed",
		VideoStatus.NotNeeded => "notNeeded",
		VideoStatus.Failed => "failed",
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
}
