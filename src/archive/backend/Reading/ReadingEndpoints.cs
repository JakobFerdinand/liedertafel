using Archive.Backend.Auth;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Archive.Backend.Reading;

/// <summary>
/// ARC-034-1 editor surface of the AI reading: restart it for a score and ask
/// for the pages beyond the first request ("Rest lesen"). The state itself
/// rides on the extraction batch query (<c>reading</c> in every result), so
/// the lists keep their one poll. Both actions are editor-only with
/// antiforgery, no-store and German ProblemDetails, like the extraction
/// retry; members never reach them.
/// </summary>
public static class ReadingEndpoints
{
	public const string NotScoreMessage = "Nur Noten eines Liedes werden von der KI gelesen.";

	public const string ExtractionNotFinishedMessage = "Die Textauswertung ist noch nicht abgeschlossen.";

	public const string ReadingRunningMessage = "Die Seiten werden gerade gelesen.";

	public const string NothingMoreToReadMessage = "Es gibt keine weiteren Seiten zu lesen.";

	public const string NotEnabledMessage = "Das Lesen durch die KI ist noch nicht eingerichtet.";

	/// <summary>The primary key of <c>reading_jobs</c>: two requests creating the same row collide on it.</summary>
	public const string PrimaryKeyName = "PK_reading_jobs";

	public static void MapReadingEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/dateien/{id}/reading/retry", (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, ILoggerFactory loggers, IReadingQueue queue,
			IOptions<ReadingOptions> options, IOptions<ExtractionOptions> extractionOptions, Guid id, CancellationToken token)
			=> ActAsync(context, antiforgery, accessor, access, db, time, loggers, queue, options, extractionOptions, id, restart: true, token))
			.DisableAntiforgery();

		app.MapPost("/api/dateien/{id}/reading/continue", (
			HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
			ArchiveDbContext db, TimeProvider time, ILoggerFactory loggers, IReadingQueue queue,
			IOptions<ReadingOptions> options, IOptions<ExtractionOptions> extractionOptions, Guid id, CancellationToken token)
			=> ActAsync(context, antiforgery, accessor, access, db, time, loggers, queue, options, extractionOptions, id, restart: false, token))
			.DisableAntiforgery();
	}

	private static async Task<IResult> ActAsync(
		HttpContext context, IAntiforgery antiforgery, CurrentUserAccessor accessor, ArchiveAccessService access,
		ArchiveDbContext db, TimeProvider time, ILoggerFactory loggers, IReadingQueue queue,
		IOptions<ReadingOptions> options, IOptions<ExtractionOptions> extractionOptions, Guid id, bool restart,
		CancellationToken token)
	{
		var (decision, error) = await ArchiveAccess.RequireEditorAsync(context, antiforgery, accessor, access);
		if (error is not null)
			return error;
		if (!options.Value.Enabled)
			return Results.Problem(statusCode: 409, title: NotEnabledMessage);
		var datei = await DateiQueries.Visible(db).FirstOrDefaultAsync(d => d.Id == id, token);
		if (datei is null)
			return Results.Problem(statusCode: 404, title: ExtractionEndpoints.DateiNotFoundMessage);
		if (!ReadingService.IsReadable(datei))
			return Results.Problem(statusCode: 422, title: NotScoreMessage);
		var extraction = await db.ExtractionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.DateiId == id, token);
		if (extraction is null || extraction.Status is not (ExtractionStatus.Completed or ExtractionStatus.NoText))
			return Results.Problem(statusCode: 409, title: ExtractionNotFinishedMessage);
		var textFull = ReadingService.TextIsFull(extraction, extractionOptions.Value.MaxTextCharacters);

		// Two passes: a concurrent request may insert the same primary key
		// (the only collision that is retried); the second pass reloads and
		// runs the state matrix again, so the loser answers idempotently.
		for (var pass = 0; pass < 2; pass++)
		{
			var now = time.GetUtcNow();
			var job = await db.ReadingJobs.FirstOrDefaultAsync(j => j.DateiId == id, token);
			var enqueue = false;
			var creating = false;
			if (job is null)
			{
				if (!restart)
					return Results.Problem(statusCode: 409, title: NothingMoreToReadMessage);
				// Dateien extracted before ARC-034-1 gain their row here.
				job = ReadingService.CreateForDatei(datei, decision!.AccountId, options.Value.MaxPagesPerRequest, now);
				db.ReadingJobs.Add(job);
				enqueue = true;
				creating = true;
			}
			else if (job.Status == ReadingStatus.Running && !ReadingService.HasStaleLease(job, now, options.Value))
			{
				return Results.Problem(statusCode: 409, title: ReadingRunningMessage);
			}
			else if (!restart)
			{
				if (job.Status == ReadingStatus.Completed && job.IsPartial && !textFull)
				{
					// "Rest lesen": the next pages, and the fields again for what is still empty.
					job.PageLimit = job.PagesRead + options.Value.MaxPagesPerRequest;
					job.FieldsDone = false;
					job.AttemptCount = 0;
					job.CompletedAt = null;
					Requeue(job, now);
					enqueue = true;
				}
				else if (job.Status is ReadingStatus.Queued or ReadingStatus.WaitingForBudget or ReadingStatus.Running)
				{
					// Already on its way: an idempotent answer.
					return Results.Ok(new { reading = ReadingPayload(job, textFull) });
				}
				else
				{
					return Results.Problem(statusCode: 409, title: NothingMoreToReadMessage);
				}
			}
			else if (job.Status is ReadingStatus.Failed or ReadingStatus.Running)
			{
				// A failed reading that read nothing of a longer scan asks for the next pages too.
				if (job.FailureReason == ReadingWorker.NothingReadReason && job.IsPartial)
					job.PageLimit = job.PagesRead + options.Value.MaxPagesPerRequest;
				job.AttemptCount = 0;
				job.FailureReason = null;
				Requeue(job, now);
				enqueue = true;
			}
			else if (job.Status == ReadingStatus.Queued && job.LastEnqueuedAt is null)
			{
				enqueue = true;
			}
			else
			{
				// Completed, waiting for budget or already queued: nothing new to start.
				return Results.Ok(new { reading = ReadingPayload(job, textFull) });
			}

			try
			{
				await db.SaveChangesAsync(token);
			}
			catch (DbUpdateConcurrencyException)
			{
				return Results.Problem(statusCode: 409, title: ExtractionEndpoints.ConcurrencyMessage);
			}
			catch (DbUpdateException exception) when (creating && pass == 0 && IsPrimaryKeyCollision(exception))
			{
				// A concurrent request inserted this row first: detach the
				// losing insert and answer from what is stored now.
				db.Entry(job).State = EntityState.Detached;
				continue;
			}
			if (enqueue)
				await ReadingService.TryEnqueueAsync(db, queue, loggers.CreateLogger("Archive.Reading"), id, time, token);
			return Results.Ok(new { reading = ReadingPayload(job, textFull) });
		}
		return Results.Problem(statusCode: 409, title: ExtractionEndpoints.ConcurrencyMessage);
	}

	/// <summary>True only for the unique violation on the reading row's primary key; any other failure surfaces.</summary>
	private static bool IsPrimaryKeyCollision(DbUpdateException exception)
		=> exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
			&& string.Equals(postgres.ConstraintName, PrimaryKeyName, StringComparison.Ordinal);

	private static void Requeue(ReadingJob job, DateTimeOffset now)
	{
		job.Status = ReadingStatus.Queued;
		job.LastAttemptAt = null;
		job.LastEnqueuedAt = null;
		job.UpdatedAt = now;
		job.RowVersion++;
	}

	/// <summary>Wire shape of the reading state, shared by the batch query and the actions.</summary>
	public static object ReadingPayload(ReadingJob job, bool textFull = false) => new
	{
		status = job.Status switch
		{
			ReadingStatus.Queued => "queued",
			ReadingStatus.Running => "running",
			ReadingStatus.WaitingForBudget => "waitingForBudget",
			ReadingStatus.Completed => "completed",
			ReadingStatus.Failed => "failed",
			_ => job.Status.ToString().ToLowerInvariant(),
		},
		pagesTotal = job.PagesTotal,
		pagesRead = job.PagesRead,
		// "Teilweise gelesen" with pages left that the text bound still has room for;
		// when the stored text is full another request could add nothing.
		partial = job.IsPartial && !textFull,
		fieldsApplied = job.FieldsApplied,
		failureReason = job.FailureReason,
		attemptCount = job.AttemptCount,
		completedAt = job.CompletedAt,
		updatedAt = job.UpdatedAt,
		rowVersion = job.RowVersion,
	};
}
