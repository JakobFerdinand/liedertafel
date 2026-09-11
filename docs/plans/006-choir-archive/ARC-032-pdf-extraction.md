---
id: ARC-032
status: planned
phase: core
kind: slice
depends_on: ["ARC-012", "ARC-049"]
touches: ["document-extraction", "assets", "azure-jobs", "apphost", "service-defaults", "db-migrations"]
external_inputs: ["azure-maintainer-access"]
---

# ARC-032 — Upload a PDF and see durable text-extraction progress

**Depends on:** [ARC-012](ARC-012-maintenance-release.md),
[ARC-049](ARC-049-hosted-private-file.md).

## Outcome

An editor uploads a PDF and sees queued/running/completed/failed extraction state,
even if the web application stops before the document is processed.

## Acceptance criteria

- [ ] Persist revision-keyed work and reliably hand it to Azure Queue-triggered
  finite C# jobs; a database-commit/queue-send failure cannot lose the request.
- [ ] Extract embedded PDF text with bounded size/time/memory, store results by
  revision and show a small editor preview/status; scanned PDFs remain useful
  with an explicit no-extractable-text result. No OCR is introduced.
- [ ] Honor maintenance, constrain worker permissions/concurrency, and implement
  idempotent processing, bounded retry and explicit terminal failure/retry action.
- [ ] Configure the real job/queue/identity path and register the local worker in
  AppHost against Azurite. Idle production queue checking does not repeatedly query Neon.
- [ ] Use shared Service Defaults to export worker logs, traces and metrics to
  Aspire in development, propagate queue trace context, and flush finite runs
  and handled failures before exit.
- [ ] Version results so late work cannot overwrite another revision's text.

## Verification

Upload a text PDF and scanned PDF, interrupt the web process, duplicate a queue
message, inject handoff/worker failures and observe correct final state. Verify
the actual Azure job starts, finishes and leaves no continuously running worker.
First run the same journey entirely through AppHost and inspect correlated API/
worker traces plus logs and metrics in the Aspire dashboard.

## Handoff and parallel work

Publish revision/text/status queries and job conventions for search/import.
ARC-035 can implement its independent manual import job concurrently; coordinate
shared Bicep job modules, maintenance checks and migration snapshots.
