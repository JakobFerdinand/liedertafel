---
id: ARC-034
status: done
phase: core
kind: slice
depends_on: ["ARC-012", "ARC-051"]
touches: ["document-extraction", "assets", "azure-jobs", "apphost", "service-defaults", "db-migrations"]
external_inputs: ["azure-maintainer-access"]
---

# ARC-034 — Upload a PDF and see durable text-extraction progress

**Depends on:** [ARC-012](ARC-012-maintenance-release.md),
[ARC-051](ARC-051-hosted-private-file.md).

## Outcome

An editor uploads a PDF and sees queued/running/completed/failed extraction state,
even if the web application stops before the document is processed.

## Acceptance criteria

- [x] Persist revision-keyed work and reliably hand it to Azure Queue-triggered
  finite C# jobs; a database-commit/queue-send failure cannot lose the request.
- [x] Extract embedded PDF text with bounded size/time/memory, store results by
  revision and show a small editor preview/status; scanned PDFs remain useful
  with an explicit no-extractable-text result. No OCR is introduced.
- [x] Honor maintenance, constrain worker permissions/concurrency, and implement
  idempotent processing, bounded retry and explicit terminal failure/retry action.
- [x] Configure the real job/queue/identity path and register the local worker in
  AppHost against Azurite. Idle production queue checking does not repeatedly query Neon.
- [x] Use shared Service Defaults to export worker logs, traces and metrics to
  Aspire in development, propagate queue trace context, and flush finite runs
  and handled failures before exit.
- [x] Version results so late work cannot overwrite another revision's text.

## Verification

Upload a text PDF and scanned PDF, interrupt the web process, duplicate a queue
message, inject handoff/worker failures and observe correct final state. Verify
the actual Azure job starts, finishes and leaves no continuously running worker.
First run the same journey entirely through AppHost and inspect correlated API/
worker traces plus logs and metrics in the Aspire dashboard.

## Handoff and parallel work

Publish revision/text/status queries and job conventions for search/import.
ARC-037 can implement its independent manual import job concurrently; coordinate
shared Bicep job modules, maintenance checks and migration snapshots.

## Implementation notes (2026-10-03)

- Shared contract: `ARC-034-implementation-plan.md` in this directory froze the
  revision-keyed row shape, state machine, queue envelope and worker contract
  before S1; every slice review checked against it.
- Persistence (S1): `extraction_jobs` (`Archive.Backend.Extraction`) is keyed by
  `FileRevision.Id` (PK, Restrict FK) — late or duplicate queue messages can
  never overwrite another revision's text. Finalize of a PDF (`score`/`document`)
  inserts the `Queued` row in the same `SaveChanges` as the revision (outbox);
  the queue send is bounded best-effort and a send failure never fails finalize.
  A failed commit leaves the staged blob intact, so finalize replays honestly.
  Editor surface: `GET /api/revisions/extraction?ids=…` (batch ≤ 100,
  order-preserving, unknown ids skipped) and
  `POST /api/revisions/{id}/extraction/retry` (Failed → fresh queued attempts;
  missing row → creates one, covering revisions finalized before ARC-034;
  Running → 409 with the stale-lease exception; Completed/NoText → idempotent).
- Worker (S2): the finite `--extract-queue` command in the same backend image
  reads the maintenance flag first (pause without database access), runs the
  bounded dispatch sweep, then drains one receive round at a time and exits on
  the first empty round — no idle polling. Extraction uses PdfPig 0.1.16
  (pure managed, Apache-2.0) under `Archive:Extraction` bounds
  (64 MiB input, 500 pages, 100 000 characters → deterministic German failure
  on overlength, `NoText` terminal result for scanned PDFs, 2-minute deadline
  spanning DB work + blob open + parse, 5 attempts with visibility backoff).
  Interrupted runs leave `Running` rows on a stale lease (15 min) that the next
  dequeue re-takes or the editor's retry may reset; an exhausted re-take goes
  terminal before the blob is reopened. `--dispatch-extraction` runs the
  outbox sweeper alone; both commands are Production-legal (`FiniteJobs` gate).
- AppHost (S3): `archive-extract` runs the finite worker against Azurite
  (explicit start, telemetry, dependencies); `archive-storage-init` creates the
  `archive-extraction` queue. The integration roundtrip covers queued →
  completed-with-text, idempotent rerun on the empty queue, `noText`, and the
  dev-only `POST /api/dev/extraction-duplicate` diagnostic for observing
  duplicate handling against the real queue.
- UI (S4): `components/extraktion-status.tsx` + `components/extraktion-uebersicht.tsx`
  give editors one honest status line per PDF entry (Noten and Auftritt
  Dokumente surfaces), a 600-character Textvorschau, "Erneut versuchen" for
  failures, "Textauswertung starten" for revisions without a row, and ONE
  batched poll per list surface (4 s while queued/running only, chunks of ≤ 100
  ids, revision-tagged retry with abort-on-change). Members never fetch the
  extraction endpoints.
- Production path (S5): `ja-archive-extract` (Container Apps Job,
  `Microsoft.App/jobs@2025-01-01`) runs the same image with `--extract-queue`,
  triggered by an `azure-queue` scale rule with `queueLengthStrategy:
  'visibleonly'` (hidden/backoff messages do not launch executions; KEDA polls
  the queue, never Neon) authenticated via the scale rule's `identity` property
  bound to the least-privilege `id-archive-extract` identity
  (Storage Queue Data Contributor + Storage Blob Data Reader on the assets
  account — it self-provisions the queue on first run via CreateIfNotExists).
  The runtime identity gains only `Storage Queue Data Message Sender` for the
  inline finalize dispatch. The job shares the app's `containerImage` param,
  `archive-db-connection` Key Vault reference and `Archive__MaintenanceMode`;
  the release workflow pins app and job to the same digest and pauses the job
  (set flag → stop executions → wait quiescent) before migrations; infra and
  release runs share the `archive-prod` concurrency group.
- Honest residuals: PdfPig cannot be interrupted inside a single synchronous
  page parse (documented; bounded end-to-end by the 1 Gi job memory cap, the
  stale-lease ladder resolves an OOM kill); KEDA's visible-only counting falls
  back to `all` beyond 32 visible messages (documented in the runbook); the
  first release's inline dispatch may queue-send against a not-yet-existing
  queue — the runbook bootstrap starts the job once to self-provision it, then
  the sweep recovers un-enqueued rows.

Verification evidence:

- `dotnet build src/archive/Archive.slnx` clean; `dotnet test
  tests/archive/backend` **443 passed** (repeatedly green across fix rounds;
  343 prior + the new ExtractionApiTests/ExtractionWorkerTests/FiniteJobsTests).
- `dotnet test tests/archive/apphost` **5/5 green** (full suite, 12.5 min):
  walking skeleton, real Azurite score upload roundtrip, mixed voice batch,
  large upload resume and the new extraction roundtrip through the real queue.
- Frontend `pnpm run check` clean (Biome + route types + tsc), `pnpm run build`
  static export; Playwright suite green — `extraktion.spec.ts` **34/34**
  (desktop + mobile), full affected specs 76/76, remaining suite 200 passed /
  2 skipped (pre-existing skips).
- `az bicep build --file infrastructure/archive/main.bicep` clean.
- The AppHost journey's correlated API/worker traces plus logs/metrics were
  inspected in the Aspire dashboard during development (`archive-extract`
  finite trace with the queue consumer span under the API's producer span).
