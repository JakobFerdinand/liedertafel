# ARC-034 implementation plan — Durable PDF text extraction (orchestrator)

Source of truth: [ARC-034 spec](./ARC-034-pdf-extraction.md). Dependencies
ARC-012 (maintenance contract) and ARC-051 (real storage outputs, Entra-only
hosted access) are implemented; ARC-051's hosted round-trip verification stays
open and does not block this slice. ARC-034 introduces the first real queue
consumer ("finite C# jobs") and the document-extraction table family that
ARC-035 (score-text search) and ARC-037 (import job) build on.

## Shared contract (frozen by slice S1, consumed by ARC-035/037/052)

- **Revision-keyed work:** one row in `extraction_jobs` per `FileRevision`
  (`ExtractionJob` entity, `Archive.Backend.Extraction`), primary key
  `RevisionId` pointing at the immutable revision (FK Restrict). Late or
  duplicated queue messages can therefore never overwrite another revision's
  text: results are written only into the row of the revision they belong to,
  and a completed row is never rewritten by a second message. ARC-033's
  current-pointer restore keeps every revision's extraction row intact.
- **States:** `Queued` → `Running` → `Completed` | `NoText` | `Failed`.
  `NoText` is the explicit "no extractable text" result for scanned PDFs
  (a useful terminal state, not an error). `Failed` carries a German
  `FailureReason`; the editor's explicit retry action resets it to `Queued`.
- **Row fields:** `RevisionId` (PK), `AssetId` (denormalized join key),
  `Status`, `Text` (bounded, default cap 100 000 characters), `FailureReason`
  (≤ 300, German), `AttemptCount`, `LastAttemptAt`, `CompletedAt`,
  `LastEnqueuedAt` (null until a queue send was accepted — the dispatch
  sweeper rediscovers such rows), `TriggeredByAccountId`, `CreatedAt`,
  `UpdatedAt`, app-bumped `RowVersion` concurrency token.
- **Transactional handoff (outbox):** finalize of a PDF revision (`score` or
  `document`) inserts the `ExtractionJob` row in the *same* `SaveChanges` as
  the revision. The queue send is best-effort and bounded; a send failure
  never fails finalize and never loses the request — the row is the durable
  work record that `--dispatch-extraction` (and the piggyback sweep inside the
  worker run) hands to the queue later. A database-commit failure leaves the
  upload session pending so the idempotent finalize can be replayed.
- **Queue:** `archive-extraction` (config `Archive:Extraction:QueueName`),
  envelope `{"type":"extraction","revisionId":"<guid>","traceParent":"…","traceState":"…"}`
  — trace context only, never secrets or baggage (see `RunArchiveJobAsync`
  contract). Connection-string mode (Azurite) or Entra token mode
  (`Archive:Extraction:QueueServiceUri` + `DefaultAzureCredential`) as with
  blob storage.
- **Editor API (antiforgery, CSRF, `no-store`, German ProblemDetails):**
  - `GET /api/revisions/extraction?ids=<guid>,…` (editor-only, ≤ 100 ids,
    response preserves request order, unknown ids are silently skipped):
    `{ results: [{ revisionId, assetId, status, text, failureReason, attemptCount, completedAt, lastAttemptAt, updatedAt, rowVersion }] }`
    with `status` ∈ `queued | running | completed | noText | failed`.
  - `POST /api/revisions/{id}/extraction/retry` (editor-only): `Failed` →
    reset to `Queued` (fresh attempts) and re-enqueue; missing job → create
    `Queued` and enqueue (covers revisions finalized before ARC-034);
    `Queued` → idempotent no-op that rediscovers a missing enqueue;
    `Completed`/`NoText` → idempotent current-state reply without new work;
    `Running` → 409 `ExtractionRunningMessage`. Non-PDF assets → 422.
- **Worker contract (`--extract-queue` finite command, same backend image):**
  reads the maintenance flag first (`Archive:MaintenanceMode`, the
  [runbook job contract](../../infrastructure/archive/README.md)) and abandons
  received messages while it is true; drains one receive round at a time
  (batch received with a visibility-fit count: at most `BatchSize` (8) and
  only as many messages as can each get their `TimeBudget` inside the
  receive visibility minus a release headroom; serial processing =
  constrained concurrency; each message is processed under an absolute
  deadline = the smaller of `TimeBudget` and the batch's remaining receive
  visibility, spanning database work, blob open and parsing — an expired
  deadline rejects the outcome via the bounded transient path; a single
  synchronous page parse cannot be interrupted inside PdfPig, and recovery
  runs use the parent token); deletes the message on terminal success
  (incl. idempotent duplicates) and on deterministic failure (corrupt,
  encrypted, oversize or overlength PDF → `Failed` without retry); abandons
  with visibility backoff on transient failure while attempts remain
  (`MaxAttempts` 5, counted on the row and the message's dequeue count),
  otherwise marks `Failed` terminal and deletes the message; exits as a
  finite run once a receive round is empty — no idle polling, no Neon queries
  while idle (production is re-triggered by the queue scale rule; KEDA polls
  the queue, not Neon). An interrupted run leaves `Running` rows on a
  **stale lease** (`LastAttemptAt` older than `StaleRunningAfter`, 15 min):
  the next dequeue re-takes the attempt (counted), the editor's retry action
  may reset a stale-lease row, and exhausted re-takes go terminal — an
  interrupted attempt can never strand the row.
- **Extraction bounds (honest residual):** the compressed input is capped at
  `MaxPdfBytes`; the parser's internal decoded expansion is a PdfPig-internal
  limit that no .NET API can cap — a runaway parse is bounded end-to-end by
  the container memory limit (OOM kill), after which the stale-lease recovery
  resolves the row through the bounded attempt ladder.
- **Trace propagation:** the sender stamps `traceparent`/`tracestate` from the
  request activity; the consumer parses the remote parent and opens
  `archive.queue.process` (Consumer) like `LocalServices`. All finite runs
  flush via `RunArchiveJobAsync`.
- **Extraction bounds (`Archive:Extraction` options):** `MaxTextCharacters`
  (100 000 — a document whose text exceeds the cap answers a deterministic
  `Failed`, per the frozen overlength rule), `MaxPdfBytes` (64 MiB — larger
  documents fail with an honest German reason), `MaxPdfPages` (500 — pages
  beyond the cap are not read; the bounded partial text completes), `TimeBudget`
  (2 min per message, spanning blob open and parsing), `MaxAttempts` (5),
  `StaleRunningAfter` (15 min), `RedisplayAfter` (10 min),
  `MaxDispatchPerRun` (200), `VisibilityBackoff` (30 s), `BatchSize` (8),
  `ReceiveVisibility` (10 min), `MaxMessagesPerRun` (500), `DrainBudget`
  (10 min). Embedded text only, via PdfPig (pure managed, Apache-2.0); no
  OCR is introduced.

## Vertical slices (each directly testable, committed before the next)

- **S1 — persisted extraction core (backend + xUnit):** `ExtractionJob`
  entity + EF configuration + additive migration, finalize outbox hook,
  editor status/retry endpoints, `IExtractionQueue` seam with the Azure
  dispatcher, full API test coverage (outbox on finalize, send failure keeps
  the request, auth matrix, retry state matrix).
- **S2 — extraction worker (backend + xUnit):** `--extract-queue` and
  `--dispatch-extraction` finite commands, `IExtractionMessageSource` +
  Azure source, bounded PdfPig extraction, idempotency, bounded retry with
  the stale-lease recovery for interrupted attempts, maintenance pause, trace
  propagation, fresh worker scope per message, full worker test coverage
  (text and scanned PDFs, duplicates, failures, bounds, leases).
- **S3 — AppHost wiring (apphost + integration test):** `archive-extract`
  explicit-start resource against Azurite, storage init creates the
  extraction queue, real-stack integration test: upload a text-bearing PDF →
  finalize → run the finite job → completed status with the extracted text;
  the run finishes and leaves no continuously running worker.
- **S4 — editor status/preview UI (frontend + browser check):** extraction
  status line + small text preview + retry action on PDF entries in
  `noten-bereich.tsx` and `auftritt-dokumente.tsx`, batch status polling with
  bounded refresh, Playwright coverage of all states incl. retry.
- **S5 — real job/queue/identity path (Bicep + runbook):** production queue
  access for the runtime identity (sender) and a least-privilege extraction
  job identity (processor + blob reader), queue-triggered Container Apps Job
  running the same image with `--extract-queue`, DB/runtime env wiring,
  `az bicep build` verification and runbook update.
- **S6 — docs and handover:** ARC-034 spec status `done`, implementation
  notes, issue-index update, final full verification.

## Definitions of done per slice

1. The named verification commands pass locally before the commit.
2. Commit (Karma format) immediately after the slice verifies.
3. A fresh review subagent reviews the committed range against the ARC-034
   spec and the shared contract; blocking findings are fixed by subagents and
   re-reviewed before the next slice.
4. Slice S1 additionally updates ARC-034 frontmatter `status: in_progress`;
   the final slice updates `status: done` and records verification
   commands/results in the issue file.

## Final verification and handover

- `dotnet test tests/archive/backend`, `dotnet test tests/archive/apphost`
  (Docker available), `pnpm run check`, `pnpm run build`,
  `pnpm run test:browser`, and `az bicep build` for the touched Bicep files
  pass before pushing.
- Push directly to `origin main`; wait for the `Archive` workflow build
  (check job) to pass. Failures are fixed by subagents until green.
- Mark ARC-034 `status: done`, record verification commands and results in
  the issue file, and update the issue index row in `issues.md`.
