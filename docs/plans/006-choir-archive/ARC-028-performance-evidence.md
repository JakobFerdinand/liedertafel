---
id: ARC-028
status: done
phase: core
kind: slice
depends_on: ["ARC-013", "ARC-024"]
touches: ["performances", "events", "db-migrations"]
external_inputs: []
---

# ARC-028 — Record what a historical source actually establishes

**Depends on:** [ARC-013](ARC-013-first-published-song.md),
[ARC-024](ARC-024-historical-event.md).

## Outcome

An editor links a song to an old event as either a confirmed performance or an
unconfirmed programme mention, even when its arrangement is unknown.

## Acceptance criteria

- [x] Add event-side entry/editing with song, optional known arrangement/version,
  evidence status, source notes and stable occurrence identity.
- [x] Preserve event date uncertainty and reject mismatched song/arrangement
  selections. Missing facts remain unknown rather than invented.
- [x] Distinguish repeated occurrences of the same song at one event from repeated
  submissions of the same occurrence; idempotent retries cannot inflate totals.
- [x] Record editor attribution and publish useful partial history. Date passage
  or a scanned programme alone never silently confirms a performance.
- [x] Define the performance ID and evidence contract consumed by recordings and
  programme confirmation, independent of future-programme editing.

## Verification

Enter an approximate-year programme mention and a confirmed performance with
unknown arrangement. Edit evidence status, retry a submission and enter a genuine
repeat occurrence; verify identity, source context and permissions.

## Handoff and parallel work

ARC-026/025 may implement future programmes concurrently. Agree only the shared
event/song IDs and planned-versus-actual boundary; do not combine their persistence
into a single ambiguous 'performed' flag.

## Implementation record

Slice 1 (backend evidence core) completed 2026-09-26 — entities
`Performance`/`PerformanceEvidenceStatus` in `src/archive/backend/Events/`, additive
migration `20260926061548_PerformanceEvidence`, `PerformanceEndpoints` API
(POST /api/events/{eventId}/performances, PATCH /api/performances/{id},
POST /api/performances/{id}/delete, GET /api/events/{eventId}/performances,
GET /api/songs/{songId}/performances), event-detail embed with role-gated member
visibility (source notes editor-only), idempotency keys plus content-duplicate guard,
editor attribution with RowVersion/409 concurrency.

- `dotnet test tests/archive/backend --filter "FullyQualifiedName~PerformanceApiTests"` → 15/15 passed.
- `dotnet test tests/archive/backend` → 353 passed, 0 failed.
- Remaining slices: editor workbench UI, member partial history UI.

Slices 2 and 3 completed 2026-09-26 — editor workbench for recording/editing/deleting evidence on the event detail (`components/auftritt-belege.tsx`, idempotency key per form instance, 409 reload-and-redo) and the member partial-history view with honest evidence distinction ("Bestätigt" vs "Programmangabe"), unknown chains rendered as "Fassung unbekannt", deep links via the shared recipe; reviewed by three fresh review passes (approve after fixes).

- `dotnet test tests/archive/backend --filter "FullyQualifiedName~PerformanceApiTests"` → 15/15 passed
- `dotnet test tests/archive/backend` → 353 passed, 0 failed
- `dotnet test tests/archive/apphost --filter "FullyQualifiedName~CleanStartConnectsDependenciesAndRunsExplicitMigrationsAndWorker"` → passed (clean Aspire stack; `_PerformanceEvidence` asserted pending before `archive-migrate`, empty after; an initial failure traced to a stale `next dev` process from a parallel session holding the frontend dev lock — resolved by terminating it, no code change)
- `dotnet build src/archive/Archive.slnx` → 0 warnings, 0 errors
- `pnpm run check` → green; `pnpm run build` → static export succeeded
- `pnpm run test:browser -- --grep "Belege"` → 10/10 on desktop + mobile; `programm.spec` 28/28 and `auftritte.spec` 16/16 no regressions (full browser suite exercised during Slice 2; the 3 `shell.spec` cases that need a real API origin are covered by the workflow's production smoke)
