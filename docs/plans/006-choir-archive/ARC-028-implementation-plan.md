# ARC-028 implementation plan — Performance evidence (orchestrator)

Source of truth: [ARC-028 spec](./ARC-028-performance-evidence.md). Dependencies
ARC-013 (published songs/arrangements/versions) and ARC-024 (events with honest
date uncertainty) are done.

## Shared contract (frozen by slice S1, consumed by ARC-029/031/032)

Occurrence identity is the `Auffuehrung` row (`performances` table, EF entity
`Performance` in `Archive.Backend.Events`). One row is one **historical
occurrence** of a song at one event — repeated editors' submissions and retries
never create a second identity. Its stable GUID (GET v7) is the performance ID
that ARC-031 (song history/extensible records) and ARC-032 (recordings) consume.

- Evidence status: closed set `confirmed` (confirmed performance) /
  `mention` (unconfirmed programme mention). No ambiguous combined
  `performed` flag anywhere; planned programmes (ARC-026 stays untouched) and
  actual occurrences remain separate persistence.
- Occurrence fields: `EventId` (Restrict), `SongId` (Restrict),
  `ArrangementId?`, `MusicalVersionId?` (both Restrict; all rewritten together
  by one PATCH request — the optional known-chain remains resolvable),
  `Position` (1-based within the event, unique `(EventId, Position)`,
  deterministic ordering), `EvidenceStatus` (required), `SourceNote?` (≤2000,
  required for `mention`), plus the attribution quad
  (`CreatedAt/ByAccountId`, `UpdatedAt/ByAccountId`) and `uint RowVersion`
  concurrency token. No `PublishedAt` — occurrences are editor data that
  publishes as part of the event's partial history.
- Song/arrangement/version consistency: a song without known chains stays
  addressable with `musicalVersionId: null` ("Fassung unbekannt"). When a
  known chain is supplied, the musical version must exist and belong to the
  arrangement, and the arrangement must belong to the song — Germans 400
  (`FassungPasstNichtMessage`) / 404 replies, mirroring the ARC-026 `ResolveItemsAsync`
  validation shape.
- Idempotency: the write endpoint accepts an `idempotencyKey`. The same
  key replayed answers the stored occurrence; retries never inflate totals.
  Distinct repeated occurrences are distinct submissions (separate rows).
- Attribution: per-submission stamps resolve to account IDs; the detail embed
  also carries per-submission `capturedAt` (CF `UpdatedAt`) so the editorial
  trail ("when did we learn this") is visible without new columns.
- Partial history publishes with the member-visible event; the event detail
  embed renders both evidence statuses so members see the honest partial
  state. Counts in any response are occurrence-based, not submission-based.
- API surface (editor-only mutations, member-visible reads, antiforgery, CSRF,
  `no-store`, German ProblemDetails, 409 on rowVersion conflicts):
  - `GET /api/events/{eventId}/performances` — editors only (embed exposes them)
  - `POST /api/events/{eventId}/performances` — create occurrence
  - `PATCH /api/performances/{id}` — edit evidence/status/notes/chain
  - `POST /api/performances/{id}/delete` — remove occurrence
  - `GET /api/songs/{songId}/performances` — editors only (ARC-031 groundwork)
  All are additive; ARC-026/027 programme endpoints are unchanged.

## Vertical slices (each directly testable, committed before the next)

- **S1 — persisted evidence core (backend + xUnit):** entities, EF
  configuration, additive migration, DbSets, endpoint wiring, embed
  integration into the event detail payload, full API test coverage
  (incl. idempotent retry, genuine repeat occurrence, evidence edit,
  permissions, member visibility, planned/actual boundary).
- **S2 — editor workbench (frontend + browser check):** new
  `Belege` (evidence) section on the event detail with entry/edit/delete,
  LiedWahl + Fassung-unbekannt handling, honest empty/uncertain states,
  Playwright coverage. Local UI exercise starts the Aspire stack and applies
  `archive-migrate` explicitly before browser interaction.
- **S3 — member partial history (frontend):** the same section renders the
  member-visible partial history with evidence-status distinctions and no
  invented states; Playwright coverage.

## Definitions of done per slice

1. The named verification commands pass locally before the commit.
2. Commit (Karma format) immediately after the slice verifies.
3. A fresh review subagent reviews the committed range against the ARC-028
   spec; its blocking findings are fixed before the next slice (also by
   subagents, re-reviewed).
4. Slice S1 additionally updates ARC-028 frontmatter `status: in_progress`
   and appends its verification commands/results to the issue file; the
   final slice updates `status: done` and `issues.md` (see the issue-index
   contract).

## Final verification and handover

- Full `dotnet test tests/archive/backend`,
  `pnpm run check` + `pnpm run test:browser`, and the AppHost integration
  test (if the environment allows) pass before pushing.
- Push directly to `origin main`; wait for the `Archive` workflow's Check
  build to pass. Failures are fixed by subagents until green.
- Mark ARC-028 `status: done`, record verification commands and results in
  the issue file, and update the issue index row in `issues.md`.
