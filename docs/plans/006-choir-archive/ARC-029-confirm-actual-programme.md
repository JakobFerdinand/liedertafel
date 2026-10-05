---
id: ARC-029
status: done
phase: core
kind: slice
depends_on: ["ARC-026", "ARC-028"]
touches: ["programmes", "performances", "db-migrations"]
external_inputs: []
---

# ARC-029 — Confirm what was actually sung after an event

**Depends on:** [ARC-026](ARC-026-publish-programme.md),
[ARC-028](ARC-028-performance-evidence.md).

## Outcome

An editor confirms an unchanged concert programme in one action, or marks a
skipped song and adds an encore while preserving the original plan.

## Acceptance criteria

- [x] Build an actual-performance review from a specific published revision,
  allowing bulk confirmation, omissions, additions and corrected selections.
- [x] Persist actual occurrences using ARC-028's identity/evidence model and link
  them to their originating planned items where applicable.
- [x] Repeated confirmation updates the same occurrences rather than doubling
  them; genuine repeated songs remain distinct performances.
- [x] Reject or explicitly resolve stale published-revision changes and preserve
  the planned list. Show members the distinction between plan and actual history.
- [x] Restrict confirmation to editors/admins and retain attribution.

## AI assistance

None in this slice. Confirming what was actually sung is a human statement by
definition. Drafted programmes from scans (ARC-025-1) arrive here as ordinary
unconfirmed entries. Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Confirm a programme unchanged, then exercise skip-plus-encore and repeated-song
cases. Retry the operation and submit against a stale revision. Assert planned
content remains intact and only confirmed actual occurrences affect totals.

## Handoff and parallel work

ARC-031/030 consume performance IDs regardless of whether entered historically
or confirmed here. Coordinate shared occurrence mapping with ARC-027, but do not
block unrelated recording-player work.

## Implementation and verification (2026-10-05)

Contract (decisions made in this slice):

- Plan and actual are separate persistence. A new `programme_confirmations`
  row (one per programme, bound to the published revision it reviewed,
  attribution quad, application-bumped `RowVersion`; token 0 means "nothing
  confirmed yet", so a created confirmation starts at 1) says the actual
  programme was reviewed. The sung songs are ARC-028 `performances` rows —
  always evidence `confirmed` — with two new nullable columns:
  `ProgrammeItemId` (the frozen planned entry; unique index) and
  `ConfirmationId` (owner; also set for added encores). A planned entry of the
  confirmed revision without an occurrence is "skipped". Historical ARC-028
  rows (both columns null) are never read or written by a confirmation; the
  planned revision/item rows and the programme `RowVersion` are never written.
- `GET`/`PUT /api/events/{eventId}/programme/confirmation` (Editor/
  Administrator, antiforgery on PUT, `no-store`, German ProblemDetails): GET
  returns the review of a published revision (`?revisionId=`, default newest)
  with per-entry `outcome` open/sung/skipped/unconfirmed, linked occurrence,
  `suggestedPerformanceId` carry-over, additions, `upToDate`, `rowVersion`.
  PUT is a complete, idempotent statement (every planned entry exactly once as
  `sung` — optionally with a corrected musical version of the same song — or
  `skipped`, plus `additions`). Identical state answers 200 without writing
  (lost-response retries); a different statement with a stale confirmation or
  occurrence token is 409; a revision other than the newest publication is 409
  (`StaleRevisionMessage`) and nothing is written; a certainly-future event
  date is 409; foreign items/occurrences/revisions are 400/404 before any
  write. Existing occurrences update in place (stable performance ids), encores
  are keyed by a client key (`programme-addition:<key>` retry key, planned
  entries `programme-item:<id>`), genuine repeated songs are distinct rows.
  After a republication the review suggests earlier occurrences of the same
  song and the editor adopts them explicitly with `performanceId`, so
  performance ids (recordings!) survive; unclaimed owned occurrences are
  removed. All in one `SaveChanges`.
- `GET /api/events/{id}` embeds `programme.confirmation` (members without the
  token): ordered `actual` (planned-linked in plan order, then encores; `added`,
  `differsFromPlan`, evidence status) and `skipped`. Editor ARC-028 embeds gain
  `programmeItemId`/`confirmationId`; `POST /api/performances/{id}/delete`
  refuses confirmation-owned rows (409) so the outcome only changes there.
- Migration `20261004165004_ProgrammeConfirmation` (additive).
  `dotnet ef migrations has-pending-model-changes --project backend`: "No
  changes have been made to the model since the last migration."

UI: `components/auftritt-bestaetigung.tsx` ("Tatsächlich gesungen", below
Programm in `auftritt-detail.tsx`), `lib/events.ts` types/helpers, CSS block in
`app/globals.css`; `LiedWahl` in `auftritt-programm.tsx` is exported with a
configurable field id.

Verification (actual commands/results):

- `dotnet ef migrations has-pending-model-changes --project backend` (from
  `src/archive`, placeholder `ConnectionStrings__archive-migrations`): no changes.
- `dotnet build src/archive/Archive.slnx`: 0 warnings, 0 errors.
- `dotnet test tests/archive/backend --filter FullyQualifiedName~ProgrammeConfirmationApiTests`:
  11 passed (red first: all 11 failed with 404 before the endpoints existed).
- `dotnet test tests/archive/backend`: **491 passed**, 0 failed (480 + 11).
- `corepack pnpm run check`: clean (Biome + route types + tsc); `corepack pnpm run
  build`: static export succeeds.
- Playwright against the static export (`ARCHIVE_BASE_URL=http://localhost:3111`,
  `--workers=1`): `tests/auftritt-bestaetigung.spec.ts` 10/10 (5 tests x desktop +
  mobile); regression `tests/programm.spec.ts` 28/28 (selectors scoped to
  `section.auftritt-programm` because the confirmation workbench reuses the
  `programm-verwaltung` details class), `tests/auftritt-belege.spec.ts` 10/10,
  `tests/auftritte.spec.ts` + `tests/auftritt-dokumente.spec.ts` 32/32.
- Not run: `dotnet test tests/archive/apphost` (clean-stack Aspire run; only a
  `_ProgrammeConfirmation` pending-migration assertion was added to
  `WalkingSkeletonTests.cs`) and `shell.spec.ts` (needs a real backend).

Known limits: no endpoint withdraws a confirmation (mark everything skipped, or
wait for ARC-040 trash); actual order is plan order then encores (no
re-ordering of the sung sequence); confirming does not touch the event date or
publication; omitted owned occurrences are deleted (a future recording FK must
decide how this behaves, see ARC-032/040 notes in the handoff).
